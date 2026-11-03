using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Saves "Match the other episodes in this season by number" (kind <c>sibling_match</c>, universal editor v3 D4). The chosen episode's own
/// match is a separate identity change and runs first; this one then gives each other episode of the folder season the episode of the
/// same season of the chosen show that has its number, through the very write the retail-match action uses, and never moves an episode.
/// <para>
/// <b>No undo</b>, for the same reason an identity change has none (provider claims, bridge ids and a durable enrichment job are written
/// in several steps), so the session service runs it after the identity changes, last among the data changes.
/// </para>
/// </summary>
public sealed class SiblingMatchEditSessionChangeHandler(IServiceScopeFactory scopes, EditSessionCallerAccessor callers) : IEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.SiblingMatch;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (!EditSessionSiblingMatchTarget.TryParse(change.TargetId, out var target) || target is null)
        {
            return EditSessionReasons.MatchUnreadable;
        }

        using var scope = scopes.CreateScope();
        var matcher = scope.ServiceProvider.GetRequiredService<IEpisodeSiblingMatcher>();
        return await matcher.CheckAsync(change.EntityId, target, callers.Current?.CanEdit, ct).ConfigureAwait(false);
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (!EditSessionSiblingMatchTarget.TryParse(change.TargetId, out var target) || target is null)
        {
            throw new InvalidOperationException(EditSessionReasons.MatchUnreadable);
        }

        using var scope = scopes.CreateScope();
        var matcher = scope.ServiceProvider.GetRequiredService<IEpisodeSiblingMatcher>();
        await matcher.ApplyAsync(change.EntityId, target, callers.Current?.CanEdit, ct).ConfigureAwait(false);
        return null;
    }
}
