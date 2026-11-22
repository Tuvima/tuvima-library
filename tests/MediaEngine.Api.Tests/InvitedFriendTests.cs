using MediaEngine.Api.Endpoints;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Identity;
using World = MediaEngine.Api.Tests.UserDeletionTests.World;

namespace MediaEngine.Api.Tests;

/// <summary>
/// "Share with friends" (spec remote-access WP5b, D13 and D15): an invitation made by the server administrator for a friend works once,
/// expires, always ends with the friend choosing a password, leaves them only in the household and libraries the administrator chose, and
/// never grants administration. Real temporary data store; the invitation system is the existing one.
/// </summary>
public sealed class InvitedFriendTests
{
    private const string FriendPassword = "a friendly long password";

    [Fact]
    public async Task ARedeemedInvitation_FromOutside_LeavesAPasswordProtectedMemberInOnlyTheChosenHouseholdAndLibraries()
    {
        await using var world = await World.CreateAsync();
        var hiddenLibrary = Guid.NewGuid();
        var friends = (await world.Management.CreateAsync(world.ServerAdmin, "Friends")).Household;
        await new HouseholdLibraryAccessRepository(world.Database)
            .ReplaceHiddenLibrariesAsync(friends.Id, new HashSet<Guid> { hiddenLibrary }, DateTimeOffset.UtcNow, null);
        var created = await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "friend@example.com", null, new NewUserProfileCommand("Friend", null, friends.Id, null), NewUserSignInMethod.Invitation));
        var identity = Identity(world, DateTimeOffset.UtcNow);

        var accepted = await identity.AcceptInvitationAsync(
            created.Invitation!.Code, FriendPassword, "phone", "Phone", "Dashboard", ingress: ClientIngress.Remote);

        Assert.Equal(created.User.Id, accepted.Account.Id);
        Assert.NotNull(await world.Identities.GetAccountCredentialAsync(created.User.Id, AccountCredentialKind.Password));
        Assert.True(await world.Accounts.IsUserSecuredAsync(created.User.Id));
        var profile = (await world.Profiles.GetByIdAsync(created.Profile.Id))!;
        Assert.Equal(friends.Id, profile.HouseholdId);
        Assert.False(profile.IsServerAdministrator);
        Assert.False(profile.IsHouseholdAdministrator);
        var hidden = await new HouseholdLibraryAccessRepository(world.Database).GetHiddenLibrariesForProfileAsync(profile.Id);
        Assert.Equal([hiddenLibrary], hidden.ToArray());
    }

    [Fact]
    public async Task AnInvitationCode_WorksOnce_AndTheSecondTryIsRefused()
    {
        await using var world = await World.CreateAsync();
        var created = await InviteFriendAsync(world);
        var identity = Identity(world, DateTimeOffset.UtcNow);

        await identity.AcceptInvitationAsync(created.Invitation!.Code, FriendPassword, "a", "A", "Dashboard", ingress: ClientIngress.Remote);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            identity.AcceptInvitationAsync(created.Invitation.Code, "another long password", "b", "B", "Dashboard", ingress: ClientIngress.Remote));
        Assert.Null(await identity.PreviewInvitationAsync(created.Invitation.Code));
    }

    [Fact]
    public async Task AnInvitation_ExpiresAfterItsLifetime_AndIsRefusedFromOutside()
    {
        await using var world = await World.CreateAsync();
        var created = await InviteFriendAsync(world);
        // The lifetime is seven days by default (168 hours).
        Assert.InRange(created.Invitation!.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromDays(6.9), TimeSpan.FromDays(7.1));
        var later = Identity(world, DateTimeOffset.UtcNow.AddDays(8));

        Assert.Null(await later.PreviewInvitationAsync(created.Invitation.Code));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            later.AcceptInvitationAsync(created.Invitation.Code, FriendPassword, "a", "A", "Dashboard", ingress: ClientIngress.Remote));
        Assert.Null(await world.Identities.GetAccountCredentialAsync(created.User.Id, AccountCredentialKind.Password));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    public async Task AnInvitation_NeverCreatesAnAccountWithoutAPassword_AndStaysUsable(string password)
    {
        await using var world = await World.CreateAsync();
        var created = await InviteFriendAsync(world);
        var identity = Identity(world, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            identity.AcceptInvitationAsync(created.Invitation!.Code, password, "a", "A", "Dashboard", ingress: ClientIngress.Remote));

        Assert.Null(await world.Identities.GetAccountCredentialAsync(created.User.Id, AccountCredentialKind.Password));
        Assert.NotNull(await identity.PreviewInvitationAsync(created.Invitation!.Code));
    }

    [Theory]
    [InlineData(ClientIngress.Remote, NetworkConnectionModes.LocalOnly, false)]
    [InlineData("", NetworkConnectionModes.LocalOnly, false)]
    [InlineData("somewhere-else", NetworkConnectionModes.LocalOnly, false)]
    [InlineData(ClientIngress.Remote, NetworkConnectionModes.Tailscale, true)]
    [InlineData(ClientIngress.Remote, NetworkConnectionModes.Custom, true)]
    [InlineData(ClientIngress.Remote, NetworkConnectionModes.DirectOnly, true)]
    [InlineData(ClientIngress.HomeNetwork, NetworkConnectionModes.LocalOnly, true)]
    [InlineData(ClientIngress.ThisComputer, NetworkConnectionModes.LocalOnly, true)]
    public void AnInvitationFromOutside_IsRefusedWhileOutsideAccessIsOff(string ingress, string mode, bool allowed)
    {
        var network = new NetworkSettings { Remote = { ConnectionMode = mode } };

        Assert.Equal(allowed, AuthenticationEndpoints.AllowsInvitationFrom(network, ingress));
    }

    private static async Task<CreatedUser> InviteFriendAsync(World world)
    {
        var friends = (await world.Management.CreateAsync(world.ServerAdmin, "Friends")).Household;
        return await world.Mutations.CreateUserAsync(world.ServerAdmin, new CreateUserCommand(
            "friend@example.com", null, new NewUserProfileCommand("Friend", null, friends.Id, null), NewUserSignInMethod.Invitation));
    }

    private static FirstPartyIdentityService Identity(World world, DateTimeOffset now) =>
        new(world.Identities, world.Accounts, world.Profiles,
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            new FixedTime(now), new ConfigurationAuthenticationPolicyProvider(world.Configuration));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
