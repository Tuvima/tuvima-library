namespace MediaEngine.Contracts.Playback;

/// <summary>
/// What the Engine measured about how many videos this computer can convert at the same time and still keep up with playback.
/// Drawn by the first-run "Checking your system" screen and Settings &gt; Playback &gt; Quality.
/// </summary>
public sealed record PlaybackCapacityDto
{
    /// <summary>How many conversions at once kept up in real time with room to spare (at least 1).</summary>
    public int StreamsSupported { get; init; } = 1;

    /// <summary>True when this number was measured; false when the test could not run and the safe default of 1 was kept.</summary>
    public bool Measured { get; init; }

    /// <summary>"graphics-card" or "processor": the hardware the test used.</summary>
    public string RanOn { get; init; } = "processor";

    /// <summary>The graphics card's name when the test used it, or null.</summary>
    public string? CardName { get; init; }

    /// <summary>One plain sentence saying why a test did not run or fell back, or null.</summary>
    public string? Reason { get; init; }

    /// <summary>How long the test took, in seconds.</summary>
    public int SecondsTaken { get; init; }

    /// <summary>When the test ran.</summary>
    public DateTimeOffset? TestedAt { get; init; }
}
