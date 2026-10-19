using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Ingestion;

/// <summary>
/// Works out which <see cref="IngestionFileStep"/> a file has reached, and how it ended, from its intake operation and
/// its identity job. This is the only place the pipeline's own stage and state names are turned into steps.
/// </summary>
public static class IngestionFileStepMapper
{
    /// <summary>What an intake operation records in its result summary when the file was not new (see the pipeline).</summary>
    private static readonly string[] NotNewSummaries = ["duplicate", "same_path_redetected", "duplicate_race"];

    /// <summary>Where one file is, and how it ended if it is finished.</summary>
    public static IngestionFileProgress Map(IngestionQueueFileRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        // A file that was already in the Library (an exact duplicate, or the same path seen again) is finished the
        // moment it is recognised. It points at the existing asset, so the asset's own identity work must not count for it.
        if (string.Equals(row.Status, MediaOperationStatus.Succeeded, StringComparison.OrdinalIgnoreCase)
            && NotNewSummaries.Contains(row.ResultSummary ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return Finished(IngestionFileOutcome.Skipped);
        }

        var identity = ParseIdentityState(row.IdentityState);
        var progress = identity is { } state
            ? MapIdentity(state, row)
            : MapOperation(row);

        if (row.HeldToolId is not null)
        {
            // A file whose inspection waits on a missing tool is never finished while it waits.
            var step = progress.Step > IngestionFileStep.Enriching ? IngestionFileStep.Enriching : progress.Step;
            return new IngestionFileProgress(step, false, IngestionFileOutcome.None, row.HeldToolId);
        }

        return progress;
    }

    private static IngestionFileProgress MapIdentity(IdentityJobState state, IngestionQueueFileRow row) => state switch
    {
        IdentityJobState.Queued or IdentityJobState.RetailSearching
            => Working(IngestionFileStep.Identifying),

        IdentityJobState.RetailMatched
            or IdentityJobState.RetailMatchedNeedsReview
            or IdentityJobState.BridgeSearching
            or IdentityJobState.QidResolved
            or IdentityJobState.Hydrating
            => Working(IngestionFileStep.Hydrating),

        IdentityJobState.UniverseEnriching => Working(IngestionFileStep.Enriching),

        // The title was matched; a missing Wikidata identity does not stop it being added (the retail identity stays).
        IdentityJobState.Ready
            or IdentityJobState.ReadyWithoutUniverse
            or IdentityJobState.QidNoMatch
            => Finished(row.HasPendingReview ? IngestionFileOutcome.NeedsReview : IngestionFileOutcome.Added),

        IdentityJobState.RetailNoMatch or IdentityJobState.QidNeedsReview
            => Finished(IngestionFileOutcome.NeedsReview),

        IdentityJobState.Failed => Finished(IngestionFileOutcome.Failed),

        _ => Working(IngestionFileStep.Identifying),
    };

    private static IngestionFileProgress MapOperation(IngestionQueueFileRow row)
    {
        switch (row.Status.ToLowerInvariant())
        {
            case MediaOperationStatus.FailedTerminal:
            case MediaOperationStatus.DeadLettered:
                return Finished(IngestionFileOutcome.Failed);

            case MediaOperationStatus.Skipped:
            case MediaOperationStatus.NoResult:
            case MediaOperationStatus.MissingConfirmed:
            case MediaOperationStatus.NotApplicable:
            case MediaOperationStatus.Cancelled:
                return Finished(IngestionFileOutcome.Skipped);

            case MediaOperationStatus.Succeeded:
                // Finished with no identity job: the file's library does not look titles up, so it is simply added.
                return Finished(row.HasPendingReview ? IngestionFileOutcome.NeedsReview : IngestionFileOutcome.Added);
        }

        // Still in the queue (including retrying, interrupted, paused and blocked): it keeps the step its stage shows.
        return Working(StepForStage(row));
    }

    private static IngestionFileStep StepForStage(IngestionQueueFileRow row) => row.Stage?.ToLowerInvariant() switch
    {
        MediaOperationStage.Discovered => IngestionFileStep.Found,

        MediaOperationStage.Queued
            or MediaOperationStage.Settling
            or MediaOperationStage.WaitingForLock
            or MediaOperationStage.Hashing
            or MediaOperationStage.Parsing
            => IngestionFileStep.Settled,

        MediaOperationStage.Scoring => IngestionFileStep.Read,

        MediaOperationStage.Registered
            or MediaOperationStage.QueuedIdentity
            or MediaOperationStage.Completed
            => IngestionFileStep.Registered,

        // A retry leaves the stage as "failed": the file was at least picked up, and registered if it has an asset.
        _ => row.AssetId is null ? IngestionFileStep.Settled : IngestionFileStep.Registered,
    };

    private static IngestionFileProgress Working(IngestionFileStep step)
        => new(step, false, IngestionFileOutcome.None, null);

    private static IngestionFileProgress Finished(IngestionFileOutcome outcome)
        => new(IngestionFileStep.Done, true, outcome, null);

    private static IdentityJobState? ParseIdentityState(string? state)
        => !string.IsNullOrWhiteSpace(state) && Enum.TryParse<IdentityJobState>(state, ignoreCase: true, out var parsed)
            ? parsed
            : null;
}
