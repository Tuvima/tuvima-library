using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Helpers;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Raises the one Needs Review item for a work that two Series claim equally as main sequence. Every path that takes a
/// work off its shelf for that reason (collection assignment and manifest linking) calls this, so the item is the same
/// and the dedupe is the same: the item hangs on the work's first file, so a second run, or the other path, adds nothing.
/// </summary>
public sealed class SeriesAmbiguityReviewer
{
    private readonly StageOutcomeFactory _outcomes;
    private readonly ICollectionRepository _collections;
    private readonly IMediaAssetRepository? _assets;
    private readonly ILogger<SeriesAmbiguityReviewer> _logger;

    public SeriesAmbiguityReviewer(
        StageOutcomeFactory outcomes,
        ICollectionRepository collections,
        ILogger<SeriesAmbiguityReviewer> logger,
        IMediaAssetRepository? assets = null)
    {
        _outcomes = outcomes;
        _collections = collections;
        _assets = assets;
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
        if (_assets is not null)
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
}
