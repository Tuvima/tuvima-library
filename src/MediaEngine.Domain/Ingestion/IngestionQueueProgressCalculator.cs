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
        var mapped = rows.Select(row => (Row: row, Progress: IngestionFileStepMapper.Map(row))).ToList();

        var state = prev;
        var startedNewPeriod = false;
        if (prev.PeriodStart is null || prev.PeriodEnded)
        {
            var open = mapped.Where(file => !file.Progress.IsDone).ToList();
            if (open.Count > 0)
            {
                // A period starts with the earliest unfinished file that arrived after the last period ended
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
                return new IngestionQueueProgressResult(IngestionQueueProgressSnapshot.Empty(now), prev, false);
            }
        }

        var periodStart = state.PeriodStart!.Value;
        var set = mapped
            .Where(file => file.Row.CreatedAt >= periodStart || !file.Progress.IsDone)
            .ToList();

        var steps = new Dictionary<Guid, IngestionFileStep>(set.Count);
        long stepsCompleted = 0;
        var filesDone = 0;
        var filesWaiting = 0;
        var waitingFor = new SortedSet<string>(StringComparer.Ordinal);
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

            if (progress.IsWaiting)
            {
                filesWaiting++;
                waitingFor.Add(progress.WaitingForToolId!);
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
        var openFiles = filesFound - filesDone;
        var runState = openFiles == 0
            ? IngestionQueueRunState.Idle
            : filesWaiting == openFiles ? IngestionQueueRunState.Waiting : IngestionQueueRunState.Working;

        var stepsTotal = (long)filesFound * StepsPerFile;
        var fraction = stepsTotal > 0 ? (double)stepsCompleted / stepsTotal : 0d;
        var progressValue = runState == IngestionQueueRunState.Idle
            ? 1d
            : Math.Min(Math.Max(state.LastProgress, fraction), Math.BitDecrement(1d));

        var ended = state.PeriodEnded;
        var endedAt = state.EndedAt;
        if (runState == IngestionQueueRunState.Idle && !ended)
        {
            ended = true;
            endedAt = now;
        }

        // The whole-queue step rate over a sliding window, and the steadiness test that decides whether to show it.
        var rateSamples = runState == IngestionQueueRunState.Idle
            ? Array.Empty<IngestionQueueRateSample>()
            : TrimSamples(state.RateSamples, new IngestionQueueRateSample(now, stepsCompleted), now);
        var estimateSamples = state.EstimateSamples;
        var lastEstimateSampleAt = state.LastEstimateSampleAt;
        int? estimateSeconds = null;
        if (runState == IngestionQueueRunState.Working)
        {
            var remaining = RemainingSeconds(rateSamples, stepsTotal, stepsCompleted, now, out var steady);
            if (remaining is { } seconds)
            {
                if (lastEstimateSampleAt is null || now - lastEstimateSampleAt >= IngestionQueueEstimatePolicy.SampleInterval)
                {
                    // A sample is the whole run length this estimate predicts (time already spent plus time left), so a
                    // steady pace gives the same sample every time even though the time left keeps shrinking.
                    estimateSamples = estimateSamples
                        .Append((now - periodStart).TotalSeconds + seconds)
                        .TakeLast(IngestionQueueEstimatePolicy.SamplesRequired)
                        .ToList();
                    lastEstimateSampleAt = now;
                }

                if (steady
                    && fraction >= IngestionQueueEstimatePolicy.MinFractionDone
                    && SamplesAgree(estimateSamples))
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
            FilesWaiting = filesWaiting,
            WaitingFor = waitingFor.ToList(),
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

    /// <summary>Seconds left at the window's step rate, or <see langword="null"/> when the rate is not above zero.</summary>
    private static double? RemainingSeconds(
        IReadOnlyList<IngestionQueueRateSample> samples,
        long stepsTotal,
        long stepsCompleted,
        DateTimeOffset now,
        out bool steady)
    {
        steady = false;
        var rate = RateSince(samples, samples[0].At, stepsCompleted, now);
        if (rate <= 0)
        {
            return null;
        }

        // A pace that is still changing (a burst just ended, a stall just cleared) is not a pace to count down on.
        var recentFrom = now - IngestionQueueEstimatePolicy.RecentWindow;
        var recentStart = samples.FirstOrDefault(sample => sample.At >= recentFrom);
        var recentRate = RateSince(samples, recentStart.At, stepsCompleted, now);
        steady = Math.Abs(recentRate - rate) <= IngestionQueueEstimatePolicy.MaxRateDrift * rate;

        return (stepsTotal - stepsCompleted) / rate;
    }

    private static double RateSince(
        IReadOnlyList<IngestionQueueRateSample> samples,
        DateTimeOffset from,
        long stepsCompleted,
        DateTimeOffset now)
    {
        var start = samples.FirstOrDefault(sample => sample.At >= from);
        var seconds = (now - start.At).TotalSeconds;
        return seconds > 0 ? (stepsCompleted - start.StepsCompleted) / seconds : 0d;
    }

    private static bool SamplesAgree(IReadOnlyList<double> samples)
    {
        if (samples.Count < IngestionQueueEstimatePolicy.SamplesRequired)
        {
            return false;
        }

        var smallest = samples.Min();
        return smallest > 0 && samples.Max() <= smallest * IngestionQueueEstimatePolicy.MaxSampleSpread;
    }
}
