using System.Security.Claims;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Shy's walkthrough (users, profiles and households spec section 2) through the real sign-in, switching and per-request authority:
/// a user signs into its own profile, administrator rights and Read/Watch/Listen/View and library access belong to the profile being
/// acted as, and switching stays inside the user's household.
/// </summary>
public sealed class ProfileHeldAccessWalkthroughTests : IDisposable
{
    private const string ShyPassword = "correct horse battery staple";
    private const string ShyPin = "2468";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_walkthrough_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly ProfileAccessRepository _access;
    private readonly FirstPartyIdentityService _service;
    private readonly RequestAuthorityResolver _resolver;
    private readonly AccountAccessDecisionService _decisions;
    private readonly Guid _libraryA = Guid.NewGuid();
    private readonly Guid _libraryB = Guid.NewGuid();

    public ProfileHeldAccessWalkthroughTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _access = new ProfileAccessRepository(_database);
        _service = new FirstPartyIdentityService(
            new IdentityRepository(_database), _accounts, new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            TimeProvider.System, new FixedPolicy());
        _resolver = new RequestAuthorityResolver(_accounts, new ApplicationRepository(_database));
        _decisions = new AccountAccessDecisionService(_access, new GrantAdminUnlockService(_access, new StubProfilePins(), TimeProvider.System),
            new LibraryVisibilityRule(_access, new VaultLibraryRepository(_database)));
    }

    [Fact]
    public async Task AUser_SignsIntoItsOwnProfile_IsAnAdministratorOnlyWhileOnAProfileThatHoldsTheRole_AndLosesItSwitchingBack()
    {
        var world = await WorldAsync();
        var mom = await SignInAsync(world.Mom);

        Assert.Equal(world.MomProfile.Id, mom.ActiveProfile.Id);
        Assert.False((await AuthorityAsync(mom.Session, world.Mom)).IsEffectiveAdministrator);

        var onShy = await _service.SwitchActiveProfileAsync(mom.PlaintextToken, world.Shy.Profile.Id, null);
        Assert.Equal(world.Shy.Profile.Id, onShy.ActiveProfile.Id);
        Assert.True((await AuthorityAsync(onShy.Session, world.Mom)).IsEffectiveAdministrator);

        var backOnMom = await _service.SwitchActiveProfileAsync(mom.PlaintextToken, world.MomProfile.Id, null);
        Assert.False((await AuthorityAsync(backOnMom.Session, world.Mom)).IsEffectiveAdministrator);
    }

    [Fact]
    public async Task APrivateAdministratorProfile_AsksForItsPinBeforeAnyoneSwitchesToIt()
    {
        var world = await WorldAsync();
        await _service.SetProfilePinAsync(world.Shy.Profile.Id, ShyPin);
        var mom = await SignInAsync(world.Mom);

        await Assert.ThrowsAsync<ProfilePinRequiredException>(() =>
            _service.SwitchActiveProfileAsync(mom.PlaintextToken, world.Shy.Profile.Id, null));
        Assert.False((await AuthorityAsync((await _service.ValidateSessionAsync(mom.PlaintextToken))!.Session, world.Mom)).IsEffectiveAdministrator);

        var onShy = await _service.SwitchActiveProfileAsync(mom.PlaintextToken, world.Shy.Profile.Id, ShyPin);
        Assert.True((await AuthorityAsync(onShy.Session, world.Mom)).IsEffectiveAdministrator);
    }

    [Fact]
    public async Task AKidSignsStraightIntoTheKidsProfile_AnotherKidOpensBySwitching_AndNeitherActsAsAnAdministrator()
    {
        var world = await WorldAsync();
        var kid1 = await SignInAsync(world.Kid1);
        Assert.Equal(world.Kid1Profile.Id, kid1.ActiveProfile.Id);
        Assert.False((await AuthorityAsync(kid1.Session, world.Kid1)).IsEffectiveAdministrator);

        var shy = await SignInAsync(world.Shy.Account);
        Assert.Equal(world.Shy.Profile.Id, shy.ActiveProfile.Id);
        Assert.True((await AuthorityAsync(shy.Session, world.Shy.Account)).IsEffectiveAdministrator);
        var onKid2 = await _service.SwitchActiveProfileAsync(shy.PlaintextToken, world.Kid2.Id, null);
        Assert.Equal(world.Kid2.Id, onKid2.ActiveProfile.Id);
        // A Kids profile never carries administrator rights, even for the administrator acting as it.
        Assert.False((await AuthorityAsync(onKid2.Session, world.Shy.Account)).IsEffectiveAdministrator);

        // A Kids profile switches into an adult profile that has no PIN freely.
        var kidOnAdult = await _service.SwitchActiveProfileAsync(kid1.PlaintextToken, world.MomProfile.Id, null);
        Assert.Equal(world.MomProfile.Id, kidOnAdult.ActiveProfile.Id);
    }

    [Fact]
    public async Task AProfileOfAnotherHousehold_IsRefusedExactlyLikeAnUnknownProfile()
    {
        var world = await WorldAsync();
        var mom = await SignInAsync(world.Mom);

        var other = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.SwitchActiveProfileAsync(mom.PlaintextToken, world.Outsider.Id, null));
        var unknown = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.SwitchActiveProfileAsync(mom.PlaintextToken, Guid.NewGuid(), null));

        Assert.Equal(unknown.Message, other.Message);
        Assert.Equal(world.MomProfile.Id, (await _service.ValidateSessionAsync(mom.PlaintextToken))!.Session.ActiveProfileId);
        Assert.False(await _accounts.HasProfileAccessAsync(world.Mom.Id, world.Outsider.Id));
        Assert.DoesNotContain(world.Outsider.Id, await _accounts.GetProfileIdsAsync(world.Mom.Id));
    }

    [Fact]
    public async Task FeatureAndLibraryAccess_FollowTheProfileBeingActedAs()
    {
        var world = await WorldAsync();
        var mom = await SignInAsync(world.Mom);
        var asMom = await AuthorityAsync(mom.Session, world.Mom);

        Assert.True((await _decisions.EvaluateFeatureAsync(asMom, AccountFeatureId.Watch)).IsAllowed);
        Assert.False((await _decisions.EvaluateFeatureAsync(asMom, AccountFeatureId.Listen)).IsAllowed);
        // Mom sees every library in her areas except the one hidden from her profile.
        Assert.True((await _decisions.EvaluateLibraryAsync(asMom, _libraryA)).IsAllowed);
        Assert.False((await _decisions.EvaluateLibraryAsync(asMom, _libraryB)).IsAllowed);

        // On the Kids profile only what is hidden from that profile counts, not Mom's own hidden library.
        var onKid2 = await _service.SwitchActiveProfileAsync(mom.PlaintextToken, world.Kid2.Id, null);
        var asKid2 = await AuthorityAsync(onKid2.Session, world.Mom);
        Assert.False((await _decisions.EvaluateFeatureAsync(asKid2, AccountFeatureId.Watch)).IsAllowed);
        Assert.False((await _decisions.EvaluateLibraryAsync(asKid2, _libraryA)).IsAllowed);
        Assert.True((await _decisions.EvaluateLibraryAsync(asKid2, _libraryB)).IsAllowed);

        // On the server administrator's profile the administrator bypass applies, as before.
        var onShy = await _service.SwitchActiveProfileAsync(mom.PlaintextToken, world.Shy.Profile.Id, null);
        var asShy = await AuthorityAsync(onShy.Session, world.Mom);
        Assert.True((await _decisions.EvaluateFeatureAsync(asShy, AccountFeatureId.Listen)).IsAllowed);
        Assert.True((await _decisions.EvaluateLibraryAsync(asShy, _libraryB)).IsAllowed);
    }

    [Fact]
    public async Task ChangingAProfilesAccess_ChangesItsAuthorizationVersion_SoSessionsAndPairedAppsCheckAgain()
    {
        var world = await WorldAsync();
        var mom = await SignInAsync(world.Mom);
        var before = await AuthorityAsync(mom.Session, world.Mom);

        await _access.ReplaceAccessAsync(world.MomProfile.Id, new HashSet<AccountFeatureId> { AccountFeatureId.Read },
            new HashSet<Guid>(), DateTimeOffset.UtcNow);
        var after = await AuthorityAsync(mom.Session, world.Mom);

        Assert.True(after.ProfileAuthorizationVersion > before.ProfileAuthorizationVersion);
        Assert.False((await _decisions.EvaluateFeatureAsync(after, AccountFeatureId.Watch)).IsAllowed);
        Assert.Equal(before.AccountAuthorizationVersion, after.AccountAuthorizationVersion);
    }

    [Fact]
    public async Task APasswordlessFirstStart_IsAServerAdministratorOnItsOwnProfile()
    {
        var owner = await _service.BootstrapThisComputerAdministratorAsync(null, "Owner", "setup-device", "Setup", "Dashboard");

        var authority = await AuthorityAsync(owner.Session, owner.Account);

        Assert.True(authority.IsEffectiveAdministrator);
        Assert.Equal(owner.Profile.Id, authority.ActiveProfileId);
        Assert.True((await _access.GetAdminFlagsAsync(owner.Profile.Id))!.IsServerAdministrator);
    }

    [Fact]
    public async Task TheOnlyServerAdministratorProfileWithAUser_CannotLoseTheRole()
    {
        var world = await WorldAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _access.SetServerAdministratorAsync(world.Shy.Profile.Id, false, DateTimeOffset.UtcNow));

        Assert.True((await _access.GetAdminFlagsAsync(world.Shy.Profile.Id))!.IsServerAdministrator);
    }

    private async Task<World> WorldAsync()
    {
        var shy = await _service.BootstrapAdministratorAsync("shy@example.com", ShyPassword, "Shy", "setup-device", "Setup", "Dashboard");
        var household = (await _accounts.GetHouseholdIdAsync(_database, shy.Account.Id))!.Value;
        var now = DateTimeOffset.UtcNow;

        var momProfile = new Profile { Id = Guid.NewGuid(), DisplayName = "Mom", AvatarColor = "#7C4DFF", Role = ProfileRole.StandardUser, CreatedAt = now };
        var mom = await AddUserAsync("mom@example.com", momProfile, household, new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch }, new HashSet<Guid> { _libraryB });
        var kid1Profile = new Profile { Id = Guid.NewGuid(), DisplayName = "Kid 1", AvatarColor = "#7C4DFF", Role = ProfileRole.RestrictedProfile, CreatedAt = now };
        var kid1 = await AddUserAsync("kid1@example.com", kid1Profile, household, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid>());

        var kid2 = new Profile { Id = Guid.NewGuid(), DisplayName = "Kid 2", AvatarColor = "#7C4DFF", Role = ProfileRole.RestrictedProfile, CreatedAt = now };
        await _accounts.CreateHouseholdPersonAsync(kid2, household);

        await _access.ReplaceAccessAsync(kid2.Id, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid> { _libraryA }, now);

        var outsiderProfile = new Profile { Id = Guid.NewGuid(), DisplayName = "Outsider", AvatarColor = "#112233", Role = ProfileRole.StandardUser, CreatedAt = now };
        await AddUserAsync("outsider@example.com", outsiderProfile, null, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid>());

        return new World(shy, mom, momProfile, kid1, kid1Profile, kid2, outsiderProfile);
    }

    private async Task<Account> AddUserAsync(string email, Profile profile, Guid? household, IReadOnlySet<AccountFeatureId> features, IReadOnlySet<Guid> hiddenLibraries)
    {
        var now = DateTimeOffset.UtcNow;
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _accounts.CreateAccountAsync(
            account,
            new TestGrant(account.Id, profile.Id, IsDefault: true, AdminEnabled: false),
            features, hiddenLibraries, newProfile: profile, householdId: household);
        return (await _accounts.GetByIdAsync(account.Id))!;
    }

    private Task<SessionIssueResult> SignInAsync(Account account) =>
        _service.CreateExternalSessionAsync(account.Id, "google", "laptop", "Laptop", "Dashboard");

    private async Task<RequestAuthority> AuthorityAsync(AuthSession session, Account account)
    {
        var claims = new List<Claim>
        {
            new(TuvimaClaimTypes.PrincipalKind, PrincipalKind.Human.ToString()),
            new(TuvimaClaimTypes.AccountId, account.Id.ToString("D")),
            new(TuvimaClaimTypes.ActiveProfileId, session.ActiveProfileId.ToString("D")),
            new(TuvimaClaimTypes.SessionId, session.Id.ToString("D")),
        };
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        return await _resolver.ResolveAsync(http);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed record World(SessionIssueResult Shy, Account Mom, Profile MomProfile, Account Kid1, Profile Kid1Profile, Profile Kid2, Profile Outsider);

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }
}
