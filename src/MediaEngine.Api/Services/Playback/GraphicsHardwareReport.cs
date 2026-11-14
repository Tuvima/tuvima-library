using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Turns what FFmpeg's graphics test found into the one <see cref="GraphicsHardwareDto"/> that Settings and first-run setup both draw
/// (playback-path spec D6, D11). Facts only; the Dashboard owns the sentences around them.
/// </summary>
public static class GraphicsHardwareReport
{
    /// <summary>The answer from the Engine's last test, without testing again.</summary>
    public static GraphicsHardwareDto Current(IFFmpegService ffmpeg, string hardwareAcceleration) =>
        Build(ffmpeg.IsAvailable, ffmpeg.HardwareCapabilities, hardwareAcceleration, testedAt: null);

    /// <summary>Tests the graphics encoders again right now and returns what was found.</summary>
    public static GraphicsHardwareDto Test(IFFmpegService ffmpeg, string hardwareAcceleration, TimeProvider clock)
    {
        if (!ffmpeg.IsAvailable)
        {
            return Build(false, ffmpeg.HardwareCapabilities, hardwareAcceleration, clock.GetUtcNow());
        }

        return Build(true, ffmpeg.RetestHardware(), hardwareAcceleration, clock.GetUtcNow());
    }

    public static GraphicsHardwareDto Build(bool ffmpegReady, HardwareCapabilities hardware, string hardwareAcceleration, DateTimeOffset? testedAt)
    {
        var processorOnly = hardwareAcceleration.Trim().ToLowerInvariant() is "none" or "cpu";
        if (!ffmpegReady)
        {
            return new GraphicsHardwareDto { Outcome = GraphicsHardwareOutcomes.Unavailable, ProcessorOnlyChosen = processorOnly, TestedAt = testedAt };
        }

        var accelerator = hardware.HasNvenc ? "NVIDIA NVENC"
            : hardware.HasQuickSync ? "Intel Quick Sync"
            : hardware.HasVaapi ? "VAAPI"
            : null;
        if (accelerator is not null)
        {
            return new GraphicsHardwareDto
            {
                Outcome = GraphicsHardwareOutcomes.Works,
                CardName = NameOrNull(hardware.GpuName) ?? accelerator,
                Accelerator = accelerator,
                ProcessorOnlyChosen = processorOnly,
                TestedAt = testedAt,
            };
        }

        if (hardware.ProbeFailures.Count > 0)
        {
            return new GraphicsHardwareDto
            {
                Outcome = GraphicsHardwareOutcomes.Failed,
                CardName = NameOrNull(hardware.GpuName),
                Reason = HardwareReasons.Plain(hardware.ProbeFailures.First().Value),
                Detail = string.Join(Environment.NewLine, hardware.ProbeFailures.Select(failure => $"{failure.Key}: {failure.Value}")),
                ProcessorOnlyChosen = processorOnly,
                TestedAt = testedAt,
            };
        }

        return new GraphicsHardwareDto
        {
            Outcome = GraphicsHardwareOutcomes.None,
            CardName = NameOrNull(hardware.GpuName),
            ProcessorOnlyChosen = processorOnly,
            TestedAt = testedAt,
        };
    }

    private static string? NameOrNull(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}
