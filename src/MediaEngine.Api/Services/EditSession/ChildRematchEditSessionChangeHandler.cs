using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Saves "Re-match seasons and episodes" (kind <c>child_rematch</c>, universal editor v3 D8 item 3). The show's own match is a separate
/// identity change and runs first; this one then matches each of the show's episodes against the new show by season and episode number,
/// and sends the ones the new show has no slot for to Needs Review.
/// <para>
/// <b>No undo</b>, for the same reason an identity change has none, so the session service runs it last among the data changes.
/// </para>
/// </summary>
public sealed class ChildRematchEditSessionChangeHandler(IServiceScopeFactory scopes, EditSessionCallerAccessor callers) : IEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.ChildRematch;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (!EditSessionChildRematchTarget.TryParse(change.TargetId, out var target) || target is null)
        {
            return EditSessionReasons.MatchUnreadable;
        }

        using var scope = scopes.CreateScope();
        var matcher = scope.ServiceProvider.GetRequiredService<IShowChildRematcher>();
        return await matcher.CheckAsync(change.EntityId, target, callers.Current?.CanEdit, ct).ConfigureAwait(false);
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (!EditSessionChildRematchTarget.TryParse(change.TargetId, out var target) || target is null)
        {
            throw new InvalidOperationException(EditSessionReasons.MatchUnreadable);
        }

        using var scope = scopes.CreateScope();
        var matcher = scope.ServiceProvider.GetRequiredService<IShowChildRematcher>();
        await matcher.ApplyAsync(change.EntityId, target, callers.Current?.CanEdit, ct).ConfigureAwait(false);
        return null;
    }
}
