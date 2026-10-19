using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Ingestion;

/// <summary>
/// Works out which <see cref="IngestionFileStep"/> a file has reached, and how it ended, from its intake operation and
/// its identity job. This is the only place the pipeline's own stage and state names are turned into steps.
/// <para>
/// A file's intake operation is "open" while it is waiting or running, or while a retry is still due (see
/// <see cref="IsOperationOpen"/>). While it is open its own stage decides the step. Once it has stopped, the identity
/// job of the file's asset decides; with no identity job the operation's status decides. An operation that stopped
/// without finishing (interrupted, blocked, or a retry that never came) counts as finished and failed, so a stuck row
/// can never keep the live page busy for ever. The data store query (<c>IngestionQueueProgressRepository</c>) applies
/// the same rule, so the two agree.
/// </para>
/// </summary>
public static class IngestionFileStepMapper
{
    /// <summary>How long after a retry was due it is still treated as coming. After this a retry row counts as failed.</summary>
    public static readonly TimeSpan RetryGrace = TimeSpan.FromMinutes(5);

    /// <summary>What an intake operation records in its result summary when the file was not new (see the pipeline).</summary>
    private static readonly string[] NotNewSummaries = ["duplicate", "same_path_redetected", "duplicate_race"];

    /// <summary>What an intake operation records when the Library saw one of its own, unchanged files again.</summary>
    private const string SamePathRedetectedSummary = "same_path_redetected";

    /// <summary>
    /// True for a file the Library already had, noticed again where it already was (for example after the organiser
    /// moved it into place). It is not a file someone added, so it is left out of the live numbers entirely.
    /// </summary>
    public static bool IsAlreadyKnownFile(IngestionQueueFileRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return string.Equals(row.Status, MediaOperationStatus.Succeeded, StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.ResultSummary, SamePathRedetectedSummary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the intake operation is still in the queue: pending, queued, leased, running or paused, or waiting for a
    /// retry that is due in the future or was due no more than <see cref="RetryGrace"/> ago.
    /// </summary>
    public static bool IsOperationOpen(string status, DateTimeOffset? nextRetryAt, DateTimeOffset now)
    {
        switch (status.ToLowerInvariant())
        {
            case MediaOperationStatus.Pending:
            case MediaOperationStatus.Queued:
            case MediaOperationStatus.Leased:
            case MediaOperationStatus.Running:
            case MediaOperationStatus.Paused:
                return true;

            case MediaOperationStatus.RetryWaiting:
            case MediaOperationStatus.FailedRetryable:
                return nextRetryAt is { } due && due >= now - RetryGrace;

            default:
                return false;
        }
    }

    /// <summary>Where one file is, and how it ended if it is finished.</summary>
    public static IngestionFileProgress Map(IngestionQueueFileRow row, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(row);

        // A file that was already in the Library (an exact duplicate, or the same path seen again) is finished the
        // moment it is recognised. It points at the existing asset, so the asset's own identity work and holds must not
        // count for it.
        if (string.Equals(row.Status, MediaOperationStatus.Succeeded, StringComparison.OrdinalIgnoreCase)
            && NotNewSummaries.Contains(row.ResultSummary ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return Finished(IngestionFileOutcome.Skipped);
        }

        var identity = ParseIdentityState(row.IdentityState);
        IngestionFileProgress progress;
        if (IsOperationOpen(row.Status, row.NextRetryAt, now))
        {
            // Still being worked on: the operation's own stage says where, whatever its asset's identity job says.
            progress = Working(StepForStage(row));
        }
        else if (identity is { } state)
        {
            progress = MapIdentity(state, row);
        }
        else
        {
            progress = MapStoppedOperation(row);
        }

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

    private static IngestionFileProgress MapStoppedOperation(IngestionQueueFileRow row)
    {
        switch (row.Status.ToLowerInvariant())
        {
            case MediaOperationStatus.Skipped:
            case MediaOperationStatus.NoResult:
            case MediaOperationStatus.MissingConfirmed:
            case MediaOperationStatus.NotApplicable:
            case MediaOperationStatus.Cancelled:
                return Finished(IngestionFileOutcome.Skipped);

            case MediaOperationStatus.Succeeded:
                // Finished with no identity job: the file's library does not look titles up, so it is simply added.
                return Finished(row.HasPendingReview ? IngestionFileOutcome.NeedsReview : IngestionFileOutcome.Added);

            default:
                // Failed, dead-lettered, interrupted, blocked, or a retry that never came: it will not finish by itself.
                return Finished(IngestionFileOutcome.Failed);
        }
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
