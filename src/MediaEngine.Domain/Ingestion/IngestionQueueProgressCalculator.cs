namespace MediaEngine.Domain.Ingestion;

/// <summary>What one look at the queue produced: the numbers to publish and what to remember for the next look.</summary>
/// <param name="Snapshot">The live ingestion numbers.</param>
/// <param name="State">What to hand back to the calculator next time.</param>
/// <param name="StartedNewPeriod">Whether this look began a new active period (the caller may then re-read the full set).</param>
public sealed record IngestionQueueProgressResult(
    IngestionQueueProgressSnapshot Snapshot,
    IngestionQueueProgressState State,
    bool StartedNewPeriod);

/// <summary>
/// Works out the live ingestion numbers once, from the per-file work rows: files found, the tally by type, the step each
/// file has reached, the run state and the bar. It does no I/O and holds no state of its own; the caller hands back the
/// <see cref="IngestionQueueProgressState"/> from the previous look.
/// <para>
/// The bar is the sum of every file's step number divided by the steps possible (nine per file), so it never changes
/// units. Within an active period the displayed value never decreases, and it is 1 only when the queue is idle.
/// Restarting the Engine resets the bar's floor: the first snapshot after a restart can be lower than the last one before it.
/// </para>
/// <para>
/// A period runs from idle to idle, but files that are only waiting for a missing tool do not keep it open: when every
/// unfinished file is waiting, the period closes (state Waiting, bar held where it was, no estimate). Files already
/// waiting before a period starts are left out of its set; they are still reported as waiting, and rejoin when their
/// hold clears and they have work to do.
/// </para>
/// </summary>
public static class IngestionQueueProgressCalculator
{
    private const int StepsPerFile = (int)IngestionFileStep.Done;

    private static readonly string[] TypeOrder = ["Movies", "TV", "Music", "Books", "Audiobooks", "Comics"];

