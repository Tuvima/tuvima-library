using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Provides access to FFmpeg binaries for media probing and processing.
/// The implementation auto-detects binaries at startup in priority order:
///   1. Explicit path from config/transcoding.json (authoritative when set)
///   2. tools/ffmpeg/ in the Tuvima data folder
///   3. tools/ffmpeg/ relative to the app base directory (self-contained)
///   4. System PATH
///   5. The operating system's standard install folders
/// </summary>
public interface IFFmpegService
{
    /// <summary>Absolute path to ffmpeg.exe; null when not available.</summary>
    string? FfmpegPath { get; }

    /// <summary>Absolute path to ffprobe.exe; null when not available.</summary>
    string? FfprobePath { get; }

    /// <summary>True when both binaries are located and executable.</summary>
    bool IsAvailable { get; }

    /// <summary>Hardware acceleration capabilities detected at startup.</summary>
    HardwareCapabilities HardwareCapabilities { get; }

    /// <summary>Where the latest search found FFmpeg (or that it did not).</summary>
    FFmpegDetection Detection => FFmpegDetection.NotFound;

    /// <summary>
    /// Searches again (the explicit path in settings may have changed) and returns <see cref="IsAvailable"/>.
    /// Work already running keeps the paths it started with.
    /// </summary>
    bool Redetect() => IsAvailable;

    /// <summary>
    /// Run ffprobe on the given file and return structured media metadata.
    /// Returns null if ffprobe is unavailable or the file cannot be probed.
    /// </summary>
    Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Execute ffmpeg with the given argument string.
    /// Returns (exit code, stdout, stderr).
    /// Throws <see cref="InvalidOperationException"/> if ffmpeg is unavailable.
    /// </summary>
    Task<(int ExitCode, string Output, string Error)> RunAsync(
        string arguments, CancellationToken ct = default);

    Task<(int ExitCode, string Output, string Error)> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken ct = default) =>
        RunAsync(
            string.Join(' ', arguments.Select(argument =>
                argument.Any(char.IsWhiteSpace) || argument.Contains('"')
                    ? $"\"{argument.Replace("\"", "\\\"")}\""
                    : argument)),
            ct);
}
