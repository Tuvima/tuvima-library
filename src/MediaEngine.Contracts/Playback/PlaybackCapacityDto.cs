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

    /// <summary>How many 4K (3840 x 2160) conversions at once kept up in real time; 0 when the computer cannot keep up with even one, or the test did not run.</summary>
    public int FourKStreamsSupported { get; init; }

    /// <summary>How many times faster than playback one 1080p conversion ran (12 means 12x); 0 when not measured.</summary>
    public double SpeedFactor { get; init; }

    /// <summary>The overall score out of 100 drawn as the gauge (the Engine's capacity score rule); 0 when not measured.</summary>
    public int Score { get; init; }

    /// <summary>One plain sentence saying why a test did not run or fell back, or null.</summary>
    public string? Reason { get; init; }

    /// <summary>How long the test took, in seconds.</summary>
    public int SecondsTaken { get; init; }

    /// <summary>When the test ran.</summary>
    public DateTimeOffset? TestedAt { get; init; }
}
