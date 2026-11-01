using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests.Support;

/// <summary>Signed-in Dashboard sessions for tests: an adult, an unlocked administrator or a Kids profile.</summary>
internal static class TestSessions
{
    public static readonly Guid Account = Guid.NewGuid();
    public static readonly Guid Profile = Guid.NewGuid();

    public static DashboardSessionAccessor For(bool kids, bool administrator)
    {
        var grants = new[]
        {
            new AccountProfileGrantDto(
                Account, Profile, "Profile", null, true, true, administrator,
                new ProfileAdminProtectionResponse(false, "UntilProfileSwitch", 30, 1), 1, DateTimeOffset.UnixEpoch, kids),
        };
        var authority = new DashboardAuthorityResponse(
            Account, Profile, true, true, 1, 1, administrator, administrator, null, 1, grants, [],
            administrator ? ["access.manage"] : []);
        var session = new DashboardSessionAccessor();
        session.Set("token", Account, Profile, Guid.NewGuid(), authority);
        return session;
    }
}
