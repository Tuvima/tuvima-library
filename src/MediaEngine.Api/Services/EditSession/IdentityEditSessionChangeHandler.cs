using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Saves a retail match chosen in the Match tool (kind <c>identity</c>): which provider record this item is. It runs the very write
/// the retail-match action runs, with one difference that is the point of the kind (universal editor spec D6): the item is never moved.
/// Identity and details change; its album, season or series, and its files, stay as they are until a relationship or a physical-files
/// change says otherwise.
/// <para>
/// <b>No undo.</b> A retail match writes provider claims, bridge ids, a revision and a durable enrichment job in several steps, and none
/// of that can be put back exactly. So this kind says so (<see cref="ApplyAsync"/> returns no undo) and the session service runs it
/// <i>last</i> among the data changes: by then the changes that can be undone were already written, and if the match itself fails they
/// are undone rather than the other way round.
/// </para>
/// </summary>
public sealed class IdentityEditSessionChangeHandler(IServiceScopeFactory scopes) : IEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.Identity;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (!EditSessionIdentityTarget.TryParse(change.TargetId, out var match) || match is null)
        {
            return EditSessionReasons.MatchUnreadable;
        }

        using var scope = scopes.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IRetailMatchWriter>();
        var outcome = ReadOutcome(await writer.CheckAsync(change.EntityId, match, ct).ConfigureAwait(false));
        return outcome.Ok ? null : outcome.Reason;
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (!EditSessionIdentityTarget.TryParse(change.TargetId, out var match) || match is null)
        {
            throw new InvalidOperationException(EditSessionReasons.MatchUnreadable);
        }

        using var scope = scopes.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IRetailMatchWriter>();
        var outcome = ReadOutcome(await writer.ReplaceWithoutMovingAsync(change.EntityId, match, ct).ConfigureAwait(false));
        if (!outcome.Ok)
        {
            throw new InvalidOperationException(outcome.Reason);
        }

        await ResolveReviewItemsAsync(scope.ServiceProvider, change.EntityId, ct).ConfigureAwait(false);
        return null;
    }

    // Needs Review's Fix opens the item's own page on the Match tool (spec D16), so a match chosen there has to close the review items it
    // answers, as the old review pop-up did by carrying the review id. The match is already saved, so a failure here is logged and never fails it.
    private static async Task ResolveReviewItemsAsync(IServiceProvider services, Guid entityId, CancellationToken ct)
    {
        var reviews = services.GetService<IReviewQueueRepository>();
        if (reviews is null)
        {
            return;
        }

        try
        {
            await reviews.ResolveAllByEntityAsync(entityId, ResolvedBy, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var logger = services.GetService<ILogger<IdentityEditSessionChangeHandler>>();
            logger?.LogWarning(ex, "The review items of item {EntityId} could not be closed after its match was saved.", entityId);
        }
    }

    /// <summary>Who the review items are closed by: the person who chose the match.</summary>
    internal const string ResolvedBy = "user";

    /// <summary>Reads the plain-English reason out of the result the shared retail-match write answers with.</summary>
    internal static (bool Ok, string Reason) ReadOutcome(IResult result)
    {
        if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
        {
            return (true, string.Empty);
        }

        var reason = result is IValueHttpResult { Value: ProblemDetails problem }
            ? problem.Detail ?? problem.Title
            : null;
        return (false, string.IsNullOrWhiteSpace(reason) ? EditSessionReasons.SaveFailed : reason);
    }
}
