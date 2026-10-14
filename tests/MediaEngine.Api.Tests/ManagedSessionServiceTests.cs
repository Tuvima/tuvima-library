using MediaEngine.Api.Security;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Devices page's signed-in sessions: who is listed for a server administrator, a household administrator, and
/// nobody else, and which sessions each of them may sign out.
/// </summary>
public sealed class ManagedSessionServiceTests
{
    [Fact]
    public async Task ServerAdministrator_SeesEveryHouseholdsSessions_WithLabelsAndPeople()
    {
        await using var world = await World.CreateAsync();

        var listed = await world.Service.ListAsync(world.ServerAdmin);

        Assert.Equal(
            new[] { world.Owner.Id, world.Mary.Id, world.OtherOwner.Id, world.Admin.Id }.OrderBy(id => id),
            listed.Select(person => person.AccountId).OrderBy(id => id));
        var owner = listed.Single(person => person.AccountId == world.Owner.Id);
        Assert.Equal("owner@example.com", owner.AccountLabel);
        Assert.Equal("owner@example.com", owner.Email);
        Assert.Null(owner.ProfileDisplayName);
        Assert.Equal(2, owner.Sessions.Count);

        // A person's own sign-in is named by the person it opens.
        var mary = listed.Single(person => person.AccountId == world.Mary.Id);
        Assert.Equal("Mary", mary.ProfileDisplayName);
        Assert.Equal("mary@example.com", mary.Email);
    }

