using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Services.Integration;

public enum ShellActivityKind
{
    Playback,
    Ingestion,
    Ai,
    Enrichment,
    Maintenance,
}

public sealed record ShellActivityItem(
    string Key,
    ShellActivityKind Kind,
    string Label,
    string? Detail,
    int? ProgressPercent,
    DateTimeOffset UpdatedAt);

public sealed record ShellActivitySnapshot(IReadOnlyList<ShellActivityItem> Items)
{
    public bool IsBusy => Items.Count > 0;
    public int ActiveCount => Items.Count;
    public ShellActivityItem? Primary => Items.FirstOrDefault();
}

public sealed class ShellActivityState : IDisposable
{
    private static readonly TimeSpan VideoActivityTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LiveIngestionActivityTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EnrichmentActivityTtl = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan ModelDownloadActivityTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan SystemActivityRefreshInterval = TimeSpan.FromSeconds(10);

    private readonly UniverseStateContainer _universeState;
    private readonly PlaybackSessionController _playback;
    private readonly IEngineApiClient _api;
    private readonly ILogger<ShellActivityState> _logger;
    private readonly Timer _expiryTimer;
    private readonly object _videoGate = new();
    private VideoPlaybackActivity? _videoPlayback;
    private string _lastSignature = string.Empty;
    private DateTimeOffset _lastTransportNotification = DateTimeOffset.MinValue;
    private long _lastSystemActivityRefreshTicks;
    private int _systemActivityRefreshInFlight;
    private bool _disposed;

