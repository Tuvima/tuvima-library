using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// What kind of Library work an indicator item describes. Playback is not Library work, so it is not a kind here
/// (see <see cref="ShellActivityState.IsPlaybackActive"/>).
/// </summary>
public enum ShellActivityKind
{
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

/// <summary>
/// The Library's work as the menu indicator sees it: working (blue) when any item is present, idle (green) otherwise.
/// <see cref="Queue"/> is the Engine's latest ingestion snapshot, kept so the tooltip can use its numbers and its last-activity time.
/// </summary>
public sealed record ShellActivitySnapshot(IReadOnlyList<ShellActivityItem> Items, IngestionQueueProgressDto? Queue = null)
{
    public bool IsWorking => Items.Count > 0;
    public int ActiveCount => Items.Count;
    public ShellActivityItem? Primary => Items.FirstOrDefault();
}

public sealed class ShellActivityState : IDisposable
{
    private static readonly TimeSpan VideoActivityTtl = TimeSpan.FromSeconds(15);
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
        _expiryTimer = new Timer(_ => OnTimerTick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public event Action? Changed;

    public ShellActivitySnapshot Current => BuildSnapshot(DateTimeOffset.UtcNow);

    /// <summary>
    /// True while video or listen playback is active. Playback is not Library work, so it never appears in
    /// <see cref="Current"/>; the notification host uses this to stay quiet while something is playing.
    /// </summary>
    public bool IsPlaybackActive => IsVideoPlaybackActive(DateTimeOffset.UtcNow) || IsListenPlaybackActive();

    /// <summary>True when the Engine's ingestion queue reports it is working (waiting and idle are not work).</summary>
    public static bool IsQueueWorking(IngestionQueueProgressDto? queue) =>
        string.Equals(queue?.State, "working", StringComparison.OrdinalIgnoreCase);

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
    }

    public void ClearVideoPlayback(Guid assetId)
    {
        lock (_videoGate)
        {
            if (_videoPlayback?.AssetId == assetId)
            {
                _videoPlayback = null;
            }
        }
    }

    private ShellActivitySnapshot BuildSnapshot(DateTimeOffset now)
    {
        var items = new List<ShellActivityItem>();

        AddIngestion(items);
        var hasUniverseEnrichment = AddUniverseEnrichment(items, now);
        AddModelDownloads(items, now);
        AddMediaOperations(items, hasUniverseEnrichment);

        return new ShellActivitySnapshot(
            items
                .OrderBy(item => Priority(item.Kind))
                .ThenByDescending(item => item.UpdatedAt)
                .ToList(),
            _universeState.IngestionQueueProgress);
    }

    private bool IsVideoPlaybackActive(DateTimeOffset now)
    {
        VideoPlaybackActivity? video;
        lock (_videoGate)
        {
            video = _videoPlayback;
        }

        return video is not null && now - video.UpdatedAt <= VideoActivityTtl;
    }

    private bool IsListenPlaybackActive() => _playback.HasQueue
        && _playback.Phase is PlaybackPhase.Playing or PlaybackPhase.Loading or PlaybackPhase.Ready
        && (_playback.IsPlaying || _playback.Phase == PlaybackPhase.Loading);

    /// <summary>One item while the Engine's ingestion queue is working; waiting and idle add nothing.</summary>
    private void AddIngestion(List<ShellActivityItem> items)
    {
        if (_universeState.IngestionQueueProgress is not { } queue || !IsQueueWorking(queue))
        {
            return;
        }

        items.Add(new ShellActivityItem(
            "ingestion:queue",
            ShellActivityKind.Ingestion,
            "Processing library files",
            null,
            Math.Clamp((int)Math.Round(queue.Progress * 100d), 0, 100),
            queue.UpdatedAt));
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

    private void AddMediaOperations(List<ShellActivityItem> items, bool hasUniverseEnrichment)
    {
        foreach (var operation in _universeState.MediaOperationActivity)
        {
            var kind = ToKind(operation.OperationKind);
            if (kind == ShellActivityKind.Ingestion)
            {
                // The Engine's ingestion queue snapshot is the single source for ingestion work.
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
        if (_universeState.MediaOperationActivity.Count > 0 || IsQueueWorking(_universeState.IngestionQueueProgress))
        {
            _ = RefreshSystemActivityAsync();
        }
    }

    private void PublishIfChanged()
    {
        var snapshot = BuildSnapshot(DateTimeOffset.UtcNow);
        var queue = snapshot.Queue;
        var signature = string.Join('|', snapshot.Items.Select(item => $"{item.Key}:{item.ProgressPercent}:{item.Label}"))
            + $"#{queue?.State}:{queue?.FilesFound}:{queue?.FilesDone}:{queue?.EstimatedSecondsRemaining}:{queue?.LastActivityAt:O}";
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastSignature = signature;
        Changed?.Invoke();
    }

    private static int Priority(ShellActivityKind kind) => kind switch
    {
        ShellActivityKind.Ingestion => 0,
        ShellActivityKind.Ai => 1,
        ShellActivityKind.Enrichment => 2,
        _ => 3,
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
        "view.document_text_read" => "Reading document text",
        "view.document_ocr" => "Reading scanned documents",
        "writeback.metadata" => "Writing media metadata",
        "organise_folder" => "Organising a folder",
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
        _expiryTimer.Dispose();
    }

    private sealed record VideoPlaybackActivity(
        Guid AssetId,
        string Title,
        double CurrentTimeSeconds,
        double DurationSeconds,
        DateTimeOffset UpdatedAt);
}
