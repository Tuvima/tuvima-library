// Linked into the test projects that run a real FFmpeg (Processors.Tests, Api.Tests). Tests that need it carry [RealFfmpegFact] and
// are skipped where FFmpeg is not installed, so a machine without it stays green.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.TestSupport;

/// <summary>A fact that runs only where ffmpeg and ffprobe are installed.</summary>
internal sealed class RealFfmpegFactAttribute : FactAttribute
{
    public RealFfmpegFactAttribute()
    {
        if (RealFfmpeg.Find() is null)
        {
            Skip = "ffmpeg and ffprobe are not installed here, so the real-FFmpeg check cannot run.";
        }
    }
}

/// <summary>The real FFmpeg and ffprobe behind <see cref="IFFmpegService"/>, plus a way to make a small sample video.</summary>
internal sealed class RealFfmpeg : IFFmpegService
{
    private readonly string _ffmpeg;
    private readonly string _ffprobe;

    private RealFfmpeg(string ffmpeg, string ffprobe)
    {
        _ffmpeg = ffmpeg;
        _ffprobe = ffprobe;
    }

    public static RealFfmpeg? Find()
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => folder.Trim())
            .Append("/usr/bin")
            .Append("/usr/local/bin");
        var suffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        foreach (var folder in folders)
        {
            var ffmpeg = Path.Combine(folder, "ffmpeg" + suffix);
            var ffprobe = Path.Combine(folder, "ffprobe" + suffix);
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
            {
                return new RealFfmpeg(ffmpeg, ffprobe);
            }
        }

        return null;
    }

    public string? FfmpegPath => _ffmpeg;
    public string? FfprobePath => _ffprobe;
    public bool IsAvailable => true;
    public HardwareCapabilities HardwareCapabilities { get; } = new();

    /// <summary>Makes a colourful test-pattern video: key frames every two seconds, small and quick to encode.</summary>
    public async Task MakeSampleVideoAsync(string path, int seconds, int width = 160, int height = 90)
    {
        var (exit, _, error) = await RunAsync(
            $"-v error -y -f lavfi -i testsrc2=s={width}x{height}:d={seconds.ToString(CultureInfo.InvariantCulture)}:r=5 -pix_fmt yuv420p -c:v libx264 -preset ultrafast -g 10 \"{path}\"");
        if (exit != 0 || !File.Exists(path))
        {
            throw new InvalidOperationException("Could not make the sample video: " + error);
        }
    }

    public async Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        var (exit, output, _) = await RunAsync(_ffprobe,
            $"-v error -print_format json -show_entries format=duration:stream=codec_type,codec_name \"{filePath}\"", ct);
        if (exit != 0)
        {
            return null;
        }

        using var document = JsonDocument.Parse(output);
        var duration = document.RootElement.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var value)
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
        string? videoCodec = null;
        if (document.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                if (stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video")
                {
                    videoCodec = stream.GetProperty("codec_name").GetString();
                    break;
                }
            }
        }

        return new MediaProbeResult { Duration = duration, VideoCodec = videoCodec };
    }

    public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default) =>
        RunAsync(_ffmpeg, arguments, ct);

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string exe, string arguments, CancellationToken ct = default)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
        })!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await output, await error);
    }
}
