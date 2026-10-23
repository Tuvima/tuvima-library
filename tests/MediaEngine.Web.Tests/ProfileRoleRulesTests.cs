using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Profiles;

namespace MediaEngine.Web.Tests;

/// <summary>What the Profiles screens offer for roles and Delete profile. The Engine enforces the same limits; these only decide what is offered.</summary>
public sealed class ProfileRoleRulesTests
{
    private static readonly Guid Home = Guid.NewGuid();
    private static readonly Guid Cabin = Guid.NewGuid();

    private static ManagedProfileResponse Person(string name, bool household = false, bool server = false, bool kids = false, bool signIn = true, Guid? householdId = null) =>
        new(Guid.NewGuid(), name, "#C9922E", null, DateTimeOffset.UtcNow, householdId ?? Home, kids,
            IsServerAdmin: server, IsHouseholdAdmin: household,
            User: signIn ? new ManagedProfileUserDto(Guid.NewGuid(), $"{name.ToLowerInvariant()}@home.test") : null);

    [Fact]
    public void HouseholdAdministratorSwitch_IsEnabled_ForAnOrdinaryMemberWithASignIn()
    {
        var sam = Person("Sam");

        var state = ProfileRoleRules.HouseholdAdministrator(sam, [Person("Mom", household: true), sam], viewerIsServerAdministrator: false);

        Assert.True(state.Visible);
        Assert.True(state.Enabled);
        Assert.False(state.On);
        Assert.Null(state.Reason);
    }

    [Fact]
    public void HouseholdAdministratorSwitch_IsDisabled_WhenThePersonHasNoSignIn()
    {
        var sam = Person("Sam", signIn: false);

        var state = ProfileRoleRules.HouseholdAdministrator(sam, [Person("Mom", household: true), sam], viewerIsServerAdministrator: false);

        Assert.False(state.Enabled);
        Assert.Equal(ProfileRoleRules.NoSignInReason, state.Reason);
    }

    [Fact]
    public void HouseholdAdministratorSwitch_IsDisabled_ForAKidsProfile()
    {
        var robin = Person("Robin", kids: true);

        var state = ProfileRoleRules.HouseholdAdministrator(robin, [Person("Mom", household: true), robin], viewerIsServerAdministrator: true);

        Assert.False(state.Enabled);
        Assert.Equal(ProfileRoleRules.KidsReason, state.Reason);
    }

    [Fact]
    public void HouseholdAdministratorSwitch_IsOnButLocked_ForTheLastHouseholdAdministrator()
    {
        var mom = Person("Mom", household: true);
        var sam = Person("Sam");

        var state = ProfileRoleRules.HouseholdAdministrator(mom, [mom, sam], viewerIsServerAdministrator: false);

        Assert.True(state.On);
        Assert.False(state.Enabled);
        Assert.Equal(ProfileRoleRules.LastHouseholdAdministratorReason, state.Reason);
    }

    [Fact]
    public void HouseholdAdministratorSwitch_CanBeTurnedOff_WhileAnotherHouseholdAdministratorWithASignInRemains()
    {
        var mom = Person("Mom", household: true);
        var dad = Person("Dad", household: true);

        var state = ProfileRoleRules.HouseholdAdministrator(mom, [mom, dad], viewerIsServerAdministrator: false);

        Assert.True(state.Enabled);
    }

    [Fact]
    public void HouseholdAdministratorSwitch_IsLocked_ForAServerAdministratorsProfile_WhenTheViewerIsOnlyAHouseholdAdministrator()
    {
        var shy = Person("Shy", household: true, server: true);

        var asHousehold = ProfileRoleRules.HouseholdAdministrator(shy, [shy, Person("Mom", household: true)], viewerIsServerAdministrator: false);
        var asServer = ProfileRoleRules.HouseholdAdministrator(shy, [shy, Person("Mom", household: true)], viewerIsServerAdministrator: true);

        Assert.False(asHousehold.Enabled);
        Assert.Equal(ProfileRoleRules.ServerAdministratorOnlyReason, asHousehold.Reason);
        Assert.True(asServer.Enabled);
    }

    [Fact]
    public void ServerAdministratorSwitch_IsHidden_ForAnyoneButAServerAdministrator()
    {
        var sam = Person("Sam");

        var state = ProfileRoleRules.ServerAdministrator(sam, [sam], viewerIsServerAdministrator: false, viewerHouseholdId: Home);

        Assert.False(state.Visible);
    }