    public ShellActivityState(
        UniverseStateContainer universeState,
        PlaybackSessionController playback,
        IEngineApiClient api,
        ILogger<ShellActivityState> logger)
    {
        _universeState = universeState;
        _playback = playback;
        _api = api;
        _logger = logger;

        _universeState.OnStateChanged += OnUniverseStateChanged;
        _playback.Changed += OnPlaybackChanged;
        _expiryTimer = new Timer(_ => OnTimerTick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public event Action? Changed;

    public ShellActivitySnapshot Current => BuildSnapshot(DateTimeOffset.UtcNow);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await RefreshSystemActivityAsync(force: true, ct);
    }

    private async Task RefreshSystemActivityAsync(bool force = false, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var lastRefreshTicks = Interlocked.Read(ref _lastSystemActivityRefreshTicks);
        if (!force && lastRefreshTicks != 0
            && now - new DateTimeOffset(lastRefreshTicks, TimeSpan.Zero) < SystemActivityRefreshInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _systemActivityRefreshInFlight, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _lastSystemActivityRefreshTicks, now.UtcTicks);
        try
        {
            var activeOperations = await _api.GetSystemActivityOperationsAsync(ct);
            if (_disposed)
            {
                return;
            }

            _universeState.SetMediaOperationActivity(activeOperations.Select(operation =>
                new MediaOperationChangedEvent(
                    operation.Id,
                    operation.OperationType,
                    operation.OperationKind,
                    operation.Status,
                    operation.Stage,
                    operation.ProgressPercent,
                    operation.ItemsTotal,
                    operation.ItemsCompleted,
                    operation.UpdatedAt)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to reconcile the shell activity snapshot.");
        }
        finally
        {
            Interlocked.Exchange(ref _systemActivityRefreshInFlight, 0);
        }
    }

    public void ReportVideoPlayback(
        Guid assetId,
        string title,
        double currentTimeSeconds,
        double durationSeconds,
        bool isPlaying,
        bool ended)
    {
        lock (_videoGate)
        {
            if (ended || !isPlaying)
            {
                if (_videoPlayback?.AssetId == assetId)
                {
                    _videoPlayback = null;
                }
            }
            else
            {
                _videoPlayback = new VideoPlaybackActivity(
                    assetId,
                    string.IsNullOrWhiteSpace(title) ? "Video" : title.Trim(),
                    Math.Max(0, currentTimeSeconds),
                    Math.Max(0, durationSeconds),
                    DateTimeOffset.UtcNow);
            }
        }

        PublishIfChanged(force: true);
    }

    public void ClearVideoPlayback(Guid assetId)
    {
        lock (_videoGate)
        {
            if (_videoPlayback?.AssetId != assetId)
            {
                return;
            }

            _videoPlayback = null;
        }

        PublishIfChanged(force: true);
    }

    private ShellActivitySnapshot BuildSnapshot(DateTimeOffset now)
    {
        var items = new List<ShellActivityItem>();

        AddVideoPlayback(items, now);
        AddListenPlayback(items, now);

        var hasBatch = AddIngestion(items, now);
        var hasUniverseEnrichment = AddUniverseEnrichment(items, now);
        AddModelDownloads(items, now);
        AddMediaOperations(items, hasBatch, hasUniverseEnrichment);

        return new ShellActivitySnapshot(items
            .OrderBy(item => Priority(item.Kind))
            .ThenByDescending(item => item.UpdatedAt)
            .ToList());
    }

    private void AddVideoPlayback(List<ShellActivityItem> items, DateTimeOffset now)
    {
        VideoPlaybackActivity? video;
        lock (_videoGate)
        {
            video = _videoPlayback;
        }

        if (video is null || now - video.UpdatedAt > VideoActivityTtl)
        {
            return;
        }

        items.Add(new ShellActivityItem(
            $"video:{video.AssetId:D}",
            ShellActivityKind.Playback,
            $"Playing {video.Title}",
            "Video playback",
            Percent(video.CurrentTimeSeconds, video.DurationSeconds),
            video.UpdatedAt));
    }

    private void AddListenPlayback(List<ShellActivityItem> items, DateTimeOffset now)
    {
        if (!_playback.HasQueue
            || _playback.Phase is not (PlaybackPhase.Playing or PlaybackPhase.Loading or PlaybackPhase.Ready)
            || (!_playback.IsPlaying && _playback.Phase != PlaybackPhase.Loading))
        {
            return;
        }

        var item = _playback.CurrentItem;
        items.Add(new ShellActivityItem(
            $"listen:{item?.WorkId:D}",
            ShellActivityKind.Playback,
            $"Playing {item?.Title ?? "audio"}",
            _playback.IsAudiobookMode ? "Audiobook playback" : "Audio playback",
            Percent(_playback.CurrentTimeSeconds, _playback.DurationSeconds),
            now));
    }

    private bool AddIngestion(List<ShellActivityItem> items, DateTimeOffset now)
    {
        if (_universeState.BatchProgress is { IsComplete: false } batch
            && _universeState.BatchProgressReceivedAt is { } batchReceivedAt
            && now - batchReceivedAt <= LiveIngestionActivityTtl)
        {
            var label = batch.ProgressIsIndeterminate ? "Finishing library enrichment" : string.IsNullOrWhiteSpace(batch.CurrentFileTitle)
                ? "Processing library files"
                : $"Processing {batch.CurrentFileTitle}";
            items.Add(new ShellActivityItem(
                $"batch:{batch.BatchId:D}",
                ShellActivityKind.Ingestion,
                label,
                FriendlyStage(batch.CurrentStage),
                !batch.ProgressIsIndeterminate && (batch.IsComplete || batch.ProgressPercent < 100)
                    ? Math.Clamp(batch.ProgressPercent, 0, 100)
                    : null,
                batchReceivedAt));
            return true;
        }

        if (_universeState.IngestionProgress is not { } ingestion
            || _universeState.IngestionProgressReceivedAt is not { } ingestionReceivedAt
            || now - ingestionReceivedAt > LiveIngestionActivityTtl
            || string.Equals(ingestion.Stage, "Complete", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        items.Add(new ShellActivityItem(
            "ingestion:live",
            ShellActivityKind.Ingestion,
            string.IsNullOrWhiteSpace(ingestion.CurrentFile) ? "Scanning your library" : $"Processing {Path.GetFileName(ingestion.CurrentFile)}",
            FriendlyStage(ingestion.Stage),
            ingestion.TotalCount > 0
                ? Math.Clamp((int)Math.Round(ingestion.ProcessedCount * 100d / ingestion.TotalCount), 0, 100)
                : null,
            ingestionReceivedAt));
        return true;
    }

    private bool AddUniverseEnrichment(List<ShellActivityItem> items, DateTimeOffset now)
    {
        var enrichment = _universeState.UniverseEnrichmentProgress;
        var receivedAt = _universeState.UniverseEnrichmentProgressReceivedAt;
        if (enrichment is null || receivedAt is null || now - receivedAt > EnrichmentActivityTtl)
        {
            return false;
        }

        items.Add(new ShellActivityItem(
            $"enrichment:{enrichment.WorkQid}",
            ShellActivityKind.Enrichment,
            $"Enriching {enrichment.WorkTitle}",
            FriendlyStage(enrichment.CurrentStep),
            enrichment.TotalCount > 0
                ? Math.Clamp((int)Math.Round(enrichment.ProcessedCount * 100d / enrichment.TotalCount), 0, 100)
                : null,
            receivedAt.Value));
        return true;
    }

    private void AddModelDownloads(List<ShellActivityItem> items, DateTimeOffset now)
    {
        foreach (var model in _universeState.ModelDownloadActivity)
        {
            if (now - model.ReceivedAt > ModelDownloadActivityTtl)
            {
                continue;
            }

            items.Add(new ShellActivityItem(
                $"model:{model.Event.Role}",
                ShellActivityKind.Ai,
                $"Downloading {FriendlyRole(model.Event.Role)}",
                "AI model download",
                Math.Clamp(model.Event.Percent, 0, 100),
                model.ReceivedAt));
        }
    }

    private void AddMediaOperations(List<ShellActivityItem> items, bool hasBatch, bool hasUniverseEnrichment)
    {
        foreach (var operation in _universeState.MediaOperationActivity)
        {
            var kind = ToKind(operation.OperationKind);
            if (hasBatch && kind == ShellActivityKind.Ingestion)
            {
                continue;
            }

            if (hasUniverseEnrichment && kind == ShellActivityKind.Enrichment)
            {
                continue;
            }

            items.Add(new ShellActivityItem(
                $"operation:{operation.Id:D}",
                kind,
                FriendlyOperation(operation.OperationType, operation.OperationKind),
                FriendlyStage(operation.Stage),
                operation.ProgressPercent > 0 || operation.ItemsTotal > 0
                    ? Math.Clamp(operation.ProgressPercent, 0, 100)
                    : null,
                operation.UpdatedAt));
        }
    }

    private void OnUniverseStateChanged()
    {
        PublishIfChanged();
        if (_universeState.LastStateChangeRequiresSnapshotRefresh)
        {
            _ = RefreshSystemActivityAsync(force: true);
        }
    }

    private void OnTimerTick()
    {
        if (_disposed)
        {
            return;
        }

        PublishIfChanged();
        var now = DateTimeOffset.UtcNow;
        var hasFreshBatch = _universeState.BatchProgressReceivedAt is { } batchReceivedAt
            && now - batchReceivedAt <= LiveIngestionActivityTtl;
        var hasFreshIngestion = _universeState.IngestionProgressReceivedAt is { } ingestionReceivedAt
            && now - ingestionReceivedAt <= LiveIngestionActivityTtl;
        if (_universeState.MediaOperationActivity.Count > 0 || hasFreshBatch || hasFreshIngestion)
        {
            _ = RefreshSystemActivityAsync();
        }
    }

    private void OnPlaybackChanged(PlaybackChangeKind kind)
    {
        var now = DateTimeOffset.UtcNow;
        if (kind == PlaybackChangeKind.TransportTick && now - _lastTransportNotification < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastTransportNotification = now;
        PublishIfChanged();
    }

    private void PublishIfChanged(bool force = false)
    {
        var snapshot = BuildSnapshot(DateTimeOffset.UtcNow);
        var signature = string.Join('|', snapshot.Items.Select(item => $"{item.Key}:{item.ProgressPercent}:{item.Label}"));
        if (!force && string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastSignature = signature;
        Changed?.Invoke();
    }

    private static int? Percent(double current, double duration) => duration > 0
        ? Math.Clamp((int)Math.Round(current * 100d / duration), 0, 100)
        : null;

    private static int Priority(ShellActivityKind kind) => kind switch
    {
        ShellActivityKind.Playback => 0,
        ShellActivityKind.Ingestion => 1,
        ShellActivityKind.Ai => 2,
        ShellActivityKind.Enrichment => 3,
        _ => 4,
    };

    private static ShellActivityKind ToKind(string? operationKind) => operationKind?.ToLowerInvariant() switch
    {
        "ingestion" or "identity" => ShellActivityKind.Ingestion,
        "ai" => ShellActivityKind.Ai,
        "enrichment" or "text_track" => ShellActivityKind.Enrichment,
        _ => ShellActivityKind.Maintenance,
    };

    private static string FriendlyOperation(string? operationType, string? operationKind) => operationType?.ToLowerInvariant() switch
    {
        "ingestion.file" => "Processing a library file",
        "identity.retail_match" => "Matching retail metadata",
        "identity.wikidata_bridge" => "Aligning Wikidata identity",
        "identity.quick_hydration" => "Preparing library metadata",
        "enrichment.cover_art" => "Enriching artwork",
        "enrichment.people" => "Enriching people",
        "enrichment.description" => "Enriching descriptions",
        "enrichment.relationships" => "Building relationships",
        "text_track.lyrics" => "Finding lyrics",
        "text_track.subtitles" => "Parsing subtitles",
        "ai.tldr" => "AI is summarizing media",
        "ai.vibe_tags" => "AI is analyzing themes",
        "ai.smart_labels" => "AI is organizing labels",
        "writeback.metadata" => "Writing media metadata",
        _ => operationKind?.ToLowerInvariant() switch
        {
            "ai" => "AI is processing media",
            "enrichment" => "Enriching library metadata",
            "plugin" => "Running a library plugin",
            "writeback" => "Updating media files",
            _ => "Updating your library",
        },
    };

    private static string? FriendlyStage(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return null;
        }

        return string.Concat(stage
            .Replace('_', ' ')
            .Select((character, index) => index > 0 && char.IsUpper(character) && stage[index - 1] != ' '
                ? $" {char.ToLowerInvariant(character)}"
                : character.ToString()))
            .Trim()
            .ToLowerInvariant() switch
        {
            var value when value.Length == 0 => null,
            var value => char.ToUpperInvariant(value[0]) + value[1..],
        };
    }

    private static string FriendlyRole(string role) => string.Join(' ', role
        .Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries)
        .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));

    public void Dispose()
    {
        _disposed = true;
        _universeState.OnStateChanged -= OnUniverseStateChanged;
        _playback.Changed -= OnPlaybackChanged;
        _expiryTimer.Dispose();
    }

    private sealed record VideoPlaybackActivity(
        Guid AssetId,
        string Title,
        double CurrentTimeSeconds,
        double DurationSeconds,
        DateTimeOffset UpdatedAt);
}
