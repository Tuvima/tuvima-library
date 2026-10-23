using MediaEngine.Api.Security;
using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Deleting a user (their sign-in and their own profiles): who may do it, the last-owner rule, what happens to their photos, and
/// what is recorded. Real temporary data store; the photo handling is a hand-written spy.
/// </summary>
public sealed class UserDeletionTests
{
    [Fact]
    public async Task DeletingAUser_DeletesTheirOwnProfiles_AndKeepsHouseholdPeopleWithoutASignIn()
    {
        await using var world = await World.CreateAsync();

        await world.Mutations.DeleteAsync(world.ServerAdmin, world.Sam.Id);

        Assert.Null(await world.Accounts.GetByIdAsync(world.Sam.Id));
        Assert.Null(await world.Profiles.GetByIdAsync(world.SamProfile.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(world.Mary.Id));
        Assert.NotNull(await world.Accounts.GetGrantAsync(world.Alex.Id, world.Mary.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(await DefaultProfileAsync(world, world.Alex)));
    }

    [Fact]
    public async Task DeletingAUsersOwnSignIn_ThroughDeleteUser_DeletesThePerson_WhileRemovingTheSignInKeepsThem()
    {
        await using var world = await World.CreateAsync();
        var carl = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(world.Household.Id, "Carl", null, false, null));
        var carlSignIn = (await world.Mutations.GiveOwnSignInAsync(world.ServerAdmin,
            new GiveOwnSignInCommand(world.Mary.Id, "mary@example.com", "a long temporary password"))).Account;
        var dee = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(world.Household.Id, "Dee", null, false, null));
        var deeSignIn = (await world.Mutations.GiveOwnSignInAsync(world.ServerAdmin,
            new GiveOwnSignInCommand(dee.Id, "dee@example.com", "a long temporary password"))).Account;

        await world.Mutations.RemoveOwnSignInAsync(world.ServerAdmin, deeSignIn.Id);
        Assert.NotNull(await world.Profiles.GetByIdAsync(dee.Id));

        await world.Mutations.DeleteAsync(world.ServerAdmin, carlSignIn.Id);
        Assert.Null(await world.Profiles.GetByIdAsync(world.Mary.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(carl.Id));
    }

    [Fact]
    public async Task Photos_MoveToTheSharedLibraryByDefault_AndAreDeletedWhenAsked()
    {
        await using var world = await World.CreateAsync();
        await world.Mutations.DeleteAsync(world.ServerAdmin, world.Sam.Id);
        Assert.Equal([(world.SamProfile.Id, ProfilePhotoDisposition.MoveToShared)], world.Photos.Prepared);

        world.Photos.Prepared.Clear();
        await world.Mutations.DeleteAsync(world.ServerAdmin, world.Kim.Id, ProfilePhotoDisposition.Delete);
        var prepared = Assert.Single(world.Photos.Prepared);
        Assert.Equal((world.KimProfile.Id, ProfilePhotoDisposition.Delete), prepared);
    }

    [Fact]
    public async Task DeletingAUser_RecordsHowManyProfilesWentButNeverTheirNames()
    {
        await using var world = await World.CreateAsync();

        await world.Mutations.DeleteAsync(world.ServerAdmin, world.Sam.Id);

        var deleted = Assert.Single(world.Audit.Events, e => e.EventType == "account.deleted");
        Assert.Equal("1", deleted.Changes["profiles_deleted"]);
        Assert.DoesNotContain(deleted.Changes.Values, value => value is not null && value.Contains("Sam", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(world.Sam.Id.ToString("D"), deleted.SubjectId);
    }

    [Fact]
    public async Task ThePreviewNamesTheProfilesThatWouldGo_ChangesNothing_AndHidesOtherHouseholds()
    {
        await using var world = await World.CreateAsync();

        var preview = await world.Mutations.PreviewDeleteAsync(world.AlexAuthority, world.Sam.Id);

        Assert.Equal(["Sam"], preview.Select(profile => profile.DisplayName).ToArray());
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Sam.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(world.SamProfile.Id));
        Assert.Empty(world.Photos.Prepared);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.PreviewDeleteAsync(world.AlexAuthority, world.Bea.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.PreviewDeleteAsync(world.KimAuthority, world.Sam.Id));
    }

    // ---- What is deleted is what was confirmed ----

    [Fact]
    public async Task DeletingAUser_WithAConfirmedListThatNoLongerMatches_IsRefusedBeforeAnythingMoves_AndChangesNothing()
    {
        await using var world = await World.CreateAsync();
        var confirmed = (await world.Mutations.PreviewDeleteAsync(world.ServerAdmin, world.Sam.Id)).Select(profile => profile.Id).ToArray();
        // Someone gives Sam another profile of their own after the list was shown.
        var kids = await world.Mutations.CreateProfileAsync(world.ServerAdmin,
            new CreateManagedProfileCommand(world.Sam.Id, "Sam (Kids)", null, false));

        var refused = await Assert.ThrowsAsync<AccountRemovalChangedException>(
            () => world.Mutations.DeleteAsync(world.ServerAdmin, world.Sam.Id, ProfilePhotoDisposition.MoveToShared, confirmed));
        var empty = await Assert.ThrowsAsync<AccountRemovalChangedException>(
            () => world.Mutations.DeleteAsync(world.ServerAdmin, world.Sam.Id, ProfilePhotoDisposition.MoveToShared, []));

        Assert.Equal("Something changed since you looked. Check the list and try again.", refused.Message);
        Assert.Equal(refused.Message, empty.Message);
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Sam.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(world.SamProfile.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(kids.Id));
        Assert.Empty(world.Photos.Prepared);
        Assert.DoesNotContain(world.Audit.Events, e => e.EventType == "account.deleted");

        // The list as shown now, in any order, goes through; a caller that sends no list is not compared.
        var current = (await world.Mutations.PreviewDeleteAsync(world.ServerAdmin, world.Sam.Id)).Select(profile => profile.Id).Reverse().ToArray();
        await world.Mutations.DeleteAsync(world.ServerAdmin, world.Sam.Id, ProfilePhotoDisposition.MoveToShared, current);
        Assert.Null(await world.Accounts.GetByIdAsync(world.Sam.Id));
        Assert.Null(await world.Profiles.GetByIdAsync(kids.Id));
        await world.Mutations.DeleteAsync(world.ServerAdmin, world.Kim.Id);
        Assert.Null(await world.Accounts.GetByIdAsync(world.Kim.Id));
    }

    [Fact]
    public async Task ChangesToWhoCanOpenWhat_WaitForTheSameGateAsDeletingAUser()
    {
        var gate = new AuthenticationPolicyMutationGate();
        await using var world = await World.CreateAsync(gate: gate);
        using var held = await gate.EnterAsync(CancellationToken.None);

        var profile = world.Mutations.CreateProfileAsync(world.ServerAdmin,
            new CreateManagedProfileCommand(world.Sam.Id, "Sam (Kids)", null, false));
        var person = world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(world.Household.Id, "Zed", null, false, null));
        var removal = world.Mutations.RemoveOwnSignInAsync(world.ServerAdmin, Guid.NewGuid());
        await Task.Delay(150);

        // All of them are queued behind the held gate, so a delete's plan and the delete itself see the same grants.
        Assert.False(profile.IsCompleted);
        Assert.False(person.IsCompleted);
        Assert.False(removal.IsCompleted);

        held.Dispose();
        await profile;
        await person;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => removal);
    }

