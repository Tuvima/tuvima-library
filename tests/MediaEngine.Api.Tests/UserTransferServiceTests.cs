using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Backups;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Backup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// People moved between Libraries (backup and restore spec D19, users spec D9 and D16), through the real export, the real file checks, the
/// real import and the real Data Protection codec, on two temporary data stores that stand for two computers with their own keys.
/// </summary>
public sealed class UserTransferServiceTests : IDisposable
{
    private const string TwoStepSecret = "JBSWY3DPEHPK3PXP";
    private const string Stamp = "2026-10-21T10:00:00Z";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-user-transfer-{Guid.NewGuid():N}");
    private readonly List<DatabaseConnection> _stores = [];

    public UserTransferServiceTests() => Directory.CreateDirectory(_root);

    // ---------------------------------------------------------------------------------------------------------
    // Export
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ExportOwn_HoldsOnlyTheSignedInPersonsData_IsNamedByTheClock_AndIsAudited()
    {
        var old = Computer.New(this, "old");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var other = old.AddUser("noor-private@example.com", ["Noor Private"]);
        var audit = new ManagedSessionServiceTests.RecordingAudit();

        var download = await old.Service(audit: audit).ExportOwnAsync(Human(maya.Account), CancellationToken.None);

        Assert.Matches(new Regex(@"^tuvima-user-\d{8}-\d{6}\.zip$"), download.FileName);
        var text = ZipText(download.Content);
        Assert.Contains("maya@example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("noor-private@example.com", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Noor Private", text, StringComparison.Ordinal);
        Assert.Equal(TwoStepSecret, ReadPortableTwoStep(download.Content));
        using (var archive = new ZipArchive(new MemoryStream(download.Content), ZipArchiveMode.Read))
        {
            using var manifest = JsonDocument.Parse(new StreamReader(archive.GetEntry("manifest.json")!.Open()).ReadToEnd());
            Assert.Equal("user", manifest.RootElement.GetProperty("kind").GetString());
            Assert.Equal("profile", manifest.RootElement.GetProperty("scope").GetString());
            Assert.False(manifest.RootElement.TryGetProperty("user_email", out _));
            Assert.Equal(1, manifest.RootElement.GetProperty("profile_count").GetInt32());
            Assert.Equal(1, manifest.RootElement.GetProperty("sign_in_count").GetInt32());
            Assert.False(manifest.RootElement.GetProperty("two_step_left_out").GetBoolean());
        }

        var audited = Assert.Single(audit.Events);
        Assert.Equal("account.exported", audited.EventType);
        Assert.Equal(maya.Account, audited.ActorAccountId);
        Assert.Equal(maya.Account.ToString("D"), audited.SubjectId);
        Assert.DoesNotContain(audited.Changes, pair => (pair.Value ?? string.Empty).Contains("example.com", StringComparison.Ordinal));
        Assert.NotEqual(other.Account, audited.ActorAccountId);
    }

    [Theory]
    [InlineData(PrincipalKind.ServiceApplication)]
    [InlineData(PrincipalKind.DelegatedUserClient)]
    [InlineData(PrincipalKind.Anonymous)]
    public async Task ExportOwn_IsRefusedForAnyCallerThatIsNotAPersonSignedInHere(PrincipalKind kind)
    {
        var old = Computer.New(this, "old");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var audit = new ManagedSessionServiceTests.RecordingAudit();
        var caller = new RequestAuthority(kind, true, AccountId: maya.Account, ActiveProfileId: maya.Profile, ApplicationId: Guid.NewGuid());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => old.Service(audit: audit).ExportOwnAsync(caller, CancellationToken.None));

        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Export_OfAnotherPerson_NeedsAnAdministrator_AndOfAMissingAccountIsNotFound()
    {
        var old = Computer.New(this, "old");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var admin = old.AddUser("admin@example.com", ["Admin"]);
        var audit = new ManagedSessionServiceTests.RecordingAudit();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => old.Service(administrator: null, audit: audit).ExportAsync(Human(maya.Account), admin.Account, CancellationToken.None));
        Assert.Empty(audit.Events);

        var download = await old.Service(administrator: admin.Account, audit: audit).ExportAsync(Human(admin.Account), maya.Account, CancellationToken.None);
        Assert.Contains("maya@example.com", ZipText(download.Content), StringComparison.Ordinal);
        var audited = Assert.Single(audit.Events);
        Assert.Equal("account.exported", audited.EventType);
        Assert.Equal(admin.Account, audited.ActorAccountId);
        Assert.Equal(maya.Account.ToString("D"), audited.SubjectId);

