using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Ingestion.Tests.Helpers;

/// <summary>How the fake media tool behaves: missing, working, or working but unable to read every file.</summary>
internal enum FakeFFmpegMode
{
    /// <summary>The tool is not installed: no paths, <see cref="IFFmpegService.IsAvailable"/> is false, and probing is a bug.</summary>
    Unavailable,

    /// <summary>The tool is installed and every probe returns plausible technical facts.</summary>
    Available,

    /// <summary>The tool is installed, but every probe returns nothing (a corrupt or unreadable file).</summary>
    ProbeReturnsNull,
}

/// <summary>
/// A hand-written <see cref="IFFmpegService"/> a test configures. Files named in <see cref="CorruptFileNames"/> always probe
/// as unreadable, whatever the mode, so one batch can hold a good file and a corrupt one.
/// </summary>
internal sealed class FakeFFmpegService(FakeFFmpegMode mode) : IFFmpegService
{
    private readonly object _gate = new();
    private readonly List<string> _probedPaths = [];

    /// <summary>File names (not paths) that probe as unreadable even when the tool is available.</summary>
    public HashSet<string> CorruptFileNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? FfmpegPath => mode == FakeFFmpegMode.Unavailable ? null : "ffmpeg-fake";

    public string? FfprobePath => mode == FakeFFmpegMode.Unavailable ? null : "ffprobe-fake";

    public bool IsAvailable => mode != FakeFFmpegMode.Unavailable;

    public HardwareCapabilities HardwareCapabilities { get; } = new();

    public IReadOnlyList<string> ProbedPaths
    {
        get
        {
            lock (_gate)
            {
                return [.. _probedPaths];
            }
        }
    }

    public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        if (mode == FakeFFmpegMode.Unavailable)
        {
            throw new InvalidOperationException(
                $"ProbeAsync was called for '{filePath}' although the media tool is not installed.");
        }

        lock (_gate)
        {
            _probedPaths.Add(filePath);
        }

        if (mode == FakeFFmpegMode.ProbeReturnsNull || CorruptFileNames.Contains(Path.GetFileName(filePath)))
        {
            return Task.FromResult<MediaProbeResult?>(null);
        }

        var isAudio = Path.GetExtension(filePath).Equals(".mp3", StringComparison.OrdinalIgnoreCase);
        return Task.FromResult<MediaProbeResult?>(new MediaProbeResult
        {
            Duration = TimeSpan.FromMinutes(isAudio ? 3 : 100),
            FileSizeBytes = new FileInfo(filePath).Length,
            AudioCodec = isAudio ? "mp3" : "aac",
            VideoCodec = isAudio ? null : "h264",
            Width = isAudio ? null : 1920,
            Height = isAudio ? null : 1080,
        });
    }

    public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default)
        => throw new InvalidOperationException("The fake media tool does not run ffmpeg.");
}
