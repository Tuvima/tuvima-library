using System.Collections.Concurrent;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>
/// Real use of the Vault keeps NSFW mode awake from the Engine's side (Videos spec D33: the idle timer never interrupts a video that
/// is playing). Every request that is cleared to reach a Vault file, such as a stream range, a heartbeat, a cover or a detail
/// read, restarts the unlocked session's idle timer, so the Dashboard does not have to remember to ask. The restart is spread out
/// (at most one write per session every <see cref="MinimumGap"/>), because a video player asks for many ranges a minute.
/// </summary>
public sealed class NsfwIdleKeeper(INsfwAccessRepository nsfw, TimeProvider clock)
{
    /// <summary>How long a restart is trusted before the next request writes another one. Far below any idle time a profile can pick.</summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(15);

    private const int PruneAbove = 256;
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> LastRestart = new();

    /// <summary>Restarts the idle timer of an unlocked session, unless it was restarted a moment ago. Never throws for a locked session.</summary>
    public async ValueTask KeepAwakeAsync(Guid sessionId, Guid profileId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (LastRestart.TryGetValue(sessionId, out var last) && now - last < MinimumGap && now >= last)
        {
            return;
        }

        var access = await nsfw.GetAsync(profileId, ct).ConfigureAwait(false);
        if (!access.CanUnlock)
        {
            return;
        }

        var idle = NsfwAccessRules.ResolveIdleLockMinutes(access.EffectiveMode, access.IdleLockMinutes);
        if (await nsfw.TouchAsync(sessionId, profileId, idle, now, ct).ConfigureAwait(false))
        {
            LastRestart[sessionId] = now;
            Prune(now);
        }
    }

    private static void Prune(DateTimeOffset now)
    {
        if (LastRestart.Count <= PruneAbove)
        {
            return;
        }

        foreach (var (session, at) in LastRestart)
        {
            if (now - at > TimeSpan.FromMinutes(10) || at > now)
            {
                LastRestart.TryRemove(session, out _);
            }
        }
    }
}
