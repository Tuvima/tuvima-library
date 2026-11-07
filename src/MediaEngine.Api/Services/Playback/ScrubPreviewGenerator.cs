using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.Processors;

namespace MediaEngine.Api.Services.Playback;

public enum ScrubPreviewOutcome
{
    /// <summary>The sprite sheets and index were made.</summary>
    Generated,

    /// <summary>A complete set already exists; nothing was done.</summary>
    AlreadyComplete,

    /// <summary>The switch is off.</summary>
    Disabled,

    /// <summary>FFmpeg is not installed or not found, so previews cannot be made on this machine yet.</summary>
    FfmpegUnavailable,

    /// <summary>The asset or its file is gone.</summary>
    SourceMissing,

    /// <summary>The file has no picture to take frames from (audio) or no length.</summary>
    NotVideo,

    /// <summary>FFmpeg ran and failed.</summary>
    Failed,
}

public sealed record ScrubPreviewResult(ScrubPreviewOutcome Outcome, string Message, int FrameCount = 0, int SheetCount = 0);

/// <summary>
/// Makes the scrub previews of one video file: one frame every few seconds tiled into sprite sheets plus a small index, stored in the
/// Library's own cache. Safe to call again; a complete set is left alone.
/// </summary>
public sealed class ScrubPreviewGenerator(
    IMediaAssetRepository assets,
    IFFmpegService ffmpeg,
    VideoFrameExtractor extractor,
    ScrubPreviewCache store,
    IConfigurationLoader configuration,
    ILogger<ScrubPreviewGenerator> logger)
{
    public async Task<ScrubPreviewResult> GenerateAsync(Guid assetId, CancellationToken ct)
    {
        var settings = configuration.LoadTranscoding().ScrubPreviews;
        if (!settings.Enabled)
        {
            return new(ScrubPreviewOutcome.Disabled, "Scrub previews are switched off.");
        }

        var asset = await assets.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null || !File.Exists(asset.FilePathRoot))
        {
            return new(ScrubPreviewOutcome.SourceMissing, "The video file is not there.");
        }

        if (store.TryReadComplete(assetId) is { } existing)
        {
            return new(ScrubPreviewOutcome.AlreadyComplete, "Previews already exist.", existing.FrameCount, existing.Sheets.Count);
        }

        if (!ffmpeg.IsAvailable)
        {
            return new(ScrubPreviewOutcome.FfmpegUnavailable, "FFmpeg is not available, so scrub previews cannot be made yet.");
        }

        var probe = await ffmpeg.ProbeAsync(asset.FilePathRoot, ct).ConfigureAwait(false);
        if (probe is null || string.IsNullOrWhiteSpace(probe.VideoCodec) || probe.Duration <= TimeSpan.Zero)
        {
            return new(ScrubPreviewOutcome.NotVideo, "The file has no video picture or no length.");
        }

        var options = new ScrubSpriteOptions(
            Math.Clamp(settings.IntervalSeconds, 1, 120),
            Math.Clamp(settings.TileWidth, 80, 640),
            Math.Clamp(settings.Columns, 1, 20),
            Math.Clamp(settings.Rows, 1, 20));
        var staging = store.NewStagingFolder(assetId);
        try
        {
            var built = await extractor.BuildSpriteSheetsAsync(
                asset.FilePathRoot, probe.Duration.TotalSeconds, options, staging, ct).ConfigureAwait(false);
            if (!built.Succeeded)
            {
                logger.LogWarning("Scrub previews for asset {AssetId} could not be made: {Error}", assetId, built.Error);
                return new(ScrubPreviewOutcome.Failed, built.Error ?? "FFmpeg produced no sprite sheets.");
            }

            // The switch may have been turned off (and the cache cleared) while FFmpeg was working.
            if (!configuration.LoadTranscoding().ScrubPreviews.Enabled)
            {
                return new(ScrubPreviewOutcome.Disabled, "Scrub previews were switched off while they were being made.");
            }

            store.Publish(assetId, staging, new ScrubPreviewIndexDto
            {
                IntervalSeconds = options.IntervalSeconds,
                TileWidth = built.TileWidth,
                TileHeight = built.TileHeight,
                Columns = options.Columns,
                Rows = options.Rows,
                FrameCount = built.FrameCount,
                DurationSeconds = Math.Round(probe.Duration.TotalSeconds, 3),
                Sheets = built.SheetFileNames,
            });
            logger.LogInformation(
                "Made scrub previews for asset {AssetId}: {Frames} frames in {Sheets} sprite sheets.",
                assetId, built.FrameCount, built.SheetFileNames.Count);
            return new(ScrubPreviewOutcome.Generated, $"{built.FrameCount} frames in {built.SheetFileNames.Count} sprite sheets.", built.FrameCount, built.SheetFileNames.Count);
        }
        finally
        {
            store.DeleteStaging(staging);
        }
    }
}
