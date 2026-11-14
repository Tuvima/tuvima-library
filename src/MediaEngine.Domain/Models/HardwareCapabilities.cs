namespace MediaEngine.Domain.Models;

/// <summary>
/// Hardware video encoding/decoding capabilities detected from the local FFmpeg installation.
/// </summary>
public sealed record HardwareCapabilities
{
    /// <summary>NVIDIA NVENC GPU encoder available.</summary>
    public bool HasNvenc { get; init; }

    /// <summary>Intel Quick Sync Video encoder available.</summary>
    public bool HasQuickSync { get; init; }

    /// <summary>AMD/Intel VAAPI encoder available (Linux).</summary>
    public bool HasVaapi { get; init; }

    /// <summary>
    /// The preferred encoder name for FFmpeg based on detected hardware.
    /// Defaults to <c>libx264</c> (software) when no hardware encoder is found.
    /// </summary>
    public string PreferredEncoder { get; init; } = "libx264";

    /// <summary>Human-readable description of the detected accelerator, or null.</summary>
    public string? DetectedAccelerator { get; init; }

    /// <summary>
    /// Why a graphics encoder that FFmpeg lists did not pass the start-up test, keyed by encoder name (for example
    /// <c>h264_nvenc</c>), holding the tail of FFmpeg's own error text. Empty when every listed encoder worked or none was listed.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProbeFailures { get; init; } = new Dictionary<string, string>();

    /// <summary>The graphics card's name when the operating system could say (for example "NVIDIA GeForce RTX 5080"), or null.</summary>
    public string? GpuName { get; init; }

    public bool HasHlsMuxer { get; init; }
    public bool HasH264Encoder { get; init; }
    public bool HasAacEncoder { get; init; }
    public bool HasWebVttEncoder { get; init; }
    public bool AdaptiveHlsReady => HasHlsMuxer && HasH264Encoder && HasAacEncoder;
}
