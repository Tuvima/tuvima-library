using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
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
        var peer = await world.AddUserAsync(world.A, "peer@example.com");
        await mutations.SetHouseholdAdminAsync(world.ServerAdmin, peer.User.Id, true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mutations.SetTemporaryPasswordAsync(
            world.AdminOfA, peer.User.Id, "another long temporary password"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mutations.ResetTwoStepAsync(
            world.AdminOfA, peer.User.Id));

        // The server administrator's sign-in is in another household and is off limits as well.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mutations.SetTemporaryPasswordAsync(
            world.AdminOfA, world.ServerAdminAccount.Id, "another long temporary password"));

        // Their email is off limits too.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mutations.ChangeEmailAsync(
            world.AdminOfA, peer.User.Id, "taken.over@example.com"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mutations.ChangeEmailAsync(
            world.AdminOfA, world.ServerAdminAccount.Id, "taken.over@example.com"));

        // Making a user is for a server administrator, so a household administrator cannot sidestep a person's sign-in.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mutations.CreateUserAsync(world.AdminOfA,
            new CreateUserCommand("sidestep@example.com", world.A.Spare.Id, null, NewUserSignInMethod.TemporaryPassword, "a long starting password")));

        // They cannot turn on administrator access for anyone, nor touch the roles of a profile in another household.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            mutations.SetProfileRolesAsync(world.AdminOfA, world.A.Person.Id, false, isServerAdministrator: true));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            mutations.SetProfileRolesAsync(world.AdminOfA, world.B.Person.Id, true, isServerAdministrator: false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            mutations.SetProfileRolesAsync(world.MemberOfA, world.A.Person.Id, true, isServerAdministrator: false));
    }

    [Fact]
    public async Task HouseholdAdministrator_CanOnlyHandOutWhatTheHouseholdAlreadyHas()
    {
        await using var world = await World.CreateAsync();
        var mutations = world.Mutations;

        // A second adult in household A.
        var secondAdult = await world.AddUserAsync(world.A, "second.adult@example.com");
        Assert.Equal(world.A.Household.Id, (await world.Households.GetForAccountAsync(secondAdult.User.Id))!.Id);
        Assert.False((await new ProfileAccessRepository(world.Database).GetAdminFlagsAsync(secondAdult.Profile.Id))!.IsHouseholdAdministrator);

        await mutations.ReplaceProfileAccessAsync(world.ServerAdmin, secondAdult.Profile.Id, new HashSet<AccountFeatureId>(), new HashSet<Guid>());

        // The household has Read; handing out Read is fine, Watch (which the household lacks) is not.
        await mutations.ReplaceProfileAccessAsync(world.AdminOfA, secondAdult.Profile.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid>());
        Assert.Equal(new[] { "read" }, (await new ProfileAccessRepository(world.Database).GetFeatureGrantsAsync(secondAdult.Profile.Id)).Select(feature => feature.Value));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => mutations.ReplaceProfileAccessAsync(world.AdminOfA,
            secondAdult.Profile.Id, new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch }, new HashSet<Guid>()));

        // The server administrator is not held to that ceiling.
        await mutations.ReplaceProfileAccessAsync(world.ServerAdmin, secondAdult.Profile.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch }, new HashSet<Guid>());
    }

    [Fact]
    public async Task HouseholdAdministrator_CanNotShowAgainALibraryHiddenFromTheirOwnProfile_ButCanHideMore()
    {
        await using var world = await World.CreateAsync();
        var libraries = world.ConfigureCataloguedLibraries(2);
        var hiddenFromAdmin = libraries[0];
        var other = libraries[1];
        var access = new ProfileAccessRepository(world.Database);
        var read = new HashSet<AccountFeatureId> { AccountFeatureId.Read };

        // The server administrator hid a library from the household administrator's own profile.
        await world.Mutations.ReplaceProfileAccessAsync(world.ServerAdmin, world.AdminOfA.ActiveProfileId!.Value, read, new HashSet<Guid> { hiddenFromAdmin });
        var secondAdult = await world.AddUserAsync(world.A, "second.adult@example.com");
        var person = secondAdult.Profile.Id;

        // It is hidden from the second adult too. The household administrator saves a list that hides nothing: it stays hidden.
        await world.Mutations.ReplaceProfileAccessAsync(world.ServerAdmin, person, read, new HashSet<Guid> { hiddenFromAdmin });
        await world.Mutations.ReplaceProfileAccessAsync(world.AdminOfA, person, read, new HashSet<Guid>());
        Assert.Equal([hiddenFromAdmin], (await access.GetHiddenLibrariesAsync(person)).ToArray());
        Assert.True(await access.IsLibraryHiddenAsync(person, hiddenFromAdmin));

        // They can hide more, and show again a library that is visible to their own profile.
        await world.Mutations.ReplaceProfileAccessAsync(world.AdminOfA, person, read, new HashSet<Guid> { other });
        Assert.Equal(new[] { hiddenFromAdmin, other }.Order(), (await access.GetHiddenLibrariesAsync(person)).Order());
        await world.Mutations.ReplaceProfileAccessAsync(world.AdminOfA, person, read, new HashSet<Guid>());
        Assert.Equal([hiddenFromAdmin], (await access.GetHiddenLibrariesAsync(person)).ToArray());

        // A library hidden from their own profile that is still visible to the person can be hidden from them, not the other way round.
        await world.Mutations.ReplaceProfileAccessAsync(world.ServerAdmin, person, read, new HashSet<Guid>());
        await world.Mutations.ReplaceProfileAccessAsync(world.AdminOfA, person, read, new HashSet<Guid> { hiddenFromAdmin });
        Assert.Equal([hiddenFromAdmin], (await access.GetHiddenLibrariesAsync(person)).ToArray());

        // The server administrator is not held to that rule, and another household's person is untouched.
        await world.Mutations.ReplaceProfileAccessAsync(world.ServerAdmin, person, read, new HashSet<Guid>());
        Assert.Empty(await access.GetHiddenLibrariesAsync(person));
        Assert.Empty(await access.GetHiddenLibrariesAsync(world.B.Person.Id));
    }

    [Fact]
    public async Task AHouseholdAdministrator_CannotTickALibraryTheHouseholdHides_AndTheServerAdministratorIsHeldToIt()
    {
        await using var world = await World.CreateAsync();
        var libraries = world.ConfigureCataloguedLibraries(2);
        var householdHidden = libraries[0];
        var other = libraries[1];
        var access = new ProfileAccessRepository(world.Database);
        var read = new HashSet<AccountFeatureId> { AccountFeatureId.Read };
        var secondAdult = await world.AddUserAsync(world.A, "second.adult@example.com");
        var person = secondAdult.Profile.Id;
        await new HouseholdLibraryAccessRepository(world.Database).ReplaceHiddenLibrariesAsync(
            world.A.Household.Id, new HashSet<Guid> { householdHidden }, DateTimeOffset.UtcNow, null);

        // A save that leaves out the household's hidden library is an attempt to show it: refused, nothing changes.
        var refusedForHouseholdAdmin = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Mutations.ReplaceProfileAccessAsync(world.AdminOfA, person, read, new HashSet<Guid>()));
        Assert.Equal(AccountAccessMutationService.HouseholdHiddenLibraryMessage, refusedForHouseholdAdmin.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Mutations.ReplaceProfileAccessAsync(world.ServerAdmin, person, read, new HashSet<Guid> { other }));
        Assert.Empty(await access.GetHiddenLibrariesAsync(person));

        // Keeping it ticked while hiding another library works, and the household's own row is not copied onto the profile.
        await world.Mutations.ReplaceProfileAccessAsync(world.AdminOfA, person, read, new HashSet<Guid> { householdHidden, other });
        Assert.Equal([other], (await access.GetHiddenLibrariesAsync(person)).ToArray());

        // Another household is not affected by household A's list.
        await world.Mutations.ReplaceProfileAccessAsync(world.ServerAdmin, world.B.Person.Id, read, new HashSet<Guid>());
        Assert.Empty(await access.GetHiddenLibrariesAsync(world.B.Person.Id));
    }

    [Fact]
    public async Task TheHouseholdsHiddenLibraries_AreReadByTheirOwnAdministratorAndAServerAdministrator_ButReplacedByTheServerAdministratorOnly()
    {
        await using var world = await World.CreateAsync();
        var libraries = world.ConfigureCataloguedLibraries(2);
        var service = world.HouseholdLibraries();

        await service.ReplaceAsync(world.ServerAdmin, world.A.Household.Id, new HashSet<Guid> { libraries[0] });

        Assert.Equal([libraries[0]], (await service.GetAsync(world.ServerAdmin, world.A.Household.Id)).ToArray());
        Assert.Equal([libraries[0]], (await service.GetAsync(world.AdminOfA, world.A.Household.Id)).ToArray());
        Assert.Empty(await service.GetAsync(world.ServerAdmin, world.B.Household.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(world.AdminOfA, world.B.Household.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(world.MemberOfA, world.A.Household.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ReplaceAsync(world.AdminOfA, world.A.Household.Id, new HashSet<Guid>()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.ReplaceAsync(world.ServerAdmin, Guid.NewGuid(), new HashSet<Guid>()));
        Assert.Equal([libraries[0]], (await service.GetAsync(world.ServerAdmin, world.A.Household.Id)).ToArray());
    }

    [Fact]
    public async Task AHouseholdList_RefusesAnUnknownLibrary_AndAVaultLibraryIsNeverAccepted()
    {
        await using var world = await World.CreateAsync();
        var libraries = world.ConfigureCataloguedLibraries(1);
        var service = world.HouseholdLibraries();
        using (var connection = world.Database.CreateConnection())
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            connection.Execute(
                "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES(@vault,'none',@now,@now,@now);",
                new { vault = VaultLibraryId.ToString("D"), now });
        }

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReplaceAsync(world.ServerAdmin, world.A.Household.Id, new HashSet<Guid> { Guid.NewGuid() }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new HouseholdLibraryAccessRepository(world.Database).ReplaceHiddenLibrariesAsync(
                world.A.Household.Id, new HashSet<Guid> { VaultLibraryId }, DateTimeOffset.UtcNow, null));
        Assert.Empty(await service.GetAsync(world.ServerAdmin, world.A.Household.Id));

        // A library that is known works, and clearing the list shows everything again.
        await service.ReplaceAsync(world.ServerAdmin, world.A.Household.Id, new HashSet<Guid> { libraries[0] });
        await service.ReplaceAsync(world.ServerAdmin, world.A.Household.Id, new HashSet<Guid>());
        Assert.Empty(await service.GetAsync(world.ServerAdmin, world.A.Household.Id));
    }

    [Fact]
    public async Task AHouseholdsHiddenLibraries_RaiseTheAuthorizationVersionOfItsProfilesOnlyWhenTheyReallyChange()
    {
        await using var world = await World.CreateAsync();
        var libraries = world.ConfigureCataloguedLibraries(1);
        var repository = new HouseholdLibraryAccessRepository(world.Database);

        Assert.True(await repository.ReplaceHiddenLibrariesAsync(world.A.Household.Id, new HashSet<Guid> { libraries[0] }, DateTimeOffset.UtcNow, null));
        var before = Version(world, world.A.Person.Id);
        await repository.ReplaceHiddenLibrariesAsync(world.A.Household.Id, new HashSet<Guid> { libraries[0] }, DateTimeOffset.UtcNow, null);

        Assert.Equal(before, Version(world, world.A.Person.Id));
        await repository.ReplaceHiddenLibrariesAsync(world.A.Household.Id, new HashSet<Guid>(), DateTimeOffset.UtcNow, null);
        Assert.True(Version(world, world.A.Person.Id) > before);
        Assert.Equal(Version(world, world.B.Person.Id), Version(world, world.B.Person.Id));
    }

    private static readonly Guid VaultLibraryId = Guid.Parse("66666666-6666-4666-8666-666666666666");

    private static long Version(World world, Guid profileId)
    {
        using var connection = world.Database.CreateConnection();
        return connection.ExecuteScalar<long>("SELECT authorization_version FROM profiles WHERE id=@id;", new { id = profileId });
    }

    [Fact]
    public async Task AHiddenLibraryThatLeftTheConfiguration_DoesNotBlockASave_ButANewUnknownOneIsStillRefused()
    {
        await using var world = await World.CreateAsync();
        world.ConfigureCataloguedLibraries(1);
        var gone = Guid.NewGuid();
        var person = world.A.Person.Id;
        var access = new ProfileAccessRepository(world.Database);
        var read = new HashSet<AccountFeatureId> { AccountFeatureId.Read };
        await access.ReplaceAccessAsync(person, read, new HashSet<Guid> { gone }, DateTimeOffset.UtcNow);

        // Saving the list the page loaded (which still names the stale id) works, and the stale row goes.
        await world.Mutations.ReplaceProfileAccessAsync(world.ServerAdmin, person, read, new HashSet<Guid> { gone });
        Assert.Empty(await access.GetHiddenLibrariesAsync(person));

        // An id nobody stored for this profile, that is not configured, is refused.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            world.Mutations.ReplaceProfileAccessAsync(world.ServerAdmin, person, read, new HashSet<Guid> { Guid.NewGuid() }));
    }

    [Fact]
    public async Task WithNoLibrariesConfiguredAtAll_StoredHiddenLibrariesAreKept_SoTheyStayHiddenWhenTheFileComesBack()
    {
        await using var world = await World.CreateAsync();
        var hidden = Guid.NewGuid();
        var person = world.A.Person.Id;
        var access = new ProfileAccessRepository(world.Database);
        var read = new HashSet<AccountFeatureId> { AccountFeatureId.Read };
        await access.ReplaceAccessAsync(person, read, new HashSet<Guid> { hidden }, DateTimeOffset.UtcNow);

        await world.Mutations.ReplaceProfileAccessAsync(world.ServerAdmin, person, read, new HashSet<Guid> { hidden });

        Assert.Equal([hidden], await access.GetHiddenLibrariesAsync(person));
    }

    [Fact]
    public async Task OnlyAServerAdministrator_DecidesWhoLooksAfterAHousehold_ByMakingTheirProfileOne()
    {
        await using var world = await World.CreateAsync();
        var access = new ProfileAccessRepository(world.Database);
        var second = await world.AddUserAsync(world.A, "second.adult@example.com");
        Assert.False((await access.GetAdminFlagsAsync(second.Profile.Id))!.IsHouseholdAdministrator);

        await world.Mutations.SetProfileRolesAsync(world.ServerAdmin, second.Profile.Id, true, isServerAdministrator: false);
        Assert.True((await access.GetAdminFlagsAsync(second.Profile.Id))!.IsHouseholdAdministrator);

        await world.Mutations.SetProfileRolesAsync(world.ServerAdmin, second.Profile.Id, false, isServerAdministrator: false);
        Assert.False((await access.GetAdminFlagsAsync(second.Profile.Id))!.IsHouseholdAdministrator);
    }

    [Fact]
    public async Task SomeoneInvitedFromOutside_BecomesTheAdministratorOfTheirNewHousehold_AndOnlyThatOne()
    {
        await using var world = await World.CreateAsync();
        var guest = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "guest@example.com", null, new NewUserProfileCommand("Guest", null, null, "Guest household"), NewUserSignInMethod.Invitation));

        var flags = (await new ProfileAccessRepository(world.Database).GetAdminFlagsAsync(guest.Profile.Id))!;
        Assert.True(flags.IsHouseholdAdministrator);
        Assert.False(flags.IsServerAdministrator);
        var household = (await world.Households.GetForAccountAsync(guest.User.Id))!;
        Assert.NotEqual(world.A.Household.Id, household.Id);

        var guestAuthority = world.HouseholdAdminAuthority(guest.User, Guid.NewGuid(), household.Id);
        var guestPerson = Assert.Single(await world.Households.ListProfilesAsync(household.Id));
        await world.Mutations.SetProfilePinAsync(guestAuthority, guestPerson.Id, "4321");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            world.Mutations.SetProfilePinAsync(guestAuthority, world.A.Person.Id, "4321"));
    }

    [Fact]
    public async Task EveryWayOfMakingASignIn_PointsItAtItsOwnPerson()
    {
        await using var world = await World.CreateAsync();
        var mutations = world.Mutations;

        // An invitation for a new person in the household, one that starts a household, and one for an existing person.
        var spare = await mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(world.A.Id, "Another", null, false, null));
        var second = await mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "second.adult@example.com", spare.Id, null, NewUserSignInMethod.Invitation));
        var guest = await mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "guest@example.com", null, new NewUserProfileCommand("Guest", null, null, "Guest household"), NewUserSignInMethod.Invitation));
        var own = await mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "spare.own@example.com", world.A.Spare.Id, null, NewUserSignInMethod.Invitation));

        Assert.Equal(spare.Id, second.User.ProfileId);
        Assert.Equal(world.A.Spare.Id, own.User.ProfileId);
        var guestHousehold = (await world.Households.GetForAccountAsync(guest.User.Id))!;
        var guestPerson = Assert.Single(await world.Households.ListProfilesAsync(guestHousehold.Id));
        Assert.Equal(guestPerson.Id, guest.User.ProfileId);
    }

    [Fact]
    public async Task ASecondSignInOnAPersonWhoAlreadyHasOne_IsRefusedWithOnePlainSentence_ByEveryMethod()
    {
        await using var world = await World.CreateAsync();
        var mutations = world.Mutations;
        const string sentence = "That profile already has a sign-in.";

        var password = await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("again@example.com", world.A.Person.Id, null, NewUserSignInMethod.TemporaryPassword, TemporaryPassword)));
        var invite = await Assert.ThrowsAsync<InvalidOperationException>(() => mutations.CreateUserAsync(world.ServerAdmin,
            new CreateUserCommand("again.too@example.com", world.A.Person.Id, null, NewUserSignInMethod.Invitation)));

        Assert.Equal(sentence, password.Message);
        Assert.Equal(sentence, invite.Message);
        Assert.Null(await world.Accounts.GetByNormalizedEmailAsync("AGAIN@EXAMPLE.COM"));
        Assert.Null(await world.Accounts.GetByNormalizedEmailAsync("AGAIN.TOO@EXAMPLE.COM"));
    }

    [Fact]
    public async Task EveryAccessChange_IsWrittenOnTheProfile_AndRaisesItsVersion()
    {
        await using var world = await World.CreateAsync();
        var mutations = world.Mutations;
        var access = new ProfileAccessRepository(world.Database);
        var adults = await mutations.AddHouseholdPersonAsync(world.ServerAdmin, new AddHouseholdPersonCommand(world.A.Id, "Adult", null, false, null));
        await world.AddUserAsync(world.A, "second.adult@example.com", adults);
        var version = (await access.GetAdminFlagsAsync(adults.Id))!.AuthorizationVersion;

        await mutations.ReplaceProfileAccessAsync(world.ServerAdmin, adults.Id,
            new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch }, new HashSet<Guid>());
        Assert.Equal(new[] { "read", "watch" }, (await access.GetFeatureGrantsAsync(adults.Id)).Select(feature => feature.Value).Order());
        var afterAccess = (await access.GetAdminFlagsAsync(adults.Id))!.AuthorizationVersion;
        Assert.True(afterAccess > version);

        await mutations.SetProfileRolesAsync(world.ServerAdmin, adults.Id, true, isServerAdministrator: false);
        var promoted = (await access.GetAdminFlagsAsync(adults.Id))!;
        Assert.True(promoted.IsHouseholdAdministrator);
        Assert.True(promoted.AuthorizationVersion > afterAccess);

        await mutations.SetProfileRolesAsync(world.ServerAdmin, adults.Id, false, isServerAdministrator: false);
        Assert.False((await access.GetAdminFlagsAsync(adults.Id))!.IsHouseholdAdministrator);

        // The person who owns the household gets the same: their profile is the household administrator.
        var ownerProfile = world.A.Owner.ProfileId;
        Assert.True((await access.GetAdminFlagsAsync(ownerProfile))!.IsHouseholdAdministrator);

        // Each profile holds its own access: changing the household owner's does not reach the person with their own sign-in.
        var personBefore = (await access.GetFeatureGrantsAsync(world.A.Person.Id)).Select(feature => feature.Value).Order().ToArray();
        await mutations.ReplaceProfileAccessAsync(world.ServerAdmin, ownerProfile,
            new HashSet<AccountFeatureId> { AccountFeatureId.Listen }, new HashSet<Guid>());
        Assert.Equal(new[] { "listen" }, (await access.GetFeatureGrantsAsync(ownerProfile)).Select(feature => feature.Value));
        Assert.Equal(personBefore, (await access.GetFeatureGrantsAsync(world.A.Person.Id)).Select(feature => feature.Value).Order());
    }

    [Fact]
    public async Task TheServerAdministratorRole_CanOnlyBeGivenToAPersonInTheLibrarysOwnHousehold()
    {
        await using var world = await World.CreateAsync();
        var access = new ProfileAccessRepository(world.Database);

        // Household A is not the Library's own household, so none of its people can be a server administrator.
        var adult = await world.AddPersonAsync(world.A);
        await world.AddUserAsync(world.A, "elsewhere@example.com", adult);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.SetProfileRolesAsync(
            world.ServerAdmin, adult.Id, false, isServerAdministrator: true));
        Assert.Equal("A server administrator must belong to the Library's own household.", refused.Message);
        Assert.False((await access.GetAdminFlagsAsync(adult.Id))!.IsServerAdministrator);

        var spare = await world.AddPersonAsync(world.A);
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.SetProfileRolesAsync(
            world.ServerAdmin, spare.Id, false, isServerAdministrator: true));
        Assert.False((await access.GetAdminFlagsAsync(spare.Id))!.IsServerAdministrator);

        // The Library's own household takes one.
        var home = (await world.Households.GetForAccountAsync(world.ServerAdminAccount.Id))!;
        var partner = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(home.Id, "Partner", null, false, null));
        await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "partner@example.com", partner.Id, null, NewUserSignInMethod.TemporaryPassword, TemporaryPassword));
        await world.Mutations.SetProfileRolesAsync(world.ServerAdmin, partner.Id, false, isServerAdministrator: true);
        Assert.True((await access.GetAdminFlagsAsync(partner.Id))!.IsServerAdministrator);
    }

    [Fact]
    public async Task APersonAdded_StartsWithTheAccessOfTheActorsPerson_OrOfTheHouseholdAdministratorForAnApp_AndNeverAnAdministrator()
    {
        await using var world = await World.CreateAsync();
        var mutations = world.Mutations;
        var access = new ProfileAccessRepository(world.Database);
        var ownerProfile = world.A.Owner.ProfileId;
        await access.ReplaceAccessAsync(ownerProfile, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid>(), DateTimeOffset.UtcNow);

        // A household administrator adds a person: they start with what the administrator's own person holds.
        var byAdmin = await mutations.AddHouseholdPersonAsync(world.AdminOfA, new AddHouseholdPersonCommand(world.A.Id, "By admin", null, false, null));
        Assert.Equal(new[] { "read" }, (await access.GetFeatureGrantsAsync(byAdmin.Id)).Select(feature => feature.Value));

        // An app has no person: the household's first household administrator is used.
        var app = world.ServerAdmin with { PrincipalKind = PrincipalKind.ServiceApplication, AccountId = null, ActiveProfileId = null };
        var byApp = await mutations.AddHouseholdPersonAsync(app, new AddHouseholdPersonCommand(world.A.Id, "By app", null, false, null));
        Assert.Equal(new[] { "read" }, (await access.GetFeatureGrantsAsync(byApp.Id)).Select(feature => feature.Value));

        // A server administrator from another household: the same features, never View, and no libraries.
        var byServer = await mutations.AddHouseholdPersonAsync(world.ServerAdmin, new AddHouseholdPersonCommand(world.A.Id, "By server", null, false, null));
        Assert.Equal(new[] { "read", "watch" }, (await access.GetFeatureGrantsAsync(byServer.Id)).Select(feature => feature.Value).Order());
        Assert.Empty(await access.GetHiddenLibrariesAsync(byServer.Id));

        foreach (var added in new[] { byAdmin, byApp, byServer })
        {
            var flags = (await access.GetAdminFlagsAsync(added.Id))!;
            Assert.False(flags.IsServerAdministrator);
            Assert.False(flags.IsHouseholdAdministrator);
        }
    }

    [Fact]
    public async Task TheDefaultHousehold_IsTheServerAdministratorsOwn_AndCannotBeRemoved_WhileOthersCanBeWhenEmpty()
    {
        await using var world = await World.CreateAsync();
        var households = new HouseholdManagementService(world.Households, world.Accounts, world.Decisions, new AllowEvaluator(), new NoOpAudit(), TimeProvider.System);
        var serverHousehold = (await world.Households.GetForAccountAsync(world.ServerAdminAccount.Id))!;
        Assert.True(serverHousehold.IsDefault);
        Assert.False(world.A.Household.IsDefault);
        Assert.False(world.B.Household.IsDefault);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => households.DeleteAsync(world.ServerAdmin, serverHousehold.Id));
        Assert.Equal("This is the Library's own household and can't be removed.", refused.Message);
        Assert.NotNull(await world.Households.GetByIdAsync(serverHousehold.Id));

        var extra = await households.CreateAsync(world.ServerAdmin, "Extra");
        Assert.False(extra.Household.IsDefault);
        await households.DeleteAsync(world.ServerAdmin, extra.Household.Id);
        Assert.Null(await world.Households.GetByIdAsync(extra.Household.Id));
    }

    [Fact]
    public async Task HouseholdAdministrator_NeedsTheAdministratorUnlock_WhenTheirPinProtectionIsOn()
    {
        await using var world = await World.CreateAsync();

        world.Decisions.HouseholdLocked = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.SetProfilePinAsync(
            world.AdminOfA, world.A.Person.Id, "4321"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.AddHouseholdPersonAsync(
            world.AdminOfA, new AddHouseholdPersonCommand(world.A.Id, "Locked out", null, false, null)));

        world.Decisions.HouseholdLocked = false;
        await world.Mutations.SetProfilePinAsync(world.AdminOfA, world.A.Person.Id, "4321");
    }

    [Fact]
    public async Task HouseholdAdministrator_CanProtectOnlyTheirOwnAdministratorAccess()
    {
        await using var world = await World.CreateAsync();
        var ownerProfile = world.A.Owner.ProfileId;
        await world.Mutations.SetProfilePinAsync(world.AdminOfA, ownerProfile, "123456");
        var command = new ProfileAdminProtectionCommand(true, AdminUnlockMode.FixedDuration, 30);

        await world.Mutations.SetProfileAdminProtectionAsync(world.AdminOfA, ownerProfile, command);
        var protection = await new ProfileAccessRepository(world.Database).GetAdminProtectionAsync(ownerProfile);
        Assert.True(protection!.IsEnabled);

        // Not another household's, not the server administrator's, not a plain member's.
        // Another household's person looks missing, so it cannot be probed.
        var otherProfile = world.B.Owner.ProfileId;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.SetProfileAdminProtectionAsync(
            world.AdminOfA, otherProfile, command));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.SetProfileAdminProtectionAsync(
            world.MemberOfA, ownerProfile, command));
    }

    [Fact]
    public async Task HouseholdAdministrator_CannotChangeAnotherAdministratorsPinProtection_ThoughTheServerAdministratorCanChangeAnyones()
    {
        await using var world = await World.CreateAsync();
        var access = new ProfileAccessRepository(world.Database);
        var mom = world.AdminOfA;
        var momProfile = world.A.Owner.ProfileId;
        var shyProfile = world.ServerAdmin.ActiveProfileId!.Value;

        // Dad is a second household administrator in Mom's household, with protection on.
        var dad = await world.AddUserAsync(world.A, "dad@example.com", world.A.Spare);
        await world.Mutations.SetHouseholdAdminAsync(world.ServerAdmin, dad.User.Id, true);
        var on = new ProfileAdminProtectionCommand(true, AdminUnlockMode.FixedDuration, 30);
        var off = new ProfileAdminProtectionCommand(false, AdminUnlockMode.FixedDuration, 30);
        await world.Mutations.SetProfilePinAsync(world.ServerAdmin, world.A.Spare.Id, "123456");
        await world.Mutations.SetProfileAdminProtectionAsync(world.ServerAdmin, world.A.Spare.Id, on);
        await world.Mutations.SetProfilePinAsync(world.ServerAdmin, shyProfile, "654321");
        await world.Mutations.SetProfileAdminProtectionAsync(world.ServerAdmin, shyProfile, on);

        // Mom cannot turn off Dad's, nor Shy's (which is in another household and looks missing to her).
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.SetProfileAdminProtectionAsync(
            mom, world.A.Spare.Id, off));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Mutations.SetProfileAdminProtectionAsync(
            mom, shyProfile, off));
        Assert.True((await access.GetAdminProtectionAsync(world.A.Spare.Id))!.IsEnabled);
        Assert.True((await access.GetAdminProtectionAsync(shyProfile))!.IsEnabled);

        // Mom can change her own.
        await world.Mutations.SetProfilePinAsync(world.ServerAdmin, momProfile, "111222");
        await world.Mutations.SetProfileAdminProtectionAsync(mom, momProfile, on);
        Assert.True((await access.GetAdminProtectionAsync(momProfile))!.IsEnabled);
        await world.Mutations.SetProfileAdminProtectionAsync(mom, momProfile, off);
        Assert.False((await access.GetAdminProtectionAsync(momProfile))!.IsEnabled);

        // The server administrator can change anyone's, Mom's included.
        await world.Mutations.SetProfileAdminProtectionAsync(world.ServerAdmin, momProfile, on);
        Assert.True((await access.GetAdminProtectionAsync(momProfile))!.IsEnabled);
        await world.Mutations.SetProfileAdminProtectionAsync(world.ServerAdmin, world.A.Spare.Id, off);
        Assert.False((await access.GetAdminProtectionAsync(world.A.Spare.Id))!.IsEnabled);
    }

    [Fact]
    public async Task ASignInGivenToAPersonWithLeftoverAdministratorRoles_NeverBringsThoseRolesBack_ByPasswordOrInvitation()
    {
        await using var world = await World.CreateAsync();
        var access = new ProfileAccessRepository(world.Database);
        var home = (await world.Households.GetForAccountAsync(world.ServerAdminAccount.Id))!;

        async Task<Profile> PersonWithLeftoverRolesAsync(Place place)
        {
            var person = await world.AddPersonAsync(place);
            using var connection = world.Database.CreateConnection();
            connection.Execute("UPDATE profiles SET is_server_admin = 1, is_household_admin = 1 WHERE id = @id;", new { id = person.Id });
            return person;
        }

        async Task AssertNoRolesAsync(Guid profileId)
        {
            var flags = (await access.GetAdminFlagsAsync(profileId))!;
            Assert.False(flags.IsServerAdministrator);
            Assert.False(flags.IsHouseholdAdministrator);
        }

        // A sign-in with a starting password.
        var created = await PersonWithLeftoverRolesAsync(world.A);
        await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "created@example.com", created.Id, null, NewUserSignInMethod.TemporaryPassword, TemporaryPassword));
        await AssertNoRolesAsync(created.Id);

        // An invitation.
        var invited = await PersonWithLeftoverRolesAsync(world.A);
        await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "invited@example.com", invited.Id, null, NewUserSignInMethod.Invitation));
        await AssertNoRolesAsync(invited.Id);

        // A sign-in made by the Library's setup for a person with leftover roles: exactly the chosen role, and no household administrator role.
        var partner = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(home.Id, "Partner", null, false, null));
        using (var connection = world.Database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET is_household_admin = 1 WHERE id = @id;", new { id = partner.Id });
        }

        await world.Mutations.CreateAsync(world.ServerAdmin, new CreateAccountAccessCommand(
            "partner@example.com", IsAdministrator: true, ProfileId: partner.Id, NewProfile: null,
            Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read }, HiddenLibraries: new HashSet<Guid>(),
            TemporaryPassword: TemporaryPassword));
        var partnerFlags = (await access.GetAdminFlagsAsync(partner.Id))!;
        Assert.True(partnerFlags.IsServerAdministrator);
        Assert.False(partnerFlags.IsHouseholdAdministrator);
    }

    [Fact]
    public async Task ASignInsHousehold_IsItsProfilesHousehold_ForScoping()
    {
        await using var world = await World.CreateAsync();
        var owner = (await world.Accounts.GetByIdAsync(world.A.Owner.Id))!;

        Assert.Equal(world.A.Id, (await world.Households.GetForAccountAsync(owner.Id))!.Id);
        Assert.Contains(owner.ProfileId, await world.Accounts.GetProfileIdsAsync(owner.Id));

        // Household A's administrator looks after household A, and household B is out of reach.
        await world.Mutations.SetProfilePinAsync(world.AdminOfA, world.A.Person.Id, "4321");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            world.Mutations.SetProfilePinAsync(world.AdminOfA, world.B.Person.Id, "4321"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            world.Mutations.SetTemporaryPasswordAsync(world.AdminOfA, world.B.Owner.Id, "another long temporary password"));
    }

    [Fact]
    public async Task AKidsProfile_CannotBeMadeAHouseholdAdministrator()
    {
        await using var world = await World.CreateAsync();
        // A household's main sign-in whose person is a Kids profile (and not yet a household administrator).
        var main = await world.Mutations.CreateAsync(world.ServerAdmin, new CreateAccountAccessCommand(
            "kids.main@example.com", IsAdministrator: false, ProfileId: null,
            NewProfile: new NewAccountProfileCommand("Little One", "#445566"),
            Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read }, HiddenLibraries: new HashSet<Guid>()));
        var kidsProfile = main.ProfileId;
        using (var connection = world.Database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET role = 'RestrictedProfile', is_household_admin = 0 WHERE id = @id;", new { id = kidsProfile });
        }

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Mutations.SetProfileRolesAsync(world.ServerAdmin, kidsProfile, true, isServerAdministrator: false));

        Assert.Contains("Kids profile", refused.Message, StringComparison.Ordinal);
        var flags = (await new ProfileAccessRepository(world.Database).GetAdminFlagsAsync(kidsProfile))!;
        Assert.False(flags.IsHouseholdAdministrator);
    }

    [Fact]
    public async Task AdminPagesPin_CannotBeTurnedOn_WithoutAProfilePin_ButCanAlwaysBeTurnedOff()
    {
        await using var world = await World.CreateAsync();
        var ownerProfile = world.A.Owner.ProfileId;

        // No PIN on the profile: the switch cannot go on (there is nothing to ask for), and nothing is saved.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Mutations.SetProfileAdminProtectionAsync(
            world.AdminOfA, ownerProfile, new ProfileAdminProtectionCommand(true, AdminUnlockMode.FixedDuration, 30)));
        Assert.Contains("Set a PIN on this profile first", refused.Message, StringComparison.Ordinal);
        Assert.False((await new ProfileAccessRepository(world.Database).GetAdminProtectionAsync(ownerProfile))?.IsEnabled ?? false);

        // Off needs no PIN at all, so administrator functions keep working for a profile that never set one.
        await world.Mutations.SetProfileAdminProtectionAsync(
            world.AdminOfA, ownerProfile, new ProfileAdminProtectionCommand(false, AdminUnlockMode.FixedDuration, 30));
        Assert.False((await new ProfileAccessRepository(world.Database).GetAdminProtectionAsync(ownerProfile))!.IsEnabled);
    }

    [Fact]
    public async Task OtherHouseholdsCannotBeProbed_ThroughRolesOnMissingOrForeignPeople()
    {
        await using var world = await World.CreateAsync();

        // A person who does not exist and a person in another household get the same answer.
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            world.Mutations.SetProfileRolesAsync(world.AdminOfA, Guid.NewGuid(), true, isServerAdministrator: false));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            world.Mutations.SetProfileRolesAsync(world.AdminOfA, world.B.Person.Id, true, isServerAdministrator: false));
    }

    [Fact]
    public async Task HouseholdAdministrator_CannotChangeAnotherAdministratorsOwnPerson()
    {
        await using var world = await World.CreateAsync();
        var peer = await world.AddUserAsync(world.A, "peer@example.com", world.A.Spare);
        await world.Mutations.SetHouseholdAdminAsync(world.ServerAdmin, peer.User.Id, true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            world.Mutations.SetProfilePinAsync(world.AdminOfA, world.A.Spare.Id, "4321"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Mutations.UpdateProfileAsync(
            world.AdminOfA, world.A.Spare.Id, new UpdateManagedProfileCommand("Renamed", null)));
        // Deleting a fellow household administrator's profile is allowed (D7); UserDeletionTests covers it.

        // Everyone else in the household is still theirs to look after.
        await world.Mutations.SetProfilePinAsync(world.AdminOfA, world.A.Person.Id, "4321");
    }

    [Fact]
    public async Task RemovingAPerson_PassesThePhotoChoiceOn_KeepingPhotosByDefault()
    {
        await using var world = await World.CreateAsync();
        var first = await world.Mutations.AddHouseholdPersonAsync(world.AdminOfA,
            new AddHouseholdPersonCommand(world.A.Id, "Temp one", null, false, null));
        var second = await world.Mutations.AddHouseholdPersonAsync(world.AdminOfA,
            new AddHouseholdPersonCommand(world.A.Id, "Temp two", null, false, null));

        await world.Mutations.DeleteProfileAsync(world.AdminOfA, first.Id);
        await world.Mutations.DeleteProfileAsync(world.AdminOfA, second.Id, ProfilePhotoDisposition.Delete);

        Assert.Equal(2, world.Photos.Prepared.Count);
        Assert.Equal(first.Id, world.Photos.Prepared[0].ProfileId);
        Assert.Equal(ProfilePhotoDisposition.MoveToShared, world.Photos.Prepared[0].Photos);
        Assert.Equal(second.Id, world.Photos.Prepared[1].ProfileId);
        Assert.Equal(ProfilePhotoDisposition.Delete, world.Photos.Prepared[1].Photos);
    }

    [Fact]
    public async Task APersonWhoCannotBeRemoved_KeepsTheirPhotos()
    {
        await using var world = await World.CreateAsync();

        // The server administrator's only profile is also the last administrator: the removal rules refuse it.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Mutations.DeleteProfileAsync(world.ServerAdmin, world.ServerAdmin.ActiveProfileId!.Value));

        Assert.Empty(world.Photos.Prepared);
    }

    [Fact]
    public async Task HouseholdAdministrator_CannotChangeOrRemoveAPersonOfAnotherHousehold()
    {
        await using var world = await World.CreateAsync();

        await world.Mutations.RequireCanChangeProfileAsync(world.AdminOfA, world.A.Person.Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            world.Mutations.RequireCanChangeProfileAsync(world.AdminOfA, world.B.Person.Id));
        // Another household's person looks missing, so it cannot be probed.
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            world.Mutations.DeleteProfileAsync(world.AdminOfA, world.B.Person.Id, ProfilePhotoDisposition.Delete));

        Assert.Empty(world.Photos.Prepared);
    }

    [Fact]
    public async Task TheFirstServerAdministrator_JoinsTheHouseholdThatAlreadyHoldsTheSharedLibrary()
    {
        await using var world = await World.CreateAsync(sharedLibraryFirst: true);

        var shared = await new ViewSharedLibraryRepository(world.Database).GetAsync();
        Assert.Equal(shared.HouseholdId, (await world.Households.GetForAccountAsync(world.ServerAdminAccount.Id))!.Id);
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

    [Fact]
    public async Task TheHouseholdKidSettings_StartOnPgWithUnratedShown_AndAreChangedByTheirOwnAdministratorOrAServerAdministratorOnly()
    {
        await using var world = await World.CreateAsync();
        var service = world.KidSettings();

        var start = await service.GetAsync(world.AdminOfA, world.A.Household.Id);
        Assert.Equal(HouseholdKidSettings.Default, start);

        await service.ReplaceAsync(world.AdminOfA, world.A.Household.Id, new HouseholdKidSettings("G", "pg-13", "R", false));
        Assert.Equal(new HouseholdKidSettings("G", "PG-13", "R", false), await service.GetAsync(world.ServerAdmin, world.A.Household.Id));

        // Another household keeps its own settings, and an administrator never reaches another household's.
        Assert.Equal(HouseholdKidSettings.Default, await service.GetAsync(world.ServerAdmin, world.B.Household.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(world.AdminOfA, world.B.Household.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ReplaceAsync(world.AdminOfA, world.B.Household.Id, HouseholdKidSettings.Strictest));

        // A member never reads or changes the levels, and nothing changed through a refused call.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(world.MemberOfA, world.A.Household.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ReplaceAsync(world.MemberOfA, world.A.Household.Id, HouseholdKidSettings.Strictest));
        Assert.Equal(new HouseholdKidSettings("G", "PG-13", "R", false), await service.GetAsync(world.ServerAdmin, world.A.Household.Id));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetAsync(world.ServerAdmin, Guid.NewGuid()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.ReplaceAsync(world.ServerAdmin, Guid.NewGuid(), HouseholdKidSettings.Default));
    }

    [Theory]
    [InlineData("NC-17", "PG", "PG")]
    [InlineData("PG", "", "PG")]
    [InlineData("PG", "PG", "everything")]
    [InlineData("PG", null, "PG")]
    public async Task AHouseholdKidLevel_MustBeOneOfGPgPg13OrR_ForEveryArea(string? watch, string? read, string? listen)
    {
        await using var world = await World.CreateAsync();
        var service = world.KidSettings();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReplaceAsync(world.ServerAdmin, world.A.Household.Id, new HouseholdKidSettings(watch!, read!, listen!, true)));

        Assert.Equal(HouseholdKidSettings.Default, await service.GetAsync(world.ServerAdmin, world.A.Household.Id));
    }

    [Fact]
    public async Task ChangingTheHouseholdKidSettings_UpdatesEveryKidsProfileOfThatHouseholdAtOnce_AndNoOtherHousehold()
    {
        await using var world = await World.CreateAsync();
        var service = world.KidSettings();
        var first = await world.Mutations.AddHouseholdPersonAsync(world.AdminOfA,
            new AddHouseholdPersonCommand(world.A.Household.Id, "Kid one", null, IsChild: true, Pin: null));
        var second = await world.Mutations.AddHouseholdPersonAsync(world.AdminOfA,
            new AddHouseholdPersonCommand(world.A.Household.Id, "Kid two", null, IsChild: true, Pin: null));
        var elsewhere = await world.Mutations.AddHouseholdPersonAsync(world.AdminOfB,
            new AddHouseholdPersonCommand(world.B.Household.Id, "Kid three", null, IsChild: true, Pin: null));
        var limits = new ProfileContentLimitRepository(world.Database);

        foreach (var kid in new[] { first, second, elsewhere })
        {
            Assert.Equal(new ProfileContentLimit(true, HouseholdKidSettings.Default), await limits.GetAsync(kid.Id));
        }

        await service.ReplaceAsync(world.AdminOfA, world.A.Household.Id, new HouseholdKidSettings("R", "G", "PG-13", false));

        var changed = new ProfileContentLimit(true, new HouseholdKidSettings("R", "G", "PG-13", false));
        Assert.Equal(changed, await limits.GetAsync(first.Id));
        Assert.Equal(changed, await limits.GetAsync(second.Id));
        Assert.Equal(new ProfileContentLimit(true, HouseholdKidSettings.Default), await limits.GetAsync(elsewhere.Id));

        // A member of the household who is not a Kid's profile has no limit whatever the levels say.
        Assert.True((await limits.GetAsync(world.A.Person.Id)).IsUnrestricted);
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

        public RealAdministratorDecisions Decisions { get; } = new();
        public NoProfilePhotos Photos { get; } = new();
        public DatabaseConnection Database => _database;

        public HouseholdKidSettingsService KidSettings() => new(
            new HouseholdKidSettingsRepository(_database), Decisions, new AllowEvaluator(), new NoOpAudit(), TimeProvider.System);

        public HouseholdLibraryAccessService HouseholdLibraries() => new(
            Households, new HouseholdLibraryAccessRepository(_database), new FixedVaultLibraries(), _configuration,
            Decisions, new AllowEvaluator(), new NoOpAudit(), TimeProvider.System);

        /// <summary>Writes a libraries file with catalogued libraries (all in the Read area), so a save has a configuration to check hidden ids against.</summary>
        public IReadOnlyList<Guid> ConfigureCataloguedLibraries(int count)
        {
            var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
            _configuration.SaveLibraries(new LibrariesConfiguration
            {
                StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Library data", Path = _configPath, AllowWrite = true }],
                ViewStorage = new ViewStorageConfig { StorageLocationId = "media", RelativeRoot = "View" },
                // The configuration allows one library of each of these kinds, so each library gets its own (all in the Read area).
                Libraries = ids.Select((id, index) => new LibraryFolderConfig
                {
                    Id = id.ToString("D"),
                    Name = index == 0 ? "Books" : "Comics",
                    Category = index == 0 ? "Books" : "Comics",
                    Kind = LibraryKinds.Catalogued,
                    KindKey = index == 0 ? "books" : "comics",
                    MediaTypes = [index == 0 ? "Books" : "Comics"],
                    Area = LibraryAreas.Read,
                }).ToList(),
            });
            return ids;
        }

        public static async Task<World> CreateAsync(bool sharedLibraryFirst = false)
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
            world.Mutations = AccountSetupKit.Attach(new AccountAccessMutationService(
                world.Accounts,
                        new ProfileAccessRepository(world._database),
                identities,
                profiles,
                world._configuration,
                world.Decisions,
                new AllowEvaluator(),
                new NoOpInvalidation(),
                new NoOpAudit(),
                identity,
                world.Photos,
                TimeProvider.System, new AlwaysUsableAdministrators(), new AuthenticationPolicyMutationGate(), new FixedVaultLibraries(),
                householdLibraries: new HouseholdLibraryAccessRepository(world._database)),
                world.Accounts, new ProfileAccessRepository(world._database), identity);

            if (sharedLibraryFirst)
            {
                // The Shared library exists (and so does its household) before the first account.
                await new ViewSharedLibraryRepository(world._database).GetAsync();
            }

            // A bootstrap actor that is a server administrator, used to set the scene.
            var bootstrap = ServerAuthority(Guid.NewGuid(), Guid.NewGuid());
            world.ServerAdminAccount = await world.Mutations.CreateAsync(bootstrap, new CreateAccountAccessCommand(
                "server@example.com", IsAdministrator: true, ProfileId: null,
                NewProfile: new NewAccountProfileCommand("Server", "#7C4DFF"),
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch },
                HiddenLibraries: new HashSet<Guid>(), TemporaryPassword: TemporaryPassword));
            await identities.GiveAsync(world.ServerAdminAccount.Id);
            var serverProfile = world.ServerAdminAccount.ProfileId;
            world.ServerAdmin = ServerAuthority(world.ServerAdminAccount.Id, serverProfile);

            world.A = await world.CreateHouseholdAsync("a@example.com", "Alex");
            world.B = await world.CreateHouseholdAsync("b@example.com", "Bea");

            var ownerProfileA = world.A.Owner.ProfileId;
            var ownerProfileB = world.B.Owner.ProfileId;
            world.AdminOfA = world.HouseholdAdminAuthority(world.A.Owner, ownerProfileA, world.A.Id);
            world.AdminOfB = world.HouseholdAdminAuthority(world.B.Owner, ownerProfileB, world.B.Id);
            world.MemberOfA = world.HouseholdAdminAuthority(world.A.OwnSignIn, world.A.Person.Id, world.A.Id, isHouseholdAdmin: false);
            world.RestrictedAdminOfA = world.HouseholdAdminAuthority(world.A.Owner, ownerProfileA, world.A.Id, restricted: true);
            return world;
        }

        public RequestAuthority HouseholdAdminAuthority(Account account, Guid profileId, Guid householdId, bool isHouseholdAdmin = true, bool restricted = false) =>
            new(PrincipalKind.Human, true,
                AccountId: account.Id,
                ActiveProfileId: profileId,
                SessionId: Guid.NewGuid(),
                AccountEnabled: true,
                ActiveProfileInUserHousehold: true,
                ActiveProfileIsRestricted: restricted,
                ActiveProfileHouseholdId: householdId,
                ActiveProfileIsHouseholdAdministrator: isHouseholdAdmin);

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
                catch (KeyNotFoundException)
                {
                    // Another household's person looks missing to a household administrator: not theirs to remove.
                    throw new UnauthorizedAccessException("Another household's person.");
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
            new("set a temporary password", (actor, place) =>
                Mutations.SetTemporaryPasswordAsync(actor, place.OwnSignIn.Id, "another long temporary password")),
            new("reset two-step codes", (actor, place) => Mutations.ResetTwoStepAsync(actor, place.OwnSignIn.Id)),
            new("change a sign-in's email", (actor, place) => Mutations.ChangeEmailAsync(actor, place.OwnSignIn.Id, $"changed{Next()}@example.com")),
            new("set an ordinary password", (actor, place) =>
                Mutations.SetTemporaryPasswordAsync(actor, place.OwnSignIn.Id, "another long ordinary password", mustChangePassword: false)),
        ];

        /// <summary>A new person in the place with no sign-in yet (a user opens a profile of its own, so a second adult needs one).</summary>
        public Task<Profile> AddPersonAsync(Place place) =>
            Mutations.AddHouseholdPersonAsync(ServerAdmin, new AddHouseholdPersonCommand(place.Id, $"Extra {Next()}", null, false, null));

        /// <summary>A new user in the place for a person (a new one when none is given), made the way the Engine makes one.</summary>
        public async Task<CreatedUser> AddUserAsync(Place place, string email, Profile? person = null)
        {
            person ??= await AddPersonAsync(place);
            return await Mutations.CreateUserAsync(ServerAdmin, new CreateUserCommand(
                email, person.Id, null, NewUserSignInMethod.TemporaryPassword, TemporaryPassword));
        }

        private string Next() => (++_counter).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);

        private async Task<Place> CreateHouseholdAsync(string email, string personName)
        {
            // The household's main sign-in: it becomes the household administrator by being the first.
            var owner = await Mutations.CreateAsync(ServerAdmin, new CreateAccountAccessCommand(
                email, IsAdministrator: false, ProfileId: null,
                NewProfile: new NewAccountProfileCommand(personName, "#112233"),
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read },
                HiddenLibraries: new HashSet<Guid>(), TemporaryPassword: null));
            var household = (await Households.GetForAccountAsync(owner.Id))!;
            var person = await Mutations.AddHouseholdPersonAsync(ServerAdmin,
                new AddHouseholdPersonCommand(household.Id, $"{personName}'s friend", null, false, null));
            var spare = await Mutations.AddHouseholdPersonAsync(ServerAdmin,
                new AddHouseholdPersonCommand(household.Id, $"{personName}'s spare", null, false, null));
            var given = await Mutations.CreateUserAsync(ServerAdmin, new CreateUserCommand(
                $"friend.{email}", person.Id, null, NewUserSignInMethod.TemporaryPassword, TemporaryPassword));
            return new Place(household, (await Accounts.GetByIdAsync(owner.Id))!, person, given.User, spare);
        }

        private static RequestAuthority ServerAuthority(Guid accountId, Guid profileId) =>
            new(PrincipalKind.Human, true,
                AccountId: accountId,
                ActiveProfileId: profileId,
                SessionId: Guid.NewGuid(),
                AccountEnabled: true,
                ActiveProfileInUserHousehold: true,
                ActiveProfileIsServerAdministrator: true);

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

    /// <summary>Allows exactly what the real rule allows for a server administrator: an effective one.</summary>
    private sealed class RealAdministratorDecisions : IAccountAccessDecisionService
    {
        /// <summary>When true, a household administrator who has turned on their PIN has not unlocked it.</summary>
        public bool HouseholdLocked { get; set; }

        public ValueTask<AuthorizationDecision> EvaluateHouseholdAdministratorAsync(RequestAuthority authority,
            bool requireSurfaceUnlock, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(!authority.IsEffectiveHouseholdAdministrator
                ? AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired)
                : HouseholdLocked && requireSurfaceUnlock
                    ? AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorUnlockRequired)
                    : AuthorizationDecision.Allow());

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
