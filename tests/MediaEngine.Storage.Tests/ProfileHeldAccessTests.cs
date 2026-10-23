using System.Security.Cryptography;
using System.Text;
using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Users, profiles and households wave UPH1b: the profile holds the access and every sign-in points at its own profile. A new person's
/// profile is set up from the sign-in's creation, an existing person is never changed by getting a sign-in, and the sign-in's own rows
/// are never what a decision reads.
/// </summary>
public sealed class ProfileHeldAccessTests : IDisposable
{
    private const string AlreadyHasSignIn = "That profile already has a sign-in.";

    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly AccountRepository _accounts;
    private readonly HouseholdRepository _households;
    private readonly ProfileAccessRepository _access;
    private readonly DateTimeOffset _now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    public ProfileHeldAccessTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_profile_mirror_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _accounts = new AccountRepository(_database);
        _households = new HouseholdRepository(_database);
        _access = new ProfileAccessRepository(_database);
    }

    // ── Every user points at its own profile ───────────────────────────────────

    [Fact]
    public async Task ANewSignInWithANewPerson_OpensThatPerson_AndTheProfileHoldsItsAccess()
    {
        var person = NewProfile("Sam");
        var account = NewAccount("sam@example.com", administrator: false);

        await _accounts.CreateAccountAsync(account, Grant(account.Id, person.Id, isDefault: true),
            new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch }, new HashSet<Guid>(), newProfile: person);

        Assert.Equal(person.Id, (await _accounts.GetByIdAsync(account.Id))!.ProfileId);
        Assert.Equal(new[] { "read", "watch" }, (await _access.GetFeatureGrantsAsync(person.Id)).Select(feature => feature.Value).Order());
    }

    [Fact]
    public async Task ASignInOpeningAnExistingPersonWithNoSignIn_OpensThatPerson()
    {
        var admin = await CreateServerAdministratorAsync();
        var mary = await AddHouseholdPersonAsync(admin, "Mary");
        var account = NewAccount("mary@example.com", administrator: false, householdId: mary.HouseholdId);

        await _accounts.CreateAccountAsync(account, Grant(account.Id, mary.Id, isDefault: true),
            new HashSet<AccountFeatureId> { AccountFeatureId.Listen }, new HashSet<Guid>());

        Assert.Equal(mary.Id, (await _accounts.GetByIdAsync(account.Id))!.ProfileId);
    }

    [Fact]
    public async Task ASignInAttachedToAnExistingPersonWhoseAccessIsToBePreserved_ChangesNothingAboutThem()
    {
        var admin = await CreateServerAdministratorAsync();
        var mary = await AddHouseholdPersonAsync(admin, "Mary");
        await _access.ReplaceAccessAsync(mary.Id, new HashSet<AccountFeatureId> { AccountFeatureId.Listen }, new HashSet<Guid>(), _now);
        var before = (await _access.GetAdminFlagsAsync(mary.Id))!.AuthorizationVersion;
        var account = NewAccount("mary@example.com", administrator: true, householdId: mary.HouseholdId);

        await _accounts.CreateAccountAsync(account, Grant(account.Id, mary.Id, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(), new HashSet<Guid> { Guid.NewGuid() }, preserveProfileAccess: true);

        Assert.Equal(mary.Id, (await _accounts.GetByIdAsync(account.Id))!.ProfileId);
        Assert.Equal(new[] { "listen" }, (await _access.GetFeatureGrantsAsync(mary.Id)).Select(feature => feature.Value));
        Assert.Empty(await _access.GetLibraryGrantsAsync(mary.Id));
        var flags = (await _access.GetAdminFlagsAsync(mary.Id))!;
        Assert.False(flags.IsServerAdministrator);
        Assert.False(flags.IsHouseholdAdministrator);
        Assert.Equal(before, flags.AuthorizationVersion);
        Assert.False((await _accounts.GetByIdAsync(account.Id))!.IsAdministrator);
    }

    [Fact]
    public async Task AnInvitationAttachedToAnExistingPersonWhoseAccessIsToBePreserved_ChangesNothingAboutThem()
    {
        var admin = await CreateServerAdministratorAsync();
        var mary = await AddHouseholdPersonAsync(admin, "Mary");
        await _access.ReplaceAccessAsync(mary.Id, new HashSet<AccountFeatureId> { AccountFeatureId.Watch }, new HashSet<Guid>(), _now);
        var invited = NewAccount("mary@example.com", administrator: false, householdId: mary.HouseholdId);

        await _accounts.CreateInvitedAccountAsync(invited, [Grant(invited.Id, mary.Id, isDefault: true)], NewInvitation(invited.Id),
            preserveProfileAccess: true);

        Assert.Equal(mary.Id, (await _accounts.GetByIdAsync(invited.Id))!.ProfileId);
        Assert.Equal(new[] { "watch" }, (await _access.GetFeatureGrantsAsync(mary.Id)).Select(feature => feature.Value));
    }

    [Fact]
    public async Task ASecondSignInOnAPersonWhoAlreadyHasOne_IsRefused_AndNothingIsWritten()
    {
        var admin = await CreateServerAdministratorAsync();
        var second = NewAccount("second@example.com", administrator: false);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.CreateAccountAsync(
            second, Grant(second.Id, Profile.SeedProfileId, isDefault: true), new HashSet<AccountFeatureId>(), new HashSet<Guid>()));

        Assert.Equal(AlreadyHasSignIn, refused.Message);
        Assert.Null(await _accounts.GetByIdAsync(second.Id));
        Assert.Equal(Profile.SeedProfileId, (await _accounts.GetByIdAsync(admin.Id))!.ProfileId);
    }

    [Fact]
    public async Task AnInvitationOnAPersonWhoAlreadyHasASignIn_IsRefused_AndInvitingANewPersonOpensThatPerson()
    {
        var admin = await CreateServerAdministratorAsync();
        var guest = NewAccount("guest@example.com", administrator: false);
        var guestPerson = NewProfile("Guest");
        await _accounts.CreateInvitedAccountAsync(guest, [Grant(guest.Id, guestPerson.Id, isDefault: true)], NewInvitation(guest.Id),
            newProfile: guestPerson);
        Assert.Equal(guestPerson.Id, (await _accounts.GetByIdAsync(guest.Id))!.ProfileId);

        var late = NewAccount("late@example.com", administrator: false);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.CreateInvitedAccountAsync(
            late, [Grant(late.Id, Profile.SeedProfileId, isDefault: true)], NewInvitation(late.Id)));

        Assert.Equal(AlreadyHasSignIn, refused.Message);
        Assert.Null(await _accounts.GetByIdAsync(late.Id));
        Assert.Equal(Profile.SeedProfileId, (await _accounts.GetByIdAsync(admin.Id))!.ProfileId);
    }

    [Fact]
    public async Task AnInvitationForSeveralPeople_OpensTheDefaultOne()
    {
        var admin = await CreateServerAdministratorAsync();
        var mary = await AddHouseholdPersonAsync(admin, "Mary");
        var tom = await AddHouseholdPersonAsync(admin, "Tom");
        var invited = NewAccount("both@example.com", administrator: false);

        await _accounts.CreateInvitedAccountAsync(invited,
            [Grant(invited.Id, mary.Id), Grant(invited.Id, tom.Id, isDefault: true)], NewInvitation(invited.Id));

        Assert.Equal(tom.Id, (await _accounts.GetByIdAsync(invited.Id))!.ProfileId);
    }

    [Fact]
    public async Task APersonsOwnSignIn_OpensThatPerson_AndTheirProfileHoldsTheMainSignInsAccess()
    {
        var admin = await CreateServerAdministratorAsync();
        var main = NewAccount("main@example.com", administrator: false, householdId: (await _accounts.GetByIdAsync(admin.Id))!.HouseholdId);
        var mainPerson = NewProfile("Main");
        await _accounts.CreateAccountAsync(main, Grant(main.Id, mainPerson.Id, isDefault: true),
            new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid>(), newProfile: mainPerson);
        var mary = await AddHouseholdPersonAsync(admin, "Mary");
        var own = NewAccount("mary.own@example.com", administrator: false, householdId: mary.HouseholdId);
        own.GrantsInheritFromAccountId = main.Id;

        await _accounts.CreateAccountAsync(own, Grant(own.Id, mary.Id, isDefault: true), new HashSet<AccountFeatureId>(), new HashSet<Guid>());

        Assert.Equal(mary.Id, (await _accounts.GetByIdAsync(own.Id))!.ProfileId);
        Assert.Equal(new[] { "read" }, (await _access.GetFeatureGrantsAsync(mary.Id)).Select(feature => feature.Value));
    }

    // ── Access lives on the profile ────────────────────────────────────────────

    [Fact]
    public async Task ReplacingAnAccountsOwnAccessRows_NeverChangesWhatTheProfileHolds()
    {
        var (_, person, account) = await CreateMemberAsync();
        var before = (await _access.GetAdminFlagsAsync(person.Id))!.AuthorizationVersion;

        await _accounts.ReplaceAccountAccessAsync(account.Id, new HashSet<AccountFeatureId> { AccountFeatureId.Watch },
            new HashSet<Guid> { Guid.NewGuid() }, _now.AddMinutes(1));

        Assert.Equal(new[] { "read" }, (await _access.GetFeatureGrantsAsync(person.Id)).Select(feature => feature.Value));
        Assert.Empty(await _access.GetLibraryGrantsAsync(person.Id));
        Assert.Equal(before, (await _access.GetAdminFlagsAsync(person.Id))!.AuthorizationVersion);
    }

    [Fact]
    public async Task ChangingTheMainSignInsAccessRows_DoesNotReachThePeopleWhoseOwnSignInFollowedIt()
    {
        var admin = await CreateServerAdministratorAsync();
        var (main, mainPerson) = await CreateMainSignInAsync(admin, "main@example.com");
        var mary = await AddHouseholdPersonAsync(admin, "Mary");
        await _access.ReplaceAccessAsync(mary.Id, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid>(), _now);
        var own = NewAccount("mary.own@example.com", administrator: false, householdId: mary.HouseholdId);
        own.GrantsInheritFromAccountId = main.Id;
        await _accounts.CreateAccountAsync(own, Grant(own.Id, mary.Id, isDefault: true), new HashSet<AccountFeatureId>(), new HashSet<Guid>(),
            preserveProfileAccess: true);

        await _accounts.ReplaceAccountAccessAsync(main.Id, new HashSet<AccountFeatureId> { AccountFeatureId.Listen },
            new HashSet<Guid>(), _now.AddMinutes(1));

        Assert.Equal(new[] { "read" }, (await _access.GetFeatureGrantsAsync(mary.Id)).Select(feature => feature.Value));
        Assert.Equal(new[] { "read" }, (await _access.GetFeatureGrantsAsync(mainPerson.Id)).Select(feature => feature.Value));
    }

    [Fact]
    public async Task TheHouseholdAdministratorRole_IsSetOnTheProfile_AndNeedsAUserAttached()
    {
        var (_, person, _) = await CreateMemberAsync();
        var admin = await FindServerAdministratorAsync();
        var noUser = await AddHouseholdPersonAsync(admin, "Nobody");
        // A second household administrator, so the first can be turned off (a household always keeps one).
        var second = NewAccount("second@example.com", administrator: false, householdId: person.HouseholdId);
        var secondPerson = NewProfile("Second");
        await _accounts.CreateAccountAsync(second, Grant(second.Id, secondPerson.Id, isDefault: true),
            new HashSet<AccountFeatureId>(), new HashSet<Guid>(), newProfile: secondPerson);
        await _access.SetHouseholdAdministratorAsync(secondPerson.Id, true, _now);
        await _access.SetHouseholdAdministratorAsync(person.Id, false, _now);
        var before = (await _access.GetAdminFlagsAsync(person.Id))!.AuthorizationVersion;

        await _access.SetHouseholdAdministratorAsync(person.Id, true, _now.AddMinutes(1));
        var promoted = (await _access.GetAdminFlagsAsync(person.Id))!;
        await _access.SetHouseholdAdministratorAsync(person.Id, false, _now.AddMinutes(2));
        var demoted = (await _access.GetAdminFlagsAsync(person.Id))!;
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _access.SetHouseholdAdministratorAsync(noUser.Id, true, _now));

        Assert.True(promoted.IsHouseholdAdministrator);
        Assert.False(promoted.IsServerAdministrator);
        Assert.True(promoted.AuthorizationVersion > before);
        Assert.False(demoted.IsHouseholdAdministrator);
        Assert.True(demoted.AuthorizationVersion > promoted.AuthorizationVersion);
        Assert.Contains("needs a sign-in", refused.Message, StringComparison.Ordinal);
        Assert.False((await _access.GetAdminFlagsAsync(noUser.Id))!.IsHouseholdAdministrator);
    }

    [Fact]
    public async Task TheLastHouseholdAdministratorRole_CannotBeTurnedOff()
    {
        var (_, person, _) = await CreateMemberAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _access.SetHouseholdAdministratorAsync(person.Id, false, _now.AddMinutes(1)));

        Assert.Equal(UserRemovalMessages.LastHouseholdAdministrator, refused.Message);
        Assert.True((await _access.GetAdminFlagsAsync(person.Id))!.IsHouseholdAdministrator);
    }

    [Fact]
    public async Task TheHouseholdsFirstMainSignIn_BecomesItsHouseholdAdministratorOnTheProfileToo()
    {
        var (_, person, _) = await CreateMemberAsync();

        Assert.True((await _access.GetAdminFlagsAsync(person.Id))!.IsHouseholdAdministrator);
    }

    [Fact]
    public async Task TheServerAdministratorRole_IsSetOnTheProfile_AndTheLastUsableOneCannotBeTakenAway()
    {
        var admin = await CreateServerAdministratorAsync();
        await GiveCredentialAsync(admin);
        var (member, memberPerson) = await CreateMainSignInAsync(admin, "member@example.com");
        await GiveCredentialAsync(member);
        Assert.False((await _access.GetAdminFlagsAsync(memberPerson.Id))!.IsServerAdministrator);
        var before = (await _access.GetAdminFlagsAsync(memberPerson.Id))!.AuthorizationVersion;

        // The role is written on the profile; saving or granting the sign-in's own rows never turns it on.
        member.IsAdministrator = true;
        member.UpdatedAt = _now.AddMinutes(1);
        await _accounts.UpdateAccountAsync(member);
        await _accounts.UpsertGrantAsync(Grant(member.Id, memberPerson.Id, isDefault: true, administrator: false));
        Assert.False((await _access.GetAdminFlagsAsync(memberPerson.Id))!.IsServerAdministrator);

        Assert.True(await _access.SetServerAdministratorAsync(memberPerson.Id, true, _now.AddMinutes(2)));
        Assert.True((await _access.GetAdminFlagsAsync(memberPerson.Id))!.IsServerAdministrator);
        Assert.True((await _access.GetAdminFlagsAsync(memberPerson.Id))!.AuthorizationVersion > before);
        Assert.True((await _accounts.GetByIdAsync(member.Id))!.IsAdministrator);

        // Either of two usable administrators can go; the last one cannot.
        Assert.True(await _access.SetServerAdministratorAsync(memberPerson.Id, false, _now.AddMinutes(3)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _access.SetServerAdministratorAsync(Profile.SeedProfileId, false, _now.AddMinutes(4)));
        Assert.True((await _access.GetAdminFlagsAsync(Profile.SeedProfileId))!.IsServerAdministrator);
        Assert.False(await _access.SetServerAdministratorAsync(Guid.NewGuid(), true, _now));
    }

    [Fact]
    public async Task TheFirstServerAdministratorsProfile_IsAServerAdministrator_AndItsHouseholdIsTheOnlyDefault()
    {
        var admin = await CreateServerAdministratorAsync();

        var flags = (await _access.GetAdminFlagsAsync(Profile.SeedProfileId))!;
        Assert.True(flags.IsServerAdministrator);
        var household = (await _households.GetForAccountAsync(admin.Id))!;
        Assert.True(household.IsDefault);
        Assert.Equal(household.Id, await _access.GetDefaultHouseholdIdAsync());
        using var connection = _database.CreateConnection();
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM households WHERE is_default = 1;"));
    }

    [Fact]
    public async Task ASecondServerAdministratorInAnotherHousehold_IsRefused_AndNothingIsWritten()
    {
        await CreateServerAdministratorAsync();
        var second = NewAccount("second.admin@example.com", administrator: true);
        var secondPerson = NewProfile("Second");

        // D17: a server administrator profile lives in the Library's own household.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.CreateAccountAsync(
            second, Grant(second.Id, secondPerson.Id, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(), new HashSet<Guid>(), newProfile: secondPerson));

        Assert.Equal("A server administrator must belong to the Library's own household.", refused.Message);
        Assert.Null(await _accounts.GetByIdAsync(second.Id));
        using var connection = _database.CreateConnection();
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM households WHERE is_default = 1;"));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles WHERE display_name = 'Second';"));
    }

    [Fact]
    public async Task TheServerAdministratorRole_IsRefusedByTheProfileRepository_ForAPersonOutsideTheDefaultHousehold()
    {
        var (_, person, _) = await CreateMemberAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _access.SetServerAdministratorAsync(person.Id, true, _now));

        Assert.False((await _access.GetAdminFlagsAsync(person.Id))!.IsServerAdministrator);
    }

    [Fact]
    public async Task ASignInAttachedToAPersonWithLeftoverRoles_DoesNotBringThoseRolesBack()
    {
        var admin = await CreateServerAdministratorAsync();
        var mary = await AddHouseholdPersonAsync(admin, "Mary");
        await _access.ReplaceAccessAsync(mary.Id, new HashSet<AccountFeatureId> { AccountFeatureId.Listen }, new HashSet<Guid>(), _now);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET is_server_admin = 1, is_household_admin = 1 WHERE id = @id;", new { id = mary.Id });
        }

        var account = NewAccount("mary@example.com", administrator: false, householdId: mary.HouseholdId);
        await _accounts.CreateAccountAsync(account, Grant(account.Id, mary.Id, isDefault: true),
            new HashSet<AccountFeatureId>(), new HashSet<Guid>(), preserveProfileAccess: true);

        var flags = (await _access.GetAdminFlagsAsync(mary.Id))!;
        Assert.False(flags.IsServerAdministrator);
        Assert.False(flags.IsHouseholdAdministrator);
        Assert.Equal(new[] { "listen" }, (await _access.GetFeatureGrantsAsync(mary.Id)).Select(feature => feature.Value));
    }

    [Fact]
    public async Task APersonWhoLosesTheirLastSignIn_LosesTheirAdministratorRoles()
    {
        // A household administrator in a household of their own.
        var (admin, person, member) = await CreateMemberAsync();
        Assert.True((await _access.GetAdminFlagsAsync(person.Id))!.IsHouseholdAdministrator);
        await _accounts.DeleteAccountAsync(member.Id);
        Assert.False((await _access.GetAdminFlagsAsync(person.Id))!.IsHouseholdAdministrator);

        // A second server administrator, in the Library's own household, while the first one stays and can still sign in.
        await GiveCredentialAsync(admin);
        var (second, secondPerson) = await CreateMainSignInAsync(admin, "second.admin@example.com");
        await _access.SetServerAdministratorAsync(secondPerson.Id, true, _now);
        Assert.True((await _access.GetAdminFlagsAsync(secondPerson.Id))!.IsServerAdministrator);
        await _accounts.DeleteAccountAsync(second.Id);
        Assert.False((await _access.GetAdminFlagsAsync(secondPerson.Id))!.IsServerAdministrator);
        Assert.True((await _access.GetAdminFlagsAsync(Profile.SeedProfileId))!.IsServerAdministrator);
    }

    [Fact]
    public async Task TheOnlyServerAdministratorProfile_CannotLoseItsSignIn_EvenWhenThatSignInIsOff()
    {
        var admin = await CreateServerAdministratorAsync();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE accounts SET is_enabled = 0 WHERE id = @id;", new { id = admin.Id });
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => _accounts.DeleteAccountAsync(admin.Id));

        Assert.True((await _access.GetAdminFlagsAsync(Profile.SeedProfileId))!.IsServerAdministrator);
        Assert.NotNull(await _accounts.GetByIdAsync(admin.Id));
    }

    [Fact]
    public async Task ASignInsHousehold_IsTheHouseholdOfItsOwnProfile_WhateverItsOwnHouseholdColumnSays()
    {
        var (admin, person, member) = await CreateMemberAsync();
        var adminHousehold = (await _accounts.GetByIdAsync(admin.Id))!.HouseholdId;
        var memberHousehold = (await _accounts.GetByIdAsync(member.Id))!.HouseholdId;
        Assert.NotEqual(adminHousehold, memberHousehold);

        // Make the column disagree with the profile.
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE accounts SET household_id = @wrong WHERE id = @id;", new { wrong = adminHousehold, id = member.Id });
        }

        var read = (await _accounts.GetByIdAsync(member.Id))!;
        Assert.Equal(memberHousehold, read.HouseholdId);
        Assert.Equal(person.Id, read.ProfileId);
        Assert.True(read.HouseholdAdmin, "the sign-in of a household administrator profile is itself protected");
        Assert.Equal(memberHousehold, (await _households.GetForAccountAsync(member.Id))!.Id);
        Assert.Contains(member.Id, (await _households.ListAccountsAsync(memberHousehold!.Value)).Select(account => account.Id));
        Assert.DoesNotContain(member.Id, (await _households.ListAccountsAsync(adminHousehold!.Value)).Select(account => account.Id));
        Assert.True(read.MaySwitchInto(memberHousehold));
        Assert.False(read.MaySwitchInto(adminHousehold));
    }

    [Fact]
    public async Task TheSignInsOwnChanges_ThroughTheOtherAccountWrites_LeaveTheProfileAsItWas()
    {
        var admin = await CreateServerAdministratorAsync();
        var (member, memberPerson) = await CreateMainSignInAsync(admin, "member@example.com");

        // Granting and revoking another person, and the account update the identity service makes, leave the profile as it was.
        var other = await AddHouseholdPersonAsync(admin, "Other");
        await _accounts.GrantProfileAsync(Grant(member.Id, other.Id));
        await _accounts.RevokeProfileAsync(member.Id, other.Id);
        await _accounts.UpsertGrantAsync(Grant(member.Id, other.Id));
        await _accounts.RevokeGrantAsync(member.Id, other.Id);
        member.UpdatedAt = _now.AddMinutes(1);
        Assert.True(await _accounts.UpdateAsync(member));

        Assert.Equal(new[] { "read" }, (await _access.GetFeatureGrantsAsync(memberPerson.Id)).Select(feature => feature.Value));
    }

    [Fact]
    public async Task DeletingTheMainSignIn_HandsTheFollowLinkOn_ButEveryonesProfileKeepsItsOwnAccess()
    {
        var admin = await CreateServerAdministratorAsync();
        var householdId = (await _accounts.GetByIdAsync(admin.Id))!.HouseholdId;
        var (first, firstPerson) = await CreateMainSignInAsync(admin, "first@example.com");
        var second = NewAccount("second@example.com", administrator: false, householdId: householdId);
        var secondPerson = NewProfile("Second");
        await _accounts.CreateAccountAsync(second, Grant(second.Id, secondPerson.Id, isDefault: true),
            new HashSet<AccountFeatureId> { AccountFeatureId.Watch }, new HashSet<Guid>(), newProfile: secondPerson);
        var mary = await AddHouseholdPersonAsync(admin, "Mary");
        await _access.ReplaceAccessAsync(mary.Id, new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid>(), _now);
        var own = NewAccount("mary.own@example.com", administrator: false, householdId: householdId);
        own.GrantsInheritFromAccountId = first.Id;
        await _accounts.CreateAccountAsync(own, Grant(own.Id, mary.Id, isDefault: true), new HashSet<AccountFeatureId>(), new HashSet<Guid>(),
            preserveProfileAccess: true);

        await _accounts.DeleteAccountAsync(first.Id);

        // The household's next main sign-in in line is the oldest one: the administrator's.
        Assert.Equal(admin.Id, (await _accounts.GetByIdAsync(own.Id))!.GrantsInheritFromAccountId);
        Assert.Equal(new[] { "read" }, (await _access.GetFeatureGrantsAsync(mary.Id)).Select(feature => feature.Value));
        Assert.Equal(new[] { "watch" }, (await _access.GetFeatureGrantsAsync(secondPerson.Id)).Select(feature => feature.Value));
        Assert.NotNull(await _accounts.GetByIdAsync(second.Id));
        Assert.NotNull(await _access.GetAdminFlagsAsync(firstPerson.Id));
    }

    // ── New people without a sign-in ───────────────────────────────────────────

    [Fact]
    public async Task APersonAddedToAHousehold_StartsWithTheAccessOfWhoeverAddedThem_AndNeverAnAdministratorRole()
    {
        var admin = await CreateServerAdministratorAsync();
        var householdId = (await _accounts.GetByIdAsync(admin.Id))!.HouseholdId!.Value;
        var (main, mainPerson) = await CreateMainSignInAsync(admin, "main@example.com");
        var library = Guid.NewGuid();
        await _access.ReplaceAccessAsync(mainPerson.Id, new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.View },
            new HashSet<Guid> { library }, _now.AddMinutes(1));
        await _access.SetHouseholdAdministratorAsync(mainPerson.Id, true, _now.AddMinutes(1));
        var mary = NewProfile("Mary");

        await _accounts.CreateHouseholdPersonAsync(mary, householdId, [Grant(main.Id, mary.Id)], accessFromProfileId: mainPerson.Id);

        Assert.Equal(new[] { "read", "view" }, (await _access.GetFeatureGrantsAsync(mary.Id)).Select(feature => feature.Value).Order());
        Assert.Equal(new[] { library }, (await _access.GetLibraryGrantsAsync(mary.Id)).ToArray());
        var flags = (await _access.GetAdminFlagsAsync(mary.Id))!;
        Assert.False(flags.IsServerAdministrator);
        Assert.False(flags.IsHouseholdAdministrator);
    }

    [Fact]
    public async Task APersonAddedByAServerAdministratorFromAnotherHousehold_StartsWithViewOffAndNoLibrary()
    {
        var admin = await CreateServerAdministratorAsync();
        var guest = NewAccount("guest@example.com", administrator: false);
        var guestPerson = NewProfile("Guest");
        await _accounts.CreateInvitedAccountAsync(guest, [Grant(guest.Id, guestPerson.Id, isDefault: true)], NewInvitation(guest.Id),
            newProfile: guestPerson);
        var guestHousehold = (await _accounts.GetByIdAsync(guest.Id))!.HouseholdId!.Value;
        var library = Guid.NewGuid();
        await _access.ReplaceAccessAsync(Profile.SeedProfileId, AccountFeatureId.All.ToHashSet(), new HashSet<Guid> { library }, _now);
        var friend = NewProfile("Friend");

        await _accounts.CreateHouseholdPersonAsync(friend, guestHousehold, [Grant(guest.Id, friend.Id)], accessFromProfileId: Profile.SeedProfileId);

        Assert.Equal(new[] { "listen", "read", "watch" }, (await _access.GetFeatureGrantsAsync(friend.Id)).Select(feature => feature.Value).Order());
        Assert.Empty(await _access.GetLibraryGrantsAsync(friend.Id));
        Assert.False((await _access.GetAdminFlagsAsync(friend.Id))!.IsServerAdministrator);
        Assert.Equal(Profile.SeedProfileId, (await _accounts.GetByIdAsync(admin.Id))!.ProfileId);
    }

    [Fact]
    public async Task APersonAddedWithNoAdderProfile_StartsWithEveryFeatureAndNoLibrary()
    {
        var admin = await CreateServerAdministratorAsync();
        var householdId = (await _accounts.GetByIdAsync(admin.Id))!.HouseholdId!.Value;
        var mary = NewProfile("Mary");

        await _accounts.CreateHouseholdPersonAsync(mary, householdId, [Grant(admin.Id, mary.Id)], accessFromProfileId: null);

        Assert.Equal(AccountFeatureId.All.Select(feature => feature.Value).Order(),
            (await _access.GetFeatureGrantsAsync(mary.Id)).Select(feature => feature.Value).Order());
        Assert.Empty(await _access.GetLibraryGrantsAsync(mary.Id));
        Assert.False((await _access.GetAdminFlagsAsync(mary.Id))!.IsHouseholdAdministrator);
    }

    [Fact]
    public async Task APersonCreatedForASignIn_StartsWithTheAccessOfWhoeverCreatedThem_AndNeverAnAdministratorRole()
    {
        var admin = await CreateServerAdministratorAsync();
        var (main, mainPerson) = await CreateMainSignInAsync(admin, "main@example.com");
        var kid = NewProfile("Kid");

        await _accounts.CreateManagedProfileAsync(kid, Grant(main.Id, kid.Id), accessFromProfileId: mainPerson.Id);

        Assert.Equal(new[] { "read" }, (await _access.GetFeatureGrantsAsync(kid.Id)).Select(feature => feature.Value));
        var flags = (await _access.GetAdminFlagsAsync(kid.Id))!;
        Assert.False(flags.IsServerAdministrator);
        Assert.False(flags.IsHouseholdAdministrator);
    }

    // ── The default household ──────────────────────────────────────────────────

    [Fact]
    public async Task TheDefaultHousehold_CannotBeRemoved_EvenWhenItIsEmpty()
    {
        var empty = await _households.CreateAsync("Empty", _now);
        Assert.True(await _access.SetDefaultHouseholdAsync(empty.Id));

        Assert.Equal(HouseholdDeleteOutcome.IsDefaultHousehold, await _households.DeleteWhenEmptyAsync(empty.Id));

        Assert.NotNull(await _households.GetByIdAsync(empty.Id));
    }

    [Fact]
    public async Task ANewHousehold_IsNeverADefault_AndSettingTheDefaultLeavesExactlyOne()
    {
        var first = await _households.CreateAsync("First", _now);
        var second = await _households.CreateAsync("Second", _now);
        Assert.False(first.IsDefault);

        await _access.SetDefaultHouseholdAsync(first.Id);
        await _access.SetDefaultHouseholdAsync(second.Id);

        using var connection = _database.CreateConnection();
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM households WHERE is_default = 1;"));
        Assert.Equal(second.Id, await _access.GetDefaultHouseholdIdAsync());
        Assert.Equal(HouseholdDeleteOutcome.Deleted, await _households.DeleteWhenEmptyAsync(first.Id));
    }

    // ── Deleting a person with a sign-in ───────────────────────────────────────

    [Fact]
    public async Task DeletingTheLastHouseholdAdministratorsUser_TakesTheHouseholdAndTheProfileWithIt_AndTheirAccessRows()
    {
        var (_, person, account) = await CreateMemberAsync();

        var outcome = await _accounts.DeleteUserAsync(account.Id);

        Assert.Equal(person.Id, Assert.Single(outcome.DeletedProfileIds));
        Assert.Null(await _accounts.GetByIdAsync(account.Id));
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profiles WHERE id = @id;", new { id = person.Id }));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profile_feature_grants WHERE profile_id = @id;", new { id = person.Id }));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>The first server administrator, signing in as the seeded Owner person, with every feature.</summary>
    private async Task<Account> CreateServerAdministratorAsync()
    {
        var admin = NewAccount("admin@example.com", administrator: true);
        await _accounts.CreateAccountAsync(admin, Grant(admin.Id, Profile.SeedProfileId, isDefault: true, administrator: true),
            AccountFeatureId.All.ToHashSet(), new HashSet<Guid>());
        return admin;
    }

    /// <summary>The household's server administrator when one already exists (the member helper makes it first).</summary>
    private async Task<Account> FindServerAdministratorAsync() =>
        (await _accounts.GetAllAsync()).First(account => account.IsAdministrator);

    private Task GiveCredentialAsync(Account account) =>
        new IdentityRepository(_database).UpsertAccountCredentialAsync(new AccountCredential
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            SecretHash = "work-factored-hash",
            SecurityStamp = "security-stamp",
            CreatedAt = _now,
            UpdatedAt = _now,
        });

    /// <summary>A household's main sign-in in the administrator's household, with its own person and Read access.</summary>
    private async Task<(Account Account, Profile Person)> CreateMainSignInAsync(Account admin, string email)
    {
        var account = NewAccount(email, administrator: false, householdId: (await _accounts.GetByIdAsync(admin.Id))!.HouseholdId);
        var person = NewProfile(email);
        await _accounts.CreateAccountAsync(account, Grant(account.Id, person.Id, isDefault: true),
            new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid>(), newProfile: person);
        return (account, person);
    }

    /// <summary>A member in a household of their own (the first sign-in there becomes its household administrator).</summary>
    private async Task<(Account Admin, Profile Person, Account Account)> CreateMemberAsync()
    {
        var admin = await CreateServerAdministratorAsync();
        var account = NewAccount("member@example.com", administrator: false);
        var person = NewProfile("Member");
        await _accounts.CreateAccountAsync(account, Grant(account.Id, person.Id, isDefault: true),
            new HashSet<AccountFeatureId> { AccountFeatureId.Read }, new HashSet<Guid>(), newProfile: person);
        return (await _accounts.GetByIdAsync(admin.Id) ?? admin, person, (await _accounts.GetByIdAsync(account.Id))!);
    }

    private async Task<Profile> AddHouseholdPersonAsync(Account admin, string name)
    {
        var household = (await _accounts.GetByIdAsync(admin.Id))!.HouseholdId!.Value;
        var person = NewProfile(name);
        await _accounts.CreateHouseholdPersonAsync(person, household, [Grant(admin.Id, person.Id)], accessFromProfileId: Profile.SeedProfileId);
        person.HouseholdId = household;
        return person;
    }

    private Account NewAccount(string email, bool administrator, Guid? householdId = null) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        IsEnabled = true,
        IsAdministrator = administrator,
        AuthorizationVersion = 1,
        CreatedAt = _now,
        UpdatedAt = _now,
        HouseholdId = householdId,
    };

    private Profile NewProfile(string name) => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = name,
        AvatarColor = "#7C4DFF",
        Role = ProfileRole.StandardUser,
        CreatedAt = _now,
    };

    private AccountProfileGrant Grant(Guid accountId, Guid profileId, bool isDefault = false, bool administrator = false) => new()
    {
        AccountId = accountId,
        ProfileId = profileId,
        IsDefault = isDefault,
        IsEnabled = true,
        AdminEnabled = administrator,
        AuthorizationVersion = 1,
        GrantedAt = _now,
    };

    private AccountInvitation NewInvitation(Guid accountId) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        TokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")))),
        CreatedAt = _now,
        ExpiresAt = _now.AddDays(7),
    };

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }
}
