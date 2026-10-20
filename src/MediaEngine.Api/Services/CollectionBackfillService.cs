using System.Diagnostics;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

public sealed record CollectionBackfillRequest(
    [property: JsonPropertyName("dry_run")] bool DryRun = false,
    [property: JsonPropertyName("batch_size")] int? BatchSize = null,
    [property: JsonPropertyName("max_items")] int? MaxItems = null);

public sealed record CollectionBackfillResult(
    [property: JsonPropertyName("candidate_count")] int CandidateCount,
    [property: JsonPropertyName("processed_count")] int ProcessedCount,
    [property: JsonPropertyName("assigned_count")] int AssignedCount,
    [property: JsonPropertyName("created_collection_count")] int CreatedCollectionCount,
    [property: JsonPropertyName("already_assigned_count")] int AlreadyAssignedCount,
    [property: JsonPropertyName("skipped_count")] int SkippedCount,
    [property: JsonPropertyName("failed_count")] int FailedCount,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs);

/// <summary>
/// Repairs works that already have media assets and canonical metadata but never
/// received a lane shelf collection assignment.
/// </summary>
public sealed class CollectionBackfillService
{
    public const int DefaultBatchSize = 100;
    public const int DefaultAutomaticMaxItems = 1000;

    private readonly ICollectionRepository _collectionRepo;
    private readonly CollectionFinalizationService _finalization;
    private readonly ILogger<CollectionBackfillService> _logger;

    public CollectionBackfillService(
        ICollectionRepository collectionRepo,
        CollectionFinalizationService finalization,
        ILogger<CollectionBackfillService> logger)
    {
        ArgumentNullException.ThrowIfNull(collectionRepo);
        ArgumentNullException.ThrowIfNull(finalization);
        ArgumentNullException.ThrowIfNull(logger);

        _collectionRepo = collectionRepo;
        _finalization = finalization;
        _logger = logger;
    }

    public Task<CollectionBackfillResult> RunAutomaticAsync(CancellationToken ct = default) =>
        RunAsync(
            new CollectionBackfillRequest(
                DryRun: false,
                BatchSize: DefaultBatchSize,
                MaxItems: DefaultAutomaticMaxItems),
            ct);

    public async Task<CollectionBackfillResult> RunAsync(
        CollectionBackfillRequest request,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var candidateCount = await _collectionRepo.CountCollectionBackfillCandidatesAsync(ct).ConfigureAwait(false);
        if (request.DryRun || candidateCount == 0)
        {
            sw.Stop();
            return new CollectionBackfillResult(candidateCount, 0, 0, 0, 0, 0, 0, sw.ElapsedMilliseconds);
        }

        var batchSize = Math.Clamp(request.BatchSize ?? DefaultBatchSize, 1, 1000);
        var maxItems = request.MaxItems is > 0 ? request.MaxItems.Value : int.MaxValue;
        var processed = 0;
        var assigned = 0;
        var created = 0;
        var alreadyAssigned = 0;
        var skipped = 0;
        var failed = 0;
        Guid? cursor = null;

        while (processed < maxItems)
        {
            ct.ThrowIfCancellationRequested();

            var take = Math.Min(batchSize, maxItems - processed);
            var candidates = await _collectionRepo
                .GetCollectionBackfillCandidatesAsync(take, cursor, ct)
                .ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                break;
            }

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                cursor = candidate.WorkId;

                var result = await _finalization.FinalizeAsync(
                    candidate.MediaAssetId,
                    CollectionFinalizationReason.Backfill,
                    ct: ct).ConfigureAwait(false);

                processed++;
                switch (result.Assignment.Outcome)
                {
                    case CollectionAssignmentOutcome.Assigned:
                        assigned++;
                        break;
                    case CollectionAssignmentOutcome.AlreadyAssigned:
                        alreadyAssigned++;
                        break;
                    case CollectionAssignmentOutcome.SkippedNoWork:
                    case CollectionAssignmentOutcome.SkippedNoShelfIdentity:
                    case CollectionAssignmentOutcome.SkippedAmbiguousSeries:
                        skipped++;
                        break;
                    case CollectionAssignmentOutcome.Failed:
                        failed++;
                        break;
                }

                if (result.Assignment.CreatedCollection)
                {
                    created++;
                }

                if (result.ParentResolutionFailed)
                {
                    failed++;
                }

                if (processed >= maxItems)
                {
                    break;
                }
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "Collection backfill complete: {Processed}/{Candidates} processed, {Assigned} assigned, {Created} created, {AlreadyAssigned} already assigned, {Skipped} skipped, {Failed} failed in {Elapsed}ms",
            processed,
            candidateCount,
            assigned,
            created,
            alreadyAssigned,
            skipped,
            failed,
            sw.ElapsedMilliseconds);

        return new CollectionBackfillResult(
            candidateCount,
            processed,
            assigned,
            created,
            alreadyAssigned,
            skipped,
            failed,
            sw.ElapsedMilliseconds);
    }
}
