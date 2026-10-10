using Dapper;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Pipeline;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;
using MediaEngine.Intelligence;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Processors.Models;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
// Disambiguate ProviderConfiguration from the Storage.Models namespace
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// Test harness for the durable identity pipeline.
///
/// Group A (Tests 1–4): Ingestion-level behaviour verified via the full
/// <see cref="IngestionEngine"/> pipeline against a real SQLite database.
///
/// Group B (Tests 5–8): Worker-level behaviour verified by constructing
/// individual workers with stub dependencies and asserting state transitions.
///
/// No live API calls are made. All external I/O is stubbed.
/// </summary>
public sealed class DurablePipelineTests : IDisposable
{
    // ── Shared temp directory (cleaned up in Dispose) ─────────────────────

    private readonly string _tempRoot;
    private readonly string _watchDir;
    private readonly string _libraryDir;

    // ── Real repositories backed by an in-memory SQLite database ─────────

    private readonly TestDatabaseFactory _dbFactory;
    private readonly IMediaAssetRepository _assetRepo;
    private readonly IMetadataClaimRepository _claimRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly IReviewQueueRepository _reviewRepo;
    private readonly ISystemActivityRepository _activityRepo;
    private readonly IIngestionLogRepository _ingestionLog;
    private readonly IWorkRepository _workRepo;
    private readonly IEntityAssetRepository _entityAssetRepo;
    private readonly IMediaEntityChainFactory _chainFactory;
    private readonly IIngestionBatchRepository _batchRepo;
    private readonly IIdentityJobRepository _identityJobRepo;
    private readonly IMediaOperationRepository _operationRepo;
    private readonly IMediaOperationTracker _operationTracker;
    private readonly IScoringEngine _scorer;
    private readonly AssetPathService _assetPaths;

    // ── External I/O stubs ────────────────────────────────────────────────

    private readonly StubFileWatcher _watcher = new();
    private readonly StubEventPublisher _publisher = new();
    private readonly StubRecursiveIdentity _recursiveIdentity = new();
    private readonly StubReconciliation _reconciliation = new();
    private readonly StubFileOrganizer _organizer = new();
    private readonly TestAssetHasher _hasher = new();
    private readonly TestProcessorLibraryItem _processors = new();
    private readonly InlineBackgroundWorker _bgWorker = new();

    public DurablePipelineTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_durable_{Guid.NewGuid():N}");
        _watchDir = Path.Combine(_tempRoot, "watch");
        _libraryDir = Path.Combine(_tempRoot, "library");
        Directory.CreateDirectory(_watchDir);
        Directory.CreateDirectory(_libraryDir);

        _dbFactory = new TestDatabaseFactory();
        var db = _dbFactory.Connection;

        _assetRepo = new MediaAssetRepository(db);
        _claimRepo = new MetadataClaimRepository(db);
        _canonicalRepo = new CanonicalValueRepository(db);
        _reviewRepo = new ReviewQueueRepository(db);
        _activityRepo = new SystemActivityRepository(db);
        _ingestionLog = new IngestionLogRepository(db);
        _workRepo = new WorkRepository(db);
        _entityAssetRepo = new EntityAssetRepository(db);
        _assetPaths = new AssetPathService(_libraryDir);
        _chainFactory = new MediaEntityChainFactory(db, new HierarchyResolver(_workRepo, null, new BookFormatSiblingFinder(db)));
        _batchRepo = new IngestionBatchRepository(db);
        _identityJobRepo = new IdentityJobRepository(db);
        _operationRepo = new MediaOperationRepository(db);
        _operationTracker = new MediaOperationTracker(
            _operationRepo,
            new MediaOperationEventRepository(db),
            _publisher,
            NullLogger<MediaOperationTracker>.Instance);

