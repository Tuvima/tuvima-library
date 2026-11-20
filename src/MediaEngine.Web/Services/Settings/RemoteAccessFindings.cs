using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Services.Settings;

/// <summary>How the guided flow shows what the Engine's check found: which tick a status gets, and which findings belong to which step.</summary>
public static class RemoteAccessFindings
{
    /// <summary>The tick for a finding's status: ok is green, warn amber, fail red, and plain information grey.</summary>
    public static AppStatusDotTone ToneFor(string? status) => status switch
    {
        RemoteCheckStatuses.Ok => AppStatusDotTone.Working,
        RemoteCheckStatuses.Warn => AppStatusDotTone.Attention,
        RemoteCheckStatuses.Fail => AppStatusDotTone.Broken,
        _ => AppStatusDotTone.Off,
    };

    /// <summary>The two findings about passwords; they are the Passwords step's, not the network check's.</summary>
    public static bool IsPasswordFinding(string? key) =>
        key is RemoteCheckFindingKeys.ProfilesWithoutPassword or RemoteCheckFindingKeys.AdminWithoutPassword;

    /// <summary>The four Tailscale findings; the Connect step reads them as its live ticks.</summary>
    public static bool IsTailscaleFinding(string? key) =>
        key is RemoteCheckFindingKeys.TailscaleNotInstalled
            or RemoteCheckFindingKeys.TailscaleNotSignedIn
            or RemoteCheckFindingKeys.TailscaleHttpsOff
            or RemoteCheckFindingKeys.TailscaleReady;
}

/// <summary>How far Tailscale is on this server, read from the check's Tailscale findings.</summary>
public enum TailscaleProgress
{
    /// <summary>The check has not said anything about Tailscale yet.</summary>
    Unknown,
    NotInstalled,
    NotSignedIn,
    HttpsOff,
    Ready,
}

public static class TailscaleProgressReader
{
    /// <summary>The furthest stage the findings show (only findings that apply are returned, so the first one found says where it stands).</summary>
    public static TailscaleProgress Read(RemoteCheckResultDto? result)
    {
        var keys = result?.Findings.Select(finding => finding.Key).ToHashSet(StringComparer.Ordinal);
        if (keys is null)
        {
            return TailscaleProgress.Unknown;
        }

        if (keys.Contains(RemoteCheckFindingKeys.TailscaleReady))
        {
            return TailscaleProgress.Ready;
        }

        if (keys.Contains(RemoteCheckFindingKeys.TailscaleHttpsOff))
        {
            return TailscaleProgress.HttpsOff;
        }

        if (keys.Contains(RemoteCheckFindingKeys.TailscaleNotSignedIn))
        {
            return TailscaleProgress.NotSignedIn;
        }

        return keys.Contains(RemoteCheckFindingKeys.TailscaleNotInstalled) ? TailscaleProgress.NotInstalled : TailscaleProgress.Unknown;
    }
}
