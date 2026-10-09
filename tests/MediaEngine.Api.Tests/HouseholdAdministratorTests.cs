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
/// Household administrators (packet B11a): a person who looks after one household and can never reach another
/// household or the server's own powers.
/// </summary>
public sealed class HouseholdAdministratorTests
{
    private const string TemporaryPassword = "a long temporary password";

    [Fact]
    public async Task ActionsOnAHousehold_AreAllowedFor_ServerAdminAnywhere_HouseholdAdminOnlyAtHome_NobodyElse()
    {
        await using var world = await World.CreateAsync();

        var actors = new (string Name, RequestAuthority Authority, bool AllowedAtA, bool AllowedAtB)[]
        {
            ("server administrator", world.ServerAdmin, true, true),
            ("household administrator of A", world.AdminOfA, true, false),
            ("household administrator of B", world.AdminOfB, false, true),
            ("plain member of A", world.MemberOfA, false, false),
            ("household administrator of A on a child profile", world.RestrictedAdminOfA, false, false),
        };

        foreach (var action in world.Actions())
        {
            foreach (var actor in actors)
            {
                Assert.True(actor.AllowedAtA == await action.IsAllowedAsync(actor.Authority, world.A),
                    $"{action.Name} at household A as {actor.Name}");
                Assert.True(actor.AllowedAtB == await action.IsAllowedAsync(actor.Authority, world.B),
                    $"{action.Name} at household B as {actor.Name}");
            }
        }
    }

    [Fact]
    public async Task HouseholdAdministrator_CannotTouchAnAdministrator_OrMakeOne()
    {
        await using var world = await World.CreateAsync();
        var mutations = world.Mutations;

        // Another household administrator in the same household is a peer: off limits.
        var peer = await mutations.IssueInvitationAsync(world.ServerAdmin, new IssueAccountInvitationCommand(
            "peer@example.com", [world.A.Person.Id], null));
        await mutations.SetHouseholdAdminAsync(world.ServerAdmin, peer.AccountId, true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mutations.SetTemporaryPasswordAsync(
            world.AdminOfA, peer.AccountId, "another long temporary password"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mutations.ResetTwoStepAsync(
            world.AdminOfA, peer.AccountId));

        // The server administrator's sign-in is in another household and is off limits as well.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mutations.SetTemporaryPasswordAsync(
            world.AdminOfA, world.ServerAdminAccount.Id, "another long temporary password"));

