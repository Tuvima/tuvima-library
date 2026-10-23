using MediaEngine.Api.Security;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using World = MediaEngine.Api.Tests.UserDeletionTests.World;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Engine's Users, Profiles and Households actions under users, profiles and households: making a user, the new-household
/// password rule (D10), at most eight profiles per household (D14), roles (D15, D17), what a profile may use, and the email change
/// that keeps everything (D2). Real temporary data store; the same scene as <see cref="UserDeletionTests"/>.
/// </summary>
public sealed class UsersProfilesHouseholdsActionTests
{
    private const string Password = "a long temporary password";

    // ---- Making a user ----

    [Fact]
    public async Task ANewUser_AttachedToAProfileThatLostItsSignIn_LeavesTheProfileExactlyAsItWas()
    {
        await using var world = await World.CreateAsync();
        await world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Kim.Id);
        var flagsBefore = (await world.Access.GetAdminFlagsAsync(world.KimProfile.Id))!;
        var featuresBefore = (await world.Access.GetFeatureGrantsAsync(world.KimProfile.Id)).Select(feature => feature.Value).Order().ToArray();

        var created = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "kim@new.example.com", world.KimProfile.Id, null, NewUserSignInMethod.TemporaryPassword, Password));

        Assert.Equal(world.KimProfile.Id, created.User.ProfileId);
        Assert.Equal(world.KimProfile.Id, created.Profile.Id);
        Assert.Equal(world.Household.Id, created.User.HouseholdId);
        Assert.Null(created.Invitation);
        var flagsAfter = (await world.Access.GetAdminFlagsAsync(world.KimProfile.Id))!;
        Assert.Equal(flagsBefore.IsServerAdministrator, flagsAfter.IsServerAdministrator);
        Assert.Equal(flagsBefore.IsHouseholdAdministrator, flagsAfter.IsHouseholdAdministrator);
        Assert.Equal(featuresBefore, (await world.Access.GetFeatureGrantsAsync(world.KimProfile.Id)).Select(feature => feature.Value).Order().ToArray());
        Assert.NotNull(await world.Identities.GetAccountCredentialAsync(created.User.Id, AccountCredentialKind.Password));
        Assert.Contains(world.Audit.Events, e => e.EventType == "account.created" && e.SubjectId == created.User.Id.ToString("D"));
    }

    [Fact]
    public async Task ANewUser_ByInvitation_GetsALinkAndNoPassword()
    {
        await using var world = await World.CreateAsync();
        await world.Mutations.DeleteUserAsync(world.ServerAdmin, world.Kim.Id);

        var created = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "kim@new.example.com", world.KimProfile.Id, null, NewUserSignInMethod.Invitation));

        Assert.NotNull(created.Invitation);
        Assert.Equal(created.User.Id, created.Invitation.AccountId);
        Assert.Matches("^[A-Z0-9]{5}-[A-Z0-9]{5}$", created.Invitation.Code);
        Assert.Null(await world.Identities.GetAccountCredentialAsync(created.User.Id, AccountCredentialKind.Password));
        Assert.False(created.User.IsThisComputerOnly);
        Assert.True(created.User.IsEnabled);
    }

    [Fact]
    public async Task AProfileThatAlreadyHasASignIn_IsRefused_WithOnePlainSentence()
    {
        await using var world = await World.CreateAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("another@example.com", world.KimProfile.Id, null, NewUserSignInMethod.Invitation)));

        Assert.Equal("That profile already has a sign-in.", refused.Message);
        Assert.Null(await world.Accounts.GetByNormalizedEmailAsync("ANOTHER@EXAMPLE.COM"));
    }

    [Fact]
    public async Task AnEmailThatAlreadyHasASignIn_IsRefused_InAnyCase()
    {
        await using var world = await World.CreateAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("KIM@Example.com", null, new NewUserProfileCommand("Zoe", null, world.Household.Id, null),
                NewUserSignInMethod.Invitation)));

        Assert.Equal("That email already has a sign-in.", refused.Message);
        Assert.DoesNotContain(await world.Profiles.GetAllAsync(), profile => profile.DisplayName == "Zoe");
    }

    [Fact]
    public async Task MakingAUser_NeedsAnEmail_ExactlyOneProfileChoice_AndAStartingPasswordWhenThatIsTheMethod()
    {
        await using var world = await World.CreateAsync();
        var newProfile = new NewUserProfileCommand("Zoe", null, world.Household.Id, null);

        await Assert.ThrowsAsync<ArgumentException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand(null, null, newProfile, NewUserSignInMethod.Invitation)));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("zoe@example.com", null, null, NewUserSignInMethod.Invitation)));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("zoe@example.com", world.Mary.Id, newProfile, NewUserSignInMethod.Invitation)));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("zoe@example.com", null, newProfile, NewUserSignInMethod.TemporaryPassword)));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("zoe@example.com", null, newProfile, NewUserSignInMethod.Invitation, Password)));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("zoe@example.com", null, new NewUserProfileCommand("Zoe", null, null, null), NewUserSignInMethod.Invitation)));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("zoe@example.com", null, new NewUserProfileCommand("Zoe", null, world.Household.Id, "Both"), NewUserSignInMethod.Invitation)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("zoe@example.com", Guid.NewGuid(), null, NewUserSignInMethod.Invitation)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("zoe@example.com", null, new NewUserProfileCommand("Zoe", null, Guid.NewGuid(), null), NewUserSignInMethod.Invitation)));

        Assert.Null(await world.Accounts.GetByNormalizedEmailAsync("ZOE@EXAMPLE.COM"));
        Assert.DoesNotContain(await world.Profiles.GetAllAsync(), profile => profile.DisplayName == "Zoe");
    }

    [Fact]
    public async Task ANewProfile_IsAnOrdinaryMemberByDefault_WithReadWatchListenAndViewAndNoLibrary()
    {
        await using var world = await World.CreateAsync();

        var created = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "zoe@example.com", null, new NewUserProfileCommand("Zoe", null, world.Household.Id, null), NewUserSignInMethod.TemporaryPassword, Password));

        var profile = (await world.Profiles.GetByIdAsync(created.Profile.Id))!;
        Assert.Equal(ProfileRole.StandardUser, profile.Role);
        Assert.Null(profile.ContentLimit);
        Assert.Equal(world.Household.Id, profile.HouseholdId);
        Assert.False(profile.IsHouseholdAdministrator);
        Assert.False(profile.IsServerAdministrator);
        Assert.Equal(["listen", "read", "view", "watch"], (await world.Access.GetFeatureGrantsAsync(profile.Id)).Select(feature => feature.Value).Order().ToArray());
        Assert.Empty(await world.Access.GetLibraryGrantsAsync(profile.Id));
        Assert.Equal(profile.Id, created.User.ProfileId);
    }

    [Fact]
    public async Task ANewProfile_IsAKidsProfileOnlyWhenAsked_WithTheKidsContentLimit_AndNeverAHouseholdAdministrator()
    {
        await using var world = await World.CreateAsync();

        var created = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "tiny@example.com", null, new NewUserProfileCommand("Tiny", null, world.Household.Id, null, IsKids: true),
            NewUserSignInMethod.Invitation));

        var profile = (await world.Profiles.GetByIdAsync(created.Profile.Id))!;
        Assert.Equal(ProfileRole.RestrictedProfile, profile.Role);
        Assert.Equal(ProfileContentLimits.KidsDefault, profile.ContentLimit);
        Assert.False(profile.IsHouseholdAdministrator);
    }

    [Fact]
    public async Task TheOlderCreateAccountRoute_AlsoMakesAnOrdinaryMember_NotAKidsProfile()
    {
        await using var world = await World.CreateAsync();

        var account = await world.Mutations.CreateAsync(world.ServerAdmin, new CreateAccountAccessCommand(
            "older@example.com", IsAdministrator: false, ProfileId: null, NewProfile: new NewAccountProfileCommand("Older", null),
            Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read }, Libraries: new HashSet<Guid>(), TemporaryPassword: null));

        Assert.Equal(ProfileRole.StandardUser, (await world.Profiles.GetByIdAsync(account.ProfileId!.Value))!.Role);
    }

    [Fact]
    public async Task ANewProfileInANewHousehold_StartsTheHousehold_LooksAfterIt_AndHasViewOff()
    {
        await using var world = await World.CreateAsync();
        var householdsBefore = (await world.Households.ListSummariesAsync()).Count;

        var created = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "pat@example.com", null, new NewUserProfileCommand("Pat", null, null, "Household 2"), NewUserSignInMethod.TemporaryPassword, Password));

        var profile = (await world.Profiles.GetByIdAsync(created.Profile.Id))!;
        var household = (await world.Households.GetByIdAsync(profile.HouseholdId!.Value))!;
        Assert.Equal("Household 2", household.Name);
        Assert.False(household.IsDefault);
        Assert.Equal(householdsBefore + 1, (await world.Households.ListSummariesAsync()).Count);
        Assert.True(profile.IsHouseholdAdministrator);
        Assert.False(profile.IsServerAdministrator);
        Assert.Equal(["listen", "read", "watch"], (await world.Access.GetFeatureGrantsAsync(profile.Id)).Select(feature => feature.Value).Order().ToArray());
        Assert.Contains(world.Audit.Events, e => e.EventType == "household.created" && e.SubjectId == household.Id.ToString("D"));
        Assert.Contains(world.Audit.Events, e => e.EventType == "household.admin_granted");
    }

    [Fact]
    public async Task TheFirstPersonOfANewHousehold_CannotBeAKidsProfile()
    {
        await using var world = await World.CreateAsync();
        var householdsBefore = (await world.Households.ListSummariesAsync()).Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "tiny@example.com", null, new NewUserProfileCommand("Tiny", null, null, "Household 2", IsKids: true), NewUserSignInMethod.Invitation)));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "long@example.com", null, new NewUserProfileCommand("Long", null, null, new string('x', 101)), NewUserSignInMethod.Invitation)));

        Assert.Equal(householdsBefore, (await world.Households.ListSummariesAsync()).Count);
    }

    [Fact]
    public async Task OnlyAServerAdministratorOrATrustedApplication_MakesUsers()
    {
        await using var world = await World.CreateAsync();
        var command = new CreateUserCommand("zoe@example.com", null,
            new NewUserProfileCommand("Zoe", null, world.Household.Id, null), NewUserSignInMethod.Invitation);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.CreateUserAsync(world.AlexAuthority, command));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.CreateUserAsync(world.KimAuthority, command));
        Assert.Null(await world.Accounts.GetByNormalizedEmailAsync("ZOE@EXAMPLE.COM"));

        var application = world.ServerAdmin with
        {
            PrincipalKind = PrincipalKind.ServiceApplication,
            AccountId = null,
            ActiveProfileId = null,
            ActiveProfileIsServerAdministrator = false,
        };
        var created = await world.Mutations.CreateUserAsync(application, command);
        Assert.NotNull(await world.Accounts.GetByIdAsync(created.User.Id));
    }

    [Fact]
    public async Task AUserWhosePasswordIsRefused_LeavesNothingBehind_NotTheSignIn_TheProfileOrTheHousehold()
    {
        await using var world = await World.CreateAsync();
        var householdsBefore = (await world.Households.ListSummariesAsync()).Count;
        var profilesBefore = (await world.Profiles.GetAllAsync()).Count;

        await Assert.ThrowsAsync<ArgumentException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "pat@example.com", null, new NewUserProfileCommand("Pat", null, null, "Household 2"), NewUserSignInMethod.TemporaryPassword, "short")));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "zoe@example.com", null, new NewUserProfileCommand("Zoe", null, world.Household.Id, null), NewUserSignInMethod.TemporaryPassword, "short")));

        Assert.Null(await world.Accounts.GetByNormalizedEmailAsync("PAT@EXAMPLE.COM"));
        Assert.Null(await world.Accounts.GetByNormalizedEmailAsync("ZOE@EXAMPLE.COM"));
        Assert.Equal(householdsBefore, (await world.Households.ListSummariesAsync()).Count);
        Assert.Equal(profilesBefore, (await world.Profiles.GetAllAsync()).Count);
    }

    // ---- Passwords before a second household (D10) ----

    [Fact]
    public async Task AddingAHousehold_IsRefusedUntilTheServerAdministratorHasAnEmailAndPassword_ThenAllowed()
    {
        await using var world = await World.CreateAsync();
        world.StripServerAdministratorPassword();
        var householdsBefore = (await world.Households.ListSummariesAsync()).Count;
        var profilesBefore = (await world.Profiles.GetAllAsync()).Count;

        var viaUser = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("pat@example.com", null, new NewUserProfileCommand("Pat", null, null, "Household 3"), NewUserSignInMethod.Invitation)));
        var viaHousehold = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Management.CreateAsync(world.ServerAdmin, "Household 3"));
        var viaInvitation = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.IssueInvitationAsync(world.ServerAdmin,
            new IssueAccountInvitationCommand("sue@example.com", [], null, "Sue")));
        var viaOlderRoute = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.CreateAsync(world.ServerAdmin,
            new CreateAccountAccessCommand("older@example.com", IsAdministrator: false, ProfileId: null,
                NewProfile: new NewAccountProfileCommand("Older", null), Features: new HashSet<AccountFeatureId>(),
                Libraries: new HashSet<Guid>(), TemporaryPassword: null)));

        const string Sentence = "Set an email and password for your own sign-in first. A Library with several households must not be open to anyone who reaches it.";
        Assert.All(new[] { viaUser, viaHousehold, viaInvitation, viaOlderRoute }, refused => Assert.Equal(Sentence, refused.Message));
        Assert.Equal(householdsBefore, (await world.Households.ListSummariesAsync()).Count);
        Assert.Equal(profilesBefore, (await world.Profiles.GetAllAsync()).Count);
        Assert.Null(await world.Accounts.GetByNormalizedEmailAsync("PAT@EXAMPLE.COM"));

        // Once Shy sets a password, the same actions go through.
        await world.SecureServerAdministratorAsync();
        await world.Management.CreateAsync(world.ServerAdmin, "Household 3");
        await world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("pat@example.com", null, new NewUserProfileCommand("Pat", null, null, "Household 4"), NewUserSignInMethod.Invitation));
        Assert.Equal(householdsBefore + 2, (await world.Households.ListSummariesAsync()).Count);
    }

    [Fact]
    public async Task WithoutAPassword_PeopleCanStillJoinAnExistingHousehold()
    {
        await using var world = await World.CreateAsync();
        world.StripServerAdministratorPassword();

        var created = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "zoe@example.com", null, new NewUserProfileCommand("Zoe", null, world.Household.Id, null), NewUserSignInMethod.Invitation));

        Assert.Equal(world.Household.Id, created.Profile.HouseholdId);
    }

    // ---- At most eight profiles per household (D14) ----

    [Fact]
    public async Task AHouseholdOfEight_TakesNoNinthProfile_ByAnyRoute_AndNothingIsLeftBehind()
    {
        await using var world = await World.CreateAsync();
        for (var number = 1; (await world.Households.ListProfilesAsync(world.Household.Id)).Count < 8; number++)
        {
            await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
                new AddHouseholdPersonCommand(world.Household.Id, $"Extra {number}", null, false, null));
        }

        var viaInvitation = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("nine@example.com", null, new NewUserProfileCommand("Nine", null, world.Household.Id, null), NewUserSignInMethod.Invitation)));
        var viaPassword = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("ten@example.com", null, new NewUserProfileCommand("Ten", null, world.Household.Id, null), NewUserSignInMethod.TemporaryPassword, Password)));
        var viaPerson = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(world.Household.Id, "Eleven", null, false, null)));

        Assert.All(new[] { viaInvitation, viaPassword, viaPerson }, refused => Assert.Equal("A household can have up to 8 people.", refused.Message));
        Assert.Equal(8, (await world.Households.ListProfilesAsync(world.Household.Id)).Count);
        Assert.Null(await world.Accounts.GetByNormalizedEmailAsync("NINE@EXAMPLE.COM"));
        Assert.Null(await world.Accounts.GetByNormalizedEmailAsync("TEN@EXAMPLE.COM"));
    }

    // ---- Changing the email keeps everything (D2) ----

    [Fact]
    public async Task ChangingAnEmail_ChangesOnlyTheSignIn_TheProfileItsRolesAndAccessStayExactlyAsTheyWere()
    {
        await using var world = await World.CreateAsync();
        var before = (await world.Access.GetAdminFlagsAsync(world.SamProfile.Id))!;
        var features = (await world.Access.GetFeatureGrantsAsync(world.SamProfile.Id)).Select(feature => feature.Value).Order().ToArray();
        var profile = (await world.Profiles.GetByIdAsync(world.SamProfile.Id))!;

        var changed = await world.Mutations.ChangeEmailAsync(world.ServerAdmin, world.Sam.Id, "sam@new.example.com");

        Assert.Equal("sam@new.example.com", changed.Email);
        Assert.Equal(world.SamProfile.Id, changed.ProfileId);
        var after = (await world.Access.GetAdminFlagsAsync(world.SamProfile.Id))!;
        Assert.True(after.IsHouseholdAdministrator);
        Assert.Equal(before.AuthorizationVersion, after.AuthorizationVersion);
        Assert.Equal(features, (await world.Access.GetFeatureGrantsAsync(world.SamProfile.Id)).Select(feature => feature.Value).Order().ToArray());
        Assert.Equal(profile.DisplayName, (await world.Profiles.GetByIdAsync(world.SamProfile.Id))!.DisplayName);
        Assert.Equal(world.Household.Id, (await world.Profiles.GetByIdAsync(world.SamProfile.Id))!.HouseholdId);
        Assert.Null(await world.Accounts.GetByNormalizedEmailAsync("SAM@EXAMPLE.COM"));
        var taken = await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Mutations.ChangeEmailAsync(world.ServerAdmin, world.Sam.Id, "alex@example.com"));
        Assert.Equal("That email already has a sign-in.", taken.Message);
    }

    // ---- What a profile may use ----

    [Fact]
    public async Task AServerAdministrator_SetsWhatAnyProfileMayUse_AndItsVersionGoesUp()
    {
        await using var world = await World.CreateAsync();
        var before = (await world.Access.GetAdminFlagsAsync(world.KimProfile.Id))!.AuthorizationVersion;

        await world.Mutations.ReplaceProfileAccessAsync(world.ServerAdmin, world.KimProfile.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch }, new HashSet<Guid>());

        Assert.Equal(["read", "watch"], (await world.Access.GetFeatureGrantsAsync(world.KimProfile.Id)).Select(feature => feature.Value).Order().ToArray());
        Assert.True((await world.Access.GetAdminFlagsAsync(world.KimProfile.Id))!.AuthorizationVersion > before);
        Assert.Contains(world.Audit.Events, e => e.EventType == "profile.access_replaced" && e.SubjectId == world.KimProfile.Id.ToString("D"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.ReplaceProfileAccessAsync(world.ServerAdmin, Guid.NewGuid(),
            new HashSet<AccountFeatureId>(), new HashSet<Guid>()));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Mutations.ReplaceProfileAccessAsync(world.ServerAdmin, world.KimProfile.Id,
            new HashSet<AccountFeatureId>(), new HashSet<Guid> { Guid.NewGuid() }));
    }

    [Fact]
    public async Task AHouseholdAdministrator_CanHandOutOnlyWhatTheirOwnProfileHolds_AndOnlyInTheirHousehold()
    {
        await using var world = await World.CreateAsync();
        var alexProfile = await DefaultProfileAsync(world, world.Alex);
        await world.Access.ReplaceAccessAsync(alexProfile, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid>(), DateTimeOffset.UtcNow);

        await world.Mutations.ReplaceProfileAccessAsync(world.AlexAuthority, world.KimProfile.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid>());
        var tooMuch = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.ReplaceProfileAccessAsync(
            world.AlexAuthority, world.KimProfile.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch }, new HashSet<Guid>()));

        Assert.Contains("only hand out", tooMuch.Message, StringComparison.Ordinal);
        Assert.Equal(["read"], (await world.Access.GetFeatureGrantsAsync(world.KimProfile.Id)).Select(feature => feature.Value).ToArray());
        // The server administrator is not held to that ceiling.
        await world.Mutations.ReplaceProfileAccessAsync(world.ServerAdmin, world.KimProfile.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch }, new HashSet<Guid>());
    }

    [Fact]
    public async Task ProfileAccess_IsNotFoundInAnotherHousehold_ForbiddenForAMember_AndNeverOnAServerAdministratorsProfile()
    {
        await using var world = await World.CreateAsync();
        var beaProfile = await DefaultProfileAsync(world, world.Bea);
        var empty = new HashSet<AccountFeatureId>();

        var outside = await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.ReplaceProfileAccessAsync(world.AlexAuthority, beaProfile, empty, new HashSet<Guid>()));
        var missing = await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.ReplaceProfileAccessAsync(world.AlexAuthority, Guid.NewGuid(), empty, new HashSet<Guid>()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.ReplaceProfileAccessAsync(world.KimAuthority, world.Mary.Id, empty, new HashSet<Guid>()));
        var serverProfile = await DefaultProfileAsync(world, world.ServerAdminAccount);
        // A server administrator's profile is in the default household; make Alex's household that one to put it in reach.
        await world.Access.SetDefaultHouseholdAsync(world.Household.Id);
        await world.GivePasswordAsync(world.Kim.Id);
        await world.Mutations.SetProfileRolesAsync(world.ServerAdmin, world.KimProfile.Id, false, true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.ReplaceProfileAccessAsync(world.AlexAuthority, world.KimProfile.Id, empty, new HashSet<Guid>()));

        Assert.Equal(missing.Message, outside.Message);
        Assert.NotEqual(Guid.Empty, serverProfile);
    }

    // ---- Roles (D15, D17) ----

    [Fact]
    public async Task AHouseholdAdministrator_MakesAnotherProfileOfTheirHouseholdAnAdministrator_AndMayTurnItOff()
    {
        await using var world = await World.CreateAsync();

        await world.Mutations.SetProfileRolesAsync(world.AlexAuthority, world.KimProfile.Id, true, false);
        Assert.True((await world.Profiles.GetByIdAsync(world.KimProfile.Id))!.IsHouseholdAdministrator);
        await world.Mutations.SetProfileRolesAsync(world.AlexAuthority, world.SamProfile.Id, false, false);
        Assert.False((await world.Profiles.GetByIdAsync(world.SamProfile.Id))!.IsHouseholdAdministrator);

        var audit = Assert.Single(world.Audit.Events, e => e.EventType == "profile.roles_changed" && e.SubjectId == world.SamProfile.Id.ToString("D"));
        Assert.Equal("false", audit.Changes["household_administrator"]);
    }

    [Fact]
    public async Task AHouseholdAlwaysKeepsAHouseholdAdministrator_SoTheLastOneCannotBeTurnedOff()
    {
        await using var world = await World.CreateAsync(secondOwner: false);
        var alexProfile = await DefaultProfileAsync(world, world.Alex);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Mutations.SetProfileRolesAsync(world.ServerAdmin, alexProfile, false, false));

        Assert.Equal("A household needs at least one household administrator. Make another profile a household administrator first.", refused.Message);
        Assert.True((await world.Profiles.GetByIdAsync(alexProfile))!.IsHouseholdAdministrator);
    }

    [Fact]
    public async Task AHouseholdAdministrator_NeedsAUser_AndIsNeverAKidsProfile()
    {
        await using var world = await World.CreateAsync();

        var noUser = await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Mutations.SetProfileRolesAsync(world.AlexAuthority, world.Mary.Id, true, false));
        world.Run("UPDATE profiles SET role='RestrictedProfile' WHERE id=@id", new { id = world.KimProfile.Id });
        var kids = await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Mutations.SetProfileRolesAsync(world.AlexAuthority, world.KimProfile.Id, true, false));

        Assert.Equal("A household administrator needs a sign-in. Attach one to this profile first.", noUser.Message);
        Assert.Equal("A Kids profile can't look after a household.", kids.Message);
        Assert.False((await world.Profiles.GetByIdAsync(world.Mary.Id))!.IsHouseholdAdministrator);
        Assert.False((await world.Profiles.GetByIdAsync(world.KimProfile.Id))!.IsHouseholdAdministrator);
    }

    [Fact]
    public async Task OnlyAServerAdministrator_ChangesTheServerAdministratorRole()
    {
        await using var world = await World.CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => world.Mutations.SetProfileRolesAsync(world.AlexAuthority, world.SamProfile.Id, true, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => world.Mutations.SetProfileRolesAsync(world.KimAuthority, world.Mary.Id, false, false));
        var beaProfile = await DefaultProfileAsync(world, world.Bea);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => world.Mutations.SetProfileRolesAsync(world.AlexAuthority, beaProfile, false, false));

        Assert.False((await world.Profiles.GetByIdAsync(world.SamProfile.Id))!.IsServerAdministrator);
    }

    [Fact]
    public async Task AServerAdministrator_MustBeInTheDefaultHousehold_AndHaveAnEmailAndPassword()
    {
        await using var world = await World.CreateAsync();
        var defaultHousehold = (await world.Access.GetDefaultHouseholdIdAsync())!.Value;

        // Not in the Library's own household.
        var elsewhere = await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Mutations.SetProfileRolesAsync(world.ServerAdmin, world.KimProfile.Id, false, true));
        Assert.Equal("A server administrator must belong to the Library's own household.", elsewhere.Message);

        // In the default household, but the user has no password yet (an invitation not yet accepted), then with one.
        var invited = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "mom@example.com", null, new NewUserProfileCommand("Mom", null, defaultHousehold, null), NewUserSignInMethod.Invitation));
        var noPassword = await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Mutations.SetProfileRolesAsync(world.ServerAdmin, invited.Profile.Id, false, true));
        Assert.Equal(HouseholdRules.ServerAdministratorNeedsSecuredSignIn, noPassword.Message);
        var withPassword = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "dad@example.com", null, new NewUserProfileCommand("Dad", null, defaultHousehold, null), NewUserSignInMethod.TemporaryPassword, Password));
        await world.Mutations.SetProfileRolesAsync(world.ServerAdmin, withPassword.Profile.Id, false, true);

        Assert.True((await world.Profiles.GetByIdAsync(withPassword.Profile.Id))!.IsServerAdministrator);
        Assert.False((await world.Profiles.GetByIdAsync(invited.Profile.Id))!.IsServerAdministrator);
        // A profile without any user cannot be one either, and neither can a Kids profile.
        var tiny = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin, new AddHouseholdPersonCommand(defaultHousehold, "Tiny", null, true, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.SetProfileRolesAsync(world.ServerAdmin, tiny.Id, false, true));
    }

    [Fact]
    public async Task TheLastServerAdministrator_KeepsTheRole_ButAnotherOneMayGiveItUp()
    {
        await using var world = await World.CreateAsync();
        var defaultHousehold = (await world.Access.GetDefaultHouseholdIdAsync())!.Value;
        var serverProfile = await DefaultProfileAsync(world, world.ServerAdminAccount);
        var second = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "dad@example.com", null, new NewUserProfileCommand("Dad", null, defaultHousehold, null), NewUserSignInMethod.TemporaryPassword, Password));
        await world.Mutations.SetProfileRolesAsync(world.ServerAdmin, second.Profile.Id, false, true);

        await world.Mutations.SetProfileRolesAsync(world.ServerAdmin, second.Profile.Id, false, false);
        Assert.False((await world.Profiles.GetByIdAsync(second.Profile.Id))!.IsServerAdministrator);

        // Only Shy's is left: the data store itself refuses to take the role away.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Mutations.SetProfileRolesAsync(world.ServerAdmin, serverProfile, false, false));
        Assert.True((await world.Profiles.GetByIdAsync(serverProfile))!.IsServerAdministrator);
    }

    [Fact]
    public async Task AKidsProfile_CannotBeAServerAdministrator()
    {
        await using var world = await World.CreateAsync();
        var defaultHousehold = (await world.Access.GetDefaultHouseholdIdAsync())!.Value;
        var kid = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "tiny@example.com", null, new NewUserProfileCommand("Tiny", null, defaultHousehold, null, IsKids: true),
            NewUserSignInMethod.TemporaryPassword, Password));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Mutations.SetProfileRolesAsync(world.ServerAdmin, kid.Profile.Id, false, true));

        Assert.Equal("Child profiles can't be administrators.", refused.Message);
    }

    [Fact]
    public async Task ARolesRequestThatChangesNothing_IsANoOp_AndRecordsNothing()
    {
        await using var world = await World.CreateAsync();

        await world.Mutations.SetProfileRolesAsync(world.AlexAuthority, world.SamProfile.Id, true, false);

        Assert.DoesNotContain(world.Audit.Events, e => e.EventType == "profile.roles_changed");
    }

    private static async Task<Guid> DefaultProfileAsync(World world, Account account) =>
        (await world.Accounts.GetDefaultProfileIdAsync(account.Id))!.Value;
}
