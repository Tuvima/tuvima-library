using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Plugins;

namespace MediaEngine.Plugin.MediaSegments;

public sealed class IntroSkipSegmentDetector : FfmpegSegmentDetector
{
    public IntroSkipSegmentDetector(PluginManifest manifest) : base(manifest) { }
    protected override string SwitchKey => SkipMarkersPlugin.DetectIntroKey;
    public override async Task<IReadOnlyList<PluginPlaybackSegment>> AnalyzeAsync(PluginMediaAssetContext asset, IPluginExecutionContext context, CancellationToken cancellationToken = default)
    {
        var duration = await ProbeDurationAsync(asset, context, cancellationToken).ConfigureAwait(false);
        var min = GetDouble(context, "minimum_intro_seconds", 20);
        var max = GetDouble(context, "maximum_intro_seconds", 120);
        var window = GetDouble(context, "intro_search_window_seconds", 360);
        var end = Math.Min(window, duration is > 0 ? duration.Value * 0.25 : window);
        if (end < min)
        {
            return [];
        }

        // Lightweight v1 heuristic: reserve an intro-sized window after a cold open.
        var start = Math.Min(90, Math.Max(0, end - max));
        var segmentEnd = Math.Min(end, start + Math.Min(max, 90));
        return segmentEnd - start >= min
            ? [Segment("intro", start, segmentEnd, "plugin:intro-skip:heuristic", 0.48)]
            : [];
    }
}

public sealed class CreditsSegmentDetector : FfmpegSegmentDetector
{
    private static readonly Regex DurationRegex = new(@"Duration:\s*(?<h>\d+):(?<m>\d+):(?<s>\d+(?:\.\d+)?)", RegexOptions.Compiled);

    public CreditsSegmentDetector(PluginManifest manifest) : base(manifest) { }
    protected override string SwitchKey => SkipMarkersPlugin.DetectCreditsKey;
    public override async Task<IReadOnlyList<PluginPlaybackSegment>> AnalyzeAsync(PluginMediaAssetContext asset, IPluginExecutionContext context, CancellationToken cancellationToken = default)
    {
        var duration = await ProbeDurationAsync(asset, context, cancellationToken).ConfigureAwait(false)
            ?? await ReadDurationFromFfmpegAsync(asset, context, cancellationToken).ConfigureAwait(false);
        if (duration is null or <= 0)
        {
            return [];
        }

        var min = GetDouble(context, "minimum_credit_seconds", 30);
        var tail = GetDouble(context, "credits_tail_window_seconds", 900);
        var start = Math.Max(0, duration.Value - Math.Min(tail, Math.Max(min, duration.Value * 0.08)));
        return duration.Value - start >= min
            ? [Segment("credits", start, duration.Value, "plugin:credits-detection:tail-heuristic", 0.58)]
            : [];
    }

    private async Task<double?> ReadDurationFromFfmpegAsync(PluginMediaAssetContext asset, IPluginExecutionContext context, CancellationToken ct)
    {
        var ffmpeg = await ResolveAsync("ffmpeg", context, ct).ConfigureAwait(false);
        if (ffmpeg is null)
        {
            return null;
        }

        var result = await context.Tools.RunToolAsync(
            ffmpeg,
            ["-hide_banner", "-i", asset.FilePath],
            context.Storage.WorkingDirectory,
            TimeSpan.FromMinutes(2),
            ct).ConfigureAwait(false);
        var match = DurationRegex.Match(result.StandardError + result.StandardOutput);
        if (!match.Success)
        {
            return null;
        }

        return int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) * 3600
            + int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) * 60
            + double.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
    }
}

public sealed class RecapSegmentDetector : IPlaybackSegmentDetector
{
    public string Kind => "playback-segment-detector";
    public bool CanAnalyze(PluginMediaAssetContext asset, IPluginExecutionContext context) =>
        FfmpegSegmentDetector.IsVideo(asset, context) && FfmpegSegmentDetector.IsSwitchedOn(context, SkipMarkersPlugin.DetectRecapKey);

    public Task<IReadOnlyList<PluginPlaybackSegment>> AnalyzeAsync(PluginMediaAssetContext asset, IPluginExecutionContext context, CancellationToken cancellationToken = default)
    {
        var min = FfmpegSegmentDetector.GetDouble(context, "minimum_recap_seconds", 20);
        var max = FfmpegSegmentDetector.GetDouble(context, "maximum_recap_seconds", 180);
        var end = Math.Min(max, Math.Max(min, 75));
        IReadOnlyList<PluginPlaybackSegment> result =
        [
            FfmpegSegmentDetector.Segment("recap", 0, end, "plugin:recap-detection:opening-window", 0.35),
        ];
        return Task.FromResult(result);
    }
}

public abstract class FfmpegSegmentDetector : IPlaybackSegmentDetector
{
    private readonly PluginManifest _manifest;
    public string Kind => "playback-segment-detector";

    protected FfmpegSegmentDetector(PluginManifest manifest)
    {
        _manifest = manifest;
    }

    /// <summary>The setting that switches this kind of marker on or off; empty for a detector that has no switch.</summary>
    protected virtual string SwitchKey => "";

    public bool CanAnalyze(PluginMediaAssetContext asset, IPluginExecutionContext context) =>
        IsVideo(asset, context) && (SwitchKey.Length == 0 || IsSwitchedOn(context, SwitchKey));

    public abstract Task<IReadOnlyList<PluginPlaybackSegment>> AnalyzeAsync(PluginMediaAssetContext asset, IPluginExecutionContext context, CancellationToken cancellationToken = default);

    public static bool IsVideo(PluginMediaAssetContext asset, IPluginExecutionContext context) =>
        context.Media.Exists(asset) && PluginMediaExtensions.Video.Contains(Path.GetExtension(asset.FilePath));

    protected async Task<double?> ProbeDurationAsync(PluginMediaAssetContext asset, IPluginExecutionContext context, CancellationToken ct)
    {
        var ffprobe = await ResolveAsync("ffprobe", context, ct).ConfigureAwait(false);
        if (ffprobe is null)
        {
            return asset.DurationSeconds;
        }

        var result = await context.Tools.RunToolAsync(
            ffprobe,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", asset.FilePath],
            context.Storage.WorkingDirectory,
            TimeSpan.FromMinutes(2),
            ct).ConfigureAwait(false);
        return double.TryParse(result.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : asset.DurationSeconds;
    }

    protected async Task<PluginToolResolution?> ResolveAsync(string toolId, IPluginExecutionContext context, CancellationToken ct)
    {
        var requirement = _manifest.ToolRequirements.FirstOrDefault(t => string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase));
        if (requirement is null)
        {
            return null;
        }

        var resolved = await context.Tools.ResolveToolAsync(requirement.Id, ct).ConfigureAwait(false);
        return resolved.IsAvailable ? resolved : null;
    }

    /// <summary>True unless the setting is explicitly false (a missing setting means on).</summary>
    public static bool IsSwitchedOn(IPluginExecutionContext context, string key) =>
        !context.Settings.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.False;

    public static PluginPlaybackSegment Segment(string kind, double start, double end, string source, double confidence) => new()
    {
        Kind = kind,
        StartSeconds = Math.Max(0, start),
        EndSeconds = Math.Max(start, end),
        Confidence = confidence,
        Source = source,
        IsSkippable = true,
        ReviewStatus = "detected",
    };

    public static double GetDouble(IPluginExecutionContext context, string key, double fallback)
    {
        if (!context.Settings.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var parsed) => parsed,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };
    }
}