        var missing = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => old.Service(administrator: admin.Account).ExportAsync(Human(admin.Account), Guid.NewGuid(), CancellationToken.None));
        Assert.Equal("That user isn't in this Library.", missing.Message);
    }

    [Fact]
    public async Task Export_ByAnAdministratorOfAnotherPerson_LeavesOutTheTwoStepSecret_AndSaysSoInTheManifest()
    {
        var old = Computer.New(this, "old");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var admin = old.AddUser("admin@example.com", ["Admin"]);

        var download = await old.Service(administrator: admin.Account).ExportAsync(Human(admin.Account), maya.Account, CancellationToken.None);

        using (var archive = new ZipArchive(new MemoryStream(download.Content), ZipArchiveMode.Read))
        {
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("account_two_step", StringComparison.Ordinal));
            using var manifest = JsonDocument.Parse(new StreamReader(archive.GetEntry("manifest.json")!.Open()).ReadToEnd());
            Assert.True(manifest.RootElement.GetProperty("two_step_left_out").GetBoolean());
        }

        Assert.DoesNotContain(TwoStepSecret, ZipText(download.Content), StringComparison.Ordinal);
        Assert.DoesNotContain(TwoStepSecret, Encoding.Latin1.GetString(download.Content), StringComparison.Ordinal);
        Assert.Contains("maya@example.com", ZipText(download.Content), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_ByAnAdministratorOfThemselvesThroughTheAdminRoute_KeepsTheTwoStepSecret()
    {
        var old = Computer.New(this, "old");
        var admin = old.AddUser("admin@example.com", ["Admin"]);

        var download = await old.Service(administrator: admin.Account).ExportAsync(Human(admin.Account), admin.Account, CancellationToken.None);

        Assert.Equal(TwoStepSecret, ReadPortableTwoStep(download.Content));
        using var archive = new ZipArchive(new MemoryStream(download.Content), ZipArchiveMode.Read);
        using var manifest = JsonDocument.Parse(new StreamReader(archive.GetEntry("manifest.json")!.Open()).ReadToEnd());
        Assert.False(manifest.RootElement.GetProperty("two_step_left_out").GetBoolean());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Import
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ImportingAFileAnAdministratorMadeForSomeoneElse_SucceedsWithTwoStepOff_AndThePreviewSaysTheyWillSetItUpAgain()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var oldAdmin = old.AddUser("old-admin@example.com", ["Old admin"]);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var download = await old.Service(administrator: oldAdmin.Account).ExportAsync(Human(oldAdmin.Account), maya.Account, CancellationToken.None);
        var service = fresh.Service(administrator: admin.Account);

        var preview = await InspectAsync(service, admin.Account, download.Content);

        Assert.Null(preview.BlockedReason);
        Assert.True(preview.TwoStepToSetUp);

        var result = await service.ImportAsync(Human(admin.Account), preview.OperationId, Into(fresh), CancellationToken.None);

        Assert.Equal("maya@example.com", Assert.Single(result.Profiles).SignInEmail);
        Assert.Equal(0, fresh.Scalar("SELECT COUNT(*) FROM account_two_step WHERE account_id = " + Blob(maya.Account)));
        Assert.Equal(1, fresh.Scalar("SELECT COUNT(*) FROM account_credentials WHERE account_id = " + Blob(maya.Account)));
    }

    [Fact]
    public async Task Inspect_OfAPersonsOwnFile_DoesNotSayTheyWillSetTwoStepUpAgain()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var download = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);

        var preview = await InspectAsync(fresh.Service(administrator: admin.Account), admin.Account, download.Content);

        Assert.False(preview.TwoStepToSetUp);
    }

    [Fact]
    public async Task Inspect_RefusesAFileThatSaysTheTwoStepSecretWasLeftOut_YetHoldsOne_AndKeepsNothing()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var own = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);
        var crafted = SetManifestFlag(own.Content, "two_step_left_out", true);
        var service = fresh.Service(administrator: admin.Account);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => InspectAsync(service, admin.Account, crafted));

        Assert.Equal("This file isn't a user export.", error.Message);
        Assert.Empty(Directory.GetFiles(fresh.StagingFolder, "*.zip"));
        Assert.Equal(0, fresh.Scalar("SELECT COUNT(*) FROM accounts WHERE email = 'maya@example.com'"));
    }

    [Fact]
    public async Task ImportingAnExport_MakesANewUserWhoCanSignInWithTheSameDetailsButSeesNothing_AndKeepsNoFile()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"], administrator: true);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var download = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);
        var audit = new ManagedSessionServiceTests.RecordingAudit();
        var service = fresh.Service(administrator: admin.Account, audit: audit);

        var preview = await InspectAsync(service, admin.Account, download.Content);

        Assert.Null(preview.BlockedReason);
        Assert.Equal("maya@example.com", Assert.Single(preview.SignIns).Email);
        Assert.Equal(["Maya"], preview.Profiles.Select(profile => profile.Name));
        Assert.Equal(0, fresh.Scalar("SELECT COUNT(*) FROM accounts WHERE email = 'maya@example.com'"));
        Assert.Single(Directory.GetFiles(fresh.StagingFolder, "*.zip"));

        var home = fresh.Home();
        var result = await service.ImportAsync(Human(admin.Account), preview.OperationId, new(TargetHouseholdId: home), CancellationToken.None);

        Assert.Equal(home, result.HouseholdId);
        Assert.False(result.CreatedHousehold);
        Assert.Equal("maya@example.com", Assert.Single(result.Profiles).SignInEmail);
        Assert.Equal(["Maya"], result.Profiles.Select(profile => profile.Name));
        Assert.Equal(1, fresh.Scalar($"SELECT COUNT(*) FROM profiles WHERE display_name = 'Maya' AND household_id = {Blob(home)}"));
        Assert.Equal(old.Text("SELECT secret_hash FROM account_credentials WHERE account_id = " + Blob(maya.Account)),
            fresh.Text("SELECT secret_hash FROM account_credentials WHERE account_id = " + Blob(maya.Account)));
        Assert.Equal(TwoStepSecret, fresh.Protector.Unprotect(fresh.Text("SELECT secret_protected FROM account_two_step WHERE account_id = " + Blob(maya.Account))));
        Assert.Null(old.Protector.Unprotect(fresh.Text("SELECT secret_protected FROM account_two_step WHERE account_id = " + Blob(maya.Account))));
        // The imported person is no administrator of any kind and holds no access; the user opens exactly the imported profile.
        Assert.Equal(0, fresh.Scalar("SELECT COUNT(*) FROM profiles WHERE is_server_admin = 1 OR is_household_admin = 1"));
        Assert.Equal(0, fresh.Scalar("SELECT COUNT(*) FROM profile_feature_grants g JOIN accounts a ON a.profile_id = g.profile_id WHERE a.email = 'maya@example.com'"));
        Assert.Equal(0, fresh.Scalar("SELECT COUNT(*) FROM profile_library_grants g JOIN accounts a ON a.profile_id = g.profile_id WHERE a.email = 'maya@example.com'"));
        Assert.Equal(1, fresh.Scalar("SELECT COUNT(*) FROM accounts a JOIN profiles p ON p.id = a.profile_id WHERE a.email = 'maya@example.com' AND p.display_name = 'Maya'"));
        Assert.Empty(Directory.GetFiles(fresh.StagingFolder, "*.zip"));

        var imported = Assert.Single(audit.Events);
        Assert.Equal("account.imported", imported.EventType);
        Assert.Equal(admin.Account, imported.ActorAccountId);
        Assert.Equal("household", imported.SubjectType);
        Assert.Equal(home.ToString("D"), imported.SubjectId);
        var everythingAudited = imported.EventType + imported.SubjectId + string.Join(",", imported.Changes.Select(pair => pair.Key + pair.Value));
        Assert.DoesNotContain("maya@example.com", everythingAudited, StringComparison.Ordinal);
        Assert.DoesNotContain(TwoStepSecret, everythingAudited, StringComparison.Ordinal);

        // The same file cannot be imported twice.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ImportAsync(Human(admin.Account), preview.OperationId, Into(fresh), CancellationToken.None));
    }

    [Fact]
    public async Task Inspect_ShowsTheProfilesRoleAndContentLimit_SoAnAdministratorCanSpotAFileThatWasChanged()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        old.Execute("UPDATE profiles SET role = 'RestrictedProfile', content_limit = 'PG', content_limit_allow_unrated = 1 WHERE display_name = 'Maya'");
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var download = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);

        var preview = await InspectAsync(fresh.Service(administrator: admin.Account), admin.Account, download.Content);

        var profile = Assert.Single(preview.Profiles);
        Assert.Equal(("Maya", "RestrictedProfile", "PG", true), (profile.Name, profile.Role, profile.ContentLimit, profile.ContentLimitAllowUnrated));
        Assert.Equal(maya.Profile, profile.Id);
        Assert.Equal(maya.Account, profile.SignInId);
        Assert.Equal(["read"], profile.Access);
    }

    [Fact]
    public async Task Inspect_NamesTheFilesHouseholdAsASuggestion_ListsTheHouseholdsHere_AndSaysHowManyProfilesFit()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var householdId = Guid.NewGuid();
        old.AddHousehold(householdId, "The Khans (old name)");
        var maya = old.AddUser("maya@example.com", ["Maya"], household: householdId);
        var noHousehold = old.AddUser("noor@example.com", ["Noor"]);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var home = fresh.Home();
        var service = fresh.Service(administrator: admin.Account);
        var mayaFile = (await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None)).Content;
        var noorFile = (await old.Service().ExportOwnAsync(Human(noHousehold.Account), CancellationToken.None)).Content;

        var preview = await InspectAsync(service, admin.Account, mayaFile);

        Assert.Equal("The Khans (old name)", preview.HouseholdName);
        Assert.Null((await InspectAsync(service, admin.Account, noorFile)).HouseholdName);
        Assert.Equal("profile", preview.Scope);
        var here = Assert.Single(preview.Households);
        Assert.Equal((home, "Home"), (here.Id, here.Name));
        Assert.Equal(8, preview.MaxProfilesPerHousehold);
        Assert.True(preview.CanAddHousehold);
        Assert.Null(preview.AddHouseholdBlockedReason);
    }

    [Fact]
    public async Task Inspect_ByTheSameAdministratorAgain_ReplacesAndDeletesTheirEarlierKeptFile()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var noor = old.AddUser("noor@example.com", ["Noor"]);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var other = fresh.AddUser("other-admin@example.com", ["Other"]);
        var service = fresh.Service(administrator: admin.Account);
        var first = await InspectAsync(service, admin.Account, (await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None)).Content);
        var second = await InspectAsync(service, admin.Account, (await old.Service().ExportOwnAsync(Human(noor.Account), CancellationToken.None)).Content);

        Assert.Single(Directory.GetFiles(fresh.StagingFolder, "*.zip"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ImportAsync(Human(admin.Account), first.OperationId, Into(fresh), CancellationToken.None));
        Assert.Equal("noor@example.com", Assert.Single((await service.ImportAsync(Human(admin.Account), second.OperationId, Into(fresh), CancellationToken.None)).Profiles).SignInEmail);
        Assert.NotEqual(admin.Account, other.Account);
    }

    [Fact]
    public async Task Inspect_SaysWhichSignInUsesAnEmailThatIsTakenHere_AndKeepsTheFileSoAnotherEmailCanBeChosen()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        fresh.AddUser("MAYA@example.com", ["Someone else"]);
        var download = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);
        var service = fresh.Service(administrator: admin.Account);

        var preview = await InspectAsync(service, admin.Account, download.Content);

        var signIn = Assert.Single(preview.SignIns);
        Assert.True(signIn.EmailInUse);
        Assert.Null(signIn.Problem);
        Assert.Null(preview.BlockedReason);
        Assert.Single(Directory.GetFiles(fresh.StagingFolder, "*.zip"));

        var imported = await service.ImportAsync(
            Human(admin.Account), preview.OperationId, Into(fresh) with { Emails = new Dictionary<Guid, string> { [signIn.Id] = "maya.new@example.com" } }, CancellationToken.None);

        Assert.Equal("maya.new@example.com", Assert.Single(imported.Profiles).SignInEmail);
        Assert.Equal(old.Text("SELECT secret_hash FROM account_credentials WHERE account_id = " + Blob(maya.Account)),
            fresh.Text("SELECT c.secret_hash FROM account_credentials c JOIN accounts a ON a.id = c.account_id WHERE a.email = 'maya.new@example.com'"));
    }

    [Fact]
    public async Task Import_IsRefusedWithTheSameSentence_WhenTheEmailWasTakenAfterThePreview_AndNothingIsWritten()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var download = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);
        var service = fresh.Service(administrator: admin.Account);
        var preview = await InspectAsync(service, admin.Account, download.Content);
        Assert.False(Assert.Single(preview.SignIns).EmailInUse);

        fresh.AddUser("maya@example.com", ["Quick"]);
        var home = fresh.Home();
        var accountsBefore = fresh.Scalar("SELECT COUNT(*) FROM accounts");
        var refused = await Assert.ThrowsAsync<UserTransferRefusedException>(() => service.ImportAsync(Human(admin.Account), preview.OperationId, new(TargetHouseholdId: home), CancellationToken.None));

        Assert.Equal("Someone in this Library already signs in with maya@example.com. Nothing was imported.", refused.Message);
        Assert.Equal(accountsBefore, fresh.Scalar("SELECT COUNT(*) FROM accounts"));
        Assert.Equal(0, fresh.Scalar("SELECT COUNT(*) FROM backup_restores"));
        // The file stays kept, so the administrator can choose another email and go on.
        Assert.Single(Directory.GetFiles(fresh.StagingFolder, "*.zip"));
    }

    [Fact]
    public async Task Import_OfAFileMadeByThisVeryLibrary_GivesTheCopyNewIds_AndLeavesTheOriginalAlone()
    {
        var old = Computer.New(this, "old");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var admin = old.AddUser("admin@example.com", ["Admin"]);
        var home = old.Home();
        var download = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);
        var service = old.Service(administrator: admin.Account);
        var preview = await InspectAsync(service, admin.Account, download.Content);
        var signIn = Assert.Single(preview.SignIns);
        Assert.True(signIn.EmailInUse);

        var result = await service.ImportAsync(
            Human(admin.Account),
            preview.OperationId,
            new(TargetHouseholdId: home, Emails: new Dictionary<Guid, string> { [signIn.Id] = "maya.copy@example.com" }),
            CancellationToken.None);

        var copy = Assert.Single(result.Profiles);
        Assert.NotEqual(maya.Profile, copy.Id);
        Assert.Equal(2, old.Scalar("SELECT COUNT(*) FROM profiles WHERE display_name = 'Maya'"));
        Assert.Equal(1, old.Scalar($"SELECT COUNT(*) FROM accounts WHERE id = {Blob(maya.Account)} AND email = 'maya@example.com' AND profile_id = {Blob(maya.Profile)}"));
        Assert.Equal(1, old.Scalar($"SELECT COUNT(*) FROM accounts WHERE email = 'maya.copy@example.com' AND profile_id = {Blob(copy.Id)}"));
    }

    [Fact]
    public async Task Import_OfTheSameKeptFileTwiceAtOnce_ImportsItOnce_AndTurnsTheSecondAwayWithAConflictSentence()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var download = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);
        using var codec = new HoldingCodec(fresh.Codec);
        var service = fresh.Service(administrator: admin.Account, importCodec: codec);
        var preview = await InspectAsync(service, admin.Account, download.Content);
        var request = Into(fresh);

        // The first import is held inside the data store write while the second request for the same file arrives.
        var first = Task.Run(() => service.ImportAsync(Human(admin.Account), preview.OperationId, request, CancellationToken.None));
        Assert.True(codec.Entered.Wait(TimeSpan.FromSeconds(60)), "The first import never reached the data store.");

        var second = await Assert.ThrowsAsync<UserTransferRefusedException>(
            () => service.ImportAsync(Human(admin.Account), preview.OperationId, request, CancellationToken.None));
        Assert.Equal("This file is already being imported.", second.Message);

        codec.Proceed.Set();
        var result = await first;
        Assert.Single(result.Profiles);
        Assert.Equal(1, fresh.Scalar("SELECT COUNT(*) FROM accounts WHERE email = 'maya@example.com'"));
        Assert.Equal(1, fresh.Scalar("SELECT COUNT(*) FROM profiles WHERE display_name = 'Maya'"));
        Assert.Empty(Directory.GetFiles(fresh.StagingFolder, "*.zip"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ImportAsync(Human(admin.Account), preview.OperationId, request, CancellationToken.None));
    }

    [Fact]
    public async Task Import_ThatIsRefused_GivesTheKeptFileBack_SoAnotherChoiceCanBeTriedAndThenWorks()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var download = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);
        var service = fresh.Service(administrator: admin.Account);
        var preview = await InspectAsync(service, admin.Account, download.Content);
        fresh.AddUser("maya@example.com", ["Quick"]);
        var signIn = Assert.Single(preview.SignIns);

        await Assert.ThrowsAsync<UserTransferRefusedException>(() => service.ImportAsync(Human(admin.Account), preview.OperationId, Into(fresh), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ImportAsync(
            Human(admin.Account), preview.OperationId, Into(fresh) with { Emails = new Dictionary<Guid, string> { [signIn.Id] = "nope" } }, CancellationToken.None));
        var result = await service.ImportAsync(
            Human(admin.Account), preview.OperationId, Into(fresh) with { Emails = new Dictionary<Guid, string> { [signIn.Id] = "maya.new@example.com" } }, CancellationToken.None);

        Assert.Equal("maya.new@example.com", Assert.Single(result.Profiles).SignInEmail);
    }

    [Fact]
    public async Task Import_ByAnotherAdministrator_IsNotFound_BecauseTheKeptFileBelongsToWhoUploadedIt()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var second = fresh.AddUser("second@example.com", ["Second"]);
        var download = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);
        var preview = await InspectAsync(fresh.Service(administrator: admin.Account), admin.Account, download.Content);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => fresh.Service(administrator: second.Account).ImportAsync(Human(second.Account), preview.OperationId, Into(fresh), CancellationToken.None));

        Assert.Equal(0, fresh.Scalar("SELECT COUNT(*) FROM accounts WHERE email = 'maya@example.com'"));
    }

    [Fact]
    public async Task InspectAndImport_NeedAnAdministrator()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var member = fresh.AddUser("member@example.com", ["Member"]);
        var download = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);
        var service = fresh.Service(administrator: null);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => InspectAsync(service, member.Account, download.Content));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ImportAsync(Human(member.Account), Guid.NewGuid(), Into(fresh), CancellationToken.None));

        Assert.Empty(Directory.Exists(fresh.StagingFolder) ? Directory.GetFiles(fresh.StagingFolder, "*.zip") : []);
    }

    [Fact]
    public async Task Inspect_RefusesAFileThatIsNotAUserExport_WithOneSentence()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        old.AddUser("maya@example.com", ["Maya"]);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var backup = await File.ReadAllBytesAsync(await old.BackupService().CreateAsync(CancellationToken.None));
        var service = fresh.Service(administrator: admin.Account);

        var wholeBackup = await Assert.ThrowsAsync<InvalidDataException>(() => InspectAsync(service, admin.Account, backup));
        var notAZip = await Assert.ThrowsAsync<InvalidDataException>(() => InspectAsync(service, admin.Account, Encoding.UTF8.GetBytes("not a zip")));

        Assert.Equal("This file isn't a user export.", wholeBackup.Message);
        Assert.Equal("This file is damaged and can't be imported.", notAZip.Message);
        Assert.Empty(Directory.GetFiles(fresh.StagingFolder, "*.zip"));
        Assert.Equal(0, fresh.Scalar("SELECT COUNT(*) FROM accounts WHERE email = 'maya@example.com'"));
    }

    [Fact]
    public async Task ExportMyData_ImportedUnderAnotherHouseholdWithADifferentEmail_SignsInWithTheSamePassword_AndSeesNothingUntilAccessIsGiven()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"], administrator: true);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var home = fresh.Home();
        var download = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);
        var service = fresh.Service(administrator: admin.Account);
        var preview = await InspectAsync(service, admin.Account, download.Content);
        var signIn = Assert.Single(preview.SignIns);

        var result = await service.ImportAsync(
            Human(admin.Account),
            preview.OperationId,
            new(TargetHouseholdId: home, Emails: new Dictionary<Guid, string> { [signIn.Id] = "maya@new-home.example" }),
            CancellationToken.None);

        var profile = Assert.Single(result.Profiles);
        Assert.Equal("maya@new-home.example", profile.SignInEmail);
        Assert.False(profile.IsHouseholdAdministrator);
        Assert.Equal(
            old.Text("SELECT secret_hash FROM account_credentials WHERE account_id = " + Blob(maya.Account)),
            fresh.Text("SELECT c.secret_hash FROM account_credentials c JOIN accounts a ON a.id = c.account_id WHERE a.email = 'maya@new-home.example'"));
        Assert.Equal(1, fresh.Scalar($"SELECT COUNT(*) FROM profiles WHERE id = {Blob(profile.Id)} AND household_id = {Blob(home)} AND is_server_admin = 0 AND is_household_admin = 0"));
        Assert.Equal(0, fresh.Scalar($"SELECT COUNT(*) FROM profile_feature_grants WHERE profile_id = {Blob(profile.Id)}"));
        Assert.Equal(0, fresh.Scalar($"SELECT COUNT(*) FROM profile_library_grants WHERE profile_id = {Blob(profile.Id)}"));
    }

    [Fact]
    public async Task ExportMyData_ImportedWithKeepTheirAccess_ArrivesWithReadWatchListenViewAsTheFileHadThem_ButNeverLibraries()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var home = fresh.Home();
        var download = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);
        var service = fresh.Service(administrator: admin.Account);
        var preview = await InspectAsync(service, admin.Account, download.Content);

        var result = await service.ImportAsync(Human(admin.Account), preview.OperationId, new(TargetHouseholdId: home, KeepAccess: true), CancellationToken.None);

        var profile = Assert.Single(result.Profiles);
        Assert.Equal(1, fresh.Scalar($"SELECT COUNT(*) FROM profile_feature_grants WHERE profile_id = {Blob(profile.Id)} AND feature_id = 'read'"));
        Assert.Equal(0, fresh.Scalar($"SELECT COUNT(*) FROM profile_library_grants WHERE profile_id = {Blob(profile.Id)}"));
    }

    [Fact]
    public async Task AHouseholdExport_ImportedAsANewHousehold_MakesTheChosenPersonItsAdministrator_KeepsPasswords_AndOnlyTheExportersTwoStep()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var khans = Guid.NewGuid();
        old.AddHousehold(khans, "The Khans");
        var mom = old.AddUser("mom@example.com", ["Mom"], household: khans, householdAdministrator: true);
        var kid = old.AddUser("kid@example.com", ["Kid"], household: khans);
        old.Execute("UPDATE profiles SET role = 'RestrictedProfile', content_limit = 'PG' WHERE display_name = 'Kid'");
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var download = await old.Service(householdAdministrator: mom.Account).ExportHouseholdAsync(Human(mom.Account, khans), khans, CancellationToken.None);

        using (var archive = new ZipArchive(new MemoryStream(download.Content), ZipArchiveMode.Read))
        {
            using var manifest = JsonDocument.Parse(new StreamReader(archive.GetEntry("manifest.json")!.Open()).ReadToEnd());
            Assert.Equal("household", manifest.RootElement.GetProperty("scope").GetString());
            Assert.Equal(2, manifest.RootElement.GetProperty("profile_count").GetInt32());
            Assert.Equal(2, manifest.RootElement.GetProperty("sign_in_count").GetInt32());
            Assert.True(manifest.RootElement.GetProperty("two_step_left_out").GetBoolean());
        }

        var service = fresh.Service(administrator: admin.Account);
        var preview = await InspectAsync(service, admin.Account, download.Content);
        Assert.Equal("household", preview.Scope);
        Assert.Equal(2, preview.Profiles.Count);
        Assert.Equal(2, preview.SignIns.Count);
        Assert.True(preview.TwoStepToSetUp);

        var result = await service.ImportAsync(
            Human(admin.Account),
            preview.OperationId,
            new(NewHouseholdName: "Imported Khans", NewHouseholdAdministratorProfileId: mom.Profile),
            CancellationToken.None);

        Assert.True(result.CreatedHousehold);
        Assert.Equal("Imported Khans", result.HouseholdName);
        Assert.Equal(["Kid", "Mom"], result.Profiles.Select(profile => profile.Name).Order());
        Assert.Equal(1, fresh.Scalar($"SELECT COUNT(*) FROM profiles WHERE household_id = {Blob(result.HouseholdId)} AND is_household_admin = 1"));
        Assert.Equal(1, fresh.Scalar($"SELECT COUNT(*) FROM profiles WHERE id = {Blob(mom.Profile)} AND is_household_admin = 1 AND role = 'StandardUser'"));
        Assert.Equal(1, fresh.Scalar($"SELECT COUNT(*) FROM profiles WHERE id = {Blob(kid.Profile)} AND is_household_admin = 0 AND role = 'RestrictedProfile' AND content_limit = 'PG'"));
        Assert.Equal(
            old.Text("SELECT secret_hash FROM account_credentials WHERE account_id = " + Blob(kid.Account)),
            fresh.Text("SELECT secret_hash FROM account_credentials WHERE account_id = " + Blob(kid.Account)));
        Assert.Equal(TwoStepSecret, fresh.Protector.Unprotect(fresh.Text("SELECT secret_protected FROM account_two_step WHERE account_id = " + Blob(mom.Account))));
        Assert.Equal(0, fresh.Scalar("SELECT COUNT(*) FROM account_two_step WHERE account_id = " + Blob(kid.Account)));
        Assert.Equal(0, fresh.Scalar($"SELECT COUNT(*) FROM profile_feature_grants WHERE profile_id IN ({Blob(mom.Profile)}, {Blob(kid.Profile)})"));
    }

    [Fact]
    public async Task AHouseholdExport_IsForTheServerAdministratorOrTheHouseholdsOwnAdministrator_AndNobodyElse()
    {
        var old = Computer.New(this, "old");
        var khans = Guid.NewGuid();
        var elsewhere = Guid.NewGuid();
        old.AddHousehold(khans, "The Khans");
        old.AddHousehold(elsewhere, "Elsewhere");
        var mom = old.AddUser("mom@example.com", ["Mom"], household: khans, householdAdministrator: true);
        var kid = old.AddUser("kid@example.com", ["Kid"], household: khans);
        var stranger = old.AddUser("stranger@example.com", ["Stranger"], household: elsewhere, householdAdministrator: true);
        var root = old.AddUser("root@example.com", ["Root"], administrator: true);
        var audit = new ManagedSessionServiceTests.RecordingAudit();

        // The household's own administrator.
        var own = await old.Service(householdAdministrator: mom.Account, audit: audit).ExportHouseholdAsync(Human(mom.Account, khans), khans, CancellationToken.None);
        Assert.Contains("kid@example.com", ZipText(own.Content), StringComparison.Ordinal);
        Assert.DoesNotContain("stranger@example.com", ZipText(own.Content), StringComparison.OrdinalIgnoreCase);
        Assert.Matches(new Regex(@"^tuvima-household-\d{8}-\d{6}\.zip$"), own.FileName);
        var audited = Assert.Single(audit.Events);
        Assert.Equal(("household.exported", "household", khans.ToString("D")), (audited.EventType, audited.SubjectType, audited.SubjectId));

        // Another household's administrator, a member, and an unknown household are all told the household is not there or not theirs.
        audit.Events.Clear();
        var anotherHousehold = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => old.Service(householdAdministrator: stranger.Account, audit: audit).ExportHouseholdAsync(Human(stranger.Account, elsewhere), khans, CancellationToken.None));
        var unknown = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => old.Service(householdAdministrator: stranger.Account, audit: audit).ExportHouseholdAsync(Human(stranger.Account, elsewhere), Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(anotherHousehold.Message, unknown.Message);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => old.Service(audit: audit).ExportHouseholdAsync(Human(kid.Account, khans), khans, CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => old.Service(audit: audit).ExportHouseholdAsync(new RequestAuthority(PrincipalKind.ServiceApplication, true, ApplicationId: Guid.NewGuid()), khans, CancellationToken.None));
        Assert.Empty(audit.Events);

        // The server administrator may export any household.
        var any = await old.Service(administrator: root.Account, audit: audit).ExportHouseholdAsync(Human(root.Account), elsewhere, CancellationToken.None);
        Assert.Contains("stranger@example.com", ZipText(any.Content), StringComparison.Ordinal);
        Assert.DoesNotContain("two_step", Encoding.Latin1.GetString(any.Content), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Restore routes
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AUserExport_IsRefusedByEveryRestoreRoute_WithOneSentence()
    {
        var old = Computer.New(this, "old");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var download = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);
        var restoreTarget = Computer.New(this, "restore");
        var backups = restoreTarget.BackupService();

        // A restore picks a file from the backup folder by name, so a user export is dropped there as someone might.
        Directory.CreateDirectory(backups.BackupDirectory);
        var inFolder = Path.Combine(backups.BackupDirectory, "tuvima-backup-20261022-090000.zip");
        await File.WriteAllBytesAsync(inFolder, download.Content);

        var message = "This file is one user's data, not a backup of the Library. Use Import a user instead.";
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => RestoreArchiveValidator.Validate(inFolder)).Message);
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => backups.ScheduleRestore(Path.GetFileName(inFolder))).Message);
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => backups.ValidateRestore(Path.GetFileName(inFolder))).Message);
        await using var upload = new MemoryStream(download.Content);
        var setup = await Assert.ThrowsAsync<InvalidDataException>(
            () => backups.UploadAndInspectAsync(upload, "recovery.zip", new OnboardingRepository(restoreTarget.Database), CancellationToken.None));
        Assert.Equal(message, setup.Message);

        // Nothing was scheduled or imported.
        Assert.False(File.Exists(Path.Combine(restoreTarget.Config, RestoreMarkers.PendingFileName)));
        Assert.Equal(0, restoreTarget.Scalar("SELECT COUNT(*) FROM accounts WHERE email = 'maya@example.com'"));
    }

    [Fact]
    public async Task AWholeBackupThatFillsAColumnAnExportLeavesEmpty_IsRefusedByEveryRestoreRoute()
    {
        var old = Computer.New(this, "old");
        old.AddUser("maya@example.com", ["Maya"]);
        var crafted = CraftAvatarPath(await File.ReadAllBytesAsync(await old.BackupService().CreateAsync(CancellationToken.None)), "/etc/passwd");
        var restoreTarget = Computer.New(this, "restore");
        var backups = restoreTarget.BackupService();
        Directory.CreateDirectory(backups.BackupDirectory);
        var inFolder = Path.Combine(backups.BackupDirectory, "tuvima-backup-20261022-090000.zip");
        await File.WriteAllBytesAsync(inFolder, crafted);

        var message = RestoreArchiveValidator.DamagedMessage;
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => RestoreArchiveValidator.Validate(inFolder)).Message);
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => backups.ScheduleRestore(Path.GetFileName(inFolder))).Message);
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => backups.ValidateRestore(Path.GetFileName(inFolder))).Message);
        await using var upload = new MemoryStream(crafted);
        var setup = await Assert.ThrowsAsync<InvalidDataException>(
            () => backups.UploadAndInspectAsync(upload, "recovery.zip", new OnboardingRepository(restoreTarget.Database), CancellationToken.None));
        Assert.Equal(message, setup.Message);
        Assert.False(File.Exists(Path.Combine(restoreTarget.Config, RestoreMarkers.PendingFileName)));
    }

    [Fact]
    public async Task AUserExportThatFillsAnAvatarPath_IsRefusedWhenInspected_AndKeepsNothing()
    {
        var old = Computer.New(this, "old");
        var fresh = Computer.New(this, "new");
        var maya = old.AddUser("maya@example.com", ["Maya"]);
        var admin = fresh.AddUser("admin@example.com", ["Admin"]);
        var download = await old.Service().ExportOwnAsync(Human(maya.Account), CancellationToken.None);
        var crafted = CraftAvatarPath(download.Content, "/etc/passwd");

        await Assert.ThrowsAsync<InvalidDataException>(() => InspectAsync(fresh.Service(administrator: admin.Account), admin.Account, crafted));

        Assert.Empty(Directory.GetFiles(fresh.StagingFolder, "*.zip"));
        Assert.Equal(0, fresh.Scalar("SELECT COUNT(*) FROM accounts WHERE email = 'maya@example.com'"));
    }

    [Fact]
    public async Task AWholeBackupFile_IsNotReadAsAUserExport()
    {
        var old = Computer.New(this, "old");
        old.AddUser("maya@example.com", ["Maya"]);
        var backupBytes = await File.ReadAllBytesAsync(await old.BackupService().CreateAsync(CancellationToken.None));
        var path = Path.Combine(_root, "whole-backup.zip");
        File.WriteAllBytes(path, backupBytes);

        var error = Assert.Throws<InvalidDataException>(() => RestoreArchiveValidator.ValidateUserExport(path));

        Assert.Equal("This file isn't a user export.", error.Message);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private static RequestAuthority Human(Guid account, Guid? householdAdministratorOf = null) =>
        new(PrincipalKind.Human, true, AccountId: account, ActiveProfileId: Guid.NewGuid(), AccountEnabled: true, ActiveProfileInUserHousehold: true,
            ActiveProfileHouseholdId: householdAdministratorOf, ActiveProfileIsHouseholdAdministrator: householdAdministratorOf is not null);

    /// <summary>Put the profiles in the household this computer has (made on first use).</summary>
    private static MediaEngine.Contracts.Backups.UserImportRequestDto Into(Computer computer) => new(TargetHouseholdId: computer.Home());

    private static async Task<MediaEngine.Contracts.Backups.UserImportPreviewDto> InspectAsync(UserTransferService service, Guid actor, byte[] file)
    {
        await using var stream = new MemoryStream(file);
        return await service.InspectAsync(Human(actor), stream, "tuvima-user-20261022-090000.zip", CancellationToken.None);
    }

    /// <summary>The archive with every profile's <c>avatar_image_path</c> set to a path, as a crafted file would carry it.</summary>
    private static byte[] CraftAvatarPath(byte[] archiveBytes, string path)
    {
        using var stream = new MemoryStream();
        stream.Write(archiveBytes);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.Entries.Single(candidate => candidate.FullName.EndsWith("/profiles.json", StringComparison.Ordinal));
            string json;
            using (var reader = new StreamReader(entry.Open()))
            {
                json = reader.ReadToEnd();
            }

            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
            var index = node["columns"]!.AsArray().Select(column => column!.GetValue<string>()).ToList().IndexOf("avatar_image_path");
            Assert.True(index >= 0);
            foreach (var row in node["rows"]!.AsArray())
            {
                row![index] = path;
            }

            var name = entry.FullName;
            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write(node.ToJsonString());
        }

        return stream.ToArray();
    }

    /// <summary>The archive with one true-or-false property of its manifest set, as a crafted file would carry it.</summary>
    private static byte[] SetManifestFlag(byte[] archiveBytes, string property, bool value)
    {
        using var stream = new MemoryStream();
        stream.Write(archiveBytes);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.GetEntry("manifest.json")!;
            string json;
            using (var reader = new StreamReader(entry.Open()))
            {
                json = reader.ReadToEnd();
            }

            var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            node[property] = value;
            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), new UTF8Encoding(false));
            writer.Write(node.ToJsonString());
        }

        return stream.ToArray();
    }

    private static string Blob(Guid id) => "X'" + Convert.ToHexString(GuidSql.ToBlob(id)) + "'";

    /// <summary>All the text in a zip, so a test can say what the file does not contain.</summary>
    private static string ZipText(byte[] content)
    {
        using var archive = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        var text = new StringBuilder();
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            text.AppendLine(reader.ReadToEnd());
        }

        return text.ToString();
    }

    private static string ReadPortableTwoStep(byte[] content)
    {
        using var archive = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        using var json = JsonDocument.Parse(new StreamReader(archive.GetEntry("data/configuration/account_two_step.json")!.Open()).ReadToEnd());
        var columns = json.RootElement.GetProperty("columns").EnumerateArray().Select(column => column.GetString()).ToList();
        var row = json.RootElement.GetProperty("rows")[0];
        return row[columns.IndexOf("secret_protected")].GetProperty("portable").GetString()!;
    }

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        // Best-effort cleanup of temporary files; a locked file must not fail the test run.
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Person(Guid Account, Guid Profile);

    /// <summary>A codec that holds the first import where it writes (it is called inside the data store write) until the test lets it go.</summary>
    private sealed class HoldingCodec(IBackupPortableValueCodec inner) : IBackupPortableValueCodec, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Proceed { get; } = new(false);

        public string? Export(string table, string column, string protectedValue, IReadOnlyDictionary<string, BackupValue> row) =>
            inner.Export(table, column, protectedValue, row);

        public string Import(string table, string column, string portableValue, IReadOnlyDictionary<string, BackupValue> row)
        {
            Entered.Set();
            Proceed.Wait(TimeSpan.FromSeconds(60));
            return inner.Import(table, column, portableValue, row);
        }

        public void Dispose()
        {
            Proceed.Set();
            Entered.Dispose();
            Proceed.Dispose();
        }
    }

    private sealed class Decisions(Guid? administrator, Guid? householdAdministrator = null) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateHouseholdAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(householdAdministrator is { } id && authority.AccountId == id && authority.IsEffectiveHouseholdAdministrator
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));

        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));

        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid libraryId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));

        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(administrator is { } id && authority.AccountId == id
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }

    /// <summary>One computer: its own data store, its own Data Protection key ring and its own backup folder.</summary>
    private sealed class Computer
    {
        private readonly UserTransferServiceTests _owner;
        private readonly EphemeralDataProtectionProvider _provider = new();
        private readonly string _backups;

        private Computer(UserTransferServiceTests owner, string name)
        {
            _owner = owner;
            Config = Path.Combine(owner._root, name + "-config");
            _backups = Path.Combine(owner._root, name + "-backups");
            Directory.CreateDirectory(Config);
            Database = BackupTestKit.OpenStore(Path.Combine(owner._root, name + ".db"));
            owner._stores.Add(Database);
            Protector = new DataProtectionTwoStepSecretProtector(_provider);
            Codec = new DataProtectionBackupPortableValueCodec(Protector, _provider);
        }

        public string Config { get; }

        public DatabaseConnection Database { get; }

        public DataProtectionTwoStepSecretProtector Protector { get; }

        public DataProtectionBackupPortableValueCodec Codec { get; }

        public string StagingFolder => Path.Combine(_backups, "user-imports");

        public static Computer New(UserTransferServiceTests owner, string name) => new(owner, name);

        public DatabaseBackupService BackupService() => BackupTestKit.Service(Database, Config, _backups, Codec);

        public UserTransferService Service(
            Guid? administrator = null,
            ManagedSessionServiceTests.RecordingAudit? audit = null,
            Guid? householdAdministrator = null,
            IBackupPortableValueCodec? importCodec = null) => new(
            new PersonalStateBackupRepository(Database, Codec),
            new UserImportRepository(Database),
            importCodec ?? Codec,
            Staging(),
            new AccountRepository(Database),
            new HouseholdRepository(Database),
            new Decisions(administrator, householdAdministrator),
            audit ?? new ManagedSessionServiceTests.RecordingAudit(),
            TimeProvider.System);

        private UserImportStagingArea? _staging;

        private UserImportStagingArea Staging() =>
            _staging ??= new UserImportStagingArea(_backups, TimeProvider.System, NullLogger<UserImportStagingArea>.Instance);

        private Guid? _home;

        /// <summary>A household on this computer to put imported profiles in (made on first use).</summary>
        public Guid Home()
        {
            if (_home is { } existing)
            {
                return existing;
            }

            _home = Guid.NewGuid();
            AddHousehold(_home.Value, "Home");
            return _home.Value;
        }

        public void AddHousehold(Guid id, string name)
        {
            using var connection = Database.CreateConnection();
            connection.Execute(
                "INSERT INTO households (id, name, created_at) VALUES (@id, @name, @now)",
                new { id = GuidSql.ToBlob(id), name, now = Stamp });
        }

        public void Execute(string sql)
        {
            using var connection = Database.CreateConnection();
            connection.Execute(sql);
        }

        /// <summary>
        /// A user with its sign-in and the profile it opens (the first name), which holds the features, libraries and roles. Any further
        /// names are other people of the same household: they exist, but the user does not open them and an export never holds them.
        /// </summary>
        public Person AddUser(string email, string[] profileNames, bool administrator = false, Guid? household = null, bool householdAdministrator = false)
        {
            var account = Guid.NewGuid();
            var accountBlob = GuidSql.ToBlob(account);
            using var connection = Database.CreateConnection();
            Guid first = Guid.Empty;
            for (var i = 0; i < profileNames.Length; i++)
            {
                var profile = Guid.NewGuid();
                first = i == 0 ? profile : first;
                connection.Execute(
                    "INSERT INTO profiles (id, display_name, role, created_at, household_id, is_server_admin, is_household_admin) VALUES (@id, @name, 'StandardUser', @now, @household, @admin, @householdAdmin)",
                    new
                    {
                        id = GuidSql.ToBlob(profile),
                        name = profileNames[i],
                        now = Stamp,
                        household = household is { } h ? GuidSql.ToBlob(h) : null,
                        admin = i == 0 && administrator ? 1 : 0,
                        householdAdmin = i == 0 && householdAdministrator ? 1 : 0,
                    });
            }

            connection.Execute(
                "INSERT INTO accounts (id, email, normalized_email, created_at, updated_at, profile_id) VALUES (@account, @email, @normalized, @now, @now, @profile)",
                new { account = accountBlob, email, normalized = email.ToUpperInvariant(), now = Stamp, profile = GuidSql.ToBlob(first) });
            connection.Execute(
                """
                INSERT INTO account_credentials (id, account_id, credential_kind, secret_hash, hash_scheme, hash_version, security_stamp, created_at, updated_at)
                VALUES (@id, @account, 'Password', @hash, 'pbkdf2', 1, 'stamp', @now, @now)
                """,
                new { id = GuidSql.ToBlob(Guid.NewGuid()), account = accountBlob, hash = "HASH-" + Guid.NewGuid().ToString("N"), now = Stamp });
            connection.Execute(
                "INSERT INTO account_two_step (account_id, secret_protected, created_at) VALUES (@account, @secret, @now)",
                new { account = accountBlob, secret = Protector.Protect(TwoStepSecret), now = Stamp });
            connection.Execute("INSERT INTO profile_feature_grants (profile_id, feature_id, granted_at) VALUES (@profile, 'read', @now)", new { profile = GuidSql.ToBlob(first), now = Stamp });
            connection.Execute(
                "INSERT INTO profile_library_grants (profile_id, library_id, granted_at) VALUES (@profile, @library, @now)",
                new { profile = GuidSql.ToBlob(first), library = GuidSql.ToBlob(Guid.NewGuid()), now = Stamp });
            return new Person(account, first);
        }

        public int Scalar(string sql)
        {
            using var connection = Database.CreateConnection();
            return connection.ExecuteScalar<int>(sql);
        }

        public string Text(string sql)
        {
            using var connection = Database.CreateConnection();
            return connection.ExecuteScalar<string>(sql) ?? string.Empty;
        }
    }
}
