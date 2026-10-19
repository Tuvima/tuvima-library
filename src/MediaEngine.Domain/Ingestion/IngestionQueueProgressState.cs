namespace MediaEngine.Domain.Ingestion;

/// <summary>
/// What the calculator remembers between two looks at the queue: the active period (idle to idle), the bar's last
/// value, the highest step each file reached, and the recent samples the estimate is judged on.
/// </summary>
public sealed record IngestionQueueProgressState
{
    /// <summary>A state for an Engine that has not seen any work yet.</summary>
    public static IngestionQueueProgressState Empty { get; } = new();

    /// <summary>When the current (or last) period started; <see langword="null"/> before the first one.</summary>
    public DateTimeOffset? PeriodStart { get; init; }

    /// <summary>Whether the period has ended (every file was finished).</summary>
    public bool PeriodEnded { get; init; }

    /// <summary>When the period ended.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>How many files the period had at its first snapshot.</summary>
    public int FirstSnapshotFiles { get; init; }

    /// <summary>The bar's last published value in this period.</summary>
    public double LastProgress { get; init; }

    /// <summary>The highest step each unfinished file has reached, so a retry never moves a file backwards.</summary>
    public IReadOnlyDictionary<Guid, IngestionFileStep> FileSteps { get; init; } = new Dictionary<Guid, IngestionFileStep>();

    /// <summary>Recent (time, steps completed) pairs inside the rate window.</summary>
    public IReadOnlyList<IngestionQueueRateSample> RateSamples { get; init; } = [];

    /// <summary>The last few projected run lengths (time spent plus time left, in seconds), taken every sample interval.</summary>
    public IReadOnlyList<double> EstimateSamples { get; init; } = [];

    /// <summary>When the last estimate sample was taken.</summary>
    public DateTimeOffset? LastEstimateSampleAt { get; init; }
}

/// <summary>The number of steps completed at one moment.</summary>
public readonly record struct IngestionQueueRateSample(DateTimeOffset At, long StepsCompleted);