        // They cannot turn on administrator access for anyone, nor promote a household administrator.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mutations.UpsertGrantAsync(world.AdminOfA,
            new AccountProfileGrant
            {
                AccountId = world.A.OwnSignIn.Id,
                ProfileId = world.A.Person.Id,
                IsEnabled = true,
                AdminEnabled = true,
                AuthorizationVersion = 1,
            }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            mutations.SetHouseholdAdminAsync(world.AdminOfA, world.A.OwnSignIn.Id, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            mutations.SetHouseholdAdminAsync(world.MemberOfA, world.A.OwnSignIn.Id, true));
    }

    [Fact]
    public async Task HouseholdAdministrator_CanOnlyHandOutWhatTheHouseholdAlreadyHas()
    {
        await using var world = await World.CreateAsync();
        var mutations = world.Mutations;

        // A second adult in household A, who is not the household's main sign-in.
        var invitation = await mutations.IssueInvitationAsync(world.ServerAdmin, new IssueAccountInvitationCommand(
            "second.adult@example.com", [world.A.Person.Id], null));
        var secondAdult = (await world.Accounts.GetByIdAsync(invitation.AccountId))!;
        Assert.Equal(world.A.Household.Id, secondAdult.HouseholdId);
        Assert.False(secondAdult.HouseholdAdmin);

        // The household has Read; handing out Read is fine, Watch (which the household lacks) is not.
        await mutations.ReplaceAccessAsync(world.AdminOfA, secondAdult.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid>());
        Assert.Equal(new[] { "read" }, (await world.Accounts.GetFeatureGrantsAsync(secondAdult.Id)).Select(feature => feature.Value));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mutations.ReplaceAccessAsync(world.AdminOfA,
            secondAdult.Id, new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch }, new HashSet<Guid>()));

        // The server administrator is not held to that ceiling.
        await mutations.ReplaceAccessAsync(world.ServerAdmin, secondAdult.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch }, new HashSet<Guid>());
    }

    [Fact]
    public async Task OnlyAServerAdministrator_DecidesWhoLooksAfterAHousehold()
    {
        await using var world = await World.CreateAsync();
        var second = (await world.Accounts.GetByIdAsync((await world.Mutations.IssueInvitationAsync(world.ServerAdmin,
            new IssueAccountInvitationCommand("second.adult@example.com", [world.A.Person.Id], null)).ConfigureAwait(false)).AccountId))!;
        Assert.False(second.HouseholdAdmin);

        await world.Mutations.SetHouseholdAdminAsync(world.ServerAdmin, second.Id, true);
        Assert.True((await world.Accounts.GetByIdAsync(second.Id))!.HouseholdAdmin);

        await world.Mutations.SetHouseholdAdminAsync(world.ServerAdmin, second.Id, false);
        Assert.False((await world.Accounts.GetByIdAsync(second.Id))!.HouseholdAdmin);

        // A person's own sign-in follows the household and can never be one.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Mutations.SetHouseholdAdminAsync(world.ServerAdmin, world.A.OwnSignIn.Id, true));
    }

    [Fact]
    public async Task SomeoneInvitedFromOutside_BecomesTheAdministratorOfTheirNewHousehold_AndOnlyThatOne()
    {
        await using var world = await World.CreateAsync();
        var invitation = await world.Mutations.IssueInvitationAsync(world.ServerAdmin, new IssueAccountInvitationCommand(
            "guest@example.com", [], null, NewHouseholdPersonName: "Guest"));
        var guest = (await world.Accounts.GetByIdAsync(invitation.AccountId))!;

        Assert.True(guest.HouseholdAdmin);
        Assert.False(guest.IsAdministrator);
        Assert.NotEqual(world.A.Household.Id, guest.HouseholdId);
        var household = (await world.Households.GetByIdAsync(guest.HouseholdId!.Value))!;
        Assert.Equal(guest.Id, household.PrimaryAccountId);

        var guestAuthority = world.HouseholdAdminAuthority(guest, Guid.NewGuid());
        var guestPerson = Assert.Single(await world.Households.ListProfilesAsync(household.Id));
        await world.Mutations.SetProfilePinAsync(guestAuthority, guestPerson.Id, "4321");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            world.Mutations.SetProfilePinAsync(guestAuthority, world.A.Person.Id, "4321"));
    }

    [Fact]
    public async Task DeletingTheMainSignIn_HandsHouseholdLeadershipToTheNextOneAndNeverLeavesAnEmptySeat()
    {
        await using var world = await World.CreateAsync();
        var invitation = await world.Mutations.IssueInvitationAsync(world.ServerAdmin, new IssueAccountInvitationCommand(
            "second.adult@example.com", [world.A.Person.Id], null));

        await world.Mutations.RemoveOwnSignInAsync(world.ServerAdmin, world.A.OwnSignIn.Id);
        await world.Mutations.DeleteAsync(world.ServerAdmin, world.A.Owner.Id);

        var household = (await world.Households.GetByIdAsync(world.A.Household.Id))!;
        Assert.Equal(invitation.AccountId, household.PrimaryAccountId);
    }

    private sealed record Place(Household Household, Account Owner, Profile Person, Account OwnSignIn, Profile Spare)
    {
        public Guid Id => Household.Id;
    }

    private sealed class ActionUnderTest(string name, Func<RequestAuthority, Place, Task> run)
    {
        public string Name { get; } = name;

        /// <summary>Allowed means the action got past authorization; a later business-rule refusal still counts.</summary>
        public async Task<bool> IsAllowedAsync(RequestAuthority authority, Place place)
        {
            try
            {
                await run(authority, place).ConfigureAwait(false);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                return true;
            }
        }
    }

    private sealed class World : IAsyncDisposable
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-household-admin-{Guid.NewGuid():N}.db");
        private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima-household-admin-{Guid.NewGuid():N}");
        private DatabaseConnection _database = null!;
        private ConfigurationDirectoryLoader _configuration = null!;
        private int _counter;

        public AccountRepository Accounts { get; private set; } = null!;
        public HouseholdRepository Households { get; private set; } = null!;
        public AccountAccessMutationService Mutations { get; private set; } = null!;
        public RequestAuthority ServerAdmin { get; private set; } = null!;
        public Account ServerAdminAccount { get; private set; } = null!;
        public RequestAuthority AdminOfA { get; private set; } = null!;
        public RequestAuthority AdminOfB { get; private set; } = null!;
        public RequestAuthority MemberOfA { get; private set; } = null!;
        public RequestAuthority RestrictedAdminOfA { get; private set; } = null!;
        public Place A { get; private set; } = null!;
        public Place B { get; private set; } = null!;

        public static async Task<World> CreateAsync()
        {
            var world = new World();
            world._database = new DatabaseConnection(world._databasePath);
            world._configuration = new ConfigurationDirectoryLoader(world._configPath);
            world._database.InitializeSchema();
            world.Accounts = new AccountRepository(world._database);
            var identities = new IdentityRepository(world._database);
            var profiles = new ProfileRepository(world._database);
            world.Households = new HouseholdRepository(world._database);
            var identity = new FirstPartyIdentityService(
                identities,
                world.Accounts,
                profiles,
                new PasswordHasher<AccountCredential>(),
                new PasswordHasher<ProfileCredential>(),
                TimeProvider.System,
                new ConfigurationAuthenticationPolicyProvider(world._configuration));
            world.Mutations = new AccountAccessMutationService(
                world.Accounts,
                identities,
                profiles,
                world._configuration,
                new RealAdministratorDecisions(),
                new AllowEvaluator(),
                new PasswordHasher<GrantAdminProtection>(),
                new NoOpInvalidation(),
                new NoOpAudit(),
                identity,
                TimeProvider.System);

            // A bootstrap actor that is a server administrator, used to set the scene.
            var bootstrap = ServerAuthority(Guid.NewGuid(), Guid.NewGuid());
            world.ServerAdminAccount = await world.Mutations.CreateAsync(bootstrap, new CreateAccountAccessCommand(
                "server@example.com", IsAdministrator: true, ProfileId: null,
                NewProfile: new NewAccountProfileCommand("Server", "#7C4DFF"),
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch },
                Libraries: new HashSet<Guid>(), TemporaryPassword: null));
            var serverProfile = (await world.Accounts.GetDefaultProfileIdAsync(world.ServerAdminAccount.Id))!.Value;
            world.ServerAdmin = ServerAuthority(world.ServerAdminAccount.Id, serverProfile);

            world.A = await world.CreateHouseholdAsync("a@example.com", "Alex");
            world.B = await world.CreateHouseholdAsync("b@example.com", "Bea");

            var ownerProfileA = (await world.Accounts.GetDefaultProfileIdAsync(world.A.Owner.Id))!.Value;
            var ownerProfileB = (await world.Accounts.GetDefaultProfileIdAsync(world.B.Owner.Id))!.Value;
            world.AdminOfA = world.HouseholdAdminAuthority(world.A.Owner, ownerProfileA);
            world.AdminOfB = world.HouseholdAdminAuthority(world.B.Owner, ownerProfileB);
            world.MemberOfA = world.HouseholdAdminAuthority(world.A.OwnSignIn, world.A.Person.Id, isHouseholdAdmin: false);
            world.RestrictedAdminOfA = world.HouseholdAdminAuthority(world.A.Owner, ownerProfileA, restricted: true);
            return world;
        }

        public RequestAuthority HouseholdAdminAuthority(Account account, Guid profileId, bool isHouseholdAdmin = true, bool restricted = false) =>
            new(PrincipalKind.Human, true,
                AccountId: account.Id,
                ActiveProfileId: profileId,
                SessionId: Guid.NewGuid(),
                AccountEnabled: true,
                GrantEnabled: true,
                ActiveProfileIsRestricted: restricted,
                AccountHouseholdId: account.HouseholdId,
                AccountIsHouseholdAdmin: isHouseholdAdmin);

        public IReadOnlyList<ActionUnderTest> Actions() =>
        [
            new("add a person", (actor, place) => Mutations.AddHouseholdPersonAsync(actor,
                new AddHouseholdPersonCommand(place.Id, $"New {Next()}", null, false, null))),
            new("set a person's PIN", (actor, place) => Mutations.SetProfilePinAsync(actor, place.Person.Id, "4321")),
            new("rename a person", (actor, place) => Mutations.UpdateProfileAsync(actor, place.Person.Id,
                new UpdateManagedProfileCommand($"Renamed {Next()}", null))),
            new("remove a person", async (actor, place) =>
            {
                var disposable = await Mutations.AddHouseholdPersonAsync(ServerAdmin,
                    new AddHouseholdPersonCommand(place.Id, $"Temp {Next()}", null, false, null));
                try
                {
                    await Mutations.DeleteProfileAsync(actor, disposable.Id);
                }
                finally
                {
                    // Keep the household under its limit of eight when the action was refused.
                    try { await Mutations.DeleteProfileAsync(ServerAdmin, disposable.Id); }
                    catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
                    {
                        // Already removed by the action under test.
                    }
                }
            }),
            // After the first allowed run the spare person has a sign-in, so later runs stop at a business rule, which
            // still proves they got past authorization.
            new("give a person their own sign-in", (actor, place) => Mutations.GiveOwnSignInAsync(actor,
                new GiveOwnSignInCommand(place.Spare.Id, $"own{Next()}@example.com", TemporaryPassword))),
            new("set a temporary password", (actor, place) =>
                Mutations.SetTemporaryPasswordAsync(actor, place.OwnSignIn.Id, "another long temporary password")),
            new("reset two-step codes", (actor, place) => Mutations.ResetTwoStepAsync(actor, place.OwnSignIn.Id)),
        ];

        private string Next() => (++_counter).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);

        private async Task<Place> CreateHouseholdAsync(string email, string personName)
        {
            // The household's main sign-in: it becomes the household administrator by being the first.
            var owner = await Mutations.CreateAsync(ServerAdmin, new CreateAccountAccessCommand(
                email, IsAdministrator: false, ProfileId: null,
                NewProfile: new NewAccountProfileCommand(personName, "#112233"),
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read },
                Libraries: new HashSet<Guid>(), TemporaryPassword: null));
            var household = (await Households.GetForAccountAsync(owner.Id))!;
            var person = await Mutations.AddHouseholdPersonAsync(ServerAdmin,
                new AddHouseholdPersonCommand(household.Id, $"{personName}'s friend", null, false, null));
            var spare = await Mutations.AddHouseholdPersonAsync(ServerAdmin,
                new AddHouseholdPersonCommand(household.Id, $"{personName}'s spare", null, false, null));
            var given = await Mutations.GiveOwnSignInAsync(ServerAdmin,
                new GiveOwnSignInCommand(person.Id, $"friend.{email}", TemporaryPassword));
            return new Place(household, (await Accounts.GetByIdAsync(owner.Id))!, person, given.Account, spare);
        }

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

    /// <summary>Allows exactly what the real rule allows for a server administrator: an effective one.</summary>
    private sealed class RealAdministratorDecisions : IAccountAccessDecisionService
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

    private sealed class NoOpAudit : IAuthorizationAuditWriter
    {
        public ValueTask WriteAsync(AuthorizationAuditEvent auditEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