    /// <summary>Works out the numbers for <paramref name="rows"/> at <paramref name="now"/>.</summary>
    /// <param name="rows">The files in the period's set (see <c>IIngestionQueueProgressRepository</c>).</param>
    /// <param name="previous">The state returned by the previous look, or <see langword="null"/> at the Engine's start.</param>
    /// <param name="now">The current time.</param>
    public static IngestionQueueProgressResult Calculate(
        IReadOnlyList<IngestionQueueFileRow> rows,
        IngestionQueueProgressState? previous,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var prev = previous ?? IngestionQueueProgressState.Empty;
        var mapped = rows
            .Where(row => !IngestionFileStepMapper.IsAlreadyKnownFile(row))
            .Select(row => (Row: row, Progress: IngestionFileStepMapper.Map(row, now)))
            .ToList();

        // Files held back by a missing tool, wherever they are in the queue and however old they are.
        var held = mapped.Where(file => file.Progress.IsWaiting).ToList();
        var waitingFor = held.Select(file => file.Progress.WaitingForToolId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        var state = prev;
        var startedNewPeriod = false;
        if (prev.PeriodStart is null || prev.PeriodEnded)
        {
            var open = mapped.Where(file => !file.Progress.IsDone && !file.Progress.IsWaiting).ToList();
            if (open.Count > 0)
            {
                // A period starts with the earliest unfinished file that arrived after the last period closed
                // (at the Engine's start, the earliest unfinished file of all). Older unfinished files are
                // part of the set but do not move the start.
                var arrivals = prev.PeriodEnded
                    ? open.Where(file => file.Row.CreatedAt > prev.EndedAt).ToList()
                    : open;
                var start = arrivals.Count > 0 ? arrivals.Min(file => file.Row.CreatedAt) : now;
                state = new IngestionQueueProgressState { PeriodStart = start };
                startedNewPeriod = true;
            }
            else if (prev.PeriodStart is null)
            {
                return new IngestionQueueProgressResult(
                    IngestionQueueProgressSnapshot.Empty(now) with
                    {
                        State = held.Count > 0 ? IngestionQueueRunState.Waiting : IngestionQueueRunState.Idle,
                        FilesWaiting = held.Count,
                        WaitingFor = waitingFor,
                    },
                    prev,
                    false);
            }
        }

        var periodStart = state.PeriodStart!.Value;

        // Older files that are unfinished and not waiting join the period and stay counted after they finish.
        var joined = new HashSet<Guid>(state.JoinedOperationIds);
        foreach (var (row, progress) in mapped)
        {
            if (row.CreatedAt < periodStart && !progress.IsDone && !progress.IsWaiting)
            {
                joined.Add(row.OperationId);
            }
        }

        var set = mapped
            .Where(file => file.Row.CreatedAt >= periodStart || joined.Contains(file.Row.OperationId))
            .ToList();

        var steps = new Dictionary<Guid, IngestionFileStep>(set.Count);
        long stepsCompleted = 0;
        var filesDone = 0;
        var filesRegistered = 0;
        var openActive = 0;
        int added = 0, needsReview = 0, failed = 0, skipped = 0;
        var unsorted = 0;
        var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? lastActivity = null;

        foreach (var (row, progress) in set)
        {
            var step = progress.Step;
            if (!progress.IsDone
                && state.FileSteps.TryGetValue(row.OperationId, out var seen)
                && seen > step)
            {
                // A retry or a re-check never moves an unfinished file backwards.
                step = seen > IngestionFileStep.Enriching ? IngestionFileStep.Enriching : seen;
            }

            steps[row.OperationId] = step;
            stepsCompleted += (int)step;
            if (step >= IngestionFileStep.Registered)
            {
                filesRegistered++;
            }

            if (progress.IsDone)
            {
                filesDone++;
                switch (progress.Outcome)
                {
                    case IngestionFileOutcome.Added: added++; break;
                    case IngestionFileOutcome.NeedsReview: needsReview++; break;
                    case IngestionFileOutcome.Failed: failed++; break;
                    default: skipped++; break;
                }
            }
            else if (!progress.IsWaiting)
            {
                openActive++;
            }

            if (IsKnownType(row.MediaType))
            {
                typeCounts[row.MediaType!] = typeCounts.GetValueOrDefault(row.MediaType!) + 1;
            }
            else
            {
                unsorted++;
            }

            lastActivity = Latest(lastActivity, row.UpdatedAt);
            if (row.IdentityUpdatedAt is { } identityUpdated)
            {
                lastActivity = Latest(lastActivity, identityUpdated);
            }
        }

        var filesFound = set.Count;
        var runState = openActive > 0
            ? IngestionQueueRunState.Working
            : held.Count > 0 ? IngestionQueueRunState.Waiting : IngestionQueueRunState.Idle;

        var stepsTotal = (long)filesFound * StepsPerFile;
        var fraction = stepsTotal > 0 ? (double)stepsCompleted / stepsTotal : 0d;
        var progressValue = runState == IngestionQueueRunState.Idle
            ? 1d
            : Math.Min(Math.Max(state.LastProgress, fraction), Math.BitDecrement(1d));

        // The period closes as soon as nothing in it is still being worked on (finished, or only waiting for a tool).
        var ended = state.PeriodEnded;
        var endedAt = state.EndedAt;
        if (runState != IngestionQueueRunState.Working && !ended)
        {
            ended = true;
            endedAt = now;
        }

        var rateSamples = Array.Empty<IngestionQueueRateSample>() as IReadOnlyList<IngestionQueueRateSample>;
        var estimateSamples = state.EstimateSamples;
        var lastEstimateSampleAt = state.LastEstimateSampleAt;
        int? estimateSeconds = null;
        if (runState == IngestionQueueRunState.Working)
        {
            rateSamples = TrimSamples(state.RateSamples, new IngestionQueueRateSample(now, filesRegistered, filesDone), now);
            var remaining = RemainingSeconds(rateSamples, filesFound, filesRegistered, filesDone, now, out var steady);
            if (remaining is { } seconds)
            {
                if (lastEstimateSampleAt is null || now - lastEstimateSampleAt >= IngestionQueueEstimatePolicy.SampleInterval)
                {
                    // A sample is the finish time this estimate predicts, so a steady pace gives the same sample every
                    // time even though the time left keeps shrinking.
                    estimateSamples = estimateSamples
                        .Append((now - periodStart).TotalSeconds + seconds)
                        .TakeLast(IngestionQueueEstimatePolicy.SamplesRequired)
                        .ToList();
                    lastEstimateSampleAt = now;
                }

                if (steady
                    && fraction >= IngestionQueueEstimatePolicy.MinFractionDone
                    && SamplesAgree(estimateSamples, seconds))
                {
                    estimateSeconds = (int)Math.Ceiling(seconds);
                }
            }
        }
        else
        {
            estimateSamples = [];
            lastEstimateSampleAt = null;
        }

        var snapshot = new IngestionQueueProgressSnapshot
        {
            State = runState,
            FilesFound = filesFound,
            FilesDone = filesDone,
            FilesWaiting = held.Count,
            WaitingFor = waitingFor,
            FilesMoreFound = Math.Max(0, filesFound - (startedNewPeriod ? filesFound : state.FirstSnapshotFiles)),
            Outcomes = new IngestionQueueOutcomes(added, needsReview, failed, skipped),
            Tally = OrderTally(typeCounts),
            FilesUnsorted = unsorted,
            StepsTotal = stepsTotal,
            StepsCompleted = stepsCompleted,
            Progress = progressValue,
            StartedAt = periodStart,
            LastActivityAt = lastActivity,
            EstimatedSecondsRemaining = estimateSeconds,
            UpdatedAt = now,
        };

        var next = state with
        {
            PeriodEnded = ended,
            EndedAt = endedAt,
            FirstSnapshotFiles = startedNewPeriod ? filesFound : state.FirstSnapshotFiles,
            LastProgress = progressValue,
            JoinedOperationIds = joined,
            FileSteps = steps,
            RateSamples = rateSamples,
            EstimateSamples = estimateSamples,
            LastEstimateSampleAt = lastEstimateSampleAt,
        };

        return new IngestionQueueProgressResult(snapshot, next, startedNewPeriod);
    }

    private static bool IsKnownType(string? mediaType) =>
        !string.IsNullOrWhiteSpace(mediaType)
        && !string.Equals(mediaType, "Unknown", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset Latest(DateTimeOffset? current, DateTimeOffset candidate) =>
        current is { } value && value >= candidate ? value : candidate;

    private static IReadOnlyList<IngestionQueueTypeTally> OrderTally(Dictionary<string, int> counts) =>
        counts
            .OrderBy(pair =>
            {
                var index = Array.FindIndex(TypeOrder, type => string.Equals(type, pair.Key, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? TypeOrder.Length : index;
            })
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new IngestionQueueTypeTally(pair.Key, pair.Value))
            .ToList();

    private static IReadOnlyList<IngestionQueueRateSample> TrimSamples(
        IReadOnlyList<IngestionQueueRateSample> existing,
        IngestionQueueRateSample latest,
        DateTimeOffset now)
    {
        var cutoff = now - IngestionQueueEstimatePolicy.RateWindow;
        var kept = existing.Where(sample => sample.At >= cutoff && sample.At < latest.At).ToList();
        kept.Add(latest);
        return kept;
    }

    /// <summary>
    /// Seconds left: the longer of the time for the files not yet registered to be registered (at the intake rate) and the
    /// time for the files not yet done to finish (at the finish rate). <see langword="null"/> when a rate that is needed is
    /// not above zero.
    /// </summary>
    private static double? RemainingSeconds(
        IReadOnlyList<IngestionQueueRateSample> samples,
        int filesFound,
        int filesRegistered,
        int filesDone,
        DateTimeOffset now,
        out bool steady)
    {
        steady = false;
        var notDone = filesFound - filesDone;
        if (notDone <= 0)
        {
            return null;
        }

        var first = samples[0];
        var span = (now - first.At).TotalSeconds;
        if (span <= 0)
        {
            return null;
        }

        var finishRate = Math.Max(0, filesDone - first.Done) / span;
        if (finishRate <= 0)
        {
            return null;
        }

        var remaining = notDone / finishRate;
        var notRegistered = filesFound - filesRegistered;
        if (notRegistered > 0)
        {
            var intakeRate = Math.Max(0, filesRegistered - first.Registered) / span;
            if (intakeRate <= 0)
            {
                return null;
            }

            remaining = Math.Max(remaining, notRegistered / intakeRate);
        }

        // A finish pace that is still changing (a burst just ended, a stall just cleared) is not a pace to count down on.
        var recentFrom = now - IngestionQueueEstimatePolicy.RecentWindow;
        var recent = samples.First(sample => sample.At >= recentFrom);
        var recentSpan = (now - recent.At).TotalSeconds;
        var recentRate = recentSpan > 0 ? Math.Max(0, filesDone - recent.Done) / recentSpan : 0d;
        var tolerance = IngestionQueueEstimatePolicy.MaxRateDrift * finishRate;

        // The recent pace must match the window's, and also the pace just before it, so the moment a stall ends (when the
        // recent pace briefly crosses the window's) is not mistaken for a steady pace.
        var earlierFrom = now - IngestionQueueEstimatePolicy.RecentWindow - IngestionQueueEstimatePolicy.RecentWindow;
        var earlier = samples.FirstOrDefault(sample => sample.At >= earlierFrom);
        var earlierSpan = (recent.At - earlier.At).TotalSeconds;
        var earlierRate = earlierSpan > 0 ? Math.Max(0, recent.Done - earlier.Done) / earlierSpan : -1d;
        steady = Math.Abs(recentRate - finishRate) <= tolerance
            && earlierRate >= 0
            && Math.Abs(recentRate - earlierRate) <= tolerance;

        return remaining;
    }

    private static bool SamplesAgree(IReadOnlyList<double> samples, double remainingNow)
    {
        if (samples.Count < IngestionQueueEstimatePolicy.SamplesRequired)
        {
            return false;
        }

        return samples.Max() - samples.Min() <= IngestionQueueEstimatePolicy.MaxSampleSpread * remainingNow;
    }
}