    [Fact]
    public void ServerAdministratorSwitch_IsHidden_ForSomeoneOutsideTheLibrarysOwnHousehold()
    {
        var alex = Person("Alex", householdId: Cabin);

        var state = ProfileRoleRules.ServerAdministrator(alex, [alex], viewerIsServerAdministrator: true, viewerHouseholdId: Home);

        Assert.False(state.Visible);
    }

    [Fact]
    public void ServerAdministratorSwitch_ExplainsWhyItIsOff_ForKidsAndForProfilesWithoutASignIn()
    {
        var robin = Person("Robin", kids: true);
        var sam = Person("Sam", signIn: false);

        var kids = ProfileRoleRules.ServerAdministrator(robin, [robin, sam], viewerIsServerAdministrator: true, viewerHouseholdId: Home);
        var noSignIn = ProfileRoleRules.ServerAdministrator(sam, [robin, sam], viewerIsServerAdministrator: true, viewerHouseholdId: Home);

        Assert.Equal(ProfileRoleRules.KidsServerReason, kids.Reason);
        Assert.Equal(ProfileRoleRules.ServerNoSignInReason, noSignIn.Reason);
        Assert.False(kids.Enabled);
        Assert.False(noSignIn.Enabled);
    }

    [Fact]
    public void ServerAdministratorSwitch_IsLocked_ForTheLastServerAdministrator()
    {
        var shy = Person("Shy", server: true);
        var mom = Person("Mom");

        var last = ProfileRoleRules.ServerAdministrator(shy, [shy, mom], viewerIsServerAdministrator: true, viewerHouseholdId: Home);
        var notLast = ProfileRoleRules.ServerAdministrator(shy, [shy, Person("Mom", server: true)], viewerIsServerAdministrator: true, viewerHouseholdId: Home);

        Assert.True(last.On);
        Assert.False(last.Enabled);
        Assert.Equal(ProfileRoleRules.LastServerAdministratorReason, last.Reason);
        Assert.True(notLast.Enabled);
    }

    [Fact]
    public void Delete_IsNeverOffered_ForTheProfileInUse_OrAHouseholdsOnlyPerson()
    {
        var mom = Person("Mom", household: true);
        var sam = Person("Sam");

        Assert.False(ProfileRoleRules.CanDelete(sam, sam.Id, [mom, sam], viewerIsServerAdministrator: false));
        Assert.False(ProfileRoleRules.CanDelete(sam, mom.Id, [sam], viewerIsServerAdministrator: false));
        Assert.True(ProfileRoleRules.CanDelete(sam, mom.Id, [mom, sam], viewerIsServerAdministrator: false));
    }

    [Fact]
    public void Delete_IsNotOffered_ForTheLastHouseholdAdministrator_ButIsForOneOfSeveral()
    {
        var mom = Person("Mom", household: true);
        var dad = Person("Dad", household: true);
        var sam = Person("Sam");
        var server = Guid.NewGuid();

        Assert.False(ProfileRoleRules.CanDelete(mom, server, [mom, sam], viewerIsServerAdministrator: true));
        Assert.True(ProfileRoleRules.CanDelete(mom, server, [mom, dad, sam], viewerIsServerAdministrator: true));
    }

    [Fact]
    public void Delete_IsNotOffered_ForAServerAdministratorsProfile_ToAHouseholdAdministrator()
    {
        var shy = Person("Shy", household: true, server: true);
        var momServer = Person("Mom", household: true, server: true);
        var viewer = Guid.NewGuid();

        Assert.False(ProfileRoleRules.CanDelete(momServer, viewer, [shy, momServer], viewerIsServerAdministrator: false));
        Assert.True(ProfileRoleRules.CanDelete(momServer, viewer, [shy, momServer], viewerIsServerAdministrator: true));
    }

    [Fact]
    public void Delete_IsNotOffered_ForTheOnlyServerAdministrator()
    {
        var shy = Person("Shy", household: true, server: true);
        var mom = Person("Mom", household: true);

        Assert.False(ProfileRoleRules.CanDelete(shy, mom.Id, [shy, mom], viewerIsServerAdministrator: true));
    }
}