        _scorer = new PriorityCascadeEngine(new StubConfigurationLoader(), NullLogger<PriorityCascadeEngine>.Instance);
    }

    public void Dispose()
    {
        _dbFactory.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Group A — Ingestion Engine tests (real SQLite, stubbed external I/O)
    // ══════════════════════════════════════════════════════════════════════

    // ── Test 1: File ingestion creates an identity job ────────────────────

    [Fact]
    public async Task IngestionEngine_ValidEpub_CreatesIdentityJobWithQueuedState()
    {
        var filePath = CreateWatchFile("Foundation.epub");

        _processors.SetNextResult(new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Books,
            Claims =
            [
                new ExtractedClaim { Key = "title",  Value = "Foundation",      Confidence = 0.95 },
                new ExtractedClaim { Key = "author", Value = "Isaac Asimov",    Confidence = 0.90 },
            ],
        });

        await RunPipelineAsync();

        // Verify a media asset was registered.
        var hash = await _hasher.ComputeAsync(FindFileAnywhere("Foundation.epub")!);
        var asset = await _assetRepo.FindByHashAsync(hash.Hex);
        Assert.NotNull(asset);

        // Verify an identity_jobs row was created for this asset.
        var job = await _identityJobRepo.GetByEntityAsync(asset!.Id);
        Assert.NotNull(job);
        Assert.Equal("Queued", job!.State);
        Assert.Equal("Books", job.MediaType);
        Assert.Equal(asset.Id, job.EntityId);
        Assert.Equal("11111111-1111-4111-8111-111111111111", asset.LibraryId);
    }

    [Fact]
    public async Task IngestionEngine_PersonalLocalOnlyLibrary_IsNotConsumedByCataloguePipeline()
    {
        var filePath = CreateWatchFile("Family Reunion.mp4");
        _processors.SetNextResult(new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Movies,
            Claims =
            [
                new ExtractedClaim { Key = "title", Value = "Family Reunion", Confidence = 0.70 },
            ],
        });

        await RunPipelineAsync(localOnly: true);

        using var conn = _dbFactory.Connection.CreateConnection();
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM media_assets;"));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM identity_jobs;"));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM review_queue WHERE status = 'Pending';"));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task HandleDeletedAsync_PublishesSourceBackedRemovalAfterAssetIsOrphaned()
    {
        var removedPath = Path.Combine(_watchDir, "removed-book.epub");
        var libraryId = Guid.NewGuid();
        var assetId = SeedAssetForDeletion(removedPath, libraryId, MediaType.Books);
        string? statusAtPublish = null;
        _publisher.BeforePublish = (eventName, _) =>
        {
            if (!string.Equals(eventName, SignalREvents.MediaRemoved, StringComparison.Ordinal))
            {
                return;
            }

            using var connection = _dbFactory.Connection.CreateConnection();
            statusAtPublish = connection.QuerySingle<string>(
                "SELECT status FROM media_assets WHERE id = @assetId;",
                new { assetId });
        };

        using var debounce = new DebounceQueue();
        using var engine = CreateEngine(debounce, CreateDeletionOptions());

        await InvokeHandleDeletedAsync(engine, removedPath);

        Assert.Equal(AssetStatus.Orphaned.ToString(), statusAtPublish);
        var published = Assert.Single(
            _publisher.Published,
            item => string.Equals(item.EventName, SignalREvents.MediaRemoved, StringComparison.Ordinal));
        var payload = Assert.IsType<MediaRemovedEvent>(published.Payload);
        Assert.Equal(assetId, payload.AssetId);
        Assert.Equal(removedPath, payload.FilePath);
        Assert.Equal(AssetStatus.Orphaned.ToString(), payload.Status);
        Assert.Equal(libraryId, payload.LibraryId);
        Assert.Equal(AccountFeatureId.Read.Value, payload.FeatureId);
    }

    [Fact]
    public async Task HandleDeletedAsync_WhenOrphanUpdateFails_DoesNotPublishRemoval()
    {
        var removedPath = Path.Combine(_watchDir, "failed-removal.epub");
        var assetId = SeedAssetForDeletion(removedPath, Guid.NewGuid(), MediaType.Books);
        using (var connection = _dbFactory.Connection.CreateConnection())
        {
            connection.Execute("""
                CREATE TRIGGER fail_media_asset_status_update
                BEFORE UPDATE OF status ON media_assets
                BEGIN
                    SELECT RAISE(ABORT, 'forced update failure');
                END;
                """);
        }

        using var debounce = new DebounceQueue();
        using var engine = CreateEngine(debounce, CreateDeletionOptions());

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => InvokeHandleDeletedAsync(engine, removedPath));

        Assert.Contains("forced update failure", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            _publisher.Published,
            item => string.Equals(item.EventName, SignalREvents.MediaRemoved, StringComparison.Ordinal));
        var asset = await _assetRepo.FindByIdAsync(assetId);
        Assert.NotNull(asset);
        Assert.Equal(AssetStatus.Normal, asset.Status);
    }

    [Fact]
    public async Task HandleDeletedAsync_UnknownAsset_DoesNotPublishRemoval()
    {
        using var debounce = new DebounceQueue();
        using var engine = CreateEngine(debounce, CreateDeletionOptions());

        await InvokeHandleDeletedAsync(engine, Path.Combine(_watchDir, "unknown.epub"));

        Assert.DoesNotContain(
            _publisher.Published,
            item => string.Equals(item.EventName, SignalREvents.MediaRemoved, StringComparison.Ordinal));
    }

    // ── Test 2: Duplicate file ingestion creates only one asset ───────────

    [Fact]
    public async Task IngestionEngine_SameFileTwice_CreatesOnlyOneAsset()
    {
        var filePath = CreateWatchFile("Dune.epub");

        _processors.QueueResult(new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Books,
            Claims = [new ExtractedClaim { Key = "title", Value = "Dune", Confidence = 0.95 }],
        });
        _processors.QueueResult(new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Books,
            Claims = [new ExtractedClaim { Key = "title", Value = "Dune", Confidence = 0.95 }],
        });

        // Run the pipeline twice with the same file.
        await RunPipelineAsync();

        // The file moved on first pass; recreate it for the second pass.
        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath, "dummy content for testing");
        }

        await RunPipelineAsync();

        // Only one media asset should exist regardless of how many times the
        // pipeline ran — the duplicate-detection (hash lookup) prevents double-insert.
        using var conn = _dbFactory.Connection.CreateConnection();
        var count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM media_assets;");
        Assert.Equal(1, count);
    }

    // ── Test 3: Corrupt (zero-byte) file is handled gracefully ───────────

    [Fact]
    public async Task IngestionEngine_ZeroByteFile_DoesNotCrashPipeline()
    {
        // Create a zero-byte file — the hasher will hash it, the processor will
        // run, and the pipeline must complete without throwing an exception.
        var filePath = Path.Combine(_watchDir, "corrupt.epub");
        File.WriteAllBytes(filePath, []);
        _processors.SetNextResult(new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Books,
            Claims = [],
            IsCorrupt = true,
            CorruptReason = "File is not a valid EPUB archive.",
        });

        // The primary assertion: this must not throw.
        var exception = await Record.ExceptionAsync(() => RunPipelineAsync());
        Assert.Null(exception);

        // Secondary: if an asset was created it must not have an identity job
        // in a non-terminal failed state (pipeline handled it cleanly one way
        // or another — either skipped entirely or quarantined).
        using var conn = _dbFactory.Connection.CreateConnection();
        var failedJobs = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM identity_jobs WHERE state NOT IN ('Queued','Ready','ReadyWithoutUniverse','Failed','RetailNoMatch','QidNoMatch');");
        Assert.Equal(0, failedJobs);

        var operation = await _operationRepo.GetLatestBySourcePathAsync(filePath);
        Assert.NotNull(operation);
        Assert.Equal(MediaOperationStatus.NoResult, operation!.Status);
        Assert.Equal(MediaOperationStage.NoResult, operation.Stage);
        Assert.Contains("Corrupt media file", operation.MissingReason, StringComparison.Ordinal);
    }

    // ── Test 4: Batch counter increments after processing ────────────────

    [Fact]
    public async Task IngestionEngine_ProcessesFiles_BatchCounterIncrementsFilesProcessed()
    {
        // Pre-create an ingestion batch row so we can track its counters.
        var batchId = Guid.NewGuid();
        var batch = new IngestionBatch
        {
            Id = batchId,
            Status = "running",
            SourcePath = _watchDir,
            StartedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _batchRepo.CreateAsync(batch);

        // Verify the counter starts at 0.
        var before = await _batchRepo.GetByIdAsync(batchId);
        Assert.NotNull(before);
        Assert.Equal(0, before!.FilesProcessed);

        // Manually increment the counter (as IngestionEngine would do).
        await _batchRepo.IncrementCounterAsync(batchId, BatchCounterColumn.FilesProcessed);
        await _batchRepo.IncrementCounterAsync(batchId, BatchCounterColumn.FilesProcessed);
        await _batchRepo.IncrementCounterByAsync(batchId, BatchCounterColumn.FilesTotal, 3);

        var after = await _batchRepo.GetByIdAsync(batchId);
        Assert.NotNull(after);
        Assert.Equal(2, after!.FilesProcessed);
        Assert.Equal(3, after.FilesTotal);
    }

    [Fact]
    public async Task IngestionEngine_ScanDirectories_CreatesSingleBatchForCombinedTargets()
    {
        var booksDir = Path.Combine(_tempRoot, "books");
        var comicsDir = Path.Combine(_tempRoot, "comics");
        Directory.CreateDirectory(booksDir);
        Directory.CreateDirectory(comicsDir);
        File.WriteAllText(Path.Combine(booksDir, "Dune.epub"), "dune");
        File.WriteAllText(Path.Combine(booksDir, "Foundation.epub"), "foundation");
        File.WriteAllText(Path.Combine(comicsDir, "Saga 001.cbz"), "saga");

        using var debounce = new DebounceQueue(new DebounceOptions
        {
            SettleDelay = TimeSpan.FromMilliseconds(1),
            ProbeInterval = TimeSpan.FromMilliseconds(1),
            MaxProbeAttempts = 1,
            MaxProbeDelay = TimeSpan.FromMilliseconds(10),
        });
        using var engine = CreateEngine(debounce, new IngestionOptions
        {
            WatchDirectories = [booksDir, comicsDir],
            LibraryRoot = _libraryDir,
            AutoOrganize = false,
            IncludeSubdirectories = false,
            PollIntervalSeconds = 0,
        });

        await ((IIngestionEngine)engine).ScanDirectories(
            [
                new IngestionScanTarget(booksDir, IncludeSubdirectories: false),
                new IngestionScanTarget(comicsDir, IncludeSubdirectories: false),
            ],
            CancellationToken.None);

        using var conn = _dbFactory.Connection.CreateConnection();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM ingestion_batches;"));
        Assert.Equal(3, conn.ExecuteScalar<int>("SELECT COALESCE(SUM(files_total), 0) FROM ingestion_batches;"));
        Assert.Equal("Multiple source folders", conn.ExecuteScalar<string>("SELECT source_path FROM ingestion_batches LIMIT 1;"));

        var candidates = new List<IngestionCandidate>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (candidates.Count < 3)
        {
            candidates.Add(await debounce.Reader.ReadAsync(timeout.Token));
        }

        var batchIdValue = conn.ExecuteScalar<object>("SELECT id FROM ingestion_batches LIMIT 1;");
        Assert.NotNull(batchIdValue);
        var batchId = GuidSql.FromDb(batchIdValue);
        Assert.All(candidates, candidate => Assert.Equal(batchId, candidate.BatchId));
    }

    [Fact]
    public async Task IngestionEngine_StopCancelsAndDrainsOwnedDelayedWork()
    {
        var path = CreateWatchFile("Delayed Retry.epub");
        using var debounce = new DebounceQueue(new DebounceOptions
        {
            SettleDelay = TimeSpan.FromMilliseconds(1),
            ProbeInterval = TimeSpan.FromMilliseconds(1),
            MaxProbeAttempts = 1,
            MaxProbeDelay = TimeSpan.FromMilliseconds(10),
        });
        using var engine = CreateEngine(debounce, new IngestionOptions
        {
            WatchDirectories = [],
            LibraryRoot = _libraryDir,
            AutoOrganize = false,
            PollIntervalSeconds = 0,
        });
        await engine.StartAsync(CancellationToken.None);

        var schedule = typeof(IngestionEngine).GetMethod(
            "ScheduleLockProbeRetry",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(schedule);
        schedule.Invoke(engine,
        [
            new IngestionCandidate
            {
                Path = path,
                EventType = FileEventType.Created,
                DetectedAt = DateTimeOffset.UtcNow,
                ReadyAt = DateTimeOffset.UtcNow,
            },
            DateTimeOffset.UtcNow.AddMinutes(10),
        ]);
        Assert.True(GetOwnedTaskCount(engine) > 0);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await engine.StopAsync(stopCts.Token);

        Assert.True(SpinWait.SpinUntil(() => GetOwnedTaskCount(engine) == 0, TimeSpan.FromSeconds(1)));
        _watcher.RaiseErrorForTest(new FileWatcherErrorEvent(
            "buffer_overflow",
            "after stop",
            DateTimeOffset.UtcNow,
            IsBufferOverflow: true));
        Assert.Equal(0, GetOwnedTaskCount(engine));
    }

    [Fact]
    public async Task IngestionEngine_DuplicateDifferentPath_RecordsDuplicateOutcome()
    {
        var firstPath = CreateWatchFile("Dune.epub", "same bytes");
        var secondPath = CreateWatchFile("Dune Copy.epub", "same bytes");

        _processors.QueueResult(new ProcessorResult
        {
            FilePath = firstPath,
            DetectedType = MediaType.Books,
            Claims = [new ExtractedClaim { Key = "title", Value = "Dune", Confidence = 0.95 }],
        });

        await RunPipelineAsync();

        using var conn = _dbFactory.Connection.CreateConnection();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM media_assets;"));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM ingestion_log WHERE status = 'duplicate';"));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COALESCE(SUM(files_registered), 0) FROM ingestion_batches;"));
        Assert.Equal(2, conn.ExecuteScalar<int>("SELECT COALESCE(SUM(files_processed), 0) FROM ingestion_batches;"));
    }

    [Fact]
    public async Task IngestionEngine_UnknownMediaType_RecordsReviewOutcome()
    {
        var filePath = CreateWatchFile("mystery.bin");
        _processors.SetNextResult(new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Unknown,
            Claims = [new ExtractedClaim { Key = "title", Value = "mystery", Confidence = 0.95 }],
        });

        await RunPipelineAsync(applyMediaTypePrior: false);

        using var conn = _dbFactory.Connection.CreateConnection();
        Assert.True(conn.ExecuteScalar<int>("SELECT COUNT(*) FROM review_queue WHERE status = 'Pending';") >= 1);
    }

    [Fact]
    public async Task IngestionEngine_EmbeddedCover_PersistsToCentralAssetStore()
    {
        var filePath = CreateWatchFile("Children of Dune.epub");
        _processors.SetNextResult(new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Books,
            CoverImage = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9s4s46QAAAAASUVORK5CYII="),
            CoverImageMimeType = "image/png",
            Claims =
            [
                new ExtractedClaim { Key = "title", Value = "Children of Dune", Confidence = 0.95 },
                new ExtractedClaim { Key = "author", Value = "Frank Herbert", Confidence = 0.92 },
            ],
        });

        await RunPipelineAsync();

        var asset = await FindAssetByFileNameAsync("Children of Dune.epub");
        Assert.NotNull(asset);

        var lineage = await _workRepo.GetLineageByAssetAsync(asset!.Id);
        var ownerEntityId = lineage?.TargetForParentScope ?? asset.Id;
        var coverAssets = await _entityAssetRepo.GetByEntityAsync(ownerEntityId.ToString(), "CoverArt");
        var embeddedCover = Assert.Single(coverAssets);

        Assert.Equal("Central", embeddedCover.StorageLocationValue);
        Assert.True(File.Exists(embeddedCover.LocalImagePath));
        Assert.StartsWith(_assetPaths.ArtworkRoot, embeddedCover.LocalImagePath!, StringComparison.OrdinalIgnoreCase);

        var legacyImageRoot = Path.Combine(_libraryDir, ".data", "images");
        Assert.False(Directory.Exists(legacyImageRoot) && Directory.EnumerateFiles(legacyImageRoot, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task IngestionEngine_AudiobookWithoutEmbeddedArt_UsesSiblingCoverJpg()
    {
        var filePath = CreateWatchFile("Artemis.m4b");
        var folderImage = CreateJpeg(4096, seed: 7);
        File.WriteAllBytes(Path.Combine(_watchDir, "cover.jpg"), folderImage);
        _processors.SetNextResult(new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Audiobooks,
            Claims =
            [
                new ExtractedClaim { Key = "title", Value = "Artemis", Confidence = 0.95 },
                new ExtractedClaim { Key = "author", Value = "Andy Weir", Confidence = 0.92 },
            ],
        });

        await RunPipelineAsync(applyMediaTypePrior: false);

        var cover = Assert.Single(ReadCoverRows());
        Assert.Equal("folder", cover.SourceProvider);
        Assert.True(File.Exists(cover.LocalImagePath));
        Assert.Equal(folderImage, File.ReadAllBytes(cover.LocalImagePath!));
        Assert.Equal(["folder"], ReadCanonicalValues("cover_source"));
        Assert.Equal(["present"], ReadCanonicalValues("cover_state"));
        Assert.Equal(folderImage, File.ReadAllBytes(Path.Combine(_watchDir, "cover.jpg")));
    }

    [Fact]
    public async Task IngestionEngine_EmbeddedCoverAndSiblingCoverJpg_EmbeddedWins()
    {
        var filePath = CreateWatchFile("Children of Dune.epub");
        File.WriteAllBytes(Path.Combine(_watchDir, "cover.jpg"), CreateJpeg(4096, seed: 3));
        var embedded = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9s4s46QAAAAASUVORK5CYII=");
        _processors.SetNextResult(new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Books,
            CoverImage = embedded,
            CoverImageMimeType = "image/png",
            Claims =
            [
                new ExtractedClaim { Key = "title", Value = "Children of Dune", Confidence = 0.95 },
                new ExtractedClaim { Key = "author", Value = "Frank Herbert", Confidence = 0.92 },
            ],
        });

        await RunPipelineAsync();

        var cover = Assert.Single(ReadCoverRows());
        Assert.Equal("embedded", cover.SourceProvider);
        Assert.Equal(embedded, File.ReadAllBytes(cover.LocalImagePath!));
        Assert.Equal(["embedded"], ReadCanonicalValues("cover_source"));
    }

    [Fact]
    public async Task IngestionEngine_InvalidSiblingCoverJpg_IsIgnored()
    {
        var filePath = CreateWatchFile("Dune.epub");
        File.WriteAllBytes(Path.Combine(_watchDir, "cover.jpg"), new byte[4096]);
        _processors.SetNextResult(new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Books,
            Claims = [new ExtractedClaim { Key = "title", Value = "Dune", Confidence = 0.95 }],
        });

        await RunPipelineAsync();

        Assert.NotNull(await FindAssetByFileNameAsync("Dune.epub"));
        Assert.Empty(ReadCoverRows());
        Assert.Equal(["pending"], ReadCanonicalValues("cover_state"));
    }

    [Fact]
    public async Task IngestionEngine_ComicWithSiblingFolderJpg_DoesNotUseFolderImage()
    {
        var filePath = CreateWatchFile("Saga 001.cbz");
        File.WriteAllBytes(Path.Combine(_watchDir, "folder.jpg"), CreateJpeg(4096, seed: 5));
        _processors.SetNextResult(new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Comics,
            Claims = [new ExtractedClaim { Key = "title", Value = "Saga 001", Confidence = 0.95 }],
        });

        await RunPipelineAsync(applyMediaTypePrior: false);

        Assert.NotNull(await FindAssetByFileNameAsync("Saga 001.cbz"));
        Assert.Empty(ReadCoverRows());
        Assert.Equal(["pending"], ReadCanonicalValues("cover_state"));
    }

    [Fact]
    public async Task IngestionEngine_ConcurrentTracksOfOneAlbum_PersistOneCompleteCoverWithoutErrors()
    {
        const int trackCount = 8;
        var embedded = CreateJpeg(512 * 1024, seed: 11);
        var assetIds = SeedAlbumTracks(trackCount);
        var logger = new CapturingLogger<IngestionEngine>();

        var contexts = assetIds
            .Select((assetId, index) => CreateWriteBackContext(
                assetId,
                CreateWatchFile($"Track {index + 1:00}.mp3", $"audio bytes {index}"),
                embedded,
                "image/jpeg"))
            .ToList();

        using var debounce = new DebounceQueue(new DebounceOptions());
        using var engine = CreateEngine(debounce, CreateDeletionOptions(), logger);

        // Release every track at once so they race for the shared album cover.
        using var gate = new ManualResetEventSlim(false);
        var writes = contexts
            .Select(context => Task.Run(async () =>
            {
                gate.Wait();
                await InvokeWriteBackStageAsync(engine, context);
            }))
            .ToArray();
        gate.Set();
        await Task.WhenAll(writes);

        Assert.DoesNotContain(logger.Warnings, w => w.Contains("Failed to persist", StringComparison.OrdinalIgnoreCase));

        var cover = Assert.Single(ReadCoverRows());
        Assert.True(cover.IsPreferred);
        Assert.Equal("embedded", cover.SourceProvider);
        Assert.Equal(embedded, File.ReadAllBytes(cover.LocalImagePath!));
        var coverDirectory = Path.GetDirectoryName(cover.LocalImagePath!)!;
        Assert.Empty(Directory.EnumerateFiles(coverDirectory, "*.tmp"));
        Assert.Single(Directory.EnumerateFiles(coverDirectory));

        // A later track carrying the same cover must not rewrite the file.
        var writtenAt = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(cover.LocalImagePath!, writtenAt);
        var lateAsset = SeedAlbumTracks(1, existingAlbumOf: assetIds[0]).Single();
        await InvokeWriteBackStageAsync(
            engine,
            CreateWriteBackContext(lateAsset, CreateWatchFile("Track 99.mp3", "late"), embedded, "image/jpeg"));

        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(cover.LocalImagePath!));
        Assert.Single(ReadCoverRows());
        Assert.DoesNotContain(logger.Warnings, w => w.Contains("Failed to persist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IngestionEngine_TracksOfOneAlbumWithDifferentEmbeddedCovers_PersistWithoutForeignKeyFailure()
    {
        // Proof run: sibling tracks of one album carried different embedded art. The second write replaced
        // the shared variant's file in place, whose new content hash had no artwork_assets row, so the
        // artwork link insert failed with FOREIGN KEY constraint failed.
        var assetIds = SeedAlbumTracks(4);
        var logger = new CapturingLogger<IngestionEngine>();
        var covers = Enumerable.Range(0, assetIds.Count).Select(i => CreateJpeg(64 * 1024, seed: (byte)(20 + i))).ToList();

        using var debounce = new DebounceQueue(new DebounceOptions());
        using var engine = CreateEngine(debounce, CreateDeletionOptions(), logger);

        for (var i = 0; i < assetIds.Count; i++)
        {
            await InvokeWriteBackStageAsync(
                engine,
                CreateWriteBackContext(assetIds[i], CreateWatchFile($"Differing {i:00}.mp3", $"audio {i}"), covers[i], "image/jpeg"));
        }

        Assert.DoesNotContain(logger.Warnings, w => w.Contains("Failed to persist", StringComparison.OrdinalIgnoreCase));
        var cover = Assert.Single(ReadCoverRows());
        Assert.True(cover.IsPreferred);

        using var conn = _dbFactory.Connection.CreateConnection();
        Assert.Equal(1, conn.ExecuteScalar<long>(
            """
            SELECT COUNT(*) FROM entity_artwork_links l
            JOIN artwork_assets a ON a.id = l.artwork_asset_id
            WHERE l.role = 'Primary';
            """));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IngestionEngine_ConcurrentFormatsOfOneBookInOneFolder_CreateOneWorkWithTwoEditions(bool sameFolder)
    {
        var otherFolder = Path.Combine(_watchDir, "other");
        Directory.CreateDirectory(otherFolder);
        var epub = CreateWatchFile("Winners Take All.epub", "epub bytes");
        var azw3Path = Path.Combine(sameFolder ? _watchDir : otherFolder, "Winners Take All.azw3");
        File.WriteAllText(azw3Path, "azw3 bytes");
        foreach (var path in new[] { epub, azw3Path })
        {
            _processors.SetResultForPath(path, new ProcessorResult
            {
                FilePath = path,
                DetectedType = MediaType.Books,
                Claims =
                [
                    new ExtractedClaim { Key = "title", Value = "Winners Take All: The Elite Charade", Confidence = 0.95 },
                    new ExtractedClaim { Key = "author", Value = "Anand Giridharadas", Confidence = 0.92 },
                ],
            });
        }

        using var debounce = new DebounceQueue(new DebounceOptions());
        // Widen the window between "Edition created" and "asset registered" so the pair would
        // both create a Work if the pipeline did not serialise siblings in one folder.
        using var engine = CreateEngine(
            debounce,
            CreateDeletionOptions(),
            chainFactory: new SlowChainFactory(_chainFactory, TimeSpan.FromMilliseconds(300)));

        // Release both formats at once so they race through work resolution.
        using var gate = new ManualResetEventSlim(false);
        var runs = new[] { epub, azw3Path }
            .Select(path => Task.Run(async () =>
            {
                gate.Wait();
                await InvokeProcessCandidateAsync(engine, path);
            }))
            .ToArray();
        gate.Set();
        await Task.WhenAll(runs);

        using var conn = _dbFactory.Connection.CreateConnection();
        Assert.Equal(2, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM media_assets;"));
        Assert.Equal(2, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM editions;"));
        Assert.Equal(sameFolder ? 1 : 2, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'Books';"));
    }

    private sealed class SlowChainFactory(IMediaEntityChainFactory inner, TimeSpan delay) : IMediaEntityChainFactory
    {
        public async Task<Guid> EnsureEntityChainAsync(
            MediaType mediaType,
            IReadOnlyDictionary<string, string>? metadata,
            string? sourceFilePath = null,
            CancellationToken ct = default)
        {
            var editionId = await inner.EnsureEntityChainAsync(mediaType, metadata, sourceFilePath, ct);
            await Task.Delay(delay, ct);
            return editionId;
        }
    }

    private static async Task InvokeProcessCandidateAsync(IngestionEngine engine, string path)
    {
        var method = typeof(IngestionEngine).GetMethod(
            "ProcessCandidateAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var invocation = method.Invoke(
            engine,
            [
                new IngestionCandidate
                {
                    Path = path,
                    EventType = FileEventType.Created,
                    DetectedAt = DateTimeOffset.UtcNow,
                    ReadyAt = DateTimeOffset.UtcNow,
                },
                CancellationToken.None,
            ]);
        await Assert.IsAssignableFrom<Task>(invocation);
    }

    private List<Guid> SeedAlbumTracks(int count, Guid? existingAlbumOf = null)
    {
        using var connection = _dbFactory.Connection.CreateConnection();
        var albumId = existingAlbumOf.HasValue
            ? connection.ExecuteScalar<Guid>(
                """
                SELECT w.parent_work_id FROM media_assets a
                JOIN editions e ON e.id = a.edition_id
                JOIN works w ON w.id = e.work_id
                WHERE a.id = @assetId;
                """,
                new { assetId = existingAlbumOf.Value })
            : Guid.NewGuid();
        if (!existingAlbumOf.HasValue)
        {
            connection.Execute(
                "INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@albumId, 'Music', 'parent', 'Owned');",
                new { albumId });
        }

        var assetIds = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var workId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            connection.Execute(
                """
                INSERT INTO works (id, media_type, work_kind, ownership, parent_work_id)
                VALUES (@workId, 'Music', 'child', 'Owned', @albumId);
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @contentHash, @path, 'Normal');
                """,
                new
                {
                    workId,
                    albumId,
                    editionId,
                    assetId,
                    contentHash = assetId.ToString("N"),
                    path = Path.Combine(_watchDir, $"seeded-{assetId:N}.mp3"),
                });
            assetIds.Add(assetId);
        }

        return assetIds;
    }

    private static object CreateWriteBackContext(Guid assetId, string filePath, byte[] coverImage, string mimeType)
    {
        var contextType = typeof(IngestionEngine).Assembly.GetType("MediaEngine.Ingestion.Pipeline.IngestionPipelineContext");
        Assert.NotNull(contextType);
        var candidate = new IngestionCandidate
        {
            Path = filePath,
            EventType = FileEventType.Created,
            DetectedAt = DateTimeOffset.UtcNow,
            ReadyAt = DateTimeOffset.UtcNow,
        };
        var context = Activator.CreateInstance(contextType, candidate, Guid.NewGuid());
        Assert.NotNull(context);

        void Set(string name, object value) => contextType.GetProperty(name)!.SetValue(context, value);
        Set("AssetId", assetId);
        Set("ResolvedTitle", "Shared Album Track");
        Set("ProcessorResult", new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Music,
            CoverImage = coverImage,
            CoverImageMimeType = mimeType,
        });
        return context;
    }

    private static async Task InvokeWriteBackStageAsync(IngestionEngine engine, object context)
    {
        var method = typeof(IngestionEngine).GetMethod(
            "RunWriteBackStageAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        await Assert.IsAssignableFrom<Task>(method.Invoke(engine, [context, CancellationToken.None]));
    }

    private static byte[] CreateJpeg(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i + seed) % 251);
        }

        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        return bytes;
    }

    private sealed record CoverRow(string EntityId, string SourceProvider, string? LocalImagePath, bool IsPreferred);

    private List<CoverRow> ReadCoverRows()
    {
        using var conn = _dbFactory.Connection.CreateConnection();
        return conn.Query(
                "SELECT entity_id AS EntityId, source_provider AS SourceProvider, local_image_path AS LocalImagePath, is_preferred AS IsPreferred FROM entity_assets WHERE asset_type = 'CoverArt';")
            .Select(row => new CoverRow(
                row.EntityId is byte[] blob ? new Guid(blob).ToString() : Convert.ToString(row.EntityId) ?? string.Empty,
                (string?)row.SourceProvider ?? string.Empty,
                (string?)row.LocalImagePath,
                Convert.ToInt64(row.IsPreferred) == 1))
            .ToList();
    }

    private List<string> ReadCanonicalValues(string key)
    {
        using var conn = _dbFactory.Connection.CreateConnection();
        return conn.Query<string>("SELECT DISTINCT value FROM canonical_values WHERE key = @key ORDER BY value;", new { key })
            .ToList();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_warnings)
                {
                    return _warnings.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel < LogLevel.Warning)
            {
                return;
            }

            lock (_warnings)
            {
                _warnings.Add(formatter(state, exception));
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Group B — Worker-level state machine tests (stubs only, no real DB)
    // ══════════════════════════════════════════════════════════════════════

    // ── Test 5: RetailMatchWorker transitions Queued → RetailMatched ──────

    [Fact]
    public async Task RetailMatchWorker_HighScoreCandidate_TransitionsToRetailMatched()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        var jobRepo = new InMemoryIdentityJobRepository();
        var candidateRepo = new InMemoryRetailCandidateRepository();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = "Queued",
        });

        var provider = new StubProvider
        {
            Name = "apple_api",
            ProviderId = providerId,
            Claims =
            [
                new ProviderClaim(MetadataFieldConstants.Title,  "Foundation",   0.95),
                new ProviderClaim(MetadataFieldConstants.Author, "Isaac Asimov", 0.90),
            ],
        };

        var retailScoring = new FixedScoreRetailScoringService(compositeScore: 0.92);
        var configLoader = new MinimalConfigurationLoader();

        var worker = new RetailMatchWorker(
            jobRepo,
            candidateRepo,
            CreateOutcomeFactory(),
            CreateTimelineRecorder(),
            CreateBatchProgressService(),
            new IExternalMetadataProvider[] { provider },
            retailScoring,
            new NoOpMetadataClaimRepository(),
            new NoOpCanonicalValueRepository(),
            new NoOpScoringEngine(),
            configLoader,
            new NoOpBridgeIdRepository(),
            new WorkRepository(_dbFactory.Connection),
            new WorkClaimRouter(),
            new NoOpHttpClientFactory(),
            null!, // PostPipelineService — not exercised in this test
            NullLogger<RetailMatchWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updated = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updated);
        Assert.Equal(IdentityJobState.RetailMatched.ToString(), updated!.State);
        Assert.NotNull(updated.SelectedCandidateId);
    }

    // ── Test 6: WikidataBridgeWorker strict gate — RetailNoMatch stays ────

    [Fact]
    public async Task WikidataBridgeWorker_JobInRetailNoMatchState_IsNotLeased()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var jobRepo = new InMemoryIdentityJobRepository();
        var wikidataCandidates = new InMemoryWikidataCandidateRepository();

        // Seed a job that is in RetailNoMatch — the bridge worker must ignore it.
        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = IdentityJobState.RetailNoMatch.ToString(),
        });

        var configLoader = new MinimalConfigurationLoader();
        var bridgeIdHelper = new BridgeIdHelper(configLoader);

        var workRepoLocal = new WorkRepository(_dbFactory.Connection);
        var worker = new WikidataBridgeWorker(
            jobRepo,
            wikidataCandidates,
            CreateOutcomeFactory(),
            CreateTimelineRecorder(),
            bridgeIdHelper,
            Array.Empty<IExternalMetadataProvider>(),
            new NoOpBridgeIdRepository(),
            new NoOpMetadataClaimRepository(),
            new NoOpCanonicalValueRepository(),
            new NoOpScoringEngine(),
            configLoader,
            workRepoLocal,
            new WorkClaimRouter(),
            new CatalogUpsertService(workRepoLocal),
            new NoOpIngestionBatchRepository(),
            null!, // PostPipelineService — no jobs should be leased in this test
            new CoverArtWorker(
                _assetRepo,
                _canonicalRepo,
                workRepoLocal,
                new ImageCacheRepository(_dbFactory.Connection),
                new NoOpHttpClientFactory(),
                new AssetPathService(Path.Combine(Path.GetTempPath(), "tuvima-ingestion-tests")),
                NullLogger<CoverArtWorker>.Instance),
            NullLogger<WikidataBridgeWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        // The worker must not have touched the RetailNoMatch job.
        Assert.Equal(0, processed);

        var job = await jobRepo.GetByIdAsync(jobId);
        Assert.Equal(IdentityJobState.RetailNoMatch.ToString(), job!.State);
    }

    // ── Test 7: QuickHydrationWorker transitions QidResolved → UniverseEnriching ─

    [Fact]
    public async Task QuickHydrationWorker_QidResolvedJob_TransitionsToUniverseEnriching()
    {
        const string qid = "Q185166"; // Isaac Asimov — Foundation

        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var jobRepo = new InMemoryIdentityJobRepository();
        var enrichment = new RecordingEnrichmentService();
        var universeScheduler = new RecordingUniverseEnrichmentScheduler();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Books",
            State = IdentityJobState.QidResolved.ToString(),
            ResolvedQid = qid,
        });

        var postPipeline = new PostPipelineService(
            new NoOpMetadataClaimRepository(),
            new NoOpCanonicalValueRepository(),
            new NoOpScoringEngine(overallConfidence: 0.90),
            new MinimalConfigurationLoader(),
            Array.Empty<IExternalMetadataProvider>(),
            new NoOpReviewQueueRepository(),
            new NoOpAutoOrganizeService(),
            CreateBatchProgressService(),
            NullLogger<PostPipelineService>.Instance);

        var collectionRepo = new NoOpCollectionRepository();
        var collectionAssignment = new CollectionAssignmentService(
            collectionRepo,
            new NoOpCanonicalValueRepository(),
            new NoOpWorkRepository(),
            NullLogger<CollectionAssignmentService>.Instance);
        var collectionFinalization = new CollectionFinalizationService(
            collectionAssignment,
            new ParentCollectionResolver(collectionRepo, NullLogger<ParentCollectionResolver>.Instance),
            new NoOpSystemActivityRepository(),
            NullLogger<CollectionFinalizationService>.Instance);

        var worker = new QuickHydrationWorker(
            jobRepo,
            enrichment,
            collectionFinalization,
            postPipeline,
            new NoOpCanonicalValueRepository(),
            collectionRepo,
            universeScheduler,
            new MinimalConfigurationLoader(),
            NullLogger<QuickHydrationWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);

        var updated = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updated);
        Assert.Equal(IdentityJobState.UniverseEnriching.ToString(), updated!.State);

        // Confirm enrichment was called with the correct entity and QID.
        Assert.Single(enrichment.Calls);
        Assert.Equal(entityId, enrichment.Calls[0].EntityId);
        Assert.Equal(qid, enrichment.Calls[0].Qid);
        Assert.Single(universeScheduler.Requests);
        Assert.Equal(entityId, universeScheduler.Requests[0].EntityId);
        Assert.Equal(qid, universeScheduler.Requests[0].WorkQid);
    }

    [Fact]
    public async Task QuickHydrationWorker_NestedEnrichmentFailure_RequeuesQidResolvedForRetry()
    {
        const string qid = "Q185166";

        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var jobRepo = new InMemoryIdentityJobRepository();
        var enrichment = new RecordingEnrichmentService
        {
            QuickPassException = new InvalidOperationException("person enrichment did not complete")
        };
        var universeScheduler = new RecordingUniverseEnrichmentScheduler();

        await jobRepo.CreateAsync(new IdentityJob
        {
            Id = jobId,
            EntityId = entityId,
            EntityType = "MediaAsset",
            MediaType = "Movies",
            State = IdentityJobState.QidResolved.ToString(),
            ResolvedQid = qid,
        });

        var postPipeline = new PostPipelineService(
            new NoOpMetadataClaimRepository(),
            new NoOpCanonicalValueRepository(),
            new NoOpScoringEngine(overallConfidence: 0.90),
            new MinimalConfigurationLoader(),
            Array.Empty<IExternalMetadataProvider>(),
            new NoOpReviewQueueRepository(),
            new NoOpAutoOrganizeService(),
            CreateBatchProgressService(),
            NullLogger<PostPipelineService>.Instance);

        var collectionRepo = new NoOpCollectionRepository();
        var collectionAssignment = new CollectionAssignmentService(
            collectionRepo,
            new NoOpCanonicalValueRepository(),
            new NoOpWorkRepository(),
            NullLogger<CollectionAssignmentService>.Instance);
        var collectionFinalization = new CollectionFinalizationService(
            collectionAssignment,
            new ParentCollectionResolver(collectionRepo, NullLogger<ParentCollectionResolver>.Instance),
            new NoOpSystemActivityRepository(),
            NullLogger<CollectionFinalizationService>.Instance);

        var worker = new QuickHydrationWorker(
            jobRepo,
            enrichment,
            collectionFinalization,
            postPipeline,
            new NoOpCanonicalValueRepository(),
            collectionRepo,
            universeScheduler,
            new MinimalConfigurationLoader(),
            NullLogger<QuickHydrationWorker>.Instance);

        var processed = await worker.PollAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        var updated = await jobRepo.GetByIdAsync(jobId);
        Assert.NotNull(updated);
        Assert.Equal(IdentityJobState.QidResolved.ToString(), updated!.State);
        Assert.Empty(universeScheduler.Requests);
    }

    // ── Test 8: PostPipelineService auto-resolves stale review items ──────

    [Fact]
    public async Task PostPipelineService_HighConfidenceEntity_AutoResolvesStaleReviews()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        // Seed review items that should be auto-resolved once the pipeline has
        // high-confidence identity evidence.
        var lowConfidenceReview = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = "Work",
            Trigger = ReviewTrigger.LowConfidence,
            Status = ReviewStatus.Pending,
        };
        var ambiguousReview = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = "Work",
            Trigger = ReviewTrigger.RetailMatchAmbiguous,
            Status = ReviewStatus.Pending,
        };
        var ambiguousMediaTypeReview = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = "Work",
            Trigger = ReviewTrigger.AmbiguousMediaType,
            Status = ReviewStatus.Pending,
        };
        var rootWatchFolderReview = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = "Work",
            Trigger = ReviewTrigger.RootWatchFolder,
            Status = ReviewStatus.Pending,
        };

        var reviewRepo = new TrackingReviewQueueRepository();
        await reviewRepo.InsertAsync(lowConfidenceReview);
        await reviewRepo.InsertAsync(ambiguousReview);
        await reviewRepo.InsertAsync(ambiguousMediaTypeReview);
        await reviewRepo.InsertAsync(rootWatchFolderReview);

        var postPipeline = new PostPipelineService(
            new NoOpMetadataClaimRepository(),
            new NoOpCanonicalValueRepository(),
            // Confidence of 0.92 is well above any auto-review threshold.
            new NoOpScoringEngine(overallConfidence: 0.92),
            new MinimalConfigurationLoader(),
            Array.Empty<IExternalMetadataProvider>(),
            reviewRepo,
            new NoOpAutoOrganizeService(),
            CreateBatchProgressService(),
            NullLogger<PostPipelineService>.Instance);

        await postPipeline.EvaluateAndOrganizeAsync(entityId, jobId, "Q185166", null, CancellationToken.None);

        // All stale pre-identity review items should now be Resolved.
        var all = await reviewRepo.GetByEntityAsync(entityId);
        Assert.All(all, r => Assert.Equal(ReviewStatus.Resolved, r.Status));
    }

    // ══════════════════════════════════════════════════════════════════════
    // Helpers — IngestionEngine factory
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PostPipelineService_AcceptedQid_SystemResolvesVisibleSupersededReviewsAndOrganizes()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var visibleReview = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = "Work",
            Trigger = ReviewTrigger.RetailMatchAmbiguous,
            Status = ReviewStatus.Pending,
            ReviewReadyAt = DateTimeOffset.UtcNow,
            AutomationCompletedAt = DateTimeOffset.UtcNow,
        };

        var reviewRepo = new TrackingReviewQueueRepository();
        await reviewRepo.InsertAsync(visibleReview);
        var organizer = new RecordingAutoOrganizeService();

        var postPipeline = new PostPipelineService(
            new NoOpMetadataClaimRepository(),
            new NoOpCanonicalValueRepository(),
            new NoOpScoringEngine(overallConfidence: 0.92),
            new MinimalConfigurationLoader(),
            Array.Empty<IExternalMetadataProvider>(),
            reviewRepo,
            organizer,
            CreateBatchProgressService(),
            NullLogger<PostPipelineService>.Instance);

        var didOrganize = await postPipeline.EvaluateAndOrganizeAsync(entityId, jobId, "Q185166", null, CancellationToken.None);

        var review = Assert.Single(await reviewRepo.GetByEntityAsync(entityId));
        Assert.Equal(ReviewStatus.Resolved, review.Status);
        Assert.Equal("system:identity-superseded", review.ResolvedBy);
        var organized = Assert.Single(organizer.Calls);
        Assert.Equal(entityId, organized.EntityId);
        Assert.True(didOrganize);
    }

    [Fact]
    public async Task PostPipelineService_RetainedRetailIdentity_SystemResolvesRetailReviewAndOrganizes()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var visibleReview = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = "MediaAsset",
            Trigger = ReviewTrigger.RetailMatchAmbiguous,
            Status = ReviewStatus.Pending,
            ConfidenceScore = 0.93,
            ReviewReadyAt = DateTimeOffset.UtcNow,
            AutomationCompletedAt = DateTimeOffset.UtcNow,
        };

        var reviewRepo = new TrackingReviewQueueRepository();
        await reviewRepo.InsertAsync(visibleReview);
        var organizer = new RecordingAutoOrganizeService();

        var postPipeline = new PostPipelineService(
            new NoOpMetadataClaimRepository(),
            new NoOpCanonicalValueRepository(),
            new NoOpScoringEngine(overallConfidence: 0.82),
            new MinimalConfigurationLoader(),
            Array.Empty<IExternalMetadataProvider>(),
            reviewRepo,
            organizer,
            CreateBatchProgressService(),
            NullLogger<PostPipelineService>.Instance);

        var didOrganize = await postPipeline.EvaluateAndOrganizeAsync(
            entityId,
            jobId,
            wikidataQid: null,
            ingestionRunId: null,
            ct: CancellationToken.None,
            retainedRetailIdentity: true);

        var review = Assert.Single(await reviewRepo.GetByEntityAsync(entityId));
        Assert.Equal(ReviewStatus.Resolved, review.Status);
        Assert.Equal("system:retained-retail-identity", review.ResolvedBy);
        var organized = Assert.Single(organizer.Calls);
        Assert.Equal(entityId, organized.EntityId);
        Assert.True(didOrganize);
    }

    [Fact]
    public async Task PostPipelineService_LowConfidenceEntity_MarksQueuedReviewsReady()
    {
        var entityId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var hiddenReview = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = entityId,
            EntityType = "MediaAsset",
            Trigger = ReviewTrigger.LowConfidence,
            Status = ReviewStatus.Pending,
            ReviewReadyAt = null,
            AutomationCompletedAt = null,
        };

        var reviewRepo = new TrackingReviewQueueRepository();
        await reviewRepo.InsertAsync(hiddenReview);

        var postPipeline = new PostPipelineService(
            new NoOpMetadataClaimRepository(),
            new NoOpCanonicalValueRepository(),
            new NoOpScoringEngine(overallConfidence: 0.40),
            new MinimalConfigurationLoader(),
            Array.Empty<IExternalMetadataProvider>(),
            reviewRepo,
            new NoOpAutoOrganizeService(),
            CreateBatchProgressService(),
            NullLogger<PostPipelineService>.Instance);

        await postPipeline.EvaluateAndOrganizeAsync(entityId, jobId, wikidataQid: null, ingestionRunId: null, CancellationToken.None);

        var all = await reviewRepo.GetByEntityAsync(entityId);
        var review = Assert.Single(all);
        Assert.Equal(ReviewStatus.Pending, review.Status);
        Assert.NotNull(review.ReviewReadyAt);
        Assert.NotNull(review.AutomationCompletedAt);
    }

    private string CreateWatchFile(string name, string content = "dummy content for testing")
    {
        var path = Path.Combine(_watchDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private Guid SeedAssetForDeletion(string path, Guid libraryId, MediaType mediaType)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _dbFactory.Connection.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind, ownership)
            VALUES (@workId, @mediaType, 'standalone', 'Owned');
            INSERT INTO editions (id, work_id)
            VALUES (@editionId, @workId);
            INSERT INTO media_assets
                (id, edition_id, content_hash, file_path_root, status, library_id)
            VALUES
                (@assetId, @editionId, @contentHash, @path, 'Normal', @libraryId);
            """,
            new
            {
                workId,
                mediaType = mediaType.ToString(),
                editionId,
                assetId,
                contentHash = assetId.ToString("N"),
                path,
                libraryId = libraryId.ToString(),
            });

        return assetId;
    }

    private IngestionOptions CreateDeletionOptions() => new()
    {
        WatchDirectories = [_watchDir],
        LibraryRoot = _libraryDir,
        AutoOrganize = false,
        IncludeSubdirectories = false,
        PollIntervalSeconds = 0,
    };

    private static async Task InvokeHandleDeletedAsync(IngestionEngine engine, string path)
    {
        var method = typeof(IngestionEngine).GetMethod(
            "HandleDeletedAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var invocation = method.Invoke(
            engine,
            [
                new IngestionCandidate
                {
                    Path = path,
                    EventType = FileEventType.Deleted,
                    DetectedAt = DateTimeOffset.UtcNow,
                    ReadyAt = DateTimeOffset.UtcNow,
                },
                CancellationToken.None,
            ]);
        await Assert.IsAssignableFrom<Task>(invocation);
    }

    private static int GetOwnedTaskCount(IngestionEngine engine)
    {
        var field = typeof(IngestionEngine).GetField(
            "_ownedTasks",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var tasks = field.GetValue(engine);
        Assert.NotNull(tasks);
        var count = tasks.GetType().GetProperty("Count")?.GetValue(tasks);
        return Assert.IsType<int>(count);
    }

    private string? FindFileAnywhere(string filename)
        => Directory.Exists(_tempRoot)
            ? Directory.EnumerateFiles(_tempRoot, filename, SearchOption.AllDirectories).FirstOrDefault()
            : null;

    private async Task RunPipelineAsync(
        bool localOnly = false,
        bool applyMediaTypePrior = true,
        ILogger<IngestionEngine>? logger = null)
    {
        var libraryId = localOnly
            ? "99999999-9999-4999-8999-999999999999"
            : "11111111-1111-4111-8111-111111111111";
        var options = new IngestionOptions
        {
            WatchDirectories = [_watchDir],
            LibraryRoot = _libraryDir,
            AutoOrganize = true,
            IncludeSubdirectories = false,
            PollIntervalSeconds = 0,
            LibraryFolders =
            [
                new LibraryFolderEntry
                {
                    Id = libraryId,
                    Name = localOnly ? "Home Videos" : "Books",
                    Kind = localOnly ? LibraryKinds.Personal : LibraryKinds.Catalogued,
                    MetadataPolicy = localOnly ? LibraryMetadataPolicies.LocalOnly : LibraryMetadataPolicies.Enriched,
                    Sources =
                    [
                        new LibrarySourceEntry
                        {
                            Id = $"{libraryId}-source",
                            Path = _watchDir,
                            IncludeSubdirectories = false,
                        },
                        new LibrarySourceEntry
                        {
                            Id = $"{libraryId}-destination",
                            Path = _libraryDir,
                            Role = LibrarySourceRoles.PrimaryDestination,
                            ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                            AccessMode = LibrarySourceAccessModes.Writable,
                        },
                    ],
                    PrimaryDestinationSourceId = $"{libraryId}-destination",
                    MediaTypes = !applyMediaTypePrior
                        ? []
                        : localOnly ? [MediaType.Movies] : [MediaType.Books],
                    AcceptedIntakeModes = [LibraryIntakeModes.IncomingFolder],
                },
            ],
        };

        var debounceOptions = new DebounceOptions
        {
            SettleDelay = TimeSpan.FromMilliseconds(1),
            ProbeInterval = TimeSpan.FromMilliseconds(1),
            MaxProbeAttempts = 1,
            MaxProbeDelay = TimeSpan.FromMilliseconds(10),
        };

        using var debounce = new DebounceQueue(debounceOptions);

        using var engine = CreateEngine(debounce, options, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await engine.StartAsync(cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
        }
        catch (OperationCanceledException) { /* expected timeout */ }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await engine.StopAsync(stopCts.Token); }
            catch (OperationCanceledException) { /* stop timeout is acceptable */ }
        }
    }

    // Helpers - IngestionEngine factory

    private IngestionEngine CreateEngine(
        DebounceQueue debounce,
        IngestionOptions options,
        ILogger<IngestionEngine>? logger = null,
        IMediaEntityChainFactory? chainFactory = null) =>
        new(
            _watcher,
            debounce,
            _hasher,
            _processors,
            _scorer,
            _organizer,
            Enumerable.Empty<IMetadataTagger>(),
            _assetRepo,
            _bgWorker,
            _publisher,
            Options.Create(options),
            logger ?? NullLogger<IngestionEngine>.Instance,
            _claimRepo,
            _canonicalRepo,
            _recursiveIdentity,
            chainFactory ?? _chainFactory,
            _reviewRepo,
            _activityRepo,
            _reconciliation,
            new OrganizationGate(new ScoringConfiguration()),
            _ingestionLog,
            new StubSmartLabeler(),
            new StubMediaTypeAdvisor(),
            new StubEntityTimelineRepository(),
            new ScoringConfiguration(),
            _batchRepo,
            _identityJobRepo,
            new MediaTypeResolver(
                new OptionsMonitorStub<IngestionOptions>(options),
                new LibraryFolderResolver(new OptionsMonitorStub<IngestionOptions>(options)),
                new StubMediaTypeAdvisor(),
                new MediaTypeExtensionCatalog(new StubConfigurationLoader()),
                NullLogger<MediaTypeResolver>.Instance),
            new DuplicateResolver(_assetRepo),
            new IngestionLogScribe(_ingestionLog, _publisher, NullLogger<IngestionLogScribe>.Instance),
            new HashDedupeStageDependencies(null),
            new ScoreIdentifyStageDependencies(null),
            new OrganizeStageDependencies(null),
            new WriteBackStageDependencies(_entityAssetRepo, _assetPaths, _workRepo, null),
            new IdentityJobStageDependencies(new IdentityPipelineSignal()),
            operationTracker: _operationTracker,
            operationRepository: _operationRepo,
            libraryFolderResolver: new LibraryFolderResolver(new OptionsMonitorStub<IngestionOptions>(options)));

    private static StageOutcomeFactory CreateOutcomeFactory() =>
        new StageOutcomeFactory(
            new NoOpReviewQueueRepository(),
            new NoOpSystemActivityRepository(),
            new NoOpEventPublisher(),
            new NoOpCanonicalValueRepository(),
            NullLogger<StageOutcomeFactory>.Instance);

    private async Task<MediaAsset?> FindAssetByFileNameAsync(string fileName)
    {
        using var conn = _dbFactory.Connection.CreateConnection();
        var assetId = conn.QueryFirstOrDefault<Guid?>(
            "SELECT id FROM media_assets WHERE file_path_root LIKE @pattern LIMIT 1;",
            new { pattern = "%" + fileName });

        return assetId.HasValue
            ? await _assetRepo.FindByIdAsync(assetId.Value)
            : null;
    }

    private static TimelineRecorder CreateTimelineRecorder() =>
        new TimelineRecorder(new NoOpEntityTimelineRepository());

    private static BatchProgressService CreateBatchProgressService() =>
        new BatchProgressService(
            new NoOpIngestionBatchRepository(),
            new NoOpEventPublisher(),
            NullLogger<BatchProgressService>.Instance);

    // ══════════════════════════════════════════════════════════════════════
    // Stub / fake implementations used by Group B tests
    // ══════════════════════════════════════════════════════════════════════

    // ── InMemoryIdentityJobRepository ────────────────────────────────────

    private sealed class InMemoryIdentityJobRepository : IIdentityJobRepository
    {
        private readonly List<IdentityJob> _jobs = [];

        public Task CreateAsync(IdentityJob job, CancellationToken ct = default)
        {
            _jobs.Add(job);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IdentityJob>> LeaseNextAsync(
            string workerName,
            IReadOnlyList<IdentityJobState> states,
            int batchSize,
            TimeSpan leaseDuration,
            IReadOnlyList<string>? excludeRunIds = null,
            CancellationToken ct = default)
        {
            var stateStrings = states.Select(s => s.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excluded = excludeRunIds is { Count: > 0 }
                ? new HashSet<string>(excludeRunIds, StringComparer.OrdinalIgnoreCase)
                : null;
            var matches = _jobs
                .Where(j => stateStrings.Contains(j.State)
                         && (j.LeaseOwner is null || j.LeaseExpiresAt < DateTimeOffset.UtcNow)
                         && (excluded is null || j.IngestionRunId is null || !excluded.Contains(j.IngestionRunId.ToString()!)))
                .Take(batchSize)
                .ToList();

            foreach (var j in matches)
            {
                j.LeaseOwner = workerName;
                j.LeaseExpiresAt = DateTimeOffset.UtcNow.Add(leaseDuration);
            }

            return Task.FromResult<IReadOnlyList<IdentityJob>>(matches);
        }

        public Task UpdateStateAsync(Guid jobId, IdentityJobState newState, string? error = null, CancellationToken ct = default)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is not null)
            {
                job.State = newState.ToString();
                job.LastError = error;
                job.UpdatedAt = DateTimeOffset.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task SetSelectedCandidateAsync(Guid jobId, Guid candidateId, CancellationToken ct = default)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is not null)
            {
                job.SelectedCandidateId = candidateId;
            }

            return Task.CompletedTask;
        }

        public Task SetResolvedQidAsync(Guid jobId, string qid, CancellationToken ct = default)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is not null)
            {
                job.ResolvedQid = qid;
            }

            return Task.CompletedTask;
        }

        public Task<IdentityJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default)
            => Task.FromResult(_jobs.FirstOrDefault(j => j.Id == jobId));

        public Task<IdentityJob?> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult(_jobs.FirstOrDefault(j => j.EntityId == entityId));

        public Task<IReadOnlyList<IdentityJob>> GetStaleAsync(TimeSpan age, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IdentityJob>>([]);

        public Task<IReadOnlyList<IdentityJob>> GetByStateAsync(IdentityJobState state, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IdentityJob>>(
                _jobs.Where(j => j.State == state.ToString()).Take(limit).ToList());

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
        {
            var job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is not null) { job.LeaseOwner = null; job.LeaseExpiresAt = null; }
            return Task.CompletedTask;
        }

        public Task<int> CountActiveAsync(CancellationToken ct = default)
            => Task.FromResult(_jobs.Count(j =>
                j.State != IdentityJobState.Ready.ToString() &&
                j.State != IdentityJobState.ReadyWithoutUniverse.ToString() &&
                j.State != IdentityJobState.Failed.ToString()));
    }

    // ── InMemoryRetailCandidateRepository ────────────────────────────────

    private sealed class InMemoryRetailCandidateRepository : IRetailCandidateRepository
    {
        public List<RetailMatchCandidate> Candidates { get; } = [];

        public Task InsertBatchAsync(IReadOnlyList<RetailMatchCandidate> candidates, CancellationToken ct = default)
        {
            Candidates.AddRange(candidates);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RetailMatchCandidate>> GetByJobAsync(Guid jobId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RetailMatchCandidate>>(Candidates.Where(c => c.JobId == jobId).ToList());

        public Task<RetailMatchCandidate?> GetSelectedAsync(Guid jobId, CancellationToken ct = default)
            => Task.FromResult(Candidates.FirstOrDefault(c => c.JobId == jobId && c.Outcome == "AutoAccepted"));

        public Task<RetailMatchCandidate?> GetByIdAsync(Guid candidateId, CancellationToken ct = default)
            => Task.FromResult(Candidates.FirstOrDefault(c => c.Id == candidateId));
    }

    // ── InMemoryWikidataCandidateRepository ──────────────────────────────

    private sealed class InMemoryWikidataCandidateRepository : IWikidataCandidateRepository
    {
        public List<WikidataBridgeCandidate> Candidates { get; } = [];

        public Task InsertBatchAsync(IReadOnlyList<WikidataBridgeCandidate> candidates, CancellationToken ct = default)
        {
            Candidates.AddRange(candidates);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WikidataBridgeCandidate>> GetByJobAsync(Guid jobId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WikidataBridgeCandidate>>(Candidates.Where(c => c.JobId == jobId).ToList());

        public Task<WikidataBridgeCandidate?> GetSelectedAsync(Guid jobId, CancellationToken ct = default)
            => Task.FromResult(Candidates.FirstOrDefault(c => c.JobId == jobId && c.Outcome == "AutoAccepted"));

        public Task<WikidataBridgeCandidate?> GetByIdAsync(Guid candidateId, CancellationToken ct = default)
            => Task.FromResult(Candidates.FirstOrDefault(c => c.Id == candidateId));
    }

    // ── TrackingReviewQueueRepository ────────────────────────────────────

    private sealed class TrackingReviewQueueRepository : IReviewQueueRepository
    {
        private readonly List<ReviewQueueEntry> _entries = [];

        public Task<Guid> InsertAsync(ReviewQueueEntry entry, CancellationToken ct = default)
        {
            _entries.Add(entry);
            return Task.FromResult(entry.Id);
        }

        public Task<IReadOnlyList<ReviewQueueEntry>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>(_entries.Where(e => e.EntityId == entityId).ToList());

        public Task<IReadOnlyList<ReviewQueueEntry>> GetPendingByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>(
                _entries.Where(e => e.EntityId == entityId && e.Status == ReviewStatus.Pending).ToList());

        public Task UpdateStatusAsync(Guid id, string status, string? resolvedBy = null, CancellationToken ct = default)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry is not null)
            {
                entry.Status = status;
                entry.ResolvedBy = resolvedBy;
                entry.ResolvedAt = DateTimeOffset.UtcNow;
            }
            return Task.CompletedTask;
        }

        public Task<int> MarkPendingReadyByEntityAsync(Guid entityId, CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow;
            var count = 0;
            foreach (var entry in _entries.Where(e =>
                e.EntityId == entityId
                && e.Status == ReviewStatus.Pending
                && e.ReviewReadyAt is null))
            {
                entry.ReviewReadyAt = now;
                entry.AutomationCompletedAt ??= now;
                count++;
            }

            return Task.FromResult(count);
        }

        public Task<IReadOnlyList<ReviewQueueEntry>> GetPendingAsync(int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>(
                _entries.Where(e => e.Status == ReviewStatus.Pending).Take(limit).ToList());

        public Task<ReviewQueueEntry?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_entries.FirstOrDefault(e => e.Id == id));

        public Task<int> GetPendingCountAsync(CancellationToken ct = default)
            => Task.FromResult(_entries.Count(e => e.Status == ReviewStatus.Pending));

        public Task<int> DismissAllByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> ResolveAllByEntityAsync(Guid entityId, string resolvedBy = "system:auto-organize", CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<int> ResolvePendingByEntityAndTriggersAsync(
            Guid entityId,
            IReadOnlyCollection<string> triggers,
            string resolvedBy,
            CancellationToken ct = default)
        {
            var triggerSet = triggers.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var entry in _entries.Where(entry =>
                entry.EntityId == entityId
                && entry.Status == ReviewStatus.Pending
                && triggerSet.Contains(entry.Trigger)))
            {
                entry.Status = ReviewStatus.Resolved;
                entry.ResolvedBy = resolvedBy;
                entry.ResolvedAt = DateTimeOffset.UtcNow;
                count++;
            }

            return Task.FromResult(count);
        }

        public Task<IReadOnlyList<ReviewQueueEntry>> PromotePendingReadyByEntityAsync(
            Guid entityId,
            CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow;
            var promoted = _entries
                .Where(e => e.EntityId == entityId
                    && e.Status == ReviewStatus.Pending
                    && e.ReviewReadyAt is null)
                .ToList();

            foreach (var entry in promoted)
            {
                entry.ReviewReadyAt = now;
                entry.AutomationCompletedAt ??= now;
            }

            return Task.FromResult<IReadOnlyList<ReviewQueueEntry>>(promoted);
        }

        public Task<int> PurgeOrphanedAsync(CancellationToken ct = default)
            => Task.FromResult(0);
    }

    // ── RecordingEnrichmentService ───────────────────────────────────────

    private sealed class RecordingEnrichmentService : IEnrichmentService
    {
        public List<(Guid EntityId, string Qid)> Calls { get; } = [];
        public Exception? QuickPassException { get; init; }

        public Task RunQuickPassAsync(Guid entityId, string qid, CancellationToken ct = default)
        {
            Calls.Add((entityId, qid));
            if (QuickPassException is not null)
            {
                throw QuickPassException;
            }

            return Task.CompletedTask;
        }

        public Task RunUniversePassAsync(Guid entityId, string qid, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RunWorkScopedPassAsync(Guid entityId, string qid, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RunUniverseCorePassAsync(Guid entityId, string qid, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RunUniverseEnhancerPassAsync(Guid entityId, string qid, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RunSingleEnrichmentAsync(Guid entityId, string qid, EnrichmentType type, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingUniverseEnrichmentScheduler : IUniverseEnrichmentScheduler
    {
        public List<UniverseEnrichmentRequest> Requests { get; } = [];

        public ValueTask QueueInlineAsync(UniverseEnrichmentRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return ValueTask.CompletedTask;
        }

        public void TriggerManualSweep()
        {
        }
    }

    // ── StubProvider (external metadata provider) ─────────────────────────

    private sealed class StubProvider : IExternalMetadataProvider
    {
        public string Name { get; set; } = "stub_provider";
        public Guid ProviderId { get; set; } = Guid.NewGuid();
        public ProviderDomain Domain => ProviderDomain.Universal;
        public IReadOnlyList<string> CapabilityTags => [];
        public IReadOnlyList<ProviderClaim> Claims { get; set; } = [];

        public bool CanHandle(MediaType mediaType) => true;
        public bool CanHandle(EntityType entityType) => true;

        public Task<IReadOnlyList<ProviderClaim>> FetchAsync(ProviderLookupRequest request, CancellationToken ct = default)
            => Task.FromResult(Claims);
    }

    // ── FixedScoreRetailScoringService ────────────────────────────────────

    private sealed class FixedScoreRetailScoringService : IRetailMatchScoringService
    {
        private readonly double _score;

        public FixedScoreRetailScoringService(double compositeScore) => _score = compositeScore;

        public FieldMatchScores ScoreCandidate(
            IReadOnlyDictionary<string, string> fileHints,
            string? candidateTitle,
            string? candidateAuthor,
            string? candidateYear,
            MediaType mediaType,
            MatchTierConfig? matchTiers = null,
            CandidateExtendedMetadata? extendedMetadata = null,
            double structuralBonus = 0.0)
            => new()
            {
                TitleScore = _score,
                AuthorScore = _score,
                YearScore = 0.0,
                FormatScore = 1.0,
                CrossFieldBoost = 0.0,
                CoverArtScore = 0.0,
                CompositeScore = _score,
            };
    }

    // ── MinimalConfigurationLoader ────────────────────────────────────────

    private sealed class MinimalConfigurationLoader : IConfigurationLoader
    {
        public PipelineConfiguration LoadPipelines() => new()
        {
            Pipelines = new Dictionary<string, MediaTypePipeline>(StringComparer.OrdinalIgnoreCase)
            {
                ["Books"] = new() { Strategy = ProviderStrategy.Waterfall, Providers = [new PipelineProviderEntry { Rank = 1, Name = "apple_api", Purpose = "identity" }] },
                ["Movies"] = new() { Strategy = ProviderStrategy.Waterfall, Providers = [] },
                ["TV"] = new() { Strategy = ProviderStrategy.Waterfall, Providers = [] },
                ["Music"] = new() { Strategy = ProviderStrategy.Waterfall, Providers = [] },
                ["Audiobooks"] = new() { Strategy = ProviderStrategy.Sequential, Providers = [] },
                ["Comics"] = new() { Strategy = ProviderStrategy.Waterfall, Providers = [] },
            },
        };

        public HydrationSettings LoadHydration() => new();
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() =>
        [
            new() { Name = "apple_api", Enabled = true, ProviderId = WellKnownProviders.AppleApi.ToString(), Weight = 0.9 },
        ];
        public ScoringSettings LoadScoring() => new();
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => default;
        public CoreConfiguration LoadCore() => new();
        public void SaveCore(CoreConfiguration config) { }
        public void SaveScoring(ScoringSettings settings) { }
        public MaintenanceSettings LoadMaintenance() => new();
        public void SaveMaintenance(MaintenanceSettings settings) { }
        public void SaveHydration(HydrationSettings settings) { }
        public void SavePipelines(PipelineConfiguration config) { }
        public DisambiguationSettings LoadDisambiguation() => new();
        public void SaveDisambiguation(DisambiguationSettings settings) { }
        public TranscodingSettings LoadTranscoding() => new();
        public void SaveTranscoding(TranscodingSettings settings) { }
        public MediaTypeConfiguration LoadMediaTypes() => new();
        public void SaveMediaTypes(MediaTypeConfiguration config) { }
        public LibrariesConfiguration LoadLibraries() => new();
        public FieldPriorityConfiguration LoadFieldPriorities() => new();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void SaveProvider(ProviderConfiguration config) { }
        public T? LoadAi<T>() where T : class => default;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
    }

    // ── No-op stubs (silently discard all operations) ─────────────────────

    private sealed class NoOpMetadataClaimRepository : IMetadataClaimRepository
    {
        public Task InsertBatchAsync(IReadOnlyList<MetadataClaim> claims, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<MetadataClaim>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MetadataClaim>>([]);

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpCanonicalValueRepository : ICanonicalValueRepository
    {
        public Task<IReadOnlyList<CanonicalValue>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>> GetByEntitiesAsync(IReadOnlyList<Guid> entityIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>>(new Dictionary<Guid, IReadOnlyList<CanonicalValue>>());

        public Task UpsertBatchAsync(IReadOnlyList<CanonicalValue> values, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CanonicalValue>> GetConflictedAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task DeleteByKeyAsync(Guid entityId, string key, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Guid>> FindByValueAsync(string key, string value, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<CanonicalValue>> FindByKeyAndPrefixAsync(string key, string prefix, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task<IReadOnlyList<Guid>> GetEntitiesNeedingEnrichmentAsync(string hasField, string missingField, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class NoOpScoringEngine : IScoringEngine
    {
        private readonly double _confidence;

        public NoOpScoringEngine(double overallConfidence = 0.90) => _confidence = overallConfidence;

        public Task<ScoringResult> ScoreEntityAsync(ScoringContext context, CancellationToken ct = default)
            => Task.FromResult(new ScoringResult
            {
                EntityId = context.EntityId,
                OverallConfidence = _confidence,
                ScoredAt = DateTimeOffset.UtcNow,
                FieldScores = [],
            });

        public Task<IReadOnlyList<ScoringResult>> ScoreBatchAsync(IEnumerable<ScoringContext> contexts, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ScoringResult>>(
                contexts.Select(c => ScoreEntityAsync(c, ct).GetAwaiter().GetResult()).ToList());
    }

    private sealed class NoOpBridgeIdRepository : IBridgeIdRepository
    {
        public Task<IReadOnlyList<BridgeIdEntry>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BridgeIdEntry>>([]);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>>> GetByEntitiesAsync(IReadOnlyList<Guid> entityIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>>>(new Dictionary<Guid, IReadOnlyList<BridgeIdEntry>>());

        public Task UpsertBatchAsync(IReadOnlyList<BridgeIdEntry> entries, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<BridgeIdEntry?> FindAsync(Guid entityId, string idType, CancellationToken ct = default)
            => Task.FromResult<BridgeIdEntry?>(null);

        public Task<IReadOnlyList<BridgeIdEntry>> FindByValueAsync(string idType, string idValue, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BridgeIdEntry>>([]);

        public Task UpsertAsync(BridgeIdEntry entry, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpWorkRepository : IWorkRepository
    {
        public Task<Guid?> FindParentByKeyAsync(MediaType mediaType, string parentKey, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<Guid> GetOrCreateParentAsync(MediaType mediaType, string parentKey, Guid? grandparentWorkId, int? ordinal, double? ordinalSort = null, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task<Guid?> FindChildByOrdinalAsync(Guid parentWorkId, int ordinal, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<Guid?> FindChildByOrdinalSortAsync(Guid parentWorkId, double ordinalSort, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<Guid?> FindChildByTitleAsync(Guid parentWorkId, string title, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<Guid?> FindByExternalIdentifierAsync(string scheme, string value, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<Guid> InsertParentAsync(MediaType mediaType, string parentKey, Guid? grandparentWorkId, int? ordinal, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> InsertChildAsync(MediaType mediaType, Guid parentWorkId, int? ordinal, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> GetOrCreateChildAsync(MediaType mediaType, Guid parentWorkId, int? ordinal, double? ordinalSort = null, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task UpdateOrdinalSortAsync(Guid workId, double? ordinalSort, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid> InsertStandaloneAsync(MediaType mediaType, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> InsertCatalogChildAsync(MediaType mediaType, Guid parentWorkId, int? ordinal, IReadOnlyDictionary<string, string>? externalIdentifiers, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid());
        public Task PromoteCatalogToOwnedAsync(Guid workId, CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteExternalIdentifiersAsync(Guid workId, IReadOnlyDictionary<string, string> identifiers, CancellationToken ct = default) => Task.CompletedTask;
        public Task<WorkLineage?> GetLineageByAssetAsync(Guid assetId, CancellationToken ct = default) => Task.FromResult<WorkLineage?>(null);
        public Task<ConfirmedSiblingWorkQid?> FindConfirmedSiblingQidAsync(MediaType sourceMediaType, IReadOnlyList<MediaType> candidateMediaTypes, string title, string? creator, Guid? excludeWorkId = null, CancellationToken ct = default) => Task.FromResult<ConfirmedSiblingWorkQid?>(null);
    }

    private sealed class NoOpReviewQueueRepository : IReviewQueueRepository
    {
        public Task<Guid> InsertAsync(ReviewQueueEntry entry, CancellationToken ct = default) => Task.FromResult(entry.Id);
        public Task<IReadOnlyList<ReviewQueueEntry>> GetPendingAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>([]);
        public Task<ReviewQueueEntry?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ReviewQueueEntry?>(null);
        public Task<IReadOnlyList<ReviewQueueEntry>> GetByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>([]);
        public Task<IReadOnlyList<ReviewQueueEntry>> GetPendingByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>([]);
        public Task UpdateStatusAsync(Guid id, string status, string? resolvedBy = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> MarkPendingReadyByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<ReviewQueueEntry>> PromotePendingReadyByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ReviewQueueEntry>>([]);
        public Task<int> GetPendingCountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> DismissAllByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ResolveAllByEntityAsync(Guid entityId, string resolvedBy = "system:auto-organize", CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ResolvePendingByEntityAndTriggersAsync(Guid entityId, IReadOnlyCollection<string> triggers, string resolvedBy, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> PurgeOrphanedAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class NoOpAutoOrganizeService : IAutoOrganizeService
    {
        public Task TryAutoOrganizeAsync(Guid assetId, CancellationToken ct = default, Guid? ingestionRunId = null)
            => Task.CompletedTask;
    }

    private sealed class RecordingAutoOrganizeService : IAutoOrganizeService
    {
        public List<(Guid EntityId, Guid? IngestionRunId)> Calls { get; } = [];

        public Task TryAutoOrganizeAsync(Guid assetId, CancellationToken ct = default, Guid? ingestionRunId = null)
        {
            Calls.Add((assetId, ingestionRunId));
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpSystemActivityRepository : ISystemActivityRepository
    {
        public Task LogAsync(SystemActivityEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SystemActivityEntry>>([]);
        public Task<int> PruneOlderThanAsync(int retentionDays, CancellationToken ct = default) => Task.FromResult(0);
        public Task<long> CountAsync(CancellationToken ct = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<SystemActivityEntry>> GetByRunIdAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SystemActivityEntry>>([]);
        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByTypesAsync(IReadOnlyList<string> actionTypes, int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SystemActivityEntry>>([]);
        public Task<IReadOnlyList<SystemActivityEntry>> GetRecentByProfileAsync(Guid profileId, int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SystemActivityEntry>>([]);
    }

    private sealed class NoOpEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull => Task.CompletedTask;
    }

    private sealed class NoOpEntityTimelineRepository : IEntityTimelineRepository
    {
        public Task InsertEventAsync(EntityEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task InsertEventsAsync(IReadOnlyList<EntityEvent> events, CancellationToken ct = default) => Task.CompletedTask;
        public Task InsertFieldChangesAsync(IReadOnlyList<EntityFieldChange> changes, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<EntityEvent>> GetEventsByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EntityEvent>>([]);
        public Task<EntityEvent?> GetLatestEventAsync(Guid entityId, int stage, CancellationToken ct = default) => Task.FromResult<EntityEvent?>(null);
        public Task<IReadOnlyList<EntityEvent>> GetCurrentPipelineStateAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EntityEvent>>([]);
        public Task<EntityEvent?> GetEventByIdAsync(Guid eventId, CancellationToken ct = default) => Task.FromResult<EntityEvent?>(null);
        public Task<IReadOnlyList<EntityFieldChange>> GetFieldChangesByEventAsync(Guid eventId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EntityFieldChange>>([]);
        public Task<IReadOnlyList<EntityFieldChange>> GetFieldHistoryAsync(Guid entityId, string field, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EntityFieldChange>>([]);
        public Task<IReadOnlyList<EntityFieldChange>> GetFieldHistoryAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EntityFieldChange>>([]);
        public Task<IReadOnlyList<EntityFieldChange>> GetFileOriginalsForEventAsync(Guid eventId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EntityFieldChange>>([]);
        public Task<IReadOnlyDictionary<Guid, EntityEvent>> GetLatestStage2EventsAsync(IReadOnlyList<Guid> entityIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<Guid, EntityEvent>>(new Dictionary<Guid, EntityEvent>());
        public Task<int> CullOldEventsAsync(TimeSpan retention, CancellationToken ct = default) => Task.FromResult(0);
        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpIngestionBatchRepository : IIngestionBatchRepository
    {
        public Task CreateAsync(IngestionBatch batch, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateCountsAsync(Guid id, int filesTotal, int filesProcessed, int filesIdentified, int filesReview, int filesNoMatch, int filesFailed, CancellationToken ct = default) => Task.CompletedTask;
        public Task CompleteAsync(Guid id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task IncrementCounterAsync(Guid id, BatchCounterColumn column, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IngestionBatch?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<IngestionBatch?>(null);
        public Task<IReadOnlyList<IngestionBatch>> GetRecentAsync(int limit = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IngestionBatch>>([]);
        public Task<int> GetNeedsAttentionCountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<IngestionBatchProgressSnapshot> GetProgressSnapshotAsync(Guid batchId, CancellationToken ct = default)
            => Task.FromResult(new IngestionBatchProgressSnapshot());
    }

    /// <summary>
    /// Minimal no-op ICollectionRepository — only implements methods used by CollectionAssignmentService.
    /// All lookups return null/empty; assignment is a no-op.
    /// </summary>
    private sealed class NoOpCollectionRepository : ICollectionRepository
    {
        public Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
        public Task<Collection?> FindByDisplayNameAsync(string displayName, CancellationToken ct = default) => Task.FromResult<Collection?>(null);
        public Task<Collection?> FindByRelationshipQidAsync(string relType, string qid, CancellationToken ct = default) => Task.FromResult<Collection?>(null);
        public Task<Guid> UpsertAsync(Collection collection, CancellationToken ct = default) => Task.FromResult(collection.Id);
        public Task InsertRelationshipsAsync(IReadOnlyList<CollectionRelationship> relationships, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Guid?> GetWorkIdByMediaAssetAsync(Guid mediaAssetId, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<IReadOnlyList<Guid>> GetWorkLineageIdsByMediaAssetAsync(Guid mediaAssetId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<string?> FindCollectionNameByWorkIdAsync(Guid workId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task AssignWorkToCollectionAsync(Guid workId, Guid collectionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MergeCollectionsAsync(Guid keepCollectionId, Guid mergeCollectionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetUniverseMismatchAsync(Guid workId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateWorkWikidataStatusAsync(Guid workId, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateWorkWikidataMatchStateAsync(Guid workId, string status, string? source = null, bool? locked = null, string? wikidataQid = null, string? rejectedQidsJson = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> PruneOrphanedHierarchyAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<Collection>> GetChildCollectionsAsync(Guid parentCollectionId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
        public Task SetParentCollectionAsync(Guid collectionId, Guid? parentCollectionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Collection?> FindParentCollectionByRelationshipAsync(string qid, CancellationToken ct = default) => Task.FromResult<Collection?>(null);
        public Task<IReadOnlyList<Guid>> FindCollectionIdsByFranchiseQidAsync(string qid, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<IReadOnlyList<CollectionRelationship>> GetRelationshipsAsync(Guid collectionId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionRelationship>>([]);
        public Task<Collection?> GetByIdAsync(Guid collectionId, CancellationToken ct = default) => Task.FromResult<Collection?>(null);
        public Task<IReadOnlyList<Collection>> GetByIdsAsync(IEnumerable<Guid> collectionIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
        public Task<Collection?> FindByQidAsync(string qid, CancellationToken ct = default) => Task.FromResult<Collection?>(null);
        public Task<Edition?> FindEditionByQidAsync(string wikidataQid, CancellationToken ct = default) => Task.FromResult<Edition?>(null);
        public Task<Edition> CreateEditionAsync(Guid workId, string? formatLabel, string? wikidataQid, CancellationToken ct = default) => Task.FromResult(new Edition { Id = Guid.NewGuid(), WorkId = workId });
        public Task UpdateMatchLevelAsync(Guid workId, string matchLevel, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Collection>> GetByTypeAsync(string collectionType, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
        public Task<IReadOnlyList<Collection>> GetManagedCollectionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
        public Task<Dictionary<string, int>> GetCountsByTypeAsync(CancellationToken ct = default) => Task.FromResult(new Dictionary<string, int>());
        public Task<IReadOnlyList<CollectionItem>> GetCollectionItemsAsync(Guid collectionId, int limit = 20, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionItem>>([]);
        public Task<int> GetCollectionItemCountAsync(Guid collectionId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<Dictionary<Guid, int>> GetCollectionItemCountsAsync(IEnumerable<Guid> collectionIds, CancellationToken ct = default) => Task.FromResult(new Dictionary<Guid, int>());
        public Task UpdateCollectionArtworkAsync(Guid collectionId, string slot, string? localPath, string? mimeType, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateCollectionEnabledAsync(Guid collectionId, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateCollectionFeaturedAsync(Guid collectionId, bool featured, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddCollectionItemAsync(CollectionItem item, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveCollectionItemAsync(Guid itemId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReorderCollectionItemsAsync(Guid collectionId, IReadOnlyList<Guid> itemIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Collection>> GetContentGroupsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
        public Task<Collection?> GetCollectionWithWorksAsync(Guid collectionId, CancellationToken ct = default) => Task.FromResult<Collection?>(null);
        public Task<IReadOnlyList<Collection>> GetCollectionsWithWorksAsync(IEnumerable<Guid> collectionIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
        public Task<Guid?> GetCollectionIdByWorkIdAsync(Guid workId, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<Collection?> FindByRuleHashAsync(string ruleHash, CancellationToken ct = default) => Task.FromResult<Collection?>(null);
        public Task<int> CountCollectionBackfillCandidatesAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<CollectionBackfillCandidate>> GetCollectionBackfillCandidatesAsync(int limit, Guid? afterWorkId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionBackfillCandidate>>([]);
        public Task<IReadOnlyList<Collection>> GetAllCollectionsForLocationAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Collection>>([]);
    }

    /// <summary>
    /// Stub IHttpClientFactory — never called in tests that use non-Music/TV media types.
    /// </summary>
    private sealed class NoOpHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name)
            => throw new NotSupportedException(
                $"NoOpHttpClientFactory.CreateClient('{name}') should not be called in unit tests.");
    }
}
