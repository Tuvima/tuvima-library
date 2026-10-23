using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Permissions groundwork for the Settings revamp (WP13): who may manage households, switch person, edit people, see
/// other people's Activity and Taste, delete history and see or sign out devices. Every role is tried against every
/// action, including the attempts that must be refused.
/// </summary>
public sealed class PermissionsGroundworkTests
{
    private const string TemporaryPassword = "a long temporary password";

    // ----- Households -----

    [Fact]
    public async Task HouseholdList_ShowsEveryHousehold_ToAServerAdministrator_OnlyTheirOwn_ToAHouseholdAdministrator_AndNothingToAnyoneElse()
    {
        await using var world = await World.CreateAsync();
        var homeA = (await world.Management.ListAsync(world.ServerAdmin)).Single(summary => summary.Household.Id == world.A.Id);

        var all = await world.Management.ListAsync(world.ServerAdmin);
        Assert.Contains(all, summary => summary.Household.Id == world.B.Id);
        Assert.Equal((await world.Households.ListProfilesAsync(world.A.Id)).Count, homeA.ProfileCount);
        Assert.Equal((await world.Households.ListAccountsAsync(world.A.Id)).Count, homeA.AccountCount);
        Assert.True(homeA.ProfileCount >= 3 && homeA.AccountCount == 2);

        Assert.Equal([world.A.Id], (await world.Management.ListAsync(world.AdminOfA)).Select(summary => summary.Household.Id));
        Assert.Equal([world.B.Id], (await world.Management.ListAsync(world.AdminOfB)).Select(summary => summary.Household.Id));

        foreach (var refused in new[] { world.MemberOfA, world.KidOfA, world.LockedServerAdmin })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Management.ListAsync(refused));
        }
    }

    [Fact]
    public async Task AddingRenamingAndRemovingAHousehold_IsRefused_ForEveryoneExceptAnUnlockedServerAdministrator()
    {
        await using var world = await World.CreateAsync();

        foreach (var actor in world.Actors)
        {
            var allowed = actor.Name == "server administrator";
            var spare = await world.Management.CreateAsync(world.ServerAdmin, $"Spare for {actor.Name}");

            Assert.Equal(allowed, await IsAllowedAsync(() => world.Management.CreateAsync(actor.Authority, $"New by {actor.Name}")));
            Assert.Equal(allowed, await IsAllowedAsync(() => world.Management.RenameAsync(actor.Authority, spare.Household.Id, $"Renamed by {actor.Name}")));
            Assert.Equal(allowed, await IsAllowedAsync(() => world.Management.DeleteAsync(actor.Authority, spare.Household.Id)));

            // A refused removal leaves the spare in place; clean it up so the next role starts fresh.
            if (await world.Households.GetByIdAsync(spare.Household.Id) is not null)
            {
                await world.Management.DeleteAsync(world.ServerAdmin, spare.Household.Id);
            }
        }
    }

    [Fact]
    public async Task AServerAdministrator_AddsRenamesAndRemovesAnEmptyHousehold_AndEachStepIsAudited()
    {
        await using var world = await World.CreateAsync();

        var created = await world.Management.CreateAsync(world.ServerAdmin, "  Lake house  ");
        Assert.Equal("Lake house", created.Household.Name);
        Assert.True(created.IsEmpty);

        var renamed = await world.Management.RenameAsync(world.ServerAdmin, created.Household.Id, "Cabin");
        Assert.Equal("Cabin", renamed.Household.Name);
        Assert.Equal("Cabin", (await world.Households.GetByIdAsync(created.Household.Id))!.Name);

        await world.Management.DeleteAsync(world.ServerAdmin, created.Household.Id);
        Assert.Null(await world.Households.GetByIdAsync(created.Household.Id));

        var subject = created.Household.Id.ToString("D");
        foreach (var eventType in new[] { "household.created", "household.renamed", "household.deleted" })
        {
            var row = Assert.Single(world.AuditRows(eventType));
            Assert.Equal(subject, row.SubjectId);
            Assert.Equal("household", row.SubjectType);
            Assert.Equal(world.ServerAdmin.AccountId, row.ActorAccountId);
        }
    }

    [Fact]
    public async Task RemovingAHouseholdThatStillHasPeopleOrSignIns_IsRefusedWithAPlainMessage_AndNothingIsMoved()
    {
        await using var world = await World.CreateAsync();
        var profilesBefore = (await world.Households.ListProfilesAsync(world.A.Id)).Select(profile => profile.Id).Order().ToList();
        var accountsBefore = (await world.Households.ListAccountsAsync(world.A.Id)).Select(account => account.Id).Order().ToList();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Management.DeleteAsync(world.ServerAdmin, world.A.Id));

        Assert.Equal(HouseholdManagementService.NotEmptyMessage, refusal.Message);
        Assert.NotNull(await world.Households.GetByIdAsync(world.A.Id));
        Assert.Equal(profilesBefore, (await world.Households.ListProfilesAsync(world.A.Id)).Select(profile => profile.Id).Order());
        Assert.Equal(accountsBefore, (await world.Households.ListAccountsAsync(world.A.Id)).Select(account => account.Id).Order());
        Assert.Empty(world.AuditRows("household.deleted"));
    }

    [Fact]
    public async Task RemovingOrRenamingAnUnknownHousehold_IsReportedAsNotFound()
    {
        await using var world = await World.CreateAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Management.DeleteAsync(world.ServerAdmin, Guid.NewGuid()));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Management.RenameAsync(world.ServerAdmin, Guid.NewGuid(), "Nowhere"));
        Assert.Empty(world.AuditRows("household.deleted"));
        Assert.Empty(world.AuditRows("household.renamed"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AHouseholdWithoutAName_IsRefused(string name)
    {
        await using var world = await World.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => world.Management.CreateAsync(world.ServerAdmin, name));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Management.RenameAsync(world.ServerAdmin, world.A.Id, name));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            world.Management.CreateAsync(world.ServerAdmin, new string('x', HouseholdManagementService.MaximumNameLength + 1)));
    }

    // ----- Changing people -----

    [Fact]
    public async Task EachPersonAction_IsAllowedOnlyForAnUnlockedServerAdministratorAnywhere_AndAHouseholdAdministratorAtHome()
    {
        await using var world = await World.CreateAsync();
        var expectations = new (string Name, RequestAuthority Authority, bool AtA, bool AtB)[]
        {
            ("server administrator", world.ServerAdmin, true, true),
            ("locked server administrator", world.LockedServerAdmin, false, false),
            ("household administrator of A", world.AdminOfA, true, false),
            ("household administrator of B", world.AdminOfB, false, true),
            ("member of A", world.MemberOfA, false, false),
            ("child profile of A", world.KidOfA, false, false),
        };

        foreach (var action in world.PersonActions())
        {
            foreach (var expected in expectations)
            {
                Assert.True(expected.AtA == await action.IsAllowedAsync(expected.Authority, world.A),
                    $"{action.Name} in household A as {expected.Name}");
                Assert.True(expected.AtB == await action.IsAllowedAsync(expected.Authority, world.B),
                    $"{action.Name} in household B as {expected.Name}");
            }
        }
    }

    // ----- Switching person -----

    [Fact]
    public async Task AMemberOrHouseholdAdministrator_SwitchesOnlyInsideTheirHousehold_EvenWhenAGrantReachesAnotherHousehold()
    {
        await using var world = await World.CreateAsync();
        world.GrantDirectly(world.A.Owner.Id, world.B.Person.Id);
        world.GrantDirectly(world.A.OwnSignIn.Id, world.B.Person.Id);

        foreach (var account in new[] { world.A.Owner, world.A.OwnSignIn })
        {
            var session = await world.Identity.CreatePasskeySessionAsync(account.Id, $"device-{account.Id:N}", "Test", "Dashboard");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                world.Identity.SwitchActiveProfileAsync(session.PlaintextToken, world.B.Person.Id, null));

            var home = await world.Identity.SwitchActiveProfileAsync(session.PlaintextToken, world.A.Person.Id, null);
            Assert.Equal(world.A.Person.Id, home.ActiveProfile.Id);
        }
    }

    [Fact]
    public async Task AServerAdministrator_SwitchesInsideTheirOwnHousehold_AndNeverIntoAnotherOne()
    {
        await using var world = await World.CreateAsync();
        var home = (await world.Accounts.GetByIdAsync(world.ServerAdminAccount.Id))!.HouseholdId!.Value;
        var housemate = await world.Mutations.AddHouseholdPersonAsync(world.ServerAdmin,
            new AddHouseholdPersonCommand(home, "Housemate", null, false, null));
        world.GrantDirectly(world.ServerAdminAccount.Id, world.B.Person.Id);

        var session = await world.Identity.CreatePasskeySessionAsync(world.ServerAdminAccount.Id, "device-admin", "Test", "Dashboard");
        var switched = await world.Identity.SwitchActiveProfileAsync(session.PlaintextToken, housemate.Id, null);

        Assert.Equal(housemate.Id, switched.ActiveProfile.Id);
        // Another household stays out of reach, whatever a grant row says.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            world.Identity.SwitchActiveProfileAsync(session.PlaintextToken, world.B.Person.Id, null));
    }

    // ----- Personal scope -----

    [Fact]
    public async Task EachRole_GetsExactlyTheScopesItsRoleAllows_ForActivityAndTaste()
    {
        await using var world = await World.CreateAsync();

        foreach (var kind in new[] { PersonalScopeKind.Activity, PersonalScopeKind.Taste })
        {
            foreach (var actor in world.Actors)
            {
                var own = actor.Authority.ActiveProfileId!.Value;
                var inHome = actor.Authority.ActiveProfileHouseholdId;
                var scopes = new (string Name, PersonalScope Scope, bool Expected)[]
                {
                    ("Me", PersonalScope.Self, true),
                    ("their own profile by id", PersonalScope.Profile(own), true),
                    ("a profile in A", PersonalScope.Profile(world.A.Person.Id), actor.SeesHousehold(world.A.Id) || own == world.A.Person.Id),
                    ("a profile in B", PersonalScope.Profile(world.B.Person.Id), actor.SeesHousehold(world.B.Id) || own == world.B.Person.Id),
                    ("household A", PersonalScope.Household(world.A.Id), actor.SeesHousehold(world.A.Id)),
                    ("household B", PersonalScope.Household(world.B.Id), actor.SeesHousehold(world.B.Id)),
                    ("Everyone", PersonalScope.Everyone, actor.Name == "server administrator"),
                };

                foreach (var scope in scopes)
                {
                    var decision = await world.Scope.AuthorizeAsync(actor.Authority, kind, scope.Scope);
                    Assert.True(scope.Expected == decision.Allowed,
                        $"{kind} scope '{scope.Name}' as {actor.Name} (household {inHome})");
                    if (!decision.Allowed)
                    {
                        Assert.Empty(decision.ProfileIds);
                        Assert.False(decision.IncludesOthers);
                    }
                }
            }
        }
    }

    [Fact]
    public async Task TheResolvedProfiles_AreExactlyTheScopesPeople()
    {
        await using var world = await World.CreateAsync();
        var homeA = (await world.Households.ListProfilesAsync(world.A.Id)).Select(profile => profile.Id).ToHashSet();

        var me = await world.Scope.AuthorizeAsync(world.MemberOfA, PersonalScopeKind.Activity, PersonalScope.Self);
        Assert.Equivalent(new[] { world.MemberOfA.ActiveProfileId!.Value }, me.ProfileIds);
        Assert.False(me.IncludesOthers);

        var household = await world.Scope.AuthorizeAsync(world.AdminOfA, PersonalScopeKind.Activity, PersonalScope.Household(world.A.Id));
        Assert.Equivalent(homeA, household.ProfileIds);
        Assert.True(household.IncludesOthers);
        Assert.False(household.AllProfiles);

        var person = await world.Scope.AuthorizeAsync(world.AdminOfA, PersonalScopeKind.Taste, PersonalScope.Profile(world.A.Person.Id));
        Assert.Equivalent(new[] { world.A.Person.Id }, person.ProfileIds);
        Assert.True(person.IncludesOthers);

        var everyone = await world.Scope.AuthorizeAsync(world.ServerAdmin, PersonalScopeKind.Activity, PersonalScope.Everyone);
        Assert.True(everyone.AllProfiles);
        Assert.True(everyone.IncludesOthers);

        var self = await world.Scope.AuthorizeAsync(world.AdminOfA, PersonalScopeKind.Activity,
            PersonalScope.Profile(world.AdminOfA.ActiveProfileId!.Value));
        Assert.True(self.Allowed);
        Assert.False(self.IncludesOthers);
    }

    [Fact]
    public async Task AnUnknownProfileOrHousehold_IsReportedToAServerAdministrator_AndLooksLikeAnyOtherRefusalToAHouseholdAdministrator()
    {
        await using var world = await World.CreateAsync();

        var unknownForServer = await world.Scope.AuthorizeAsync(world.ServerAdmin, PersonalScopeKind.Activity, PersonalScope.Profile(Guid.NewGuid()));
        var unknownHouseholdForServer = await world.Scope.AuthorizeAsync(world.ServerAdmin, PersonalScopeKind.Activity, PersonalScope.Household(Guid.NewGuid()));
        var unknownForHousehold = await world.Scope.AuthorizeAsync(world.AdminOfA, PersonalScopeKind.Activity, PersonalScope.Profile(Guid.NewGuid()));
        var otherForHousehold = await world.Scope.AuthorizeAsync(world.AdminOfA, PersonalScopeKind.Activity, PersonalScope.Profile(world.B.Person.Id));

        Assert.Equal(PersonalScopeRefusal.UnknownTarget, unknownForServer.Refusal);
        Assert.Equal(PersonalScopeRefusal.UnknownTarget, unknownHouseholdForServer.Refusal);
        Assert.Equal(PersonalScopeRefusal.NotAllowed, unknownForHousehold.Refusal);
        Assert.Equal(unknownForHousehold.Refusal, otherForHousehold.Refusal);
    }

    [Fact]
    public async Task ALockedServerAdministrator_SeesOnlyTheirOwn_AndIsToldToUnlock()
    {
        await using var world = await World.CreateAsync();

        var others = await world.Scope.AuthorizeAsync(world.LockedServerAdmin, PersonalScopeKind.Activity, PersonalScope.Everyone);
        var self = await world.Scope.AuthorizeAsync(world.LockedServerAdmin, PersonalScopeKind.Activity, PersonalScope.Self);

        Assert.False(others.Allowed);
        Assert.Equal(PersonalScopeRefusal.UnlockRequired, others.Refusal);
        Assert.True(self.Allowed);
        Assert.Empty(world.AuditRows(PersonalScopeAuthorizer.ActivityViewedOthers));
    }

    [Fact]
    public async Task ASignedOutCaller_AndAnApplication_GetNoScopeAtAll()
    {
        await using var world = await World.CreateAsync();
        var application = new RequestAuthority(PrincipalKind.ServiceApplication, true, ApplicationId: Guid.NewGuid(), ApplicationEnabled: true);
        var anonymous = new RequestAuthority(PrincipalKind.Anonymous, false);

        foreach (var caller in new[] { application, anonymous })
        {
            var decision = await world.Scope.AuthorizeAsync(caller, PersonalScopeKind.Activity, PersonalScope.Self);
            Assert.False(decision.Allowed);
            Assert.Equal(PersonalScopeRefusal.NotSignedIn, decision.Refusal);
        }
    }

    [Fact]
    public async Task OpeningAScopeWithOtherPeople_WritesOneAuditEntry_AndMyOwnScopeWritesNone()
    {
        await using var world = await World.CreateAsync();

        await world.Scope.AuthorizeAsync(world.AdminOfA, PersonalScopeKind.Activity, PersonalScope.Self);
        await world.Scope.AuthorizeAsync(world.AdminOfA, PersonalScopeKind.Activity, PersonalScope.Profile(world.AdminOfA.ActiveProfileId!.Value));
        Assert.Empty(world.AuditRows(PersonalScopeAuthorizer.ActivityViewedOthers));

        await world.Scope.AuthorizeAsync(world.AdminOfA, PersonalScopeKind.Activity, PersonalScope.Household(world.A.Id));

        var row = Assert.Single(world.AuditRows(PersonalScopeAuthorizer.ActivityViewedOthers));
        Assert.Equal(world.AdminOfA.AccountId, row.ActorAccountId);
        Assert.Equal(world.AdminOfA.ActiveProfileId, row.ActorProfileId);
        Assert.Equal("household", row.SubjectType);
        Assert.Equal(world.A.Id.ToString("D"), row.SubjectId);
    }

    [Fact]
    public async Task OpeningTheSameScopeAgainWithinThirtyMinutes_IsNotRecordedTwice_ButAfterThemItIs()
    {
        await using var world = await World.CreateAsync();

        for (var look = 0; look < 3; look++)
        {
            await world.Scope.AuthorizeAsync(world.ServerAdmin, PersonalScopeKind.Taste, PersonalScope.Everyone);
            world.Clock.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Single(world.AuditRows(PersonalScopeAuthorizer.TasteViewedOthers));

        world.Clock.Advance(TimeSpan.FromMinutes(30));
        await world.Scope.AuthorizeAsync(world.ServerAdmin, PersonalScopeKind.Taste, PersonalScope.Everyone);

        var rows = world.AuditRows(PersonalScopeAuthorizer.TasteViewedOthers);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("everyone", row.SubjectId));
    }

    [Fact]
    public async Task ADifferentScopeKindOrActor_IsRecordedSeparately_AndARefusedScopeIsNotRecorded()
    {
        await using var world = await World.CreateAsync();

        await world.Scope.AuthorizeAsync(world.ServerAdmin, PersonalScopeKind.Activity, PersonalScope.Household(world.A.Id));
        await world.Scope.AuthorizeAsync(world.ServerAdmin, PersonalScopeKind.Activity, PersonalScope.Household(world.B.Id));
        await world.Scope.AuthorizeAsync(world.ServerAdmin, PersonalScopeKind.Taste, PersonalScope.Household(world.A.Id));
        await world.Scope.AuthorizeAsync(world.AdminOfA, PersonalScopeKind.Activity, PersonalScope.Household(world.A.Id));
        await world.Scope.AuthorizeAsync(world.AdminOfA, PersonalScopeKind.Activity, PersonalScope.Household(world.B.Id));
        await world.Scope.AuthorizeAsync(world.MemberOfA, PersonalScopeKind.Activity, PersonalScope.Household(world.A.Id));

        Assert.Equal(3, world.AuditRows(PersonalScopeAuthorizer.ActivityViewedOthers).Count);
        Assert.Single(world.AuditRows(PersonalScopeAuthorizer.TasteViewedOthers));
    }

    [Fact]
    public async Task OnlyAnUnlockedServerAdministrator_CanDeletePersonalHistory()
    {
        await using var world = await World.CreateAsync();

        foreach (var actor in world.Actors)
        {
            Assert.Equal(actor.Name == "server administrator",
                await world.Scope.CanDeletePersonalHistoryAsync(actor.Authority));
        }
    }

    [Fact]
    public async Task TasteIsEditedOnlyByItsOwner_NeverByAnAdministratorForSomeoneElse()
    {
        await using var world = await World.CreateAsync();

        foreach (var actor in world.Actors)
        {
            var own = actor.Authority.ActiveProfileId!.Value;
            Assert.True(world.Scope.CanEditTaste(actor.Authority, own), $"{actor.Name} edits their own taste");
            Assert.False(world.Scope.CanEditTaste(actor.Authority, world.A.Person.Id == own ? world.B.Person.Id : world.A.Person.Id),
                $"{actor.Name} edits someone else's taste");
            Assert.False(world.Scope.CanEditTaste(actor.Authority, Guid.Empty));
        }

        Assert.False(world.Scope.CanEditTaste(new RequestAuthority(PrincipalKind.Anonymous, false), world.A.Person.Id));
    }

    // ----- Devices -----

    [Fact]
    public async Task DeviceList_ShowsEverythingToAServerAdministrator_TheHouseholdToItsAdministrator_AndOnlyOwnToOthers()
    {
        await using var world = await World.CreateAsync();
        var devices = await world.PairDevicesAsync();

        Assert.Equivalent(devices.All, await ListIdsAsync(world, world.ServerAdmin));
        Assert.Equivalent(new[] { devices.OwnerA, devices.MemberA }, await ListIdsAsync(world, world.AdminOfA));
        Assert.Equivalent(new[] { devices.OwnerB }, await ListIdsAsync(world, world.AdminOfB));
        Assert.Equivalent(new[] { devices.MemberA }, await ListIdsAsync(world, world.MemberOfA));
        Assert.Equivalent(new[] { devices.OwnerA }, await ListIdsAsync(world, world.KidOfA));
    }

    [Fact]
    public async Task RevokingADevice_IsAllowedForItsOwnerTheirHouseholdAdministratorAndAServerAdministrator_NobodyElse()
    {
        await using var world = await World.CreateAsync();
        var devices = await world.PairDevicesAsync();

        // Someone outside the household, a member and a child profile cannot sign out another person's device.
        Assert.Equal(ManagedDeviceRevokeOutcome.Forbidden, await world.Devices.RevokeAsync(world.AdminOfB, devices.OwnerA));
        Assert.Equal(ManagedDeviceRevokeOutcome.Forbidden, await world.Devices.RevokeAsync(world.AdminOfA, devices.OwnerB));
        Assert.Equal(ManagedDeviceRevokeOutcome.Forbidden, await world.Devices.RevokeAsync(world.MemberOfA, devices.OwnerA));
        Assert.Equal(ManagedDeviceRevokeOutcome.Forbidden, await world.Devices.RevokeAsync(world.KidOfA, devices.MemberA));
        Assert.Empty(world.Notifier.Revoked);

        Assert.Equal(ManagedDeviceRevokeOutcome.Revoked, await world.Devices.RevokeAsync(world.MemberOfA, devices.MemberA));
        Assert.Equal(ManagedDeviceRevokeOutcome.Revoked, await world.Devices.RevokeAsync(world.AdminOfA, devices.OwnerA));
        Assert.Equal(ManagedDeviceRevokeOutcome.Revoked, await world.Devices.RevokeAsync(world.ServerAdmin, devices.OwnerB));
        Assert.Equivalent(new[] { devices.MemberA, devices.OwnerA, devices.OwnerB }, world.Notifier.Revoked);
    }

    [Fact]
    public async Task AHouseholdAdministrator_CanSignOutADeviceOfTheirHousehold_ButNotWhenActingAsAChildProfile()
    {
        await using var world = await World.CreateAsync();
        var devices = await world.PairDevicesAsync();

        Assert.Equal(ManagedDeviceRevokeOutcome.Forbidden, await world.Devices.RevokeAsync(world.KidOfA, devices.MemberA));
        Assert.Equal(ManagedDeviceRevokeOutcome.Revoked, await world.Devices.RevokeAsync(world.AdminOfA, devices.MemberA));
    }

    [Fact]
    public async Task AHouseholdAdministrator_NeverListsOrSignsOutTheDevicesOfAServerAdministratorOrAnotherHouseholdAdministratorInTheHousehold()
    {
        await using var world = await World.CreateAsync();
        var devices = await world.PairDevicesAsync();
        var (serverInA, otherAdminInA) = await world.PairOtherAdministratorsInAAsync();

        var listed = await ListIdsAsync(world, world.AdminOfA);
        Assert.Equivalent(new[] { devices.OwnerA, devices.MemberA }, listed);
        Assert.DoesNotContain(serverInA, listed);
        Assert.DoesNotContain(otherAdminInA, listed);

        Assert.Equal(ManagedDeviceRevokeOutcome.Forbidden, await world.Devices.RevokeAsync(world.AdminOfA, serverInA));
        Assert.Equal(ManagedDeviceRevokeOutcome.Forbidden, await world.Devices.RevokeAsync(world.AdminOfA, otherAdminInA));
        Assert.Empty(world.Notifier.Revoked);

        // A server administrator still sees and signs out everything, and an ordinary member of the household stays in reach.
        Assert.Contains(serverInA, await ListIdsAsync(world, world.ServerAdmin));
        Assert.Equal(ManagedDeviceRevokeOutcome.Revoked, await world.Devices.RevokeAsync(world.AdminOfA, devices.MemberA));
        Assert.Equal(ManagedDeviceRevokeOutcome.Revoked, await world.Devices.RevokeAsync(world.ServerAdmin, serverInA));
    }

    [Fact]
    public async Task AHouseholdAdministratorWhoHasNotUnlocked_SeesAndSignsOutOnlyTheirOwnDevices()
    {
        await using var world = await World.CreateAsync();
        var devices = await world.PairDevicesAsync();

        Assert.Equivalent(new[] { devices.OwnerA }, await ListIdsAsync(world, world.LockedAdminOfA));
        Assert.Equal(ManagedDeviceRevokeOutcome.Forbidden, await world.Devices.RevokeAsync(world.LockedAdminOfA, devices.MemberA));
        Assert.Equal(ManagedDeviceRevokeOutcome.Revoked, await world.Devices.RevokeAsync(world.LockedAdminOfA, devices.OwnerA));
    }

    // ----- Starting profile and "always open as" -----

    [Fact]
    public async Task AnOutOfHouseholdAlwaysOpenAs_IsRefusedWhenSet_AndAnInHouseholdOneIsKept()
    {
        await using var world = await World.CreateAsync();
        world.GrantDirectly(world.A.Owner.Id, world.B.Person.Id);
        var session = await world.Identity.CreatePasskeySessionAsync(world.A.Owner.Id, "device-pref", "Test", "Dashboard");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            world.Identity.SetDeviceProfilePreferenceAsync(world.A.Owner.Id, session.Session.Id, world.B.Person.Id));
        Assert.Null(await world.Identity.GetDeviceProfilePreferenceAsync(world.A.Owner.Id, session.Session.Id));

        await world.Identity.SetDeviceProfilePreferenceAsync(world.A.Owner.Id, session.Session.Id, world.A.Person.Id);
        Assert.Equal(world.A.Person.Id, await world.Identity.GetDeviceProfilePreferenceAsync(world.A.Owner.Id, session.Session.Id));
    }

    [Fact]
    public async Task AnOutOfHouseholdAlwaysOpenAs_IsIgnoredAtSignIn_ForEveryone_BecauseASignInOpensItsOwnProfile()
    {
        await using var world = await World.CreateAsync();
        world.GrantDirectly(world.A.Owner.Id, world.B.Person.Id);
        world.GrantDirectly(world.ServerAdminAccount.Id, world.B.Person.Id);
        // Set directly, as a data store from before this rule could hold it.
        await world.Identities.SetDeviceProfilePreferenceAsync(world.A.Owner.Id, "device-old", world.B.Person.Id, DateTimeOffset.UtcNow);
        await world.Identities.SetDeviceProfilePreferenceAsync(world.ServerAdminAccount.Id, "device-old", world.B.Person.Id, DateTimeOffset.UtcNow);

        var member = await world.Identity.CreatePasskeySessionAsync(world.A.Owner.Id, "device-old", "Test", "Dashboard");
        var administrator = await world.Identity.CreatePasskeySessionAsync(world.ServerAdminAccount.Id, "device-old", "Test", "Dashboard");

        Assert.Equal((await world.Accounts.GetDefaultProfileIdAsync(world.A.Owner.Id))!.Value, member.ActiveProfile.Id);
        Assert.Equal(world.A.Id, member.ActiveProfile.HouseholdId);
        Assert.Null(await world.Identity.GetDeviceProfilePreferenceAsync(world.A.Owner.Id, member.Session.Id));
        Assert.Equal((await world.Accounts.GetDefaultProfileIdAsync(world.ServerAdminAccount.Id))!.Value, administrator.ActiveProfile.Id);
        Assert.Null(await world.Identity.GetDeviceProfilePreferenceAsync(world.ServerAdminAccount.Id, administrator.Session.Id));
    }

    [Fact]
    public async Task ADefaultProfileOutsideTheHousehold_IsNeverTheStartingProfile()
    {
        await using var world = await World.CreateAsync();
        world.GrantDirectly(world.A.Owner.Id, world.B.Person.Id);
        using (var connection = world.Database.CreateConnection())
        {
            connection.Execute("UPDATE account_profile_grants SET is_default = 0 WHERE account_id = @account;", new { account = world.A.Owner.Id });
            connection.Execute("UPDATE account_profile_grants SET is_default = 1 WHERE account_id = @account AND profile_id = @profile;",
                new { account = world.A.Owner.Id, profile = world.B.Person.Id });
        }

        var session = await world.Identity.CreatePasskeySessionAsync(world.A.Owner.Id, "device-default", "Test", "Dashboard");

        Assert.Equal(world.A.Id, session.ActiveProfile.HouseholdId);
    }

    // ----- Profile list -----

    [Fact]
    public async Task TheProfileList_HoldsOnlyPeopleInTheCallersHousehold_AServerAdministratorIncluded()
    {
        await using var world = await World.CreateAsync();
        world.GrantDirectly(world.A.Owner.Id, world.B.Person.Id);
        world.GrantDirectly(world.ServerAdminAccount.Id, world.B.Person.Id);
        var everyone = await world.Profiles.GetAllAsync();

        var member = ProfileEndpoints.ProfilesForCaller(world.A.Owner, everyone, (await world.Accounts.GetProfileIdsAsync(world.A.Owner.Id)).ToHashSet());
        var administrator = ProfileEndpoints.ProfilesForCaller(world.ServerAdminAccount, everyone, (await world.Accounts.GetProfileIdsAsync(world.ServerAdminAccount.Id)).ToHashSet());

        Assert.DoesNotContain(member, profile => profile.Id == world.B.Person.Id);
        Assert.Contains(member, profile => profile.Id == world.A.Person.Id);
        Assert.All(member, profile => Assert.Equal(world.A.Id, profile.HouseholdId));
        var administratorHome = (await world.Accounts.GetByIdAsync(world.ServerAdminAccount.Id))!.HouseholdId;
        Assert.DoesNotContain(administrator, profile => profile.Id == world.B.Person.Id);
        Assert.NotEmpty(administrator);
        Assert.All(administrator, profile => Assert.Equal(administratorHome, profile.HouseholdId));
        Assert.Empty(ProfileEndpoints.ProfilesForCaller(null, everyone, everyone.Select(profile => profile.Id).ToHashSet()));
    }

    // ----- Households with a Shared Library -----

    [Fact]
    public async Task AHouseholdWithSharedPhotos_IsRefusedWithAPlainMessage_AndNothingIsRemoved()
    {
        await using var world = await World.CreateAsync();
        var shared = new ViewSharedLibraryRepository(world.Database);
        var full = await world.Management.CreateAsync(world.ServerAdmin, "Full shared");
        var library = await shared.EnsureForHouseholdAsync(full.Household.Id);
        using (var connection = world.Database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO local_items (id, scope_kind, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
                VALUES (@id, 'shared', @library, 'image', 'beach.jpg', 'image/jpeg', @now, @now);
                """, new { id = Guid.NewGuid(), library = library.LibraryId, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            world.Management.DeleteAsync(world.ServerAdmin, full.Household.Id));

        Assert.Equal("This household still has shared photos in View. Remove them first.", refusal.Message);
        Assert.NotNull(await world.Households.GetByIdAsync(full.Household.Id));
        Assert.Empty(world.AuditRows("household.deleted"));
    }

    [Fact]
    public async Task AHouseholdWhoseSharedLibraryIsEmpty_IsRemovedAndAudited()
    {
        await using var world = await World.CreateAsync();
        var empty = await world.Management.CreateAsync(world.ServerAdmin, "Empty shared");
        await new ViewSharedLibraryRepository(world.Database).EnsureForHouseholdAsync(empty.Household.Id);

        await world.Management.DeleteAsync(world.ServerAdmin, empty.Household.Id);

        Assert.Null(await world.Households.GetByIdAsync(empty.Household.Id));
        Assert.Equal(empty.Household.Id.ToString("D"), Assert.Single(world.AuditRows("household.deleted")).SubjectId);
    }

    // ----- Paired apps -----

    [Fact]
    public async Task APairedAppActingForAnAdministrator_GetsOnlyTheirOwnScope_AndCannotDeleteHistory()
    {
        await using var world = await World.CreateAsync();
        var serverApp = world.ServerAdmin with { PrincipalKind = PrincipalKind.DelegatedUserClient, ApplicationId = Guid.NewGuid(), ApplicationEnabled = true };
        var householdApp = world.AdminOfA with { PrincipalKind = PrincipalKind.DelegatedUserClient, ApplicationId = Guid.NewGuid(), ApplicationEnabled = true };

        foreach (var app in new[] { serverApp, householdApp })
        {
            Assert.True((await world.Scope.AuthorizeAsync(app, PersonalScopeKind.Activity, PersonalScope.Self)).Allowed);
            foreach (var scope in new[] { PersonalScope.Everyone, PersonalScope.Household(world.A.Id), PersonalScope.Profile(world.A.Person.Id) })
            {
                var decision = await world.Scope.AuthorizeAsync(app, PersonalScopeKind.Taste, scope);
                Assert.False(decision.Allowed);
                Assert.Equal(PersonalScopeRefusal.NotAllowed, decision.Refusal);
            }

            Assert.False(await world.Scope.CanDeletePersonalHistoryAsync(app));
        }

        Assert.Empty(world.AuditRows(PersonalScopeAuthorizer.TasteViewedOthers));
        Assert.True(await world.Scope.CanDeletePersonalHistoryAsync(world.ServerAdmin));
    }

    private static async Task<IReadOnlyList<Guid>> ListIdsAsync(World world, RequestAuthority authority) =>
        (await world.Devices.ListAsync(authority)).Select(device => device.Id).ToList();

    private static async Task<bool> IsAllowedAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record Actor(string Name, RequestAuthority Authority, Guid? HouseholdAdminOf)
    {
        /// <summary>Whether this person may look at someone in the given household (their own household for a household administrator).</summary>
        public bool SeesHousehold(Guid householdId) =>
            Name == "server administrator" || HouseholdAdminOf == householdId;
    }

    private sealed record Place(Household Household, Account Owner, Profile Person, Account OwnSignIn, Profile Spare, Profile Kid)
    {
        public Guid Id => Household.Id;
    }

    private sealed record PairedDevices(Guid OwnerA, Guid MemberA, Guid OwnerB)
    {
        public IReadOnlyList<Guid> All => [OwnerA, MemberA, OwnerB];
    }

    private sealed class PersonAction(string name, Func<RequestAuthority, Place, Task> run)
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
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-permissions-{Guid.NewGuid():N}.db");
        private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima-permissions-{Guid.NewGuid():N}");
        private readonly Guid _lockedSession = Guid.NewGuid();
        private DatabaseConnection _database = null!;
        private ConfigurationDirectoryLoader _configuration = null!;
        private ClientAuthorizationService _authorization = null!;
        private int _counter;

        public AccountRepository Accounts { get; private set; } = null!;
        public HouseholdRepository Households { get; private set; } = null!;
        public FirstPartyIdentityService Identity { get; private set; } = null!;
        public AccountAccessMutationService Mutations { get; private set; } = null!;
        public HouseholdManagementService Management { get; private set; } = null!;
        public PersonalScopeAuthorizer Scope { get; private set; } = null!;
        public ManagedClientDeviceService Devices { get; private set; } = null!;
        public ProfileRepository Profiles { get; private set; } = null!;
        public IdentityRepository Identities { get; private set; } = null!;
        public DatabaseConnection Database => _database;
        public SpyNotifier Notifier { get; } = new();
        public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero));
        public Decisions Decisions { get; private set; } = null!;

        public Account ServerAdminAccount { get; private set; } = null!;
        public RequestAuthority ServerAdmin { get; private set; } = null!;
        public RequestAuthority LockedServerAdmin { get; private set; } = null!;
        public RequestAuthority AdminOfA { get; private set; } = null!;
        public RequestAuthority AdminOfB { get; private set; } = null!;
        public RequestAuthority MemberOfA { get; private set; } = null!;
        public RequestAuthority KidOfA { get; private set; } = null!;
        public RequestAuthority LockedAdminOfA { get; private set; } = null!;
        public Place A { get; private set; } = null!;
        public Place B { get; private set; } = null!;
        public IReadOnlyList<Actor> Actors { get; private set; } = [];

        public static async Task<World> CreateAsync()
        {
            DapperConfiguration.Configure();
            var world = new World();
            world._database = new DatabaseConnection(world._databasePath);
            world._configuration = new ConfigurationDirectoryLoader(world._configPath);
            world._database.InitializeSchema();
            world._database.RunStartupChecks();
            world.Accounts = new AccountRepository(world._database);
            var identities = new IdentityRepository(world._database);
            var profiles = new ProfileRepository(world._database);
            world.Households = new HouseholdRepository(world._database);
            world.Identity = new FirstPartyIdentityService(
                identities, world.Accounts, profiles,
                new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
                TimeProvider.System, new ConfigurationAuthenticationPolicyProvider(world._configuration));
            world.Decisions = new Decisions(world._lockedSession);
            var audit = new AuthorizationAuditWriter(world.Accounts);
            world.Mutations = new AccountAccessMutationService(
                world.Accounts, new ProfileAccessRepository(world._database), identities, profiles, world._configuration, world.Decisions, new AllowEvaluator(),
                new NoOpInvalidation(), new NoOpAudit(), world.Identity,
                new NoProfilePhotos(), TimeProvider.System, new AlwaysUsableAdministrators(), new AuthenticationPolicyMutationGate());
            world.Management = new HouseholdManagementService(
                world.Households, world.Accounts, world.Decisions, new AllowEvaluator(), audit, world.Clock);
            world.Scope = new PersonalScopeAuthorizer(
                world.Decisions, profiles, world.Households, world.Accounts, audit, world.Clock);
            var clientAuthorization = new ClientAuthorizationRepository(world._database);
            world._authorization = new ClientAuthorizationService(
                clientAuthorization, world.Accounts, new ApplicationRepository(world._database), new PermissionRegistry(), TimeProvider.System);
            world.Devices = new ManagedClientDeviceService(clientAuthorization, world.Accounts, profiles, world.Notifier, world.Decisions, TimeProvider.System);
            world.Profiles = profiles;
            world.Identities = identities;

            var bootstrap = ServerAuthority(Guid.NewGuid(), Guid.NewGuid());
            world.ServerAdminAccount = await world.Mutations.CreateAsync(bootstrap, new CreateAccountAccessCommand(
                "server@example.com", IsAdministrator: true, ProfileId: null,
                NewProfile: new NewAccountProfileCommand("Server", "#7C4DFF"),
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch },
                Libraries: new HashSet<Guid>(), TemporaryPassword: null));
            await identities.GiveAsync(world.ServerAdminAccount.Id);
            var serverProfile = (await world.Accounts.GetDefaultProfileIdAsync(world.ServerAdminAccount.Id))!.Value;
            world.ServerAdmin = ServerAuthority(world.ServerAdminAccount.Id, serverProfile);
            world.LockedServerAdmin = ServerAuthority(world.ServerAdminAccount.Id, serverProfile, world._lockedSession);

            world.A = await world.CreateHouseholdAsync("a@example.com", "Alex");
            world.B = await world.CreateHouseholdAsync("b@example.com", "Bea");
            var ownerProfileA = (await world.Accounts.GetDefaultProfileIdAsync(world.A.Owner.Id))!.Value;
            var ownerProfileB = (await world.Accounts.GetDefaultProfileIdAsync(world.B.Owner.Id))!.Value;
            world.AdminOfA = HouseholdAuthority(world.A.Owner, ownerProfileA);
            world.AdminOfB = HouseholdAuthority(world.B.Owner, ownerProfileB);
            world.MemberOfA = HouseholdAuthority(world.A.OwnSignIn, world.A.Person.Id, isHouseholdAdmin: false);
            world.KidOfA = HouseholdAuthority(world.A.Owner, world.A.Kid.Id, restricted: true);
            world.LockedAdminOfA = HouseholdAuthority(world.A.Owner, ownerProfileA, sessionId: world._lockedSession);
            world.Actors =
            [
                new("server administrator", world.ServerAdmin, null),
                new("locked server administrator", world.LockedServerAdmin, null),
                new("household administrator of A", world.AdminOfA, world.A.Id),
                new("household administrator of B", world.AdminOfB, world.B.Id),
                new("member of A", world.MemberOfA, null),
                new("child profile of A", world.KidOfA, null),
            ];
            return world;
        }

        public IReadOnlyList<PersonAction> PersonActions() =>
        [
            new("add a person", (actor, place) => Mutations.AddHouseholdPersonAsync(actor,
                new AddHouseholdPersonCommand(place.Id, $"New {Next()}", null, false, null))),
            new("rename a person", (actor, place) => Mutations.UpdateProfileAsync(actor, place.Person.Id,
                new UpdateManagedProfileCommand($"Renamed {Next()}", null))),
            new("change a person's picture", (actor, place) => Mutations.RequireCanChangeProfileAsync(actor, place.Person.Id)),
            new("set a person's PIN", (actor, place) => Mutations.SetProfilePinAsync(actor, place.Person.Id, "4321")),
            new("give a person their own sign-in", (actor, place) => Mutations.GiveOwnSignInAsync(actor,
                new GiveOwnSignInCommand(place.Spare.Id, $"own{Next()}@example.com", TemporaryPassword))),
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
                    try
                    {
                        await Mutations.DeleteProfileAsync(ServerAdmin, disposable.Id);
                    }
                    catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
                    {
                        // Already removed by the action under test.
                    }
                }
            }),
        ];

        /// <summary>Gives an account an enabled grant to a person it would not normally open, as an older data store could.</summary>
        public void GrantDirectly(Guid accountId, Guid profileId)
        {
            using var connection = _database.CreateConnection();
            connection.Execute("""
                INSERT INTO account_profile_grants (account_id, profile_id, is_default, is_enabled, admin_enabled, authorization_version, granted_at)
                VALUES (@accountId, @profileId, 0, 1, 0, 1, @now);
                """, new { accountId, profileId, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        public async Task<PairedDevices> PairDevicesAsync() => new(
            await PairAsync(A.Owner.Id, (await Accounts.GetDefaultProfileIdAsync(A.Owner.Id))!.Value, "Alex phone"),
            await PairAsync(A.OwnSignIn.Id, A.Person.Id, "Friend phone"),
            await PairAsync(B.Owner.Id, (await Accounts.GetDefaultProfileIdAsync(B.Owner.Id))!.Value, "Bea phone"));

        /// <summary>
        /// Two more sign-ins in household A that a household administrator must not manage: a server administrator and
        /// another household administrator, each with a paired device.
        /// </summary>
        public async Task<(Guid ServerAdminInA, Guid OtherAdminInA)> PairOtherAdministratorsInAAsync()
        {
            // The Engine never makes a server administrator outside the default household (D17); the flag is set
            // directly so the household administrator's view of such a person is still tested.
            var serverInA = await Mutations.CreateAsync(ServerAdmin, new CreateAccountAccessCommand(
                "server.in.a@example.com", IsAdministrator: false, ProfileId: A.Spare.Id, NewProfile: null,
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read },
                Libraries: new HashSet<Guid>(), TemporaryPassword: null));
            using (var connection = _database.CreateConnection())
            {
                connection.Execute("UPDATE profiles SET is_server_admin = 1 WHERE id = @profile;", new { profile = A.Spare.Id });
            }

            var otherAdmin = await Mutations.CreateAsync(ServerAdmin, new CreateAccountAccessCommand(
                "other.admin.in.a@example.com", IsAdministrator: false, ProfileId: A.Kid.Id, NewProfile: null,
                Features: new HashSet<AccountFeatureId> { AccountFeatureId.Read },
                Libraries: new HashSet<Guid>(), TemporaryPassword: null));
            // A Kids profile can't look after a household, so the person becomes a standard one before being promoted.
            using (var connection = _database.CreateConnection())
            {
                connection.Execute("UPDATE profiles SET role = 'StandardUser' WHERE id = @id;", new { id = A.Kid.Id });
            }

            await Mutations.SetHouseholdAdminAsync(ServerAdmin, otherAdmin.Id, true);
            Assert.Equal(A.Id, (await Accounts.GetByIdAsync(serverInA.Id))!.HouseholdId);
            Assert.Equal(A.Id, (await Accounts.GetByIdAsync(otherAdmin.Id))!.HouseholdId);
            return (
                await PairAsync(serverInA.Id, A.Spare.Id, "Server admin phone in A"),
                await PairAsync(otherAdmin.Id, A.Kid.Id, "Other admin phone in A"));
        }

        public IReadOnlyList<AuditRow> AuditRows(string eventType)
        {
            using var connection = _database.CreateConnection();
            return connection.Query<AuditRow>("""
                SELECT actor_account_id AS ActorAccountId, actor_profile_id AS ActorProfileId,
                       subject_type AS SubjectType, subject_id AS SubjectId
                FROM authorization_audit_events WHERE event_type = @eventType ORDER BY id;
                """, new { eventType }).ToList();
        }

        private async Task<Guid> PairAsync(Guid accountId, Guid profileId, string deviceName)
        {
            var started = await _authorization.BeginAsync(new DeviceAuthorizationRequest
            {
                ClientId = "tuvima-tv",
                ClientName = "Test app",
                ClientVersion = "1.0.0",
                DeviceName = deviceName,
                DeviceClass = "mobile",
                Scope = "library.read",
                Capabilities = new ClientCapabilitiesDto { Containers = ["mp4"], VideoCodecs = ["h264"], AudioCodecs = ["aac"] },
            }, "https://library.example");
            Assert.True(await _authorization.DecideAsync(
                new PairingDecisionRequest { UserCode = started.UserCode, Approved = true, Scopes = ["library.read"] },
                accountId, profileId, profileId));
            var issued = await _authorization.ExchangeAsync(new OAuthTokenRequest
            {
                GrantType = ClientAuthorizationService.DeviceGrantType,
                ClientId = "tuvima-tv",
                DeviceCode = started.DeviceCode,
            });
            return Assert.IsType<OAuthTokenResponse>(issued.Success).DeviceId;
        }

        private string Next() => (++_counter).ToString("D3", System.Globalization.CultureInfo.InvariantCulture);

        private async Task<Place> CreateHouseholdAsync(string email, string personName)
        {
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
            var kid = await Mutations.AddHouseholdPersonAsync(ServerAdmin,
                new AddHouseholdPersonCommand(household.Id, $"{personName}'s kid", null, true, null));
            var given = await Mutations.GiveOwnSignInAsync(ServerAdmin,
                new GiveOwnSignInCommand(person.Id, $"friend.{email}", TemporaryPassword));
            return new Place(household, (await Accounts.GetByIdAsync(owner.Id))!, person, given.Account, spare, kid);
        }

        private static RequestAuthority HouseholdAuthority(Account account, Guid profileId, bool isHouseholdAdmin = true, bool restricted = false, Guid? sessionId = null) =>
            new(PrincipalKind.Human, true,
                AccountId: account.Id,
                ActiveProfileId: profileId,
                SessionId: sessionId ?? Guid.NewGuid(),
                AccountEnabled: true,
                ActiveProfileInUserHousehold: true,
                ActiveProfileIsRestricted: restricted,
                ActiveProfileHouseholdId: account.HouseholdId,
                ActiveProfileIsHouseholdAdministrator: isHouseholdAdmin);

        private static RequestAuthority ServerAuthority(Guid accountId, Guid profileId, Guid? sessionId = null) =>
            new(PrincipalKind.Human, true,
                AccountId: accountId,
                ActiveProfileId: profileId,
                SessionId: sessionId ?? Guid.NewGuid(),
                AccountEnabled: true,
                ActiveProfileInUserHousehold: true,
                ActiveProfileIsServerAdministrator: true);

        public ValueTask DisposeAsync()
        {
            _configuration.Dispose();
            _database.Dispose();
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

    private sealed class AuditRow
    {
        public Guid? ActorAccountId { get; init; }
        public Guid? ActorProfileId { get; init; }
        public string SubjectType { get; init; } = "";
        public string SubjectId { get; init; } = "";
    }

    /// <summary>The real rule for who is an administrator; one session can be marked as not yet unlocked.</summary>
    private sealed class Decisions(Guid lockedSession) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateHouseholdAdministratorAsync(RequestAuthority authority,
            bool requireSurfaceUnlock, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(!authority.IsEffectiveHouseholdAdministrator
                ? AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired)
                : requireSurfaceUnlock && authority.SessionId == lockedSession
                    ? AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorUnlockRequired)
                    : AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid libraryId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(!authority.IsEffectiveAdministrator
                ? AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired)
                : requireSurfaceUnlock && authority.SessionId == lockedSession
                    ? AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorUnlockRequired)
                    : AuthorizationDecision.Allow());
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

    /// <summary>People are set up by a bootstrap actor that is not stored, so setup writes no audit rows.</summary>
    private sealed class NoOpAudit : IAuthorizationAuditWriter
    {
        public ValueTask WriteAsync(AuthorizationAuditEvent auditEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class SpyNotifier : IDeviceRevocationNotifier
    {
        public List<Guid> Revoked { get; } = [];

        public Task NotifyDeviceRevokedAsync(Guid deviceId, CancellationToken ct = default)
        {
            Revoked.Add(deviceId);
            return Task.CompletedTask;
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
