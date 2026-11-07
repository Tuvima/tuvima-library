using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Saves "Apply" on the Match tab's Wikidata side (kind <c>wikidata_link</c>): the item is linked to a Wikidata item. Only the Wikidata identity
/// changes; the provider match is never touched, and a retail match never touches the link (universal editor v5, 3c "Independence").
/// The check is the Engine's own and does not trust the Dashboard: the item is read from Wikidata again and a parent (the show for an episode,
/// the album for a song, the book series for a book) is refused.
/// <para>No undo: it rewrites claims, the canonical value and the Work's Wikidata state in several steps, so it runs with the other identity changes, after those that can be put back.</para>
/// </summary>
public sealed class WikidataLinkEditSessionChangeHandler(IServiceScopeFactory scopes, EditorSeriesGuard? seriesGuard = null) : IEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.WikidataLink;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (seriesGuard is not null && await seriesGuard.IsSeriesAsync(change.EntityId, ct).ConfigureAwait(false))
        {
            return EditSessionReasons.SeriesIsReadOnly;
        }

        if (EditorWikidataMatchService.ParseQid(change.TargetId) is not { } qid)
        {
            return EditSessionReasons.WikidataItemUnreadable;
        }

        using var scope = scopes.CreateScope();
        var lookups = scope.ServiceProvider.GetRequiredService<IExternalLookupGuard>();
        if (await lookups.BlocksEntityAsync(change.EntityId, ct).ConfigureAwait(false))
        {
            return EditSessionReasons.WikidataLookupsOff;
        }

        EditorWikidataResultDto? described;
        try
        {
            described = await scope.ServiceProvider.GetRequiredService<EditorWikidataMatchService>()
                .DescribeOneAsync(change.EntityId, qid, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            scope.ServiceProvider.GetService<ILogger<WikidataLinkEditSessionChangeHandler>>()
                ?.LogWarning(ex, "Wikidata did not answer the check of {Qid} for item {EntityId}.", qid, change.EntityId);
            return EditSessionReasons.WikidataDidNotAnswer;
        }

        if (described is null)
        {
            return EditSessionReasons.WikidataItemUnreadable;
        }

        return described.CanApply ? null : EditSessionReasons.WikidataParentNotAllowed;
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (await CheckAsync(change, ct).ConfigureAwait(false) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        var qid = EditorWikidataMatchService.ParseQid(change.TargetId)!;
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWikidataIdentityWriter>().SetAsync(change.EntityId, qid, ct).ConfigureAwait(false);
        return null;
    }
}

/// <summary>
/// Saves "Remove match" on the Match tab's Wikidata side (kind <c>wikidata_removal</c>): the Wikidata link goes and nothing else. The provider
/// match, details, locked fields and pictures stay.
/// </summary>
public sealed class WikidataRemovalEditSessionChangeHandler(IServiceScopeFactory scopes, EditorSeriesGuard? seriesGuard = null) : IEditSessionChangeHandler
{
    public string Kind => EditSessionChangeKinds.WikidataRemoval;

    public async Task<string?> CheckAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (seriesGuard is not null && await seriesGuard.IsSeriesAsync(change.EntityId, ct).ConfigureAwait(false))
        {
            return EditSessionReasons.SeriesIsReadOnly;
        }

        using var scope = scopes.CreateScope();
        var current = await scope.ServiceProvider.GetRequiredService<IWikidataIdentityWriter>().ReadAsync(change.EntityId, ct).ConfigureAwait(false);
        return current is null ? EditSessionReasons.NoWikidataLinkToRemove : null;
    }

    public async Task<Func<CancellationToken, Task>?> ApplyAsync(EditSessionChange change, CancellationToken ct = default)
    {
        if (await CheckAsync(change, ct).ConfigureAwait(false) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWikidataIdentityWriter>().RemoveAsync(change.EntityId, ct).ConfigureAwait(false);
        return null;
    }
}
