namespace MediaEngine.Contracts.Playback;

/// <summary>How the graphics-card test came out (playback-path spec D6, D11).</summary>
public static class GraphicsHardwareOutcomes
{
    /// <summary>A graphics encoder passed the test; Library uses it unless the administrator chose the processor.</summary>
    public const string Works = "works";

    /// <summary>FFmpeg lists a graphics encoder but the test failed; <see cref="GraphicsHardwareDto.Reason"/> says why in plain words.</summary>
    public const string Failed = "failed";

    /// <summary>No graphics encoding was found; Library uses the processor.</summary>
    public const string None = "none";

    /// <summary>FFmpeg is not ready yet (missing or still downloading), so nothing could be tested.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>
/// What the Engine found when it tested the server's graphics hardware, for Settings &gt; Playback &gt; Quality and the Graphics hardware row
/// in first-run setup. Both draw the same words from the same facts.
/// </summary>
public sealed record GraphicsHardwareDto
{
    /// <summary>One of <see cref="GraphicsHardwareOutcomes"/>.</summary>
    public string Outcome { get; init; } = GraphicsHardwareOutcomes.Unavailable;

    /// <summary>The graphics card's name when the system could say ("NVIDIA GeForce RTX 5080"), else the encoder family ("NVIDIA NVENC"), else null.</summary>
    public string? CardName { get; init; }

    /// <summary>The encoder family that passed ("NVIDIA NVENC", "Intel Quick Sync", "VAAPI"), or null.</summary>
    public string? Accelerator { get; init; }

    /// <summary>One plain sentence for a failed test ("The graphics driver is too old for this card or this FFmpeg."), or null.</summary>
    public string? Reason { get; init; }

    /// <summary>FFmpeg's own error text behind "Show details" for a failed test, or null.</summary>
    public string? Detail { get; init; }

    /// <summary>True when the administrator chose "Processor only", so Library uses the processor even though a card works.</summary>
    public bool ProcessorOnlyChosen { get; init; }

    /// <summary>When this answer was produced by a test, or null when it is the answer from the Engine's start-up.</summary>
    public DateTimeOffset? TestedAt { get; init; }
}
