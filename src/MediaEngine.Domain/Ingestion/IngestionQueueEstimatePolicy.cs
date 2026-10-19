namespace MediaEngine.Domain.Ingestion;

/// <summary>
/// When the "time remaining" estimate is trustworthy enough to show. The rate is the whole queue's step rate over a
/// sliding window (never the speed files are noticed at), and the estimate is shown only once it is steady.
/// The numbers are starting values tuned against the replayed 500-file run in the Domain tests; change them only with
/// that replay (<c>IngestionQueueProgressCalculatorTests</c>).
/// </summary>
public static class IngestionQueueEstimatePolicy
{
    /// <summary>How far back the step rate looks.</summary>
    public static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(120);

    /// <summary>How often an estimate is sampled for the steadiness test.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(10);

    /// <summary>How many of the latest samples must agree.</summary>
    public const int SamplesRequired = 3;

    /// <summary>The latest samples agree when the largest is at most this many times the smallest.</summary>
    public const double MaxSampleSpread = 1.25;

    /// <summary>No estimate until at least this share of the steps is done.</summary>
    public const double MinFractionDone = 0.15;

    /// <summary>The recent window the step rate is also measured over, to tell a steady pace from a changing one.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(10);

    /// <summary>The recent rate and the window rate agree when they are within this share of each other.</summary>
    public const double MaxRateDrift = 0.15;
}
