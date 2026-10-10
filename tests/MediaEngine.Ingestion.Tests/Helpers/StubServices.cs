using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Processors.Contracts;
using MediaEngine.Processors.Models;
using MediaEngine.Storage.Contracts;
using IdentityJob = MediaEngine.Domain.Entities.IdentityJob;

namespace MediaEngine.Ingestion.Tests.Helpers;

// ── Event Publisher Stub ─────────────────────────────────────────────────────

internal sealed class StubEventPublisher : IEventPublisher
{
    public List<(string EventName, object Payload)> Published { get; } = [];
    public Action<string, object>? BeforePublish { get; set; }

    public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
        where TPayload : notnull
    {
        BeforePublish?.Invoke(eventName, payload);
        Published.Add((eventName, payload));
        return Task.CompletedTask;
    }
}

// ── File Watcher Stub ─────────────────────────────────────────────────────────

internal sealed class StubFileWatcher : IFileWatcher
{
    public event EventHandler<FileEvent>? FileDetected;
    public event EventHandler<FileWatcherErrorEvent>? WatcherError;
    public long EventCount => 0;
    public DateTimeOffset? LastEventAt => null;
    public long ErrorCount => 0;
    public DateTimeOffset? LastErrorAt => null;
    public string? LastErrorKind => null;
    public string? LastErrorMessage => null;
    public bool IsRunning => false;
    public IReadOnlyList<string> WatchedPaths => [];

    public void AddDirectory(string path, bool includeSubdirectories = true) { }
    public void Start() { }
    public void Stop() { }
    public void UpdateDirectory(string path, bool includeSubdirectories = true) { }
    public void Dispose() { }

    // Suppress unused event warning.
    internal void RaiseForTest(FileEvent evt) => FileDetected?.Invoke(this, evt);
    internal void RaiseErrorForTest(FileWatcherErrorEvent evt) => WatcherError?.Invoke(this, evt);
}

// ── Background Worker Stub ────────────────────────────────────────────────────

internal sealed class InlineBackgroundWorker : IBackgroundWorker
{
    public int PendingCount => 0;

    public async ValueTask EnqueueAsync<T>(
        T workItem, Func<T, CancellationToken, Task> handler, CancellationToken ct = default)
    {
        // Execute synchronously for test determinism.
        await handler(workItem, ct);
    }

    public Task DrainAsync(CancellationToken ct = default) => Task.CompletedTask;
}

// ── Identity Job Repository Stub ─────────────────────────────────────────────

internal sealed class StubIdentityJobRepository : IIdentityJobRepository
{
    public List<IdentityJob> CreatedJobs { get; } = [];

    public Task CreateAsync(IdentityJob job, CancellationToken ct = default)
    {
        CreatedJobs.Add(job);
        return Task.CompletedTask;
    }

    public Task<IdentityJob?> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult<IdentityJob?>(null);

