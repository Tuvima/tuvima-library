using MediaEngine.Domain.Contracts;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>What happened when a person let the Library decide a Work's Series again.</summary>
public enum SeriesPlacementReleaseResult
{
    /// <summary>The choice was released and placement ran for the Work.</summary>
    Released,

    /// <summary>The choice was released but placement could not run; the next scan places the Work.</summary>
    PlacementFailed,
}

/// <summary>
/// Saves and releases a person's Series choice for one Work, and runs everything normal placement runs afterwards: the
/// Work's pending "which Series?" question is closed, and the Universe (parent-collection) step runs for the shelf it joined
/// and the shelf it left, so a choice never leaves the Universe grouping behind.
/// </summary>
public sealed class SeriesPlacementChoiceService
{
    private readonly ISeriesPlacementRepository _placements;
    private readonly ISeriesManifestRepository _manifests;
    private readonly CollectionFinalizationService _finalization;
    private readonly SeriesAmbiguityReviewer _reviewer;
    private readonly IParentCollectionResolver _parents;
    private readonly ILogger<SeriesPlacementChoiceService> _logger;

    public SeriesPlacementChoiceService(
        ISeriesPlacementRepository placements,
        ISeriesManifestRepository manifests,
        CollectionFinalizationService finalization,
        SeriesAmbiguityReviewer reviewer,
        IParentCollectionResolver parents,
        ILogger<SeriesPlacementChoiceService> logger)
    {
        _placements = placements;
        _manifests = manifests;
        _finalization = finalization;
        _reviewer = reviewer;
        _parents = parents;
        _logger = logger;
    }

    /// <summary>Saves the choice as a lock (a Series shelf, or "not in a Series" when <paramref name="collectionId"/> is null).</summary>
    public async Task<SeriesPlacementChoiceResult> ChooseAsync(Guid workId, Guid? collectionId, CancellationToken ct = default)
    {
        var before = await _placements.GetCurrentShelfIdAsync(workId, ct).ConfigureAwait(false);
        var result = await _placements.SetChoiceAsync(workId, collectionId, ct).ConfigureAwait(false);
        if (result != SeriesPlacementChoiceResult.Applied)
        {
            return result;
        }

        // The person has answered "which Series?", so any open question about this Work is closed.
        await _reviewer.ResolveAsync(workId, ct).ConfigureAwait(false);
        await ResolveParentsAsync([collectionId, before], ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Releases the choice and places the Work again, so the Library decides from the evidence.</summary>
    public async Task<SeriesPlacementReleaseResult> ReleaseAsync(Guid workId, CancellationToken ct = default)
    {
        var before = await _placements.GetCurrentShelfIdAsync(workId, ct).ConfigureAwait(false);
        await _placements.ReleaseChoiceAsync(workId, ct).ConfigureAwait(false);

        var entities = await _placements.GetReviewEntitiesAsync(workId, ct).ConfigureAwait(false);
        Guid? alreadyResolved = null;
        if (entities.StableAssetId is { } assetId)
        {
            // A Work with a file goes through collection assignment, which also uses the Work's retail identity.
            var outcome = await _finalization
                .FinalizeAsync(assetId, CollectionFinalizationReason.PersonReleasedSeriesChoice, ct)
                .ConfigureAwait(false);
            if (outcome.Assignment.Outcome == CollectionAssignmentOutcome.Failed)
            {
                return SeriesPlacementReleaseResult.PlacementFailed;
            }

            // Finalization already ran the Universe step for the shelf the work landed on.
            alreadyResolved = outcome.ParentResolutionAttempted ? outcome.Assignment.CollectionId : null;
        }
        else
        {
            // No Normal original file to run assignment through: place the Work from the manifests that name it.
            var tie = await _manifests.PlaceWorkFromManifestsAsync(workId, ct).ConfigureAwait(false);
            if (tie is not null)
            {
                await _reviewer.RaiseAsync(workId, tie.Candidates, workId, ct).ConfigureAwait(false);
            }
            else
            {
                await _reviewer.ResolveAsync(workId, ct).ConfigureAwait(false);
            }
        }

        var after = await _placements.GetCurrentShelfIdAsync(workId, ct).ConfigureAwait(false);
        await ResolveParentsAsync([after, before], ct, skip: alreadyResolved).ConfigureAwait(false);
        return SeriesPlacementReleaseResult.Released;
    }

    private async Task ResolveParentsAsync(IEnumerable<Guid?> shelves, CancellationToken ct, Guid? skip = null)
    {
        foreach (var shelf in shelves.OfType<Guid>().Distinct().Where(shelf => shelf != skip))
        {
            try
            {
                await _parents.ResolveParentCollectionAsync(shelf, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The choice is already saved; the Universe step runs again with the next placement.
                _logger.LogWarning(ex, "Universe step failed after a Series choice for collection {CollectionId}", shelf);
            }
        }
    }
}
