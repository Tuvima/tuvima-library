using System.Text.Json;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Playback;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Services;

/// <summary>What happened when a file's technical facts were recorded.</summary>
public enum PlaybackInspectionOutcome
{
    /// <summary>The file is not audio or video, so there is nothing to inspect.</summary>
    NotApplicable,

    /// <summary>The facts for this exact file content were recorded before.</summary>
    AlreadyInspected,

    /// <summary>The facts were read and recorded now.</summary>
    Inspected,

    /// <summary>The media tool (FFmpeg) is not available. The file is marked as waiting and carries on as normal.</summary>
    WaitingForTool,

    /// <summary>The tool is available but could not read this file. The caller sends the file to Needs Review.</summary>
    Failed,
}

/// <summary>Durable technical facts, captured at ingestion rather than during browsing or Play.</summary>
public sealed class PlaybackInspectionWriter(
    IFFmpegService ffmpeg,
    PlaybackStateRepository repository,
    IMediaInspectionHoldRepository holds,
    ILogger<PlaybackInspectionWriter> logger)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".wav", ".mp4", ".m4v", ".mkv", ".webm", ".avi", ".wma", ".opus", ".mov" };

    /// <summary>
    /// Records the file's technical facts. A missing tool is a state, not an error: the file is held and the outcome is
    /// <see cref="PlaybackInspectionOutcome.WaitingForTool"/>. A failed probe with the tool present leaves any hold in
    /// place so the caller can record the problem first and then clear it (<see cref="MediaInspectionReviewRecorder"/>).
    /// </summary>
    public async Task<PlaybackInspectionOutcome> InspectAsync(MediaAsset asset, CancellationToken ct)
    {
        if (!Extensions.Contains(Path.GetExtension(asset.FilePathRoot)))
        {
            return PlaybackInspectionOutcome.NotApplicable;
        }
        if (await repository.GetInspectionMetadataAsync(asset.Id, asset.ContentHash, ct) is not null)
        {
            await holds.DeleteAsync(asset.Id, ct);
            return PlaybackInspectionOutcome.AlreadyInspected;
        }
        if (!ffmpeg.IsAvailable)
        {
            await holds.UpsertAsync(asset.Id, ToolRequirementIds.Ffmpeg, DateTimeOffset.UtcNow, ct);
            logger.LogInformation("Media inspection of asset {AssetId} is waiting for FFmpeg.", asset.Id);
            return PlaybackInspectionOutcome.WaitingForTool;
        }

        MediaProbeResult? probe = null;
        try
        {
            probe = await ffmpeg.ProbeAsync(asset.FilePathRoot, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Media inspection of asset {AssetId} threw while reading {Path}.", asset.Id, asset.FilePathRoot);
        }

        if (probe is null)
        {
            logger.LogWarning("Media inspection of asset {AssetId} did not return technical facts for {Path}.", asset.Id, asset.FilePathRoot);
            return PlaybackInspectionOutcome.Failed;
        }

        await repository.StoreInspectionAsync(asset.Id, asset.ContentHash, probe.FileSizeBytes,
            probe.Duration.TotalSeconds, Path.GetExtension(asset.FilePathRoot).TrimStart('.'),
            JsonSerializer.Serialize(probe), ct);
        await holds.DeleteAsync(asset.Id, ct);
        return PlaybackInspectionOutcome.Inspected;
    }
}