    public Task<IdentityJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default)
        => Task.FromResult<IdentityJob?>(null);

    public Task<IReadOnlyList<IdentityJob>> LeaseNextAsync(string workerName, IReadOnlyList<IdentityJobState> states, int batchSize, TimeSpan leaseDuration, IReadOnlyList<string>? excludeRunIds = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IdentityJob>>([]);

    public Task UpdateStateAsync(Guid jobId, IdentityJobState newState, string? error = null, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetSelectedCandidateAsync(Guid jobId, Guid candidateId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetResolvedQidAsync(Guid jobId, string qid, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<IdentityJob>> GetStaleAsync(TimeSpan age, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IdentityJob>>([]);

    public Task<IReadOnlyList<IdentityJob>> GetByStateAsync(IdentityJobState state, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IdentityJob>>([]);

    public Task<IReadOnlyDictionary<string, int>> GetStateCountsByRunAsync(Guid ingestionRunId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

    public Task<IReadOnlyDictionary<string, int>> GetPendingStage1CountsByRunAsync(
        IReadOnlyList<string> ingestionRunIds, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

    public Task<int> ReclaimStuckJobsAsync(
        IdentityJobState processingState,
        TimeSpan stuckThreshold,
        CancellationToken ct = default)
        => Task.FromResult(0);

    public Task ReleaseLeaseAsync(Guid jobId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<int> CountActiveAsync(CancellationToken ct = default)
        => Task.FromResult(0);
}

// ── Recursive Identity Stub ───────────────────────────────────────────────────

internal sealed class StubRecursiveIdentity : IRecursiveIdentityService
{
    public List<(Guid AssetId, IReadOnlyList<PersonReference> Persons)> Calls { get; } = [];

    public Task<IReadOnlyList<HarvestRequest>> EnrichAsync(Guid mediaAssetId, IReadOnlyList<PersonReference> persons, CancellationToken ct = default)
    {
        Calls.Add((mediaAssetId, persons));
        return Task.FromResult<IReadOnlyList<HarvestRequest>>([]);
    }
}

// ── Hero Banner Generator Stub ────────────────────────────────────────────────

internal sealed class StubHeroBannerGenerator : IHeroBannerGenerator
{
    public Task<HeroBannerResult> GenerateAsync(string coverImagePath, string outputDirectory, CancellationToken ct = default)
        => Task.FromResult(new HeroBannerResult("hero.jpg", "#000000", false));
}

// ── Reconciliation Stub ───────────────────────────────────────────────────────

internal sealed class StubReconciliation : IReconciliationService
{
    public Task<ReconciliationSummary> ReconcileAsync(CancellationToken ct = default)
        => Task.FromResult(new ReconciliationSummary(0, 0, 0));
}

// ── File Organizer Stub ───────────────────────────────────────────────────────

internal sealed class StubFileOrganizer : IFileOrganizer
{
    public string CalculatePath(IngestionCandidate candidate, string template)
    {
        var metadata = candidate.Metadata;
        var category = candidate.DetectedMediaType switch
        {
            MediaType.Books => "Books",
            MediaType.Audiobooks => "Audio",
            MediaType.Movies => "Movies",
            MediaType.Comics => "Comics",
            _ => "Other",
        };
        var title = metadata?.GetValueOrDefault("title") ?? "Unknown";
        var ext = Path.GetExtension(candidate.Path);
        return $"{category}/{title}{ext}";
    }

    public string? ValidateTemplate(string template, out string? error)
    {
        error = null;
        return template;
    }

    public Task<bool> ExecuteMoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(sourcePath))
        {
            File.Move(sourcePath, destinationPath, overwrite: true);
        }

        return Task.FromResult(true);
    }
}

// ── Asset Hasher (test-friendly) ──────────────────────────────────────────────

internal sealed class TestAssetHasher : IAssetHasher
{
    public Task<HashResult> ComputeAsync(string filePath, CancellationToken ct = default)
    {
        using var stream = File.OpenRead(filePath);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        var hex = Convert.ToHexStringLower(hash);
        return Task.FromResult(new HashResult
        {
            FilePath = filePath,
            Hex = hex,
            FileSize = new FileInfo(filePath).Length,
            Elapsed = TimeSpan.FromMilliseconds(1),
        });
    }
}

// ── Processor LibraryItem (configurable) ─────────────────────────────────────────

internal sealed class TestProcessorLibraryItem : IProcessorRouter
{
    private ProcessorResult? _nextResult;
    private readonly Queue<ProcessorResult> _resultQueue = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProcessorResult> _pathResults =
        new(StringComparer.OrdinalIgnoreCase);

    public void SetNextResult(ProcessorResult result) => _nextResult = result;

    /// <summary>
    /// Returns <paramref name="result"/> whenever <paramref name="filePath"/> is processed, regardless of
    /// call order, so concurrent pipelines each see the result intended for their own file.
    /// </summary>
    public void SetResultForPath(string filePath, ProcessorResult result) => _pathResults[filePath] = result;

    /// <summary>
    /// Enqueues a result to be returned by <see cref="ProcessAsync"/> in FIFO order.
    /// Queue is checked before <see cref="_nextResult"/> and the default fallback.
    /// </summary>
    public void QueueResult(ProcessorResult result) => _resultQueue.Enqueue(result);

    public void Register(IMediaProcessor processor) { }
    public IMediaProcessor? Resolve(string filePath) => null;

    public Task<ProcessorResult> ProcessAsync(string filePath, CancellationToken ct = default)
    {
        if (_pathResults.TryGetValue(filePath, out var forPath))
        {
            return Task.FromResult(forPath);
        }

        // Priority 1: dequeue from FIFO queue.
        if (_resultQueue.TryDequeue(out var queued))
        {
            return Task.FromResult(queued);
        }

        // Priority 2: single-shot next result.
        if (_nextResult is not null)
        {
            var result = _nextResult;
            _nextResult = null;
            return Task.FromResult(result);
        }

        // Default: return minimal valid result for an EPUB-like file.
        return Task.FromResult(new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Books,
            Claims =
            [
                new ExtractedClaim { Key = "title", Value = Path.GetFileNameWithoutExtension(filePath), Confidence = 0.5 },
            ],
        });
    }
}

// ── Configuration Loader Stub ───────────────────────────────────────────────

internal sealed class StubConfigurationLoader : IConfigurationLoader
{
    public string ConfigDirectoryPath { get; set; } = string.Empty;
    public CoreConfiguration Core { get; set; } = new();
    public LibrariesConfiguration Libraries { get; set; } = new();
    public WritebackFieldsConfiguration? WritebackFields { get; set; }

    public CoreConfiguration LoadCore() => Core;
    public void SaveCore(CoreConfiguration config) { }
    public ScoringSettings LoadScoring() => new();
    public void SaveScoring(ScoringSettings settings) { }
    public MaintenanceSettings LoadMaintenance() => new();
    public void SaveMaintenance(MaintenanceSettings settings) { }
    public HydrationSettings LoadHydration() => new();
    public void SaveHydration(HydrationSettings settings) { }
    public PipelineConfiguration LoadPipelines() => new();
    public void SavePipelines(PipelineConfiguration config) { }
    public DisambiguationSettings LoadDisambiguation() => new();
    public void SaveDisambiguation(DisambiguationSettings settings) { }
    public MediaTypeConfiguration LoadMediaTypes() => new();
    public void SaveMediaTypes(MediaTypeConfiguration config) { }
    public TranscodingSettings LoadTranscoding() => new();
    public void SaveTranscoding(TranscodingSettings settings) { }
    public FieldPriorityConfiguration LoadFieldPriorities() => new();
    public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
    public LibrariesConfiguration LoadLibraries() => Libraries;
    public ProviderConfiguration? LoadProvider(string name) => null;
    public void SaveProvider(ProviderConfiguration config) { }
    public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
    public T? LoadConfig<T>(string subdirectory, string name) where T : class =>
        name.Equals("writeback-fields", StringComparison.OrdinalIgnoreCase)
            ? WritebackFields as T
            : null;
    public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
    public T? LoadAi<T>() where T : class => default;
    public void SaveAi<T>(T settings) where T : class { }
    public PaletteConfiguration LoadPalette() => new();
    public void SavePalette(PaletteConfiguration palette) { }
}

// ── SmartLabeler Stub ─────────────────────────────────────────────────────────

/// <summary>
/// No-op SmartLabeler for tests — returns the input unchanged so existing
/// test expectations about processor-derived titles are unaffected.
/// </summary>
internal sealed class StubSmartLabeler : ISmartLabeler
{
    public Task<CleanedSearchQuery> CleanAsync(string rawFilename, CancellationToken ct = default)
        => Task.FromResult(new CleanedSearchQuery
        {
            Title = rawFilename,
            Confidence = 0.0, // Below the 0.5 threshold — ingestion will ignore this result.
        });
}

// ── MediaTypeAdvisor Stub ─────────────────────────────────────────────────────

/// <summary>
/// No-op MediaTypeAdvisor for tests — returns Unknown so the existing
/// processor-based media type logic is unaffected.
/// </summary>
internal sealed class StubMediaTypeAdvisor : IMediaTypeAdvisor
{
    public Task<MediaTypeCandidate> ClassifyAsync(
        string filename,
        string? container,
        double? durationSeconds,
        int? bitrate,
        string? genre,
        bool hasChapters,
        string? folderPath,
        CancellationToken ct = default)
        => Task.FromResult(new MediaTypeCandidate
        {
            Type = MediaType.Unknown,
            Confidence = 0.0,
            Reason = "Stub — no AI classification in tests",
        });
}

// ── IngestionBatchRepository Stub ────────────────────────────────────────────

/// <summary>No-op ingestion batch repository for tests — all writes are silently discarded.</summary>
internal sealed class StubIngestionBatchRepository : IIngestionBatchRepository
{
    public Task CreateAsync(MediaEngine.Domain.Entities.IngestionBatch batch, CancellationToken ct = default) => Task.CompletedTask;
    public Task UpdateCountsAsync(Guid id, int filesTotal, int filesProcessed, int filesIdentified, int filesReview, int filesNoMatch, int filesFailed, CancellationToken ct = default) => Task.CompletedTask;
    public Task CompleteAsync(Guid id, string status, CancellationToken ct = default) => Task.CompletedTask;
    public Task IncrementCounterAsync(Guid id, MediaEngine.Domain.Enums.BatchCounterColumn column, CancellationToken ct = default) => Task.CompletedTask;
    public Task<MediaEngine.Domain.Entities.IngestionBatch?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<MediaEngine.Domain.Entities.IngestionBatch?>(null);
    public Task<IReadOnlyList<MediaEngine.Domain.Entities.IngestionBatch>> GetRecentAsync(int limit = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MediaEngine.Domain.Entities.IngestionBatch>>([]);
    public Task<int> GetNeedsAttentionCountAsync(CancellationToken ct = default) => Task.FromResult(0);
    public Task<MediaEngine.Domain.Models.IngestionBatchProgressSnapshot> GetProgressSnapshotAsync(
        Guid batchId,
        CancellationToken ct = default)
        => Task.FromResult(new MediaEngine.Domain.Models.IngestionBatchProgressSnapshot());
}

// ── EntityTimelineRepository Stub ────────────────────────────────────────────

/// <summary>
/// No-op timeline repository for tests — all writes are silently discarded.
/// </summary>
internal sealed class StubEntityTimelineRepository : IEntityTimelineRepository
{
    public Task InsertEventAsync(Domain.Entities.EntityEvent evt, CancellationToken ct = default) => Task.CompletedTask;
    public Task InsertEventsAsync(IReadOnlyList<Domain.Entities.EntityEvent> events, CancellationToken ct = default) => Task.CompletedTask;
    public Task InsertFieldChangesAsync(IReadOnlyList<Domain.Entities.EntityFieldChange> changes, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<Domain.Entities.EntityEvent>> GetEventsByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Domain.Entities.EntityEvent>>([]);
    public Task<Domain.Entities.EntityEvent?> GetLatestEventAsync(Guid entityId, int stage, CancellationToken ct = default) => Task.FromResult<Domain.Entities.EntityEvent?>(null);
    public Task<IReadOnlyList<Domain.Entities.EntityEvent>> GetCurrentPipelineStateAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Domain.Entities.EntityEvent>>([]);
    public Task<Domain.Entities.EntityEvent?> GetEventByIdAsync(Guid eventId, CancellationToken ct = default) => Task.FromResult<Domain.Entities.EntityEvent?>(null);
    public Task<IReadOnlyList<Domain.Entities.EntityFieldChange>> GetFieldChangesByEventAsync(Guid eventId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Domain.Entities.EntityFieldChange>>([]);
    public Task<IReadOnlyList<Domain.Entities.EntityFieldChange>> GetFieldHistoryAsync(Guid entityId, string field, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Domain.Entities.EntityFieldChange>>([]);
    public Task<IReadOnlyList<Domain.Entities.EntityFieldChange>> GetFieldHistoryAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Domain.Entities.EntityFieldChange>>([]);
    public Task<IReadOnlyList<Domain.Entities.EntityFieldChange>> GetFileOriginalsForEventAsync(Guid eventId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Domain.Entities.EntityFieldChange>>([]);
    public Task<IReadOnlyDictionary<Guid, Domain.Entities.EntityEvent>> GetLatestStage2EventsAsync(IReadOnlyList<Guid> entityIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<Guid, Domain.Entities.EntityEvent>>(new Dictionary<Guid, Domain.Entities.EntityEvent>());
    public Task<int> CullOldEventsAsync(TimeSpan retention, CancellationToken ct = default) => Task.FromResult(0);
    public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.CompletedTask;
}

