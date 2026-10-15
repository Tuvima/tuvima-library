using MediaEngine.Api.Security;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// The later-wins rule for progress a paired app recorded while offline. Per asset, the later of the server's last
/// update and the device's own timestamp stands. A device clock that runs ahead cannot win forever: a timestamp later
/// than the server's clock is clamped to the server's clock. Requests that are not marked offline, or that carry no
/// timestamp, never use this rule.
/// </summary>
public static class OfflineProgressSyncPolicy
{
    /// <summary>
    /// The time to record for an offline replay, or null when the request is not an offline replay with a timestamp.
    /// A timestamp later than <paramref name="now"/> is treated as <paramref name="now"/>.
    /// </summary>
    public static DateTimeOffset? ResolveClientTimestamp(bool offline, DateTimeOffset? clientTimestamp, DateTimeOffset now)
    {
        if (!offline || clientTimestamp is not { } recorded)
        {
            return null;
        }

        // Always UTC: the stored text is compared and sorted as text elsewhere, so a +05:00 offset must not survive.
        return (recorded > now ? now : recorded).ToUniversalTime();
    }

    /// <summary>
    /// Only a paired app's own token may replay offline progress. For anyone else (a Dashboard session, a service
    /// credential) <c>offline</c> and <c>client_timestamp</c> are ignored and the request is an ordinary one.
    /// </summary>
    public static bool MayReplayOffline(RequestAuthority authority) => PhoneBackupProfileService.IsPairedApp(authority);

    /// <summary>The heartbeat as the Engine will treat it: the offline fields removed unless the caller is a paired app.</summary>
    public static PlayerHeartbeatDto ForCaller(PlayerHeartbeatDto heartbeat, RequestAuthority authority) =>
        heartbeat.Offline && !MayReplayOffline(authority)
            ? heartbeat with { Offline = false, ClientTimestamp = null }
            : heartbeat;

    /// <summary>True when the device's record is strictly later than the server's. A tie keeps the server's record.</summary>
    public static bool ClientWins(DateTimeOffset serverUpdatedAt, DateTimeOffset clientTimestamp) =>
        clientTimestamp > serverUpdatedAt;
}
