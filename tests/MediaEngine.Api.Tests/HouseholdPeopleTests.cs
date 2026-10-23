using System.Reflection;
using System.Runtime.ExceptionServices;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
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
        var opened = Assert.Single(await accounts.GetHouseholdProfilesAsync(owner.Id), profile => profile.ProfileId == mary.Id);
        Assert.False(opened.IsOwn);
        Assert.False(opened.IsServerAdministrator);

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
    public async Task UserForAPerson_OpensStraightIntoThePerson_KeepsThePersonsOwnAccess_AndIsNeverAnAdministrator()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, households) = (fixture.Mutations, fixture.Accounts, fixture.Households);
        var owner = await fixture.CreateOwnerAsync(AccountFeatureId.Read, AccountFeatureId.Watch);
        // Acting as the owner's profile: new people start with what that profile holds.
        var actor = fixture.Actor with { ActiveProfileId = owner.ProfileId };
        var household = (await households.GetForAccountAsync(owner.Id))!;
        var mary = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Mary", null, true, null));
        var tom = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Tom", null, false, null));
        var before = (await fixture.Access.GetAdminFlagsAsync(mary.Id))!.AuthorizationVersion;

        var given = await mutations.CreateUserAsync(actor, new CreateUserCommand(
            "mary@example.com", mary.Id, null, NewUserSignInMethod.TemporaryPassword, "a long temporary password"));
        Assert.Null(given.Invitation);
        var maryAccount = given.User;
        Assert.Equal(household.Id, (await households.GetForAccountAsync(maryAccount.Id))!.Id);
        Assert.Equal(mary.Id, maryAccount.ProfileId);
        var flags = (await fixture.Access.GetAdminFlagsAsync(mary.Id))!;
        Assert.False(flags.IsServerAdministrator);
        Assert.False(flags.IsHouseholdAdministrator);
        Assert.True(Assert.Single(await accounts.GetHouseholdProfilesAsync(maryAccount.Id), profile => profile.IsOwn).ProfileId == mary.Id);

        // Attaching a sign-in never changes the person: Mary keeps exactly the access she started with (and her version).
        Assert.Equal(new[] { "read", "watch" }, (await fixture.Access.GetFeatureGrantsAsync(mary.Id)).Select(feature => feature.Value).Order());
        Assert.Equal(before, (await fixture.Access.GetAdminFlagsAsync(mary.Id))!.AuthorizationVersion);

        // Each profile holds its own access: changing the owner's does not reach Mary.
        await mutations.ReplaceProfileAccessAsync(actor, owner.ProfileId,
            new HashSet<AccountFeatureId> { AccountFeatureId.Listen }, new HashSet<Guid>());
        Assert.Equal(new[] { "read", "watch" }, (await fixture.Access.GetFeatureGrantsAsync(mary.Id)).Select(feature => feature.Value).Order());

        // Her access is edited on her own profile.
        await mutations.ReplaceProfileAccessAsync(actor, mary.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Listen }, new HashSet<Guid>());
        Assert.Equal(new[] { "listen" }, (await fixture.Access.GetFeatureGrantsAsync(mary.Id)).Select(feature => feature.Value));

        // An invitation works the same way and the new sign-in opens only that person.
        var invited = await mutations.CreateUserAsync(actor, new CreateUserCommand(
            "tom@example.com", tom.Id, null, NewUserSignInMethod.Invitation));
        Assert.NotNull(invited.Invitation);
        var accepted = await fixture.Identity.AcceptInvitationAsync(
            invited.Invitation.Code, "invited password", "browser", "Browser", "Dashboard");
        Assert.Equal(invited.User.Id, accepted.Account.Id);
        Assert.Equal(tom.Id, invited.User.ProfileId);

        // One user per person, and an email only ever has one sign-in.
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.CreateUserAsync(actor, new CreateUserCommand(
            "mary2@example.com", mary.Id, null, NewUserSignInMethod.Invitation)));
        var third = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Third", null, false, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.CreateUserAsync(actor, new CreateUserCommand(
            "MARY@example.com", third.Id, null, NewUserSignInMethod.Invitation)));
    }

    [Fact]
    public async Task ReplaceProfileAccess_ForAUser_ChangesOnlyThatPersonsProfile()
    {
        // A person's access lives on their profile (users, profiles and households spec D1): nothing follows the household's main sign-in.
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, households) = (fixture.Mutations, fixture.Households);
        var owner = await fixture.CreateOwnerAsync(AccountFeatureId.Read, AccountFeatureId.Watch);
        var actor = fixture.Actor with { ActiveProfileId = owner.ProfileId };
        var household = (await households.GetForAccountAsync(owner.Id))!;
        var mary = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Mary", null, false, null));
        await mutations.CreateUserAsync(actor, new CreateUserCommand(
            "mary@example.com", mary.Id, null, NewUserSignInMethod.TemporaryPassword, "a long temporary password"));

        await mutations.ReplaceProfileAccessAsync(actor, mary.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Listen }, new HashSet<Guid>());

        Assert.Equal(new[] { "listen" }, (await fixture.Access.GetFeatureGrantsAsync(mary.Id)).Select(feature => feature.Value));
        Assert.Equal(new[] { "read", "watch" }, (await fixture.Access.GetFeatureGrantsAsync(owner.ProfileId)).Select(feature => feature.Value).Order());
    }

    [Fact]
    public async Task ANewUser_StaysInThePersonsOwnHousehold()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, households, actor) = (fixture.Mutations, fixture.Households, fixture.Actor);
        var owner = await fixture.CreateOwnerAsync();
        var ownerHousehold = (await households.GetForAccountAsync(owner.Id))!;
        var guest = await mutations.CreateUserAsync(actor, new CreateUserCommand(
            "guest@example.com", null, new NewUserProfileCommand("Guest", null, null, "Guest household"), NewUserSignInMethod.Invitation));
        var guestHousehold = (await households.GetForAccountAsync(guest.User.Id))!;
        // The guest's own person has the guest's sign-in already, so a friend of theirs gets a user of their own.
        var guestFriend = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(guestHousehold.Id, "Friend", null, false, null));
        var friendSignIn = await mutations.CreateUserAsync(actor, new CreateUserCommand(
            "guest.own@example.com", guestFriend.Id, null, NewUserSignInMethod.Invitation));

        // The new sign-in lands in the person's household, not the household of whoever asked.
        Assert.Equal(guestHousehold.Id, (await households.GetForAccountAsync(friendSignIn.User.Id))!.Id);
        Assert.NotEqual(ownerHousehold.Id, (await households.GetForAccountAsync(friendSignIn.User.Id))!.Id);
    }

    [Fact]
    public async Task DeletingAUser_KeepsThePersonAndEndsTheirSessions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, households, actor) = (fixture.Mutations, fixture.Accounts, fixture.Households, fixture.Actor);
        var owner = await fixture.CreateOwnerAsync();
        var household = (await households.GetForAccountAsync(owner.Id))!;
        var mary = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Mary", null, false, null));
        var invited = await mutations.CreateUserAsync(actor, new CreateUserCommand(
            "mary@example.com", mary.Id, null, NewUserSignInMethod.Invitation));
        await fixture.Identity.AcceptInvitationAsync(
            invited.Invitation!.Code, "invited password", "phone", "Phone", "Dashboard");
        Assert.NotEmpty(await fixture.Identities.GetSessionsAsync(invited.User.Id));

        await mutations.DeleteUserAsync(actor, invited.User.Id);

        Assert.Null(await accounts.GetByIdAsync(invited.User.Id));
        Assert.Empty(await fixture.Identities.GetSessionsAsync(invited.User.Id));
        // Mary and her place in the household stay, and the household's sign-in still opens her.
        Assert.NotNull(await fixture.Profiles.GetByIdAsync(mary.Id));
        Assert.Contains(await households.ListProfilesAsync(household.Id), person => person.Id == mary.Id);
        Assert.True(await accounts.HasProfileAccessAsync(owner.Id, mary.Id));

        // Deleting it again finds nothing.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => mutations.DeleteUserAsync(actor, invited.User.Id));
    }

    [Fact]
    public async Task TheHouseholdsLastAdministratorProfile_CannotGiveUpTheRole_AndDeletingItsUserRemovesTheHousehold()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, households, actor) = (fixture.Mutations, fixture.Accounts, fixture.Households, fixture.Actor);
        var owner = await fixture.CreateOwnerAsync(AccountFeatureId.Read);
        var household = (await households.GetForAccountAsync(owner.Id))!;
        var mary = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Mary", null, false, null));
        var given = (await mutations.CreateUserAsync(actor, new CreateUserCommand(
            "mary@example.com", mary.Id, null, NewUserSignInMethod.TemporaryPassword, "a long temporary password"))).User;

        await mutations.DeleteUserAsync(actor, given.Id);
        // The household's only owner cannot give up the role (a household keeps one); deleting their sign-in removes the household.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mutations.SetProfileRolesAsync(actor, owner.ProfileId, false, isServerAdministrator: false));
        var preview = await mutations.PreviewDeleteUserAsync(actor, owner.Id);
        await mutations.DeleteUserAsync(actor, owner.Id,
            confirmedProfileIds: preview.Plan.HouseholdProfiles.Select(profile => profile.Id).ToArray());
        Assert.Null(await accounts.GetByIdAsync(owner.Id));
        Assert.Null(await households.GetByIdAsync(household.Id));
    }

    [Fact]
    public async Task CreateUser_WithAPassword_StoresATemporaryCredential()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, households, actor) = (fixture.Mutations, fixture.Accounts, fixture.Households, fixture.Actor);
        var owner = await fixture.CreateOwnerAsync();
        var household = (await households.GetForAccountAsync(owner.Id))!;
        var mary = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Mary", null, false, null));

        var given = await mutations.CreateUserAsync(actor, new CreateUserCommand(
            "mary@example.com", mary.Id, null, NewUserSignInMethod.TemporaryPassword, "a long starting password"));

        Assert.Null(given.Invitation);
        var stored = (await accounts.GetByIdAsync(given.User.Id))!;
        Assert.True(stored.MustChangePassword);
        Assert.NotNull(stored.TemporaryPasswordExpiresAt);
        Assert.NotNull(await fixture.Identities.GetAccountCredentialAsync(stored.Id, AccountCredentialKind.Password));
    }

    [Fact]
    public async Task CreateUser_AlwaysNeedsAPasswordOrAnInvitation_AndLeavesNothingBehindWhenRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, households, actor) = (fixture.Mutations, fixture.Accounts, fixture.Households, fixture.Actor);
        var owner = await fixture.CreateOwnerAsync();
        var household = (await households.GetForAccountAsync(owner.Id))!;
        var mary = await mutations.AddHouseholdPersonAsync(actor,
            new AddHouseholdPersonCommand(household.Id, "Mary", null, false, null));

        foreach (var blank in new string?[] { null, string.Empty })
        {
            var refused = await Assert.ThrowsAsync<ArgumentException>(() => mutations.CreateUserAsync(actor, new CreateUserCommand(
                "mary@example.com", mary.Id, null, NewUserSignInMethod.TemporaryPassword, blank)));
            Assert.Equal("Set a starting password.", refused.Message);
        }

        // A password and an invitation together are ambiguous, and a weak password is refused by the usual rules.
        await Assert.ThrowsAsync<ArgumentException>(() => mutations.CreateUserAsync(actor, new CreateUserCommand(
            "mary@example.com", mary.Id, null, NewUserSignInMethod.Invitation, "a long starting password")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => mutations.CreateUserAsync(actor, new CreateUserCommand(
            "mary@example.com", mary.Id, null, NewUserSignInMethod.TemporaryPassword, "short")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => mutations.CreateUserAsync(actor, new CreateUserCommand(
            "mary@example.com", mary.Id, null, NewUserSignInMethod.TemporaryPassword, "mary@example.com")));
        Assert.Null(await accounts.GetByNormalizedEmailAsync("MARY@EXAMPLE.COM"));

        // The invitation path: no password yet, a code to choose one with.
        var invited = await mutations.CreateUserAsync(actor, new CreateUserCommand(
            "mary@example.com", mary.Id, null, NewUserSignInMethod.Invitation));
        Assert.NotNull(invited.Invitation);
        Assert.Null(await fixture.Identities.GetAccountCredentialAsync(invited.User.Id, AccountCredentialKind.Password));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetTemporaryPassword_KeepsOrDropsTheMustChangeFlag(bool mustChange)
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, actor) = (fixture.Mutations, fixture.Accounts, fixture.Actor);
        var given = await fixture.CreateSignedInPersonAsync("Mary", "mary@example.com");

        await mutations.SetTemporaryPasswordAsync(actor, given.Id, "another long password", mustChange);

        var stored = (await accounts.GetByIdAsync(given.Id))!;
        Assert.Equal(mustChange, stored.MustChangePassword);
        Assert.Equal(mustChange, stored.TemporaryPasswordExpiresAt is not null);
    }

    [Fact]
    public async Task ChangeEmail_ByAServerAdministrator_NormalizesAuditsAndRefusesDuplicates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, actor) = (fixture.Mutations, fixture.Accounts, fixture.Actor);
        await fixture.CreateOwnerAsync();
        var given = await fixture.CreateSignedInPersonAsync("Mary", "mary@example.com");

        var changed = await mutations.ChangeEmailAsync(actor, given.Id, "  Mary.New@Example.com ");

        Assert.Equal("Mary.New@Example.com", changed.Email);
        Assert.Equal("MARY.NEW@EXAMPLE.COM", changed.NormalizedEmail);
        Assert.Null(await accounts.GetByNormalizedEmailAsync("MARY@EXAMPLE.COM"));
        Assert.Equal(given.Id, (await accounts.GetByNormalizedEmailAsync("MARY.NEW@EXAMPLE.COM"))!.Id);
        Assert.Contains(fixture.Audit.Events, audit => audit.EventType == "account.email_changed" && audit.SubjectId == given.Id.ToString("D"));
        // The password and the rest of the account are untouched.
        Assert.NotNull(await fixture.Identities.GetAccountCredentialAsync(given.Id, AccountCredentialKind.Password));

        // Changing to the same address in another case is still the same account, not a duplicate.
        Assert.Equal("MARY.NEW@example.com", (await mutations.ChangeEmailAsync(actor, given.Id, "MARY.NEW@example.com")).Email);

        // Another account's address is a conflict (409 at the Engine door) and nothing changes.
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.ChangeEmailAsync(actor, given.Id, "owner@example.com"));
        await Assert.ThrowsAsync<ArgumentException>(() => mutations.ChangeEmailAsync(actor, given.Id, "not an email"));
        await Assert.ThrowsAsync<ArgumentException>(() => mutations.ChangeEmailAsync(actor, given.Id, "  "));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => mutations.ChangeEmailAsync(actor, Guid.NewGuid(), "someone@example.com"));
        Assert.Equal("MARY.NEW@example.com", (await accounts.GetByIdAsync(given.Id))!.Email);
    }

    [Theory]
    [InlineData("email")]
    [InlineData("password")]
    public async Task ChangingTheEmailOrPassword_EndsPasswordResetLinksAlreadySent(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, actor) = (fixture.Mutations, fixture.Actor);
        var given = await fixture.CreateSignedInPersonAsync("Mary", "mary@example.com");
        var token = await fixture.Identity.BeginPasswordResetAsync("mary@example.com");
        Assert.NotNull(token);

        if (change == "email")
        {
            await mutations.ChangeEmailAsync(actor, given.Id, "mary.new@example.com");
        }
        else
        {
            await mutations.SetTemporaryPasswordAsync(actor, given.Id, "another long password", mustChangePassword: false);
        }

        // A link mailed to the old address (or issued before the password was set) must not still work.
        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Identity.ResetPasswordWithTokenAsync(token, "a thief chosen password"));
        Assert.Equal("The recovery information is invalid.", refused.Message);
    }

    [Fact]
    public async Task ChangeEmail_IsNeverSelfService()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.CreateOwnerAsync();
        var self = fixture.Actor with { AccountId = owner.Id };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Mutations.ChangeEmailAsync(self, owner.Id, "elsewhere@example.com"));
        Assert.Equal("owner@example.com", (await fixture.Accounts.GetByIdAsync(owner.Id))!.Email);
    }

    [Fact]
    public async Task ChangeEmail_EndsPasswordResetLinksAndTwoStepChallengesAlreadySent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, actor) = (fixture.Mutations, fixture.Accounts, fixture.Actor);
        var given = await fixture.CreateSignedInPersonAsync("Mary", "mary@example.com");
        var token = await fixture.Identity.BeginPasswordResetAsync("mary@example.com");
        Assert.NotNull(token);
        await fixture.Identities.InsertTwoStepChallengeAsync(new TwoStepChallenge
        {
            Id = Guid.NewGuid(),
            AccountId = given.Id,
            TokenHash = "two-step-hash",
            Ingress = "web",
            DeviceId = "device",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });
        Assert.NotNull(await fixture.Identities.GetActiveTwoStepChallengeAsync("two-step-hash", DateTimeOffset.UtcNow));

        var updated = await mutations.ChangeEmailAsync(actor, given.Id, "  Mary.New@Example.com ");

        Assert.Equal("Mary.New@Example.com", updated.Email);
        Assert.Equal(given.Id, (await accounts.GetByNormalizedEmailAsync("MARY.NEW@EXAMPLE.COM"))!.Id);
        // A link mailed to the old address, and a sign-in step started for it, must not still work.
        var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Identity.ResetPasswordWithTokenAsync(token, "a thief chosen password"));
        Assert.Equal("The recovery information is invalid.", refused.Message);
        Assert.Null(await fixture.Identities.GetActiveTwoStepChallengeAsync("two-step-hash", DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ChangeEmail_ToAnotherAccountsEmail_IsAConflictNotAStorageError()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, actor) = (fixture.Mutations, fixture.Accounts, fixture.Actor);
        await fixture.CreateOwnerAsync();
        var given = await fixture.CreateSignedInPersonAsync("Mary", "mary@example.com");

        // The Engine door turns InvalidOperationException into 409; a storage error would surface as 500.
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mutations.ChangeEmailAsync(actor, given.Id, "OWNER@example.com"));

        Assert.Equal("That email already has a sign-in.", conflict.Message);
        Assert.Equal("mary@example.com", (await accounts.GetByIdAsync(given.Id))!.Email);
    }

    [Fact]
    public async Task EmailChange_LosingTheRaceForTheAddress_IsTheSameConflict()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateOwnerAsync();
        var given = await fixture.CreateSignedInPersonAsync("Mary", "mary@example.com");
        // Hides the other account from the availability check, as if it took the address just after the check: only the
        // unique index (SQLite error 19) is left to refuse the write.
        var blind = DispatchProxy.Create<IAccountRepository, HidesEmailOwners>();
        ((HidesEmailOwners)(object)blind).Inner = fixture.Accounts;
        var racing = fixture.MutationsOver(blind);

        var viaChange = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            racing.ChangeEmailAsync(fixture.Actor, given.Id, "owner@example.com"));

        Assert.Equal("That email already has a sign-in.", viaChange.Message);
        Assert.Equal("mary@example.com", (await fixture.Accounts.GetByIdAsync(given.Id))!.Email);
    }

    [Fact]
    public async Task ChangeEmail_ByAnApplication_CannotChangeAnAdministratorsEmail_ButAnUnlockedAdministratorCan()
    {
        await using var fixture = await Fixture.CreateAsync();
        var (mutations, accounts, actor) = (fixture.Mutations, fixture.Accounts, fixture.Actor);
        var admin = await mutations.CreateAsync(actor, new CreateAccountAccessCommand(
            "admin@example.com", IsAdministrator: true, ProfileId: null,
            NewProfile: new NewAccountProfileCommand("Admin", "#7C4DFF"),
            Features: new HashSet<AccountFeatureId>(), HiddenLibraries: new HashSet<Guid>()));
        var application = actor with { PrincipalKind = PrincipalKind.ServiceApplication, ActiveProfileIsServerAdministrator = false };

        // A trusted application with identity.users.write could otherwise move the address and then reset the password to it.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mutations.ChangeEmailAsync(application, admin.Id, "attacker@example.com"));
        Assert.Equal("admin@example.com", (await accounts.GetByIdAsync(admin.Id))!.Email);

        var changed = await mutations.ChangeEmailAsync(actor, admin.Id, "admin.new@example.com");
        Assert.Equal("admin.new@example.com", changed.Email);
    }

    /// <summary>Forwards everything to the real repository except the email lookup, which finds no one.</summary>
    public class HidesEmailOwners : DispatchProxy
    {
        public IAccountRepository Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IAccountRepository.GetByNormalizedEmailAsync))
            {
                return Task.FromResult<Account?>(null);
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
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
        public ProfileAccessRepository Access { get; private set; } = null!;
        public FirstPartyIdentityService Identity { get; private set; } = null!;
        public AccountAccessMutationService Mutations { get; private set; } = null!;
        public RecordingAudit Audit { get; } = new();
        public RequestAuthority Actor { get; } = new(
            PrincipalKind.Human,
            true,
            AccountId: Guid.NewGuid(),
            ActiveProfileId: Guid.NewGuid(),
            SessionId: Guid.NewGuid(),
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true,
            ActiveProfileIsServerAdministrator: true);

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
            fixture.Access = new ProfileAccessRepository(fixture._database);
            fixture.Identity = new FirstPartyIdentityService(
                fixture.Identities,
                fixture.Accounts,
                fixture.Profiles,
                new PasswordHasher<AccountCredential>(),
                new PasswordHasher<ProfileCredential>(),
                TimeProvider.System,
                new ConfigurationAuthenticationPolicyProvider(fixture._configuration));
            fixture.Mutations = AccountSetupKit.Attach(new AccountAccessMutationService(
                fixture.Accounts,
                fixture.Access,
                fixture.Identities,
                fixture.Profiles,
                fixture._configuration,
                new AllowAdministratorDecisions(),
                new AllowEvaluator(),
                new NoOpInvalidation(),
                fixture.Audit,
                fixture.Identity,
                new NoProfilePhotos(), TimeProvider.System, new AlwaysUsableAdministrators(), new AuthenticationPolicyMutationGate()),
                fixture.Accounts, fixture.Access, fixture.Identity);
            return Task.FromResult(fixture);
        }

        /// <summary>A household person who has their own sign-in with the given address and a starting password.</summary>
        public async Task<Account> CreateSignedInPersonAsync(string name, string email)
        {
            var household = (await Households.GetForAccountAsync(((await Accounts.GetAllAsync()).FirstOrDefault() ?? await CreateOwnerAsync()).Id))!;
            var person = await Mutations.AddHouseholdPersonAsync(Actor,
                new AddHouseholdPersonCommand(household.Id, name, null, false, null));
            return (await Mutations.CreateUserAsync(Actor, new CreateUserCommand(
                email, person.Id, null, NewUserSignInMethod.TemporaryPassword, "a long starting password"))).User;
        }

        /// <summary>The same service over another view of the accounts, to stage a race the real repository would not allow.</summary>
        public AccountAccessMutationService MutationsOver(IAccountRepository accountRepository) => new(
            accountRepository,
            Access,
            Identities,
            Profiles,
            _configuration,
            new AllowAdministratorDecisions(),
            new AllowEvaluator(),
            new NoOpInvalidation(),
            Audit,
            Identity,
            new NoProfilePhotos(), TimeProvider.System, new AlwaysUsableAdministrators(), new AuthenticationPolicyMutationGate());

        public Task<Account> CreateOwnerAsync(params AccountFeatureId[] features) => Mutations.CreateAsync(Actor,
            new CreateAccountAccessCommand(
                "owner@example.com",
                IsAdministrator: false,
                ProfileId: null,
                NewProfile: new NewAccountProfileCommand("Owner", "#7C4DFF"),
                Features: features.ToHashSet(),
                HiddenLibraries: new HashSet<Guid>()));

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

    private sealed class RecordingAudit : IAuthorizationAuditWriter
    {
        public List<AuthorizationAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(AuthorizationAuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }
}