    [Fact]
    public async Task OnlyActiveSessions_AreListed_NewestFirst_AndPeopleWithNoneAreLeftOut()
    {
        await using var world = await World.CreateAsync();
        await world.Identities.RevokeSessionAsync(world.OwnerPhone.Id, DateTimeOffset.UtcNow, "test");
        var expired = await world.AddSessionAsync(world.Owner, "Old laptop", expiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        var listed = await world.Service.ListAsync(world.ServerAdmin);

        var owner = listed.Single(person => person.AccountId == world.Owner.Id);
        Assert.Equal([world.OwnerComputer.Id], owner.Sessions.Select(session => session.Id));
        Assert.DoesNotContain(owner.Sessions, session => session.Id == expired.Id);
        await world.Identities.RevokeSessionAsync(world.MarySession.Id, DateTimeOffset.UtcNow, "test");
        Assert.DoesNotContain(await world.Service.ListAsync(world.ServerAdmin), person => person.AccountId == world.Mary.Id);
    }

    [Fact]
    public async Task HouseholdAdministrator_SeesOnlyTheirOwnHouseholdsPeople()
    {
        await using var world = await World.CreateAsync();

        var listed = await world.Service.ListAsync(world.HouseholdAdmin);

        Assert.Equal(
            new[] { world.Owner.Id, world.Mary.Id }.OrderBy(id => id),
            listed.Select(person => person.AccountId).OrderBy(id => id));
        Assert.DoesNotContain(listed, person => person.AccountId == world.OtherOwner.Id);
        Assert.DoesNotContain(listed, person => person.AccountId == world.Admin.Id);
    }

    [Fact]
    public async Task HouseholdAdministrator_DoesNotSeeAnotherAdministratorsSignIn_InTheSameHousehold()
    {
        await using var world = await World.CreateAsync();
        var peer = await world.AddHouseholdAdministratorPeerAsync();
        await world.AddSessionAsync(peer, "Peer laptop");

        var listed = await world.Service.ListAsync(world.HouseholdAdmin);

        Assert.DoesNotContain(listed, person => person.AccountId == peer.Id);
        // A server administrator still sees them.
        Assert.Contains(await world.Service.ListAsync(world.ServerAdmin), person => person.AccountId == peer.Id);
    }

    [Fact]
    public async Task EveryoneElse_IsRefused_ForListAndRevoke()
    {
        await using var world = await World.CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Service.ListAsync(world.Member));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Service.RevokeAsync(world.Member, world.OwnerPhone.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Service.ListAsync(world.Application));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Service.RevokeAsync(world.Application, world.OwnerPhone.Id));
        Assert.DoesNotContain(world.Audit.Events, entry => entry.EventType == "account.session_revoked_by_administrator");
        Assert.Null((await world.Identities.GetSessionByIdAsync(world.OwnerPhone.Id))!.RevokedAt);
    }

    [Fact]
    public async Task ServerAdministrator_CanSignOutAnyonesSession_AndItIsAudited()
    {
        await using var world = await World.CreateAsync();

        var outcome = await world.Service.RevokeAsync(world.ServerAdmin, world.OtherOwnerPhone.Id);

        Assert.Equal(ManagedSessionRevokeOutcome.Revoked, outcome);
        var stored = (await world.Identities.GetSessionByIdAsync(world.OtherOwnerPhone.Id))!;
        Assert.NotNull(stored.RevokedAt);
        Assert.Equal(ManagedSessionService.RevokedReason, stored.RevokedReason);
        var audited = Assert.Single(world.Audit.Events, entry => entry.EventType == "account.session_revoked_by_administrator");
        Assert.Equal(world.ServerAdmin.AccountId, audited.ActorAccountId);
        Assert.Equal(world.OtherOwnerPhone.Id.ToString("D"), audited.SubjectId);
    }

    [Fact]
    public async Task HouseholdAdministrator_CanSignOutTheirHouseholdsSession_ButNotAnotherHouseholdsOrAnAdministrators()
    {
        await using var world = await World.CreateAsync();

        Assert.Equal(ManagedSessionRevokeOutcome.Revoked, await world.Service.RevokeAsync(world.HouseholdAdmin, world.MarySession.Id));
        Assert.NotNull((await world.Identities.GetSessionByIdAsync(world.MarySession.Id))!.RevokedAt);

        Assert.Equal(ManagedSessionRevokeOutcome.Forbidden, await world.Service.RevokeAsync(world.HouseholdAdmin, world.OtherOwnerPhone.Id));
        Assert.Equal(ManagedSessionRevokeOutcome.Forbidden, await world.Service.RevokeAsync(world.HouseholdAdmin, world.AdminSession.Id));
        Assert.Null((await world.Identities.GetSessionByIdAsync(world.OtherOwnerPhone.Id))!.RevokedAt);
        Assert.Null((await world.Identities.GetSessionByIdAsync(world.AdminSession.Id))!.RevokedAt);
        Assert.Single(world.Audit.Events, entry => entry.EventType == "account.session_revoked_by_administrator");
    }

    [Fact]
    public async Task OwnSession_IsRefusedWithAMessage_AndStaysSignedIn()
    {
        await using var world = await World.CreateAsync();

        Assert.Equal(ManagedSessionRevokeOutcome.OwnSession, await world.Service.RevokeAsync(world.HouseholdAdmin, world.OwnerComputer.Id));
        Assert.Equal(ManagedSessionRevokeOutcome.OwnSession, await world.Service.RevokeAsync(world.ServerAdmin, world.AdminSession.Id));
        Assert.Null((await world.Identities.GetSessionByIdAsync(world.OwnerComputer.Id))!.RevokedAt);
        Assert.Equal("Use Sign out for your own session", ManagedSessionService.OwnSessionMessage);
    }

    [Fact]
    public async Task MissingOrAlreadyEndedSessions_AreNotFound()
    {
        await using var world = await World.CreateAsync();

        Assert.Equal(ManagedSessionRevokeOutcome.NotFound, await world.Service.RevokeAsync(world.ServerAdmin, Guid.NewGuid()));
        await world.Identities.RevokeSessionAsync(world.OwnerPhone.Id, DateTimeOffset.UtcNow, "test");
        Assert.Equal(ManagedSessionRevokeOutcome.NotFound, await world.Service.RevokeAsync(world.ServerAdmin, world.OwnerPhone.Id));
    }

    [Fact]
    public async Task AnotherHouseholdsEndedSession_IsStillForbidden_SoItsStateCannotBeProbed()
    {
        await using var world = await World.CreateAsync();
        await world.Identities.RevokeSessionAsync(world.OtherOwnerPhone.Id, DateTimeOffset.UtcNow, "test");

        Assert.Equal(ManagedSessionRevokeOutcome.Forbidden, await world.Service.RevokeAsync(world.HouseholdAdmin, world.OtherOwnerPhone.Id));
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-managed-sessions-{Guid.NewGuid():N}.db");
        private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima-managed-sessions-{Guid.NewGuid():N}");
        private DatabaseConnection _database = null!;
        private ConfigurationDirectoryLoader _configuration = null!;
        private AccountRepository _accounts = null!;
        private ProfileRepository _profiles = null!;
        private FirstPartyIdentityService _identity = null!;
        private AccountAccessMutationService _mutations = null!;
        private RequestAuthority _setupActor = null!;

        public IdentityRepository Identities { get; private set; } = null!;
        public ManagedSessionService Service { get; private set; } = null!;
        public RecordingAudit Audit { get; } = new();

        public Account Owner { get; private set; } = null!;
        public Account Mary { get; private set; } = null!;
        public Account OtherOwner { get; private set; } = null!;
        public Account Admin { get; private set; } = null!;

        public AuthSession OwnerComputer { get; private set; } = null!;
        public AuthSession OwnerPhone { get; private set; } = null!;
        public AuthSession MarySession { get; private set; } = null!;
        public AuthSession OtherOwnerPhone { get; private set; } = null!;
        public AuthSession AdminSession { get; private set; } = null!;

        public RequestAuthority ServerAdmin { get; private set; } = null!;
        public RequestAuthority HouseholdAdmin { get; private set; } = null!;
        public RequestAuthority Member { get; private set; } = null!;
        public RequestAuthority Application { get; } = new(
            PrincipalKind.ServiceApplication, true, ApplicationId: Guid.NewGuid(), ApplicationEnabled: true);

        public static async Task<World> CreateAsync()
        {
            var world = new World();
            world._database = new DatabaseConnection(world._databasePath);
            world._configuration = new ConfigurationDirectoryLoader(world._configPath);
            world._database.InitializeSchema();
            world._accounts = new AccountRepository(world._database);
            world.Identities = new IdentityRepository(world._database);
            world._profiles = new ProfileRepository(world._database);
            var households = new HouseholdRepository(world._database);
            world._identity = new FirstPartyIdentityService(
                world.Identities, world._accounts, world._profiles,
                new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
                TimeProvider.System, new ConfigurationAuthenticationPolicyProvider(world._configuration));
            world._mutations = new AccountAccessMutationService(
                world._accounts, world.Identities, world._profiles, world._configuration,
                new AllowAllDecisions(), new AllowEvaluator(), new PasswordHasher<GrantAdminProtection>(),
                new NoOpInvalidation(), world.Audit, world._identity, new NoProfilePhotos(), TimeProvider.System);
            world._setupActor = new RequestAuthority(
                PrincipalKind.Human, true, Guid.NewGuid(), Guid.NewGuid(), SessionId: Guid.NewGuid(),
                AccountEnabled: true, GrantEnabled: true, AccountIsAdministrator: true, GrantAdminEnabled: true);
            world.Service = new ManagedSessionService(
                world._accounts, world.Identities, world._profiles, new ScopedDecisions(),
                world._identity, world.Audit, TimeProvider.System);

            world.Owner = await world.CreateMainAsync("owner@example.com", "Owner", isAdministrator: false);
            var household = (await households.GetForAccountAsync(world.Owner.Id))!;
            await world._mutations.SetHouseholdAdminAsync(world._setupActor, world.Owner.Id, true);
            var maryPerson = await world._mutations.AddHouseholdPersonAsync(world._setupActor,
                new AddHouseholdPersonCommand(household.Id, "Mary", null, IsChild: false, Pin: null));
            world.Mary = (await world._mutations.GiveOwnSignInAsync(world._setupActor,
                new GiveOwnSignInCommand(maryPerson.Id, "mary@example.com", "a long temporary password"))).Account;
            world.OtherOwner = await world.CreateMainAsync("other@example.com", "Other", isAdministrator: false);
            world.Admin = await world.CreateMainAsync("admin@example.com", "Admin", isAdministrator: true);
            world.Owner = (await world._accounts.GetByIdAsync(world.Owner.Id))!;

            world.OwnerComputer = await world.AddSessionAsync(world.Owner, "Owner computer");
            world.OwnerPhone = await world.AddSessionAsync(world.Owner, "Owner phone");
            world.MarySession = await world.AddSessionAsync(world.Mary, "Mary phone");
            world.OtherOwnerPhone = await world.AddSessionAsync(world.OtherOwner, "Other phone");
            world.AdminSession = await world.AddSessionAsync(world.Admin, "Admin computer");

            world.ServerAdmin = world.Authority(world.Admin, world.AdminSession, administrator: true);
            world.HouseholdAdmin = world.Authority(world.Owner, world.OwnerComputer, householdAdministrator: true);
            world.Member = world.Authority(world.Mary, world.MarySession);
            return world;
        }

        public async Task<AuthSession> AddSessionAsync(Account account, string deviceName, DateTimeOffset? expiresAt = null)
        {
            var grants = await _accounts.GetGrantsAsync(account.Id);
            var now = DateTimeOffset.UtcNow;
            var session = new AuthSession
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                ActiveProfileId = grants.First().ProfileId,
                TokenHash = Guid.NewGuid().ToString("N"),
                DeviceId = Guid.NewGuid().ToString("N"),
                DeviceName = deviceName,
                Client = "Test",
                AuthenticationMethod = "password",
                SecurityStamp = "stamp",
                CreatedAt = now.AddHours(-1),
                LastSeenAt = now.AddMinutes(-Math.Max(1, Random.Shared.Next(1, 50))),
                ExpiresAt = expiresAt ?? now.AddDays(30),
                AuthenticatedAt = now,
            };
            await Identities.InsertSessionAsync(session);
            return session;
        }

        /// <summary>A second household administrator in the owner's household (a second main sign-in).</summary>
        public async Task<Account> AddHouseholdAdministratorPeerAsync()
        {
            var ownerGrant = (await _accounts.GetGrantsAsync(Owner.Id)).First();
            var now = DateTimeOffset.UtcNow;
            var peer = new Account
            {
                Id = Guid.NewGuid(),
                Email = "peer@example.com",
                NormalizedEmail = "PEER@EXAMPLE.COM",
                IsEnabled = true,
                AuthorizationVersion = 1,
                CreatedAt = now,
                UpdatedAt = now,
                HouseholdId = Owner.HouseholdId,
            };
            await _accounts.CreateAccountAsync(peer,
                new AccountProfileGrant
                {
                    AccountId = peer.Id,
                    ProfileId = ownerGrant.ProfileId,
                    IsDefault = true,
                    IsEnabled = true,
                    AuthorizationVersion = 1,
                    GrantedAt = now,
                },
                new HashSet<AccountFeatureId>(), new HashSet<Guid>());
            await _mutations.SetHouseholdAdminAsync(_setupActor, peer.Id, true);
            return (await _accounts.GetByIdAsync(peer.Id))!;
        }

        private async Task<Account> CreateMainAsync(string email, string person, bool isAdministrator) =>
            await _mutations.CreateAsync(_setupActor, new CreateAccountAccessCommand(
                email, isAdministrator, ProfileId: null, NewProfile: new NewAccountProfileCommand(person, "#7C4DFF"),
                Features: new HashSet<AccountFeatureId>(), Libraries: new HashSet<Guid>()));

        private RequestAuthority Authority(Account account, AuthSession session,
            bool administrator = false, bool householdAdministrator = false) => new(
            PrincipalKind.Human, true, account.Id, session.ActiveProfileId, SessionId: session.Id,
            AccountEnabled: true, GrantEnabled: true, AccountIsAdministrator: administrator, GrantAdminEnabled: administrator,
            AccountHouseholdId: account.HouseholdId, AccountIsHouseholdAdmin: householdAdministrator);

        public ValueTask DisposeAsync()
        {
            _configuration.Dispose();
            _database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    /// <summary>Allows everything, so the setup steps can create the world.</summary>
    private sealed class AllowAllDecisions : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid libraryId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    /// <summary>Tells administrators from everyone else by the request's own flags, like the real service without a PIN.</summary>
    private sealed class ScopedDecisions : IAccountAccessDecisionService
    {
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

    public sealed class RecordingAudit : IAuthorizationAuditWriter
    {
        public List<AuthorizationAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(AuthorizationAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            lock (Events)
            {
                Events.Add(auditEvent);
            }

            return ValueTask.CompletedTask;
        }
    }
}
