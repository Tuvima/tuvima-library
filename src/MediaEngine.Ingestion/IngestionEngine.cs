using System.Text.Json;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Capabilities;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Detection;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Pipeline;
using MediaEngine.Ingestion.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Processors.Contracts;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion;

/// <summary>
/// Headless <see cref="BackgroundService"/> that orchestrates the full file
/// ingestion pipeline.  Also implements <see cref="IIngestionEngine"/> so that
/// the host can call <see cref="DryRunAsync"/> from test / maintenance code
/// without starting the live watcher.
///
/// ------------------------------------------------------------------
/// Pipeline (per accepted file — spec: Phase 7 – Lifecycle Automation)
/// ------------------------------------------------------------------
///
///  1. Settle/detect and handle terminal file events.
///  2. Hash/dedupe under the per-content-hash lock.
///  3. Process the media file and quarantine corrupt input.
///  4. Score/identify, persist claims, and register the asset.
///  5. Evaluate organization readiness without moving before retail matching.
///  6. Persist embedded artwork and perform only safe in-library write-back.
///  7. Create the durable identity job that begins retail-first enrichment.
///
/// Spec: Phase 7 – Interfaces § IIngestionEngine.
/// </summary>
public sealed partial class IngestionEngine : BackgroundService, IIngestionEngine
{
    // Stable GUID representing the local-file processor as a "provider".
    // Used as ProviderId in MetadataClaim rows so the scoring engine can weight it.
    private static readonly Guid LocalProcessorProviderId = WellKnownProviders.LocalProcessor;

