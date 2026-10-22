using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The one rule for whether first-run setup may add View folders (Library setup spec D46, D58): only once setup's own session is
/// gone and the Dashboard is signed in as an administrator, because View folders go through the same Engine actions Settings uses,
/// never through the setup session. Anything else keeps today's "add after setup" line.
/// </summary>
public static class SetupViewAccess
{
    /// <summary>The signed-in administrator's profile id when setup may add View folders, else null.</summary>
    public static Guid? OwnerProfileId(string? setupSession, DashboardSessionAccessor session) =>
        string.IsNullOrWhiteSpace(setupSession) && session.Authority is { EffectiveAdministrator: true } authority
            ? authority.ActiveProfileId
            : null;
}
