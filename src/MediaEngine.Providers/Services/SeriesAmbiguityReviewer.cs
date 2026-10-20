using System.Text.Json;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Helpers;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Raises the one Needs Review item for a work that two Series claim equally as main sequence. Every path that takes a
/// work off its shelf for that reason (collection assignment and manifest linking) calls this, so the item is the same
/// and the dedupe is the same.
///
/// The item hangs on one stable file of the work (its earliest-stored Normal original), and a new item is skipped when
/// a pending one already exists on the work or on any of its files, so repeated runs, new editions and orphaned files
/// never create a second one. When the work is later placed without a tie (by any path, including a person's choice),
/// <see cref="ResolveAsync"/> resolves the pending item.
/// </summary>
public sealed class SeriesAmbiguityReviewer
{
    private readonly StageOutcomeFactory _outcomes;
    private readonly ICollectionRepository _collections;
    private readonly IMediaAssetRepository? _assets;
    private readonly IReviewQueueRepository? _reviews;
    private readonly ISeriesPlacementRepository? _placements;
    private readonly ILogger<SeriesAmbiguityReviewer> _logger;

    public SeriesAmbiguityReviewer(
        StageOutcomeFactory outcomes,
        ICollectionRepository collections,
        ILogger<SeriesAmbiguityReviewer> logger,
        IMediaAssetRepository? assets = null,
        IReviewQueueRepository? reviews = null,
        ISeriesPlacementRepository? placements = null)
    {
        _outcomes = outcomes;
        _collections = collections;
        _assets = assets;
        _reviews = reviews;
        _placements = placements;
        _logger = logger;
    }

    /// <param name="workId">The work that was left on no Series.</param>
    /// <param name="candidates">The Series that claim it equally.</param>
    /// <param name="fallbackEntityId">The file to hang the item on when the work has no readable first file.</param>
    /// <returns>The detail text shown to a person.</returns>
    public async Task<string> RaiseAsync(
        Guid workId,
        IReadOnlyList<SeriesPlacementCandidate> candidates,
        Guid fallbackEntityId,
        CancellationToken ct = default)
    {
        var names = new List<string>();
        foreach (var candidate in candidates)
        {
            var shelf = await _collections.GetByIdAsync(candidate.CollectionId, ct).ConfigureAwait(false);
            names.Add(string.IsNullOrWhiteSpace(shelf?.DisplayName) ? candidate.SeriesQid : shelf.DisplayName);
        }

        var detail = $"Wikidata lists this {candidates.FirstOrDefault()?.MediaType ?? "work"} in the main sequence of more than one Series "
            + $"({string.Join(", ", names)}) and nothing says which one it belongs to. It is not on any Series until you choose.";

        _logger.LogWarning(
            "Series ambiguity: work {WorkId} is in the main sequence of {Series}; leaving it unplaced",
            workId,
            string.Join(", ", candidates.Select(c => c.SeriesQid)));

        var entityId = fallbackEntityId;
        if (_placements is not null)
        {
            var entities = await _placements.GetReviewEntitiesAsync(workId, ct).ConfigureAwait(false);
            if (_reviews is not null)
            {
                foreach (var candidateEntity in entities.EntityIds)
                {
                    var pending = await _reviews.GetPendingByEntityAsync(candidateEntity, ct).ConfigureAwait(false);
                    if (pending.Any(r => string.Equals(r.Trigger, ReviewTrigger.AmbiguousSeriesMembership, StringComparison.Ordinal)))
                    {
                        // One pending question per work, whichever file or run raised it.
                        return detail;
                    }
                }
            }

            entityId = entities.StableAssetId ?? fallbackEntityId;
        }
        else if (_assets is not null)
        {
            var first = await _assets.FindFirstByWorkIdAsync(workId, ct).ConfigureAwait(false);
            if (first is not null)
            {
                entityId = first.Id;
            }
        }

        var candidatesJson = JsonSerializer.Serialize(candidates.Select((c, index) => new
        {
            series_qid = c.SeriesQid,
            series_label = names[index],
            media_type = c.MediaType,
            membership_scope = c.Scope,
            collection_id = c.CollectionId,
        }));
        await _outcomes.CreateAmbiguousSeriesMembershipAsync(entityId, detail, candidatesJson, ct: ct).ConfigureAwait(false);
        return detail;
    }

    /// <summary>Closes the "which Series?" questions of many works at once: a few statements, not a few per work.</summary>
    public async Task ResolveManyAsync(IReadOnlyCollection<Guid> workIds, CancellationToken ct = default)
    {
        if (_reviews is null || workIds.Count == 0)
        {
            return;
        }

        var entityIds = _placements is null
            ? workIds
            : await _placements.GetReviewEntityIdsAsync(workIds, ct).ConfigureAwait(false);
        await _reviews
            .ResolvePendingByEntitiesAndTriggersAsync(
                entityIds,
                [ReviewTrigger.AmbiguousSeriesMembership],
                "system:series-placement",
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The work's placement was decided without a tie, so any pending "which Series?" item about it (on the work or on any
    /// of its files) is answered.
    /// </summary>
    public async Task ResolveAsync(Guid workId, CancellationToken ct = default)
    {
        if (_reviews is null)
        {
            return;
        }

        var entityIds = _placements is null
            ? [workId]
            : (await _placements.GetReviewEntitiesAsync(workId, ct).ConfigureAwait(false)).EntityIds;
        foreach (var id in entityIds)
        {
            await _reviews
                .ResolvePendingByEntityAndTriggersAsync(
                    id,
                    [ReviewTrigger.AmbiguousSeriesMembership],
                    "system:series-placement",
                    ct)
                .ConfigureAwait(false);
        }
    }
}
