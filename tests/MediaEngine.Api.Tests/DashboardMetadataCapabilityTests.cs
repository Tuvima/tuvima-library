using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Dashboard's "metadata.manage" signal is read-only information about an existing permission: it is emitted exactly when the
/// detail page's own "edit" action is allowed for the same signed-in person, and both come from one helper so they cannot drift.
/// Nothing here grants anything; every save is still checked by the Engine.
/// </summary>
public sealed class DashboardMetadataCapabilityTests : IDisposable
{
    private const string Capability = "metadata.manage";

    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
    private StubProfilePins _pins = new();

    public DashboardMetadataCapabilityTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_metadata_capability_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
    }

    public enum Person
    {
        ServerAdministrator,
        ServerAdministratorWithPinLocked,
        ServerAdministratorWithPinUnlocked,
        HouseholdAdministrator,
        OrdinaryMember,
        KidsProfileOfAServerAdministrator,
    }

    [Theory]
    [InlineData(Person.ServerAdministrator, true)]
    [InlineData(Person.ServerAdministratorWithPinLocked, false)]
    [InlineData(Person.ServerAdministratorWithPinUnlocked, true)]
    [InlineData(Person.HouseholdAdministrator, false)]
    [InlineData(Person.OrdinaryMember, false)]
    [InlineData(Person.KidsProfileOfAServerAdministrator, false)]
    public async Task Projector_EmitsMetadataManage_ExactlyWhenTheDetailEditActionIsAllowedForTheSameAuthority(Person person, bool expected)
    {
        var (accountId, profileId, sessionId) = await CreateAsync(person);

        var projected = await Projector().ProjectAsync(accountId, profileId, sessionId);
        var policy = await DetailActionAuthorizationPolicy.ResolveAsync(
            await AuthorityAsync(accountId, profileId, sessionId), Decisions(), Evaluator(), default);

        var emitted = projected.ActionCapabilities.Contains(Capability, StringComparer.Ordinal);
        Assert.Equal(expected, policy.CanManageMetadata);
        Assert.Equal(policy.CanManageMetadata, emitted);
        Assert.Equal(expected, emitted);
        // Edit is also shown (display only) to an administrator whose PIN is locked, so the Dashboard can ask for the PIN on tap; to nobody else.
        Assert.Equal(expected || person == Person.ServerAdministratorWithPinLocked, policy.Allows("edit"));
        Assert.Equal(person == Person.ServerAdministratorWithPinLocked, policy.OfferMetadataAfterUnlock);
        Assert.Equal(expected, policy.IsHumanAdministrator);
    }

    [Fact]
    public async Task Projector_EmitsNoMetadataManage_WhileTheProfileIsStillToBePicked()
    {
        var (accountId, profileId, sessionId) = await CreateAsync(Person.ServerAdministrator);

        var projected = await Projector().ProjectAsync(accountId, profileId, sessionId, profilePending: true);

        Assert.DoesNotContain(Capability, projected.ActionCapabilities);
    }

    [Fact]
    public void ProjectorAndDetailPolicy_AskTheSameHelperAndNeitherRepeatsTheAdministratorCheck()
    {
        var root = FindRepoRoot();
        var policy = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api", "Services", "Details", "Internals", "DetailActionAuthorizationPolicy.cs"));
        var projector = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api", "Security", "DashboardAuthorityProjector.cs"));

        Assert.Contains("DetailActionAuthorizationPolicy.MayManageMetadataAsHumanAsync(", projector);
        Assert.DoesNotContain("EvaluateAdministratorAsync", projector);
        // The helper is the only place in the policy that asks whether editing is allowed, and the policy's human and delegated paths both
        // go through it. The one other question (is this an administrator at all, PIN aside) only decides whether Edit is shown to a
        // locked administrator so the Dashboard can ask for the PIN; it allows nothing.
        Assert.Equal(1, Occurrences(policy, "EvaluateAdministratorAsync(authority, requireSurfaceUnlock: true, ct)"));
        Assert.Equal(1, Occurrences(policy, "EvaluateAdministratorAsync(authority, requireSurfaceUnlock: false, ct)"));
        Assert.Equal(2, Occurrences(policy, "EvaluateAdministratorAsync("));
        Assert.Equal(2, Occurrences(policy, "MayManageMetadataAsHumanAsync(authority, accounts, ct)"));
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private DashboardAuthorityProjector Projector() => new(
        _accounts,
        new ProfileRepository(_database),
        new ProfileAccessRepository(_database),
        Unlocks(),
        Decisions());

    private GrantAdminUnlockService Unlocks() => new(new ProfileAccessRepository(_database), _pins, _clock);

    private AccountAccessDecisionService Decisions() =>
        new(new ProfileAccessRepository(_database), Unlocks(), TestLibraryVisibility.Rule(new VaultLibraryRepository(_database)));

    private AuthorizationEvaluator Evaluator() =>
        new(Decisions(), new ApplicationRepository(_database), new PermissionRegistry(), new AuthorizationAuditWriter(_accounts), new HttpContextAccessor());

    // The authority the projector builds for a signed-in session, so the policy is asked about exactly the same person.
    private async Task<RequestAuthority> AuthorityAsync(Guid accountId, Guid profileId, Guid sessionId)
    {
        var account = await _accounts.GetByIdAsync(accountId);
        var activeProfile = await _accounts.GetActiveProfileAccessAsync(accountId, profileId);
        return new RequestAuthority(
            PrincipalKind.Human,
            true,
            accountId,
            profileId,
            SessionId: sessionId,
            AccountEnabled: true,
            AccountAuthorizationVersion: account!.AuthorizationVersion).WithActiveProfile(activeProfile!);
    }

    private async Task<(Guid AccountId, Guid ProfileId, Guid SessionId)> CreateAsync(Person person)
    {
        var administrator = person is Person.ServerAdministrator or Person.ServerAdministratorWithPinLocked
            or Person.ServerAdministratorWithPinUnlocked or Person.KidsProfileOfAServerAdministrator;
        var accountId = Guid.NewGuid();
        var profile = new Profile { Id = Guid.NewGuid(), DisplayName = "Person", Role = ProfileRole.StandardUser, CreatedAt = _clock.GetUtcNow() };
        await _accounts.CreateAccountAsync(
            new Account
            {
                Id = accountId,
                Email = $"{accountId:N}@example.com",
                NormalizedEmail = $"{accountId:N}@EXAMPLE.COM",
                IsEnabled = true,
                AuthorizationVersion = 1,
                CreatedAt = _clock.GetUtcNow(),
                UpdatedAt = _clock.GetUtcNow(),
            },
            new TestGrant(accountId, profile.Id, IsDefault: true, AdminEnabled: administrator),
            newProfile: profile);

        var profileId = profile.Id;
        if (person == Person.HouseholdAdministrator)
        {
            await _accounts.SetHouseholdAdminAsync(_database, accountId, true, _clock.GetUtcNow());
        }
        else if (person == Person.KidsProfileOfAServerAdministrator)
        {
            profileId = Guid.NewGuid();
            await ProfileTestData.InsertAsync(_database, new Profile
            {
                Id = profileId,
                DisplayName = "Kids",
                AvatarColor = "#123456",
                Role = ProfileRole.RestrictedProfile,
                CreatedAt = _clock.GetUtcNow(),
            }, await _accounts.GetHouseholdIdAsync(_database, accountId));
        }

        var sessionId = Guid.NewGuid();
        if (person is Person.ServerAdministratorWithPinLocked or Person.ServerAdministratorWithPinUnlocked)
        {
            await new ProfileAccessRepository(_database).SetAdminProtectionAsync(new ProfileAdminProtection
            {
                ProfileId = profileId,
                IsEnabled = true,
                UnlockMode = AdminUnlockMode.FixedDuration.ToString(),
                UnlockMinutes = null,
                ProtectionVersion = 1,
                UpdatedAt = _clock.GetUtcNow(),
            });
            await new IdentityRepository(_database).InsertSessionAsync(new AuthSession
            {
                Id = sessionId,
                AccountId = accountId,
                ActiveProfileId = profileId,
                TokenHash = $"hash-{sessionId:N}",
                DeviceId = "test-device",
                DeviceName = "Test device",
                Client = "Test",
                AuthenticationMethod = "Password",
                SecurityStamp = "stamp",
                CreatedAt = _clock.GetUtcNow(),
                LastSeenAt = _clock.GetUtcNow(),
                ExpiresAt = _clock.GetUtcNow().AddHours(1),
            });
            if (person == Person.ServerAdministratorWithPinUnlocked)
            {
                _pins = new StubProfilePins().With(profileId, "2468");
                await Unlocks().UnlockAsync(await AuthorityAsync(accountId, profileId, sessionId), "2468");
            }
        }

        return (accountId, profileId, sessionId);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // Temp file still held by a pooled connection; the OS temp cleaner removes it.
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
