using System.Text.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Plugins;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

public sealed class PluginSegmentDetectionService
{
    private readonly PluginCatalog _catalog;
    private readonly IPlaybackSegmentRepository _segments;
    private readonly IMediaAssetRepository _assets;
    private readonly IPluginExecutionContextFactory _contexts;
    private readonly ILogger<PluginSegmentDetectionService> _logger;
    private readonly PluginActivityService? _activity;

    public PluginSegmentDetectionService(
        PluginCatalog catalog,
        IPlaybackSegmentRepository segments,
        IMediaAssetRepository assets,
        IPluginExecutionContextFactory contexts,
        ILogger<PluginSegmentDetectionService> logger,
        PluginActivityService? activity = null)
    {
        _activity = activity;
        _catalog = catalog;
        _segments = segments;
        _assets = assets;
        _contexts = contexts;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PlaybackSegmentDto>> DetectAsync(Guid assetId, string? pluginId, CancellationToken ct = default)
    {
        var asset = await _assets.FindByIdAsync(assetId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            return [];
        }

        var context = new PluginMediaAssetContext
        {
            AssetId = asset.Id,
            FilePath = asset.FilePathRoot,
            MediaType = InferMediaType(asset.FilePathRoot),
        };

        var registrations = string.IsNullOrWhiteSpace(pluginId)
            ? _catalog.List().Where(r => r.Enabled && r.LoadError is null)
            : _catalog.List().Where(r => string.Equals(r.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));

        var detected = new List<PlaybackSegment>();
        foreach (var registration in registrations)
        {
            if (!registration.Enabled || registration.LoadError is not null)
            {
                continue;
            }

            var activityContext = new PluginActivityContext(registration.Manifest.Id, registration.Manifest.Version);
            // A detector that keeps failing is paused for a while; the rest of the Library carries on without it (D7, C10).
            if (_activity is not null && !_activity.Allow(registration.Manifest.Id))
            {
                continue;
            }

            var detectedBefore = detected.Count;
            try
            {
                await using var execution = _contexts.CreateForMedia(registration.Manifest.Id, "segment-detection", context);
                foreach (var detector in registration.Capabilities.OfType<IPlaybackSegmentDetector>())
                {
                    if (!detector.CanAnalyze(context, execution))
                    {
                        continue;
                    }

                    var results = await detector.AnalyzeAsync(context, execution, ct).ConfigureAwait(false);
                    detected.AddRange(results.Select(result => new PlaybackSegment
                    {
                        Id = Guid.NewGuid(),
                        AssetId = assetId,
                        Kind = result.Kind,
                        StartSeconds = result.StartSeconds,
                        EndSeconds = result.EndSeconds,
                        Confidence = Math.Clamp(result.Confidence, 0, 1),
                        Source = result.Source,
                        PluginId = registration.Manifest.Id,
                        IsSkippable = result.IsSkippable,
                        ReviewStatus = result.ReviewStatus,
                    }));
                }

                if (_activity is not null)
                {
                    await _activity.RecordRunAsync(activityContext, detected.Count - detectedBefore, $"Looked for skip markers and found {detected.Count - detectedBefore}.", ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Plugin segment detection failed for {PluginId} asset {AssetId}", registration.Manifest.Id, assetId);
                if (_activity is not null)
                {
                    await _activity.RecordFailureAsync(activityContext, PluginFailureClassifier.Classify(ex), exception: ex, itemId: assetId, ct: ct).ConfigureAwait(false);
                }
            }
        }

        await _segments.UpsertBatchAsync(assetId, detected, ct).ConfigureAwait(false);
        return (await _segments.ListByAssetAsync(assetId, ct).ConfigureAwait(false)).Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<PlaybackSegmentDto>> ListExistingAsync(Guid assetId, CancellationToken ct = default) =>
        (await _segments.ListByAssetAsync(assetId, ct).ConfigureAwait(false)).Select(ToDto).ToList();

    public static PlaybackSegmentDto ToDto(PlaybackSegment segment) => new()
    {
        Id = segment.Id,
        AssetId = segment.AssetId,
        Kind = segment.Kind,
        StartSeconds = segment.StartSeconds,
        EndSeconds = segment.EndSeconds,
        Confidence = segment.Confidence,
        Source = segment.Source,
        PluginId = segment.PluginId,
        IsSkippable = segment.IsSkippable,
        ReviewStatus = segment.ReviewStatus,
    };

    private static string InferMediaType(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" or ".mkv" or ".webm" or ".avi" => "Movies",
            ".mp3" or ".m4a" or ".m4b" or ".aac" or ".flac" or ".ogg" or ".wav" => "Audio",
            _ => "Unknown",
        };
    }

}

