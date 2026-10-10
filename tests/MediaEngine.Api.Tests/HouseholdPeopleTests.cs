using MediaEngine.Api.Security;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Tests;

/// <summary>Adding a person to a household, and giving a person their own sign-in (packet B9).</summary>
public sealed class HouseholdPeopleTests
{
    [Fact]
    public async Task AddPerson_JoinsTheHousehold_SetsTheChildRole_AndRefusesTheNinth()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, households, actor) = (fixture.Mutations, fixture.Accounts, fixture.Households, fixture.Actor);
        var owner = await fixture.CreateOwnerAsync();
        var household = (await households.GetForAccountAsync(owner.Id))!;

        var mary = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Mary", null, IsChild: true, Pin: "4321"));
        var sam = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Sam", "#112233", IsChild: false, Pin: null));

        Assert.Equal(household.Id, mary.HouseholdId);
        Assert.Equal(ProfileRole.RestrictedProfile, mary.Role);
        Assert.Equal(ProfileRole.StandardUser, sam.Role);
        Assert.Equal(3, (await households.ListProfilesAsync(household.Id)).Count);

        // The household's sign-in can open the new person, but it is not the default.
        var grant = await accounts.GetGrantAsync(owner.Id, mary.Id);
        Assert.NotNull(grant);
        Assert.False(grant.IsDefault);
        Assert.False(grant.AdminEnabled);

        // A PIN that is too short is refused and leaves nobody half-added.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Nope", null, false, Pin: "1")));
        Assert.Equal(3, (await households.ListProfilesAsync(household.Id)).Count);

        for (var index = 0; index < 5; index++)
        {
            await mutations.AddHouseholdPersonAsync(actor,
                new AddHouseholdPersonCommand(household.Id, $"Person {index}", null, false, null));
        }

        Assert.Equal(8, (await households.ListProfilesAsync(household.Id)).Count);
        var full = await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Ninth", null, false, null)));
        Assert.Equal(Household.FullMessage, full.Message);
    }

    [Fact]
    public async Task ContentLimit_NewKidsStartOnPg_IsIndependentOfTheKidsLabel_AndKeepsUntilChanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, households, actor) = (fixture.Mutations, fixture.Households, fixture.Actor);
        var owner = await fixture.CreateOwnerAsync();
        var household = (await households.GetForAccountAsync(owner.Id))!;

        var kid = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Mary", null, IsChild: true, Pin: null));
        var grownUp = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Sam", null, IsChild: false, Pin: null));
        var olderKid = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Tom", null, IsChild: true, Pin: null, ContentLimit: "PG-13", ContentLimitAllowUnrated: true));
        var freeKid = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Ann", null, IsChild: true, Pin: null, ContentLimit: ""));

        Assert.Equal("PG", kid.ContentLimit);
        Assert.Null(grownUp.ContentLimit);
        Assert.Equal("PG-13", olderKid.ContentLimit);
        Assert.True(olderKid.ContentLimitAllowUnrated);
        Assert.Null(freeKid.ContentLimit);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Bad", null, false, null, ContentLimit: "NC-17")));

        // An adult can be limited, and a rename that leaves the limit out keeps it.
        await mutations.UpdateProfileAsync(actor, grownUp.Id,
            new UpdateManagedProfileCommand("Sam", null, null, ContentLimit: "R"));
        await mutations.UpdateProfileAsync(actor, grownUp.Id, new UpdateManagedProfileCommand("Samuel", null));
        var stored = (await households.ListProfilesAsync(household.Id)).Single(profile => profile.Id == grownUp.Id);
        Assert.Equal("Samuel", stored.DisplayName);
        Assert.Equal("R", stored.ContentLimit);

        await mutations.UpdateProfileAsync(actor, grownUp.Id,
            new UpdateManagedProfileCommand("Samuel", null, null, ContentLimit: "", ContentLimitAllowUnrated: true));
        stored = (await households.ListProfilesAsync(household.Id)).Single(profile => profile.Id == grownUp.Id);
        Assert.Null(stored.ContentLimit);
        Assert.True(stored.ContentLimitAllowUnrated);
    }

    [Fact]
    public async Task OwnSignIn_OpensStraightIntoThePerson_FollowsHouseholdAccess_AndIsNeverAnAdministrator()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, households, actor) = (fixture.Mutations, fixture.Accounts, fixture.Households, fixture.Actor);
        var owner = await fixture.CreateOwnerAsync(AccountFeatureId.Read, AccountFeatureId.Watch);
        var household = (await households.GetForAccountAsync(owner.Id))!;
        var mary = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Mary", null, true, null));
        var tom = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Tom", null, false, null));

        var given = await mutations.GiveOwnSignInAsync(actor,
            new GiveOwnSignInCommand(mary.Id, "mary@example.com", "a long temporary password"));
        Assert.Null(given.Invitation);
        var maryAccount = given.Account;
        Assert.Equal(household.Id, maryAccount.HouseholdId);
        Assert.False(maryAccount.IsAdministrator);
        Assert.Equal(owner.Id, maryAccount.GrantsInheritFromAccountId);
        var maryGrant = Assert.Single(await accounts.GetGrantsAsync(maryAccount.Id));
        Assert.Equal(mary.Id, maryGrant.ProfileId);
        Assert.True(maryGrant.IsDefault);
        Assert.True(maryGrant.IsEnabled);
        Assert.False(maryGrant.AdminEnabled);

        // Library and lane access follows the household, so later changes reach Mary without copying.
        Assert.Equal(
            new[] { AccountFeatureId.Read, AccountFeatureId.Watch }.Select(feature => feature.Value).Order(),
            (await accounts.GetFeatureGrantsAsync(maryAccount.Id)).Select(feature => feature.Value).Order());
        await mutations.ReplaceAccessAsync(actor, owner.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Listen }, new HashSet<Guid>());
        Assert.Equal(new[] { "listen" }, (await accounts.GetFeatureGrantsAsync(maryAccount.Id)).Select(feature => feature.Value));

        // Her access is not edited on her own; it is the household's.
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.ReplaceAccessAsync(actor, maryAccount.Id,
            new HashSet<AccountFeatureId>(), new HashSet<Guid>()));

        // An invitation works the same way and the new sign-in opens only that person.
        var invited = await mutations.GiveOwnSignInAsync(actor,
            new GiveOwnSignInCommand(tom.Id, "tom@example.com", null));
        Assert.NotNull(invited.Invitation);
        var accepted = await fixture.Identity.AcceptInvitationAsync(
            invited.Invitation.Code, "invited password", "browser", "Browser", "Dashboard");
        Assert.Equal(invited.Account.Id, accepted.Account.Id);
        Assert.Equal(tom.Id, Assert.Single(await accounts.GetGrantsAsync(invited.Account.Id)).ProfileId);

        // One own sign-in per person, and an email only ever has one sign-in.
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.GiveOwnSignInAsync(actor,
            new GiveOwnSignInCommand(mary.Id, "mary2@example.com", null)));
        var third = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Third", null, false, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.GiveOwnSignInAsync(actor,
            new GiveOwnSignInCommand(third.Id, "MARY@example.com", null)));
    }

    [Fact]
    public async Task OwnSignIn_StaysInThePersonsOwnHousehold()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, households, actor) = (fixture.Mutations, fixture.Accounts, fixture.Households, fixture.Actor);
        var owner = await fixture.CreateOwnerAsync();
        var ownerHousehold = (await households.GetForAccountAsync(owner.Id))!;
        var invitation = await mutations.IssueInvitationAsync(actor, new IssueAccountInvitationCommand(
            "guest@example.com", [], null, NewHouseholdPersonName: "Guest"));
        var guestHousehold = (await households.GetForAccountAsync(invitation.AccountId))!;
        var guestPerson = Assert.Single(await households.ListProfilesAsync(guestHousehold.Id));
        var guestSignIn = await mutations.GiveOwnSignInAsync(actor,
            new GiveOwnSignInCommand(guestPerson.Id, "guest.own@example.com", null));

        // The new sign-in lands in the person's household, not the household of whoever asked.
        Assert.Equal(guestHousehold.Id, guestSignIn.Account.HouseholdId);
        Assert.NotEqual(ownerHousehold.Id, guestSignIn.Account.HouseholdId);

        // It can never be pointed at someone in another household.
        var ownerPerson = Assert.Single(await accounts.GetGrantsAsync(owner.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.UpsertGrantAsync(actor, new AccountProfileGrant
        {
            AccountId = guestSignIn.Account.Id,
            ProfileId = ownerPerson.ProfileId,
            IsEnabled = true,
            AuthorizationVersion = 1,
        }));
    }

    [Fact]
    public async Task RemovingAnOwnSignIn_KeepsThePersonAndEndsTheirSessions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, households, actor) = (fixture.Mutations, fixture.Accounts, fixture.Households, fixture.Actor);
        var owner = await fixture.CreateOwnerAsync();
        var household = (await households.GetForAccountAsync(owner.Id))!;
        var mary = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Mary", null, false, null));
        var invited = await mutations.GiveOwnSignInAsync(actor,
            new GiveOwnSignInCommand(mary.Id, "mary@example.com", null));
        await fixture.Identity.AcceptInvitationAsync(
            invited.Invitation!.Code, "invited password", "phone", "Phone", "Dashboard");
        Assert.NotEmpty(await fixture.Identities.GetSessionsAsync(invited.Account.Id));

        await mutations.RemoveOwnSignInAsync(actor, invited.Account.Id);

        Assert.Null(await accounts.GetByIdAsync(invited.Account.Id));
        Assert.Empty(await fixture.Identities.GetSessionsAsync(invited.Account.Id));
        // Mary and her place in the household stay, and the household's sign-in still opens her.
        Assert.NotNull(await fixture.Profiles.GetByIdAsync(mary.Id));
        Assert.Contains(await households.ListProfilesAsync(household.Id), person => person.Id == mary.Id);
        Assert.NotNull(await accounts.GetGrantAsync(owner.Id, mary.Id));

        // Only a person's own sign-in can be removed this way.
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.RemoveOwnSignInAsync(actor, owner.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => mutations.RemoveOwnSignInAsync(actor, invited.Account.Id));
    }

    [Fact]
    public async Task DeletingTheHouseholdsMainSignIn_HandsFollowersToTheNextOne()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, households, actor) = (fixture.Mutations, fixture.Accounts, fixture.Households, fixture.Actor);
        var owner = await fixture.CreateOwnerAsync(AccountFeatureId.Read);
        var household = (await households.GetForAccountAsync(owner.Id))!;
        var spouse = await mutations.CreateAsync(actor, new CreateAccountAccessCommand(
            "spouse@example.com", false, Assert.Single(await accounts.GetGrantsAsync(owner.Id)).ProfileId, null,
            new HashSet<AccountFeatureId> { AccountFeatureId.Watch }, new HashSet<Guid>()));
        var mary = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Mary", null, false, null));
        var given = await mutations.GiveOwnSignInAsync(actor,
            new GiveOwnSignInCommand(mary.Id, "mary@example.com", "a long temporary password"));
        Assert.Equal(owner.Id, given.Account.GrantsInheritFromAccountId);

        await mutations.DeleteAsync(fixture.Actor, owner.Id);

        var follower = (await accounts.GetByIdAsync(given.Account.Id))!;
        Assert.Equal(spouse.Id, follower.GrantsInheritFromAccountId);
        Assert.Equal(new[] { "watch" }, (await accounts.GetFeatureGrantsAsync(follower.Id)).Select(feature => feature.Value));
    }

    [Fact]
    public async Task OwnSignIn_CannotBeMadeAnAdministrator_OrOpenOthers_AndTheLastMainSignInStays()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, households, actor) = (fixture.Mutations, fixture.Accounts, fixture.Households, fixture.Actor);
        var owner = await fixture.CreateOwnerAsync(AccountFeatureId.Read);
        var household = (await households.GetForAccountAsync(owner.Id))!;
        var ownerProfileId = Assert.Single(await accounts.GetGrantsAsync(owner.Id)).ProfileId;
        var mary = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Mary", null, false, null));
        var given = (await mutations.GiveOwnSignInAsync(actor,
            new GiveOwnSignInCommand(mary.Id, "mary@example.com", "a long temporary password"))).Account;

        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.UpdateAsync(actor, given.Id,
            new UpdateAccountAccessCommand("mary@example.com", true, IsAdministrator: true)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.UpsertGrantAsync(actor, new AccountProfileGrant
        {
            AccountId = given.Id,
            ProfileId = mary.Id,
            IsEnabled = true,
            AdminEnabled = true,
            AuthorizationVersion = 1,
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.UpsertGrantAsync(actor, new AccountProfileGrant
        {
            AccountId = given.Id,
            ProfileId = ownerProfileId,
            IsEnabled = true,
            AuthorizationVersion = 1,
        }));

        // The only main sign-in cannot be deleted or disabled while Mary's follows it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.DeleteAsync(actor, owner.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.UpdateAsync(actor, owner.Id,
            new UpdateAccountAccessCommand("owner@example.com", false, false)));
        Assert.NotNull(await accounts.GetByIdAsync(owner.Id));

        await mutations.RemoveOwnSignInAsync(actor, given.Id);
        await mutations.DeleteAsync(actor, owner.Id);
        Assert.Null(await accounts.GetByIdAsync(owner.Id));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-household-people-{Guid.NewGuid():N}.db");
        private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima-household-people-{Guid.NewGuid():N}");
        private DatabaseConnection _database = null!;
        private ConfigurationDirectoryLoader _configuration = null!;

        public AccountRepository Accounts { get; private set; } = null!;
        public IdentityRepository Identities { get; private set; } = null!;
        public ProfileRepository Profiles { get; private set; } = null!;
        public HouseholdRepository Households { get; private set; } = null!;
        public FirstPartyIdentityService Identity { get; private set; } = null!;
        public AccountAccessMutationService Mutations { get; private set; } = null!;
        public RequestAuthority Actor { get; } = new(
            PrincipalKind.Human,
            true,
            AccountId: Guid.NewGuid(),
            ActiveProfileId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            AccountEnabled: true,
            GrantEnabled: true,
            AccountIsAdministrator: true,
            GrantAdminEnabled: true);

        public static Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture._database = new DatabaseConnection(fixture._databasePath);
            fixture._configuration = new ConfigurationDirectoryLoader(fixture._configPath);
            fixture._database.InitializeSchema();
            fixture.Accounts = new AccountRepository(fixture._database);
            fixture.Identities = new IdentityRepository(fixture._database);
            fixture.Profiles = new ProfileRepository(fixture._database);
            fixture.Households = new HouseholdRepository(fixture._database);
            fixture.Identity = new FirstPartyIdentityService(
                fixture.Identities,
                fixture.Accounts,
                fixture.Profiles,
                new PasswordHasher<AccountCredential>(),
                new PasswordHasher<ProfileCredential>(),
                TimeProvider.System,
                new ConfigurationAuthenticationPolicyProvider(fixture._configuration));
            fixture.Mutations = new AccountAccessMutationService(
                fixture.Accounts,
                fixture.Identities,
                fixture.Profiles,
                fixture._configuration,
                new AllowAdministratorDecisions(),
                new AllowEvaluator(),
                new PasswordHasher<GrantAdminProtection>(),
                new NoOpInvalidation(),
                new NoOpAudit(),
                fixture.Identity,
                new NoProfilePhotos(), TimeProvider.System);
            return Task.FromResult(fixture);
        }

        public Task<Account> CreateOwnerAsync(params AccountFeatureId[] features) => Mutations.CreateAsync(Actor,
            new CreateAccountAccessCommand(
                "owner@example.com",
                IsAdministrator: false,
                ProfileId: null,
                NewProfile: new NewAccountProfileCommand("Owner", "#7C4DFF"),
                Features: features.ToHashSet(),
                Libraries: new HashSet<Guid>()));

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

    private sealed class AllowAdministratorDecisions : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid libraryId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
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
