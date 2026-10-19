namespace MediaEngine.Domain.Ingestion;

/// <summary>
/// When the "time remaining" estimate is trustworthy enough to show. The estimate follows the slower of two
/// bottlenecks, each measured as files per second over a sliding window: files reaching Registered (intake) and files
/// reaching Done (finishing). It is shown only once the work is well under way and the pace is steady.
/// The numbers are starting values tuned against the replayed 500-file runs in the Domain tests; change them only with
/// those replays (<c>IngestionQueueProgressCalculatorTests</c>).
/// </summary>
public static class IngestionQueueEstimatePolicy
{
    /// <summary>How far back the intake and finish rates look.</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(120);

    /// <summary>How often an estimate is sampled for the steadiness test.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(10);

    /// <summary>How many of the latest samples must agree.</summary>
    public const int SamplesRequired = 3;

    /// <summary>
    /// The latest samples agree when the projected finish times are within this share of the time remaining from each
    /// other.
    /// </summary>
    public const double MaxSampleSpread = 0.25;

    /// <summary>No estimate until at least this share of the steps is done.</summary>
    public const double MinFractionDone = 0.15;

    /// <summary>The recent window the finish rate is also measured over, to tell a steady pace from a changing one.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(10);

    /// <summary>The recent finish rate and the window finish rate agree when they are within this share of each other.</summary>
    public const double MaxRateDrift = 0.15;
}