    // Extensions that are never media files and must be skipped by the batch
    // scanner and polling sweep.  These can appear in watch folders as sidecar
    // data (e.g. MANIFEST.json written by the test generator) or alongside
    // media files (e.g. cover art, subtitle tracks).
    private static readonly HashSet<string> NonMediaExtensions =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".xml", ".opf", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp",
        ".txt", ".md", ".nfo", ".srt", ".vtt", ".ass", ".sub", ".idx",
        ".log", ".db", ".db-wal", ".db-shm", ".lnk", ".ini", ".cfg",
    };

    private readonly PlaybackInspectionWriter? _playbackInspection;
    private readonly ICanonicalValueArrayRepository? _arrayRepo;
    private readonly IFileWatcher _watcher;
    private readonly DebounceQueue _debounce;
    private readonly IAssetHasher _hasher;
    private readonly IProcessorRouter _processors;
    private readonly IScoringEngine _scorer;
    private readonly IFileOrganizer _organizer;
    private readonly IEnumerable<IMetadataTagger> _taggers;
    private readonly IMediaAssetRepository _assetRepo;
    private readonly IBackgroundWorker _worker;
    private readonly IEventPublisher _publisher;
    private readonly IngestionOptions _options;
    private readonly ILogger<IngestionEngine> _logger;
    private readonly OnboardingActivationGate? _onboardingGate;

    // Phase 9: claim/canonical persistence + external metadata harvesting.
    private readonly IMetadataClaimRepository _claimRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly IRecursiveIdentityService _identity;

    // Collection ? Work ? Edition scaffold creation.
    private readonly IMediaEntityChainFactory _chainFactory;

    // Review queue — created when confidence is too low or category is "Other".
    private readonly IReviewQueueRepository _reviewRepo;

    // Activity ledger — records every significant ingestion event.
    private readonly ISystemActivityRepository _activityRepo;

    // Reconciliation — cleans orphaned DB records before the initial scan.
    private readonly IReconciliationService _reconciliation;

    // Centralized organization gate — single source of truth for promotion eligibility.
    private readonly IOrganizationGate _gate;

    // Managed artwork lives in the central asset store under {libraryRoot}/.data/assets/.
    // Per-file ingestion lifecycle log — tracks each file from detection to completion.
    private readonly IIngestionLogRepository _ingestionLog;

    // AI-powered filename cleaning and media type classification (Sprint 2).
    private readonly ISmartLabeler _smartLabeler;
    private readonly IMediaTypeAdvisor _typeAdvisor;

    // Pipeline provenance — records lifecycle events for timeline.
    private readonly IEntityTimelineRepository _timelineRepo;

    // Config-driven thresholds — replaces hardcoded 0.85 literals.
    private readonly ScoringConfiguration _scoringConfig;

    // Ingestion batch tracking — creates batch records and emits BatchProgress events.
    private readonly IIngestionBatchRepository _batchRepo;

    // Durable identity pipeline — creates identity_jobs rows for the three-stage
    // retail-first identity pipeline (RetailMatchWorker ? WikidataBridgeWorker ? QuickHydrationWorker).
    private readonly IIdentityJobRepository _identityJobRepo;
    private readonly IMediaOperationTracker? _operationTracker;
    private readonly IMediaOperationRepository? _operationRepository;
    private readonly IMediaTypeResolver _mediaTypeResolver;
    private readonly IDuplicateResolver _duplicateResolver;
    private readonly IIngestionLogScribe _ingestionLogScribe;
    private readonly HashDedupeStageDependencies _hashStageDependencies;
    private readonly ScoreIdentifyStageDependencies _scoreStageDependencies;
    private readonly OrganizeStageDependencies _organizeStageDependencies;
    private readonly WriteBackStageDependencies _writeBackStageDependencies;
    private readonly IdentityJobStageDependencies _identityStageDependencies;
    private readonly ILibraryFolderResolver? _libraryFolderResolver;
    private readonly ISourceMutationPolicyGate? _sourceMutationPolicyGate;
    private readonly IReadOnlyList<IIngestionStage> _ingestionStages;

    // Centralized concurrency guard (Principle 5: formalized lock hierarchy).
    // Replaces inline ConcurrentDictionary<string, SemaphoreSlim> instances.
    // Lock order: folder ? hash (see ConcurrencyGuard doc for full hierarchy).
    private readonly ConcurrencyGuard _concurrencyGuard = new();

    // Collect-then-process: accumulate FSW/poll events, create batch after quiet period.
    private readonly List<FileEvent> _fswBuffer = [];
    private readonly HashSet<string> _activePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PollFingerprint> _queuedFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PollFingerprint> _pollFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _fswBufferLock = new();
    private readonly SemaphoreSlim _watcherRecoveryLock = new(1, 1);
    private readonly object _ownedTasksLock = new();
    private readonly Dictionary<Task, string> _ownedTasks = [];
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _disposeState;
    private CancellationTokenSource? _executeCts;
    private Timer? _fswFlushTimer;
    private readonly record struct PollFingerprint(long Length, DateTime LastWriteUtc);
    private sealed record HashLookupResult(HashResult Hash, bool CacheHit);

    public IngestionEngine(
        IFileWatcher watcher,
        DebounceQueue debounce,
        IAssetHasher hasher,
        IProcessorRouter processors,
        IScoringEngine scorer,
        IFileOrganizer organizer,
        IEnumerable<IMetadataTagger> taggers,
        IMediaAssetRepository assetRepo,
        IBackgroundWorker worker,
        IEventPublisher publisher,
        IOptions<IngestionOptions> options,
        ILogger<IngestionEngine> logger,
        IMetadataClaimRepository claimRepo,
        ICanonicalValueRepository canonicalRepo,
        IRecursiveIdentityService identity,
        IMediaEntityChainFactory chainFactory,
        IReviewQueueRepository reviewRepo,
        ISystemActivityRepository activityRepo,
        IReconciliationService reconciliation,
        IOrganizationGate gate,
        IIngestionLogRepository ingestionLog,
        ISmartLabeler smartLabeler,
        IMediaTypeAdvisor typeAdvisor,
        IEntityTimelineRepository timelineRepo,
        ScoringConfiguration scoringConfig,
        IIngestionBatchRepository batchRepo,
        IIdentityJobRepository identityJobRepo,
        IMediaTypeResolver mediaTypeResolver,
        IDuplicateResolver duplicateResolver,
        IIngestionLogScribe ingestionLogScribe,
        HashDedupeStageDependencies hashStageDependencies,
        ScoreIdentifyStageDependencies scoreStageDependencies,
        OrganizeStageDependencies organizeStageDependencies,
        WriteBackStageDependencies writeBackStageDependencies,
        IdentityJobStageDependencies identityStageDependencies,
        IMediaOperationTracker? operationTracker = null,
        IMediaOperationRepository? operationRepository = null,
        ILibraryFolderResolver? libraryFolderResolver = null,
        ISourceMutationPolicyGate? sourceMutationPolicyGate = null,
        OnboardingActivationGate? onboardingGate = null,
        PlaybackInspectionWriter? playbackInspection = null,
        ICanonicalValueArrayRepository? arrayRepo = null)
    {
        _playbackInspection = playbackInspection;
        _arrayRepo = arrayRepo;
        _watcher = watcher;
        _debounce = debounce;
        _hasher = hasher;
        _processors = processors;
        _scorer = scorer;
        _organizer = organizer;
        _taggers = taggers;
        _assetRepo = assetRepo;
        _worker = worker;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
        _claimRepo = claimRepo;
        _canonicalRepo = canonicalRepo;
        _identity = identity;
        _chainFactory = chainFactory;
        _reviewRepo = reviewRepo;
        _activityRepo = activityRepo;
        _reconciliation = reconciliation;
        _gate = gate;
        _ingestionLog = ingestionLog;
        _smartLabeler = smartLabeler;
        _typeAdvisor = typeAdvisor;
        _timelineRepo = timelineRepo;
        _scoringConfig = scoringConfig;
        _batchRepo = batchRepo;
        _identityJobRepo = identityJobRepo;
        _operationTracker = operationTracker;
        _operationRepository = operationRepository;
        _mediaTypeResolver = mediaTypeResolver;
        _duplicateResolver = duplicateResolver;
        _ingestionLogScribe = ingestionLogScribe;
        _hashStageDependencies = hashStageDependencies;
        _scoreStageDependencies = scoreStageDependencies;
        _organizeStageDependencies = organizeStageDependencies;
        _writeBackStageDependencies = writeBackStageDependencies;
        _identityStageDependencies = identityStageDependencies;
        _libraryFolderResolver = libraryFolderResolver;
        _sourceMutationPolicyGate = sourceMutationPolicyGate;
        _onboardingGate = onboardingGate;
        _ingestionStages =
        [
            new DelegateIngestionStage("settle/detect", RunSettleAndDetectStageAsync),
            new DelegateIngestionStage("hash/dedupe", RunHashAndDedupeStageAsync),
            new DelegateIngestionStage("process", RunProcessStageAsync),
            new DelegateIngestionStage("score/identify", RunScoreAndIdentifyStageAsync),
            new DelegateIngestionStage("organize", RunOrganizeStageAsync),
            new DelegateIngestionStage("write-back", RunWriteBackStageAsync),
            new DelegateIngestionStage("identity-job creation", RunIdentityJobStageAsync),
        ];
    }

    // =========================================================================
    // BackgroundService
    // =========================================================================

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_onboardingGate is not null && !_onboardingGate.IsComplete)
        {
            _logger.LogInformation("Ingestion is waiting for first-run setup to complete");
            await _onboardingGate.WaitAsync(stoppingToken).ConfigureAwait(false);
        }

        _executeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _shutdownCts.Token);
        var lifetimeToken = _executeCts.Token;

        // Always wire the event handler so a schema-3 library configuration refresh
        // works even if no directory was configured at startup.
        _watcher.FileDetected += OnFileDetected;
        _watcher.WatcherError += OnWatcherError;

        // -- Step 1: Log server start (no paths — just the fact) ----------
        _logger.LogInformation("IngestionEngine started");
        await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
        {
            ActionType = Domain.Constants.SystemActionType.ServerStarted,
            EntityType = "Server",
            Detail = "Server started",
        }, stoppingToken).ConfigureAwait(false);

        // -- Step 2: Reconcile BEFORE scanning ----------------------------
        // Clean orphaned DB records so the initial scan sees files as fresh
        // rather than producing false "duplicate skipped" entries.
        try
        {
            _logger.LogInformation("Running startup reconciliation...");
            var result = await _reconciliation.ReconcileAsync(stoppingToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Startup reconciliation complete: {Total} scanned, {Missing} missing",
                result.TotalScanned, result.MissingCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Startup reconciliation failed — continuing with scan");
        }

        // -- Step 3: Start watching + initial scan ------------------------
        var scanTargets = GetConfiguredScanTargets();
        if (scanTargets.Count > 0)
        {
            foreach (var target in scanTargets)
            {
                try
                {
                    _watcher.AddDirectory(target.Path, target.IncludeSubdirectories);
                    _logger.LogInformation(
                        "Watching: {Path} (recursive={Recursive})",
                        target.Path,
                        target.IncludeSubdirectories);
                }
                catch (DirectoryNotFoundException)
                {
                    _logger.LogWarning(
                        "IngestionEngine: Watch directory does not exist: {Path}. " +
                        "Create the directory or update the path in Settings.",
                        target.Path);
                }
            }
        }
        else
        {
            _logger.LogInformation(
                "IngestionEngine: No WatchDirectory configured. " +
                "Set a Watch Folder in Settings to begin file ingestion.");
        }

        // Mark the watcher as "running" so that a later UpdateDirectory() call
        // immediately activates the new watcher.
        // Safe to call with zero directories — Start() is a no-op on an empty list.
        _watcher.Start();

        // Initial scan: FileSystemWatcher only detects NEW filesystem events — files
        // that are already present in the Watch Folder at startup are invisible to it.
        // Synthesise "Created" events for every existing file so the pipeline processes
        // them through the normal debounce ? hash ? duplicate-check ? process flow.
        // After reconciliation, orphaned records are cleaned, so files in the
        // watch folder are treated as genuinely new — no false duplicate skips.
        var startupScanTargets = scanTargets
            .Where(target => Directory.Exists(target.Path))
            .ToList();
        await ScanExistingFilesAsync(startupScanTargets, stoppingToken).ConfigureAwait(false);

        // Start the polling fallback in the background.
        // FileSystemWatcher can miss events on certain configurations — the poller
        // periodically sweeps the Watch Folder and synthesises Created events for
        // files that the debounce queue hasn't seen yet.
        if (_options.PollIntervalSeconds > 0)
        {
            TrackBackgroundTask(PollWatchDirectoryAsync(lifetimeToken), "watch-folder polling");
        }

        // Consume candidates until the service is stopped.
        // If no watcher is active yet, this loop simply waits — new events will
        // flow once the user sets a Watch Folder via the Settings page.
        await foreach (var candidate in _debounce.Reader.ReadAllAsync(stoppingToken)
                           .ConfigureAwait(false))
        {
            // Enqueue each candidate as an independent pipeline task.
            await _worker.EnqueueAsync(
                candidate,
                (c, ct) => ProcessCandidateAsync(c, ct),
                stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdownCts.Cancel();
        _watcher.FileDetected -= OnFileDetected;
        _watcher.WatcherError -= OnWatcherError;
        _watcher.Stop();
        await FlushFswBufferAsync(cancellationToken).ConfigureAwait(false);
        _debounce.Complete();
        await _worker.DrainAsync(cancellationToken).ConfigureAwait(false);

        await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
        {
            ActionType = Domain.Constants.SystemActionType.ServerStopped,
            EntityType = "Server",
            Detail = "Ingestion engine stopped.",
        }, cancellationToken).ConfigureAwait(false);

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await DrainOwnedTasksAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("IngestionEngine stopped.");
    }

    // =========================================================================
    // IIngestionEngine — explicit interface (Start/StopAsync are the public API)
    // =========================================================================

    /// <inheritdoc/>
    void IIngestionEngine.Start()
    {
        // BackgroundService is started by the host; this satisfies the interface
        // for callers that hold an IIngestionEngine reference.
        _watcher.Start();

        // Re-scan the watch directory for files that were already present before
        // the watcher started.  This covers the post-wipe restart scenario where
        // files are seeded into the watch folder and then the engine is restarted.
        var startupScanTargets = GetConfiguredScanTargets()
            .Where(target => Directory.Exists(target.Path))
            .ToList();
        TrackBackgroundTask(
            ScanExistingFilesAsync(startupScanTargets, LifetimeToken),
            "explicit-start initial scan");
    }

    /// <inheritdoc/>
    async Task IIngestionEngine.StopAsync(CancellationToken ct)
        => await StopAsync(ct).ConfigureAwait(false);

    /// <inheritdoc/>
    Task IIngestionEngine.ScanDirectory(string directory, bool includeSubdirectories, CancellationToken ct)
        => ScanExistingFilesAsync([new IngestionScanTarget(directory, includeSubdirectories)], ct);

    /// <inheritdoc/>
    Task IIngestionEngine.ScanDirectories(IReadOnlyList<IngestionScanTarget> targets, CancellationToken ct)
        => ScanExistingFilesAsync(targets, ct);

    private IReadOnlyList<IngestionScanTarget> GetConfiguredScanTargets() =>
        _options.LibraryFolders
            .Where(library => string.Equals(library.Kind, LibraryKinds.Catalogued, StringComparison.OrdinalIgnoreCase))
            .SelectMany(library => library.Sources.Select(source =>
                new IngestionScanTarget(source.Path, source.IncludeSubdirectories)))
            .Where(target => !string.IsNullOrWhiteSpace(target.Path))
            .GroupBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new IngestionScanTarget(
                group.Key,
                group.Any(target => target.IncludeSubdirectories)))
            .ToList();

    /// <inheritdoc/>
    async Task IIngestionEngine.PauseWatcherAsync(CancellationToken ct)
    {
        // Stop the FSW so no new OS events are delivered while the wipe runs.
        _watcher.Stop();

        try
        {
            await FlushFswBufferAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Pending FSW buffer flush failed while pausing watcher - continuing");
        }

        // Clear the dedup sets so that
        // files written after the wipe are not silently dropped as duplicates.
        // The debounce channel and its consumer loop are deliberately left alive.
        lock (_fswBufferLock)
        {
            _fswFlushTimer?.Dispose();
            _fswFlushTimer = null;
            _fswBuffer.Clear();
            _activePaths.Clear();
            _queuedFingerprints.Clear();
            _pollFingerprints.Clear();
        }

        _logger.LogInformation("IngestionEngine: FSW paused (watcher stopped, event buffer cleared).");
    }

    /// <inheritdoc/>
    Task IIngestionEngine.ResumeWatcherAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Clear dedup state so files that were seen before the wipe (and whose
        // paths are now back on disk after re-seeding) can be enqueued again.
        lock (_fswBufferLock)
        {
            _activePaths.Clear();
            _queuedFingerprints.Clear();
            _pollFingerprints.Clear();
        }

        // Restart the FSW — new OS events will flow into BufferFswEvent again.
        _watcher.Start();

        _logger.LogInformation("IngestionEngine: FSW resumed (watcher restarted, dedup state cleared).");
        return Task.CompletedTask;
    }

}