    // ---- Photos move only once nothing can refuse ----

    [Fact]
    public async Task WhenAnotherSignInWouldBeLeftWithNothingToOpen_NoPhotosAreMoved_AndNothingIsDeleted()
    {
        await using var world = await World.CreateAsync();
        // Pia can open only Sam's profile (a user has a profile of its own, which Pia gave up in favour of Sam's).
        var piaProfile = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(world.Sam.HouseholdId!.Value, "Pia", null, false, null));
        var pia = await world.Mutations.CreateAsync(world.ServerAdmin, new CreateAccountAccessCommand(
            "pia@example.com", IsAdministrator: false, ProfileId: piaProfile.Id, NewProfile: null,
            Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read }, Libraries: new HashSet<Guid>(), TemporaryPassword: null));
        await world.Mutations.UpsertGrantAsync(world.ServerAdmin, new AccountProfileGrant
        {
            AccountId = pia.Id,
            ProfileId = world.SamProfile.Id,
            IsDefault = true,
            IsEnabled = true,
            AuthorizationVersion = 1,
        });
        await world.Mutations.RevokeGrantAsync(world.ServerAdmin, pia.Id, piaProfile.Id);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.DeleteAsync(world.ServerAdmin, world.Sam.Id));
        var profileRefused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Mutations.DeleteProfileAsync(world.ServerAdmin, world.SamProfile.Id));

        Assert.Contains("pia@example.com can only open this profile", refused.Message, StringComparison.Ordinal);
        Assert.NotNull(profileRefused);
        Assert.Empty(world.Photos.Prepared);
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Sam.Id));
        Assert.NotNull(await world.Accounts.GetByIdAsync(pia.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(world.SamProfile.Id));
    }

    [Fact]
    public async Task WhenTheDeleteFailsAfterPhotosMoved_AnErrorNamesTheProfileId_AndNeverAName()
    {
        await using var world = await World.CreateAsync();
        world.Photos.ReportsPhotosMoved = true;
        // The usable-administrator check is a spy here, so only the data store's own backstop stops this delete, after photos moved.
        var serverProfile = (await world.Accounts.GetDefaultProfileIdAsync(world.ServerAdminAccount.Id))!.Value;

        await world.Mutations.SetHouseholdAdminAsync(world.ServerAdmin, world.ServerAdminAccount.Id, false);

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.DeleteAsync(world.ServerAdmin, world.ServerAdminAccount.Id));
        Assert.Contains("final effective administrator", failed.Message, StringComparison.Ordinal);

        Assert.Equal([serverProfile], world.Photos.Prepared.Select(prepared => prepared.ProfileId).ToArray());
        var message = Assert.Single(world.Log.Messages);
        Assert.Equal([LogLevel.Error], world.Log.Levels);
        Assert.Contains(serverProfile.ToString("D"), message, StringComparison.Ordinal);
        Assert.Contains("Shared", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Server", message, StringComparison.Ordinal);
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.ServerAdminAccount.Id));
    }

    // ---- The last owner ----

    [Fact]
    public async Task TheLastOwnerOfAHousehold_CannotBeDeleted_EvenByAServerAdministrator()
    {
        await using var world = await World.CreateAsync(secondOwner: false);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.DeleteAsync(world.ServerAdmin, world.Alex.Id));

        Assert.Equal("Alex is the last owner of this household. Make someone else an owner first.", refused.Message);
        Assert.NotNull(await world.Profiles.GetByIdAsync(await DefaultProfileAsync(world, world.Alex)));
    }

    [Fact]
    public async Task WithTwoOwners_EachCanDeleteTheOther_ButNeverTheOneLeft()
    {
        await using (var first = await World.CreateAsync())
        {
            await first.Mutations.DeleteAsync(first.AlexAuthority, first.Alex2.Id);
            Assert.Null(await first.Accounts.GetByIdAsync(first.Alex2.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => first.Mutations.DeleteAsync(first.ServerAdmin, first.Alex.Id));
        }

        await using var second = await World.CreateAsync();
        await second.Mutations.DeleteAsync(second.Alex2Authority, second.Alex.Id);
        Assert.Null(await second.Accounts.GetByIdAsync(second.Alex.Id));
        Assert.NotNull(await second.Accounts.GetByIdAsync(second.Alex2.Id));
        var last = await Assert.ThrowsAsync<InvalidOperationException>(() => second.Mutations.DeleteAsync(second.ServerAdmin, second.Alex2.Id));
        Assert.Contains("last owner", last.Message, StringComparison.Ordinal);
    }

    // ---- Who may delete whom ----

    [Fact]
    public async Task AHouseholdOwner_CanDeleteAMemberOfTheirHousehold_ButNotThemselvesOrSomeoneElsesHousehold()
    {
        await using var world = await World.CreateAsync();

        // The server administrator lives in another household, so to an owner they look like any other missing sign-in.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.DeleteAsync(world.AlexAuthority, world.ServerAdminAccount.Id));
        var outside = await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.DeleteAsync(world.AlexAuthority, world.Bea.Id));
        var missing = await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.DeleteAsync(world.AlexAuthority, Guid.NewGuid()));
        Assert.Equal(missing.Message, outside.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.DeleteAsync(world.AlexAuthority, world.Alex.Id));
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Bea.Id));
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.ServerAdminAccount.Id));

        await world.Mutations.DeleteAsync(world.AlexAuthority, world.Kim.Id);
        Assert.Null(await world.Accounts.GetByIdAsync(world.Kim.Id));
        Assert.Null(await world.Profiles.GetByIdAsync(world.KimProfile.Id));
    }

    [Fact]
    public async Task AHouseholdOwner_CannotDeleteAServerAdministratorEvenInTheirOwnHousehold()
    {
        await using var world = await World.CreateAsync();
        await world.Mutations.UpdateAsync(world.ServerAdmin, world.Kim.Id, new UpdateAccountAccessCommand(world.Kim.Email!, true, true));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.DeleteAsync(world.AlexAuthority, world.Kim.Id));

        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Kim.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(world.KimProfile.Id));
    }

    [Fact]
    public async Task APlainMember_CannotDeleteAnyone_AndAnotherHouseholdsOwnerCannotReachThisHousehold()
    {
        await using var world = await World.CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.DeleteAsync(world.KimAuthority, world.Sam.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.DeleteAsync(world.KimAuthority, world.Kim.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.DeleteAsync(world.BeaAuthority, world.Kim.Id));
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Kim.Id));
        Assert.NotNull(await world.Accounts.GetByIdAsync(world.Sam.Id));
    }

    [Fact]
    public async Task DeletingAUser_NeverTakesTheProfileAnotherOwnerSignsInAs_EvenWhenThatOwnerCanOpenTheDeletedOne()
    {
        await using var world = await World.CreateAsync();
        // Alex2 (another owner) also signs in as Kim's profile; Alex deleting Kim must not take Alex2's person away.
        await world.Accounts.UpsertGrantAsync(new AccountProfileGrant
        {
            AccountId = world.Alex2.Id,
            ProfileId = world.KimProfile.Id,
            IsDefault = false,
            IsEnabled = true,
            AdminEnabled = false,
            AuthorizationVersion = 1,
            GrantedAt = DateTimeOffset.UtcNow,
        });

        // Alex2 only holds a plain grant, so Kim goes; the profile Alex2 signs in as is never Kim's to take.
        await world.Mutations.DeleteAsync(world.AlexAuthority, world.Kim.Id);

        Assert.Null(await world.Accounts.GetByIdAsync(world.Kim.Id));
        Assert.NotNull(await world.Profiles.GetByIdAsync(await DefaultProfileAsync(world, world.Alex2)));
    }

    // ---- Profile pictures ----

    [Fact]
    public void ADeletedProfilesPicture_IsRemovedFromTheLibrarysOwnAvatarFolder_AndItsEmptyFolderWithIt()
    {
        using var temp = new TempFolder();
        var avatars = Path.Combine(temp.Path, "profiles");
        var profileId = Guid.NewGuid();
        var folder = Path.Combine(avatars, profileId.ToString("D"));
        Directory.CreateDirectory(folder);
        var picture = Path.Combine(folder, "avatar-1.png");
        File.WriteAllBytes(picture, [1, 2, 3]);

        ProfilePhotoDisposer.DeleteAvatar(picture, avatars, profileId, new CapturingLogger());

        Assert.False(File.Exists(picture));
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(avatars));
    }

    [Fact]
    public void ADeletedProfilesPicture_OutsideTheAvatarFolder_IsNeverTouched()
    {
        using var temp = new TempFolder();
        var avatars = Path.Combine(temp.Path, "profiles");
        Directory.CreateDirectory(avatars);
        var outside = Path.Combine(temp.Path, "family-photo.png");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var sibling = Path.Combine(temp.Path, "profiles-backup", "avatar.png");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllBytes(sibling, [1]);
        var traversal = Path.Combine(avatars, "..", "family-photo.png");
        var profileId = Guid.NewGuid();

        ProfilePhotoDisposer.DeleteAvatar(outside, avatars, profileId, new CapturingLogger());
        ProfilePhotoDisposer.DeleteAvatar(sibling, avatars, profileId, new CapturingLogger());
        ProfilePhotoDisposer.DeleteAvatar(traversal, avatars, profileId, new CapturingLogger());
        ProfilePhotoDisposer.DeleteAvatar(null, avatars, profileId, new CapturingLogger());

        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public void ADeletedProfilesPicture_InAnotherProfilesFolder_IsNeverDeleted_EvenInsideTheAvatarFolder()
    {
        using var temp = new TempFolder();
        var avatars = Path.Combine(temp.Path, "profiles");
        var other = Path.Combine(avatars, Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(other);
        var othersPicture = Path.Combine(other, "avatar.png");
        File.WriteAllBytes(othersPicture, [1]);
        var atTheTop = Path.Combine(avatars, "avatar.png");
        File.WriteAllBytes(atTheTop, [1]);
        var profileId = Guid.NewGuid();
        // The same letters in another case are a different folder on a case-sensitive disk and are refused everywhere.
        var upper = Path.Combine(avatars, profileId.ToString("D").ToUpperInvariant());
        var casePicture = Path.Combine(upper, "avatar.png");
        Directory.CreateDirectory(upper);
        File.WriteAllBytes(casePicture, [1]);

        ProfilePhotoDisposer.DeleteAvatar(othersPicture, avatars, profileId, new CapturingLogger());
        ProfilePhotoDisposer.DeleteAvatar(atTheTop, avatars, profileId, new CapturingLogger());
        ProfilePhotoDisposer.DeleteAvatar(casePicture, avatars, profileId, new CapturingLogger());

        Assert.True(File.Exists(othersPicture));
        Assert.True(File.Exists(atTheTop));
        Assert.True(File.Exists(casePicture));
    }

    [Fact]
    public void ADeletedProfilesPicture_ReachedThroughASymbolicLink_IsNeverDeleted_AndIsLoggedAsAWarning()
    {
        using var temp = new TempFolder();
        var avatars = Path.Combine(temp.Path, "profiles");
        var profileId = Guid.NewGuid();
        var folder = Path.Combine(avatars, profileId.ToString("D"));
        Directory.CreateDirectory(folder);
        var precious = Path.Combine(temp.Path, "family-photo.png");
        File.WriteAllBytes(precious, [1, 2, 3]);
        var linkedFile = Path.Combine(folder, "avatar.png");
        File.CreateSymbolicLink(linkedFile, precious);
        var logger = new CapturingLogger();

        ProfilePhotoDisposer.DeleteAvatar(linkedFile, avatars, profileId, logger);

        Assert.True(File.Exists(precious));
        Assert.True(File.Exists(linkedFile));
        Assert.Equal([LogLevel.Warning], logger.Levels);

        // The profile's own folder being a link to somewhere else is refused as well.
        var elsewhere = Path.Combine(temp.Path, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var target = Path.Combine(elsewhere, "avatar.png");
        File.WriteAllBytes(target, [1]);
        var linkedProfile = Guid.NewGuid();
        Directory.CreateSymbolicLink(Path.Combine(avatars, linkedProfile.ToString("D")), elsewhere);

        ProfilePhotoDisposer.DeleteAvatar(Path.Combine(avatars, linkedProfile.ToString("D"), "avatar.png"), avatars, linkedProfile, new CapturingLogger());

        Assert.True(File.Exists(target));
    }

    [Fact]
    public void ADeletedProfilesPicture_ThatCannotBeDeleted_IsLoggedAsAWarning_NotAnError()
    {
        using var temp = new TempFolder();
        var avatars = Path.Combine(temp.Path, "profiles");
        // A directory where the picture should be: File.Delete refuses it.
        var profileId = Guid.NewGuid();
        var stubborn = Path.Combine(avatars, profileId.ToString("D"), "avatar.png");
        Directory.CreateDirectory(stubborn);
        var logger = new CapturingLogger();

        ProfilePhotoDisposer.DeleteAvatar(stubborn, avatars, profileId, logger);

        Assert.Equal([LogLevel.Warning], logger.Levels);
    }

    private static async Task<Guid> DefaultProfileAsync(World world, Account account) =>
        (await world.Accounts.GetDefaultProfileIdAsync(account.Id))!.Value;

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tuvima-avatars-{Guid.NewGuid():N}");

        public TempFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private class CapturingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class CapturingLogger<T> : CapturingLogger, ILogger<T>;

    private sealed class World : IAsyncDisposable
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-user-deletion-{Guid.NewGuid():N}.db");
        private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima-user-deletion-{Guid.NewGuid():N}");
        private DatabaseConnection _database = null!;
        private ConfigurationDirectoryLoader _configuration = null!;

        public AccountRepository Accounts { get; private set; } = null!;
        public ProfileRepository Profiles { get; private set; } = null!;
        public AccountAccessMutationService Mutations { get; private set; } = null!;
        public NoProfilePhotos Photos { get; } = new();
        public CapturingAudit Audit { get; } = new();
        public CapturingLogger<AccountAccessMutationService> Log { get; } = new();
        public Household Household { get; private set; } = null!;

        public RequestAuthority ServerAdmin { get; private set; } = null!;
        public Account ServerAdminAccount { get; private set; } = null!;

        /// <summary>The household's first main sign-in and owner.</summary>
        public Account Alex { get; private set; } = null!;
        public RequestAuthority AlexAuthority { get; private set; } = null!;

        /// <summary>Another main sign-in of the household ("Sam"), an owner when the world is made with a second owner.</summary>
        public Account Sam => Alex2;
        public Account Alex2 { get; private set; } = null!;
        public RequestAuthority Alex2Authority { get; private set; } = null!;
        public Profile SamProfile { get; private set; } = null!;

        /// <summary>A household person with no sign-in of their own, held by both main sign-ins.</summary>
        public Profile Mary { get; private set; } = null!;

        /// <summary>A plain member (not an owner) with a profile of their own.</summary>
        public Account Kim { get; private set; } = null!;
        public Profile KimProfile { get; private set; } = null!;
        public RequestAuthority KimAuthority { get; private set; } = null!;

        /// <summary>The owner of another household.</summary>
        public Account Bea { get; private set; } = null!;
        public RequestAuthority BeaAuthority { get; private set; } = null!;

        public static async Task<World> CreateAsync(bool secondOwner = true, AuthenticationPolicyMutationGate? gate = null)
        {
            var world = new World();
            world._database = new DatabaseConnection(world._databasePath);
            world._configuration = new ConfigurationDirectoryLoader(world._configPath);
            world._database.InitializeSchema();
            world.Accounts = new AccountRepository(world._database);
            var identities = new IdentityRepository(world._database);
            world.Profiles = new ProfileRepository(world._database);
            var identity = new FirstPartyIdentityService(
                identities, world.Accounts, world.Profiles,
                new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
                TimeProvider.System, new ConfigurationAuthenticationPolicyProvider(world._configuration));
            world.Mutations = new AccountAccessMutationService(
                world.Accounts, identities, world.Profiles, world._configuration,
                new RealDecisions(), new AllowEvaluator(), new NoOpInvalidation(), world.Audit,
                identity, world.Photos, TimeProvider.System, new AlwaysUsableAdministrators(), gate ?? new AuthenticationPolicyMutationGate(), world.Log);
            var households = new HouseholdRepository(world._database);

            var bootstrap = ServerAuthority(Guid.NewGuid(), Guid.NewGuid());
            world.ServerAdminAccount = await world.Mutations.CreateAsync(bootstrap, new CreateAccountAccessCommand(
                "server@example.com", IsAdministrator: true, ProfileId: null,
                NewProfile: new NewAccountProfileCommand("Server", "#7C4DFF"),
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read }, Libraries: new HashSet<Guid>(), TemporaryPassword: null));
            world.ServerAdmin = ServerAuthority(world.ServerAdminAccount.Id, (await world.Accounts.GetDefaultProfileIdAsync(world.ServerAdminAccount.Id))!.Value);

            world.Alex = await world.Mutations.CreateAsync(world.ServerAdmin, new CreateAccountAccessCommand(
                "alex@example.com", IsAdministrator: false, ProfileId: null,
                NewProfile: new NewAccountProfileCommand("Alex", "#112233"),
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read }, Libraries: new HashSet<Guid>(), TemporaryPassword: null));
            world.Household = (await households.GetForAccountAsync(world.Alex.Id))!;
            world.Alex = (await world.Accounts.GetByIdAsync(world.Alex.Id))!;

            // Sam is a household person who is then invited to sign in as themselves; Alex keeps a plain grant on Sam.
            world.SamProfile = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
                new AddHouseholdPersonCommand(world.Household.Id, "Sam", null, false, null));
            var invitedSam = await world.Mutations.IssueInvitationAsync(world.ServerAdmin,
                new IssueAccountInvitationCommand("sam@example.com", [world.SamProfile.Id], null));
            if (secondOwner)
            {
                await world.Mutations.SetHouseholdAdminAsync(world.ServerAdmin, invitedSam.AccountId, true);
            }

            world.Alex2 = (await world.Accounts.GetByIdAsync(invitedSam.AccountId))!;

            // Mary and Kim are added once both main sign-ins exist, so both hold Mary.
            world.Mary = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
                new AddHouseholdPersonCommand(world.Household.Id, "Mary", null, false, null));
            world.KimProfile = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
                new AddHouseholdPersonCommand(world.Household.Id, "Kim", null, false, null));
            var invitedKim = await world.Mutations.IssueInvitationAsync(world.ServerAdmin,
                new IssueAccountInvitationCommand("kim@example.com", [world.KimProfile.Id], null));
            world.Kim = (await world.Accounts.GetByIdAsync(invitedKim.AccountId))!;

            world.Bea = await world.Mutations.CreateAsync(world.ServerAdmin, new CreateAccountAccessCommand(
                "bea@example.com", IsAdministrator: false, ProfileId: null,
                NewProfile: new NewAccountProfileCommand("Bea", "#445566"),
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read }, Libraries: new HashSet<Guid>(), TemporaryPassword: null));
            world.Bea = (await world.Accounts.GetByIdAsync(world.Bea.Id))!;

            world.AlexAuthority = await world.OwnerAuthorityAsync(world.Alex, isOwner: true);
            world.Alex2Authority = await world.OwnerAuthorityAsync(world.Alex2, isOwner: secondOwner);
            world.KimAuthority = await world.OwnerAuthorityAsync(world.Kim, isOwner: false);
            world.BeaAuthority = await world.OwnerAuthorityAsync(world.Bea, isOwner: true);
            return world;
        }

        private async Task<RequestAuthority> OwnerAuthorityAsync(Account account, bool isOwner) =>
            new(PrincipalKind.Human, true,
                AccountId: account.Id,
                ActiveProfileId: (await Accounts.GetDefaultProfileIdAsync(account.Id))!.Value,
                SessionId: Guid.NewGuid(),
                AccountEnabled: true,
                GrantEnabled: true,
                AccountHouseholdId: account.HouseholdId,
                AccountIsHouseholdAdmin: isOwner);

        private static RequestAuthority ServerAuthority(Guid accountId, Guid profileId) =>
            new(PrincipalKind.Human, true,
                AccountId: accountId,
                ActiveProfileId: profileId,
                SessionId: Guid.NewGuid(),
                AccountEnabled: true,
                GrantEnabled: true,
                AccountIsAdministrator: true,
                GrantAdminEnabled: true);

        public ValueTask DisposeAsync()
        {
            _configuration.Dispose();
            _database.Dispose();
            TestTemp.ReleaseSqlitePool(_databasePath);
            foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            if (Directory.Exists(_configPath))
            {
                Directory.Delete(_configPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingAudit : IAuthorizationAuditWriter
    {
        public List<AuthorizationAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(AuthorizationAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RealDecisions : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateHouseholdAdministratorAsync(RequestAuthority authority,
            bool requireSurfaceUnlock, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(authority.IsEffectiveHouseholdAdministrator
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));

        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid libraryId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(authority.IsEffectiveAdministrator
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }

    private sealed class AllowEvaluator : IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class NoOpInvalidation : IAuthorizationInvalidationService
    {
        public ValueTask InvalidateAccountAsync(Guid accountId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateGrantAsync(Guid accountId, Guid profileId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateApplicationAsync(Guid applicationId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask InvalidateSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
