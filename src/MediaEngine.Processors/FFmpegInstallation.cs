using MediaEngine.Domain.Models;

namespace MediaEngine.Processors;

/// <summary>
/// One immutable picture of where FFmpeg was found and what it can do. <see cref="FFmpegService"/> swaps in a new
/// one on every search, so anything holding an earlier one keeps working with the paths it started with.
/// </summary>
internal sealed record FFmpegInstallation(
    string? FfmpegPath,
    string? FfprobePath,
    HardwareCapabilities Hardware,
    string HardwareMode,
    FFmpegDetection Detection)
{
    public bool IsAvailable => FfmpegPath is not null && FfprobePath is not null;
}
