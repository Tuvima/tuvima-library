using Dapper;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Pipeline;
using MediaEngine.Ingestion.Services;
using MediaEngine.Ingestion.Tests.Helpers;
using MediaEngine.Intelligence;
using MediaEngine.Intelligence.Models;
using MediaEngine.Processors.Models;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// A machine without the media tool (FFmpeg), or a file the tool cannot read, must not leave a registered file stranded:
/// every scanned file ends up queued for identity or in Needs Review, and the other files in the batch keep moving.
/// Runs the real ingestion engine on temp folders and a real SQLite data store; only the media tool is a fake.
/// </summary>
public sealed class MissingMediaToolIngestionTests : IDisposable
{
    private const string MediaInspectionFailedTrigger = ReviewTrigger.MediaInspectionFailed;

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task A_movie_scanned_without_the_media_tool_is_registered_and_queued_for_identity_not_failed()
    {
        var run = await ScanAsync(FakeFFmpegMode.Unavailable, new SeedFile("Dune.2021.mkv", MediaType.Movies));

        var movie = run.Files.Single();
        Assert.Equal(0, await run.FailedLogCountAsync(movie));
        Assert.Equal(1, await run.AssetCountAsync(movie));
        Assert.Equal(1, await run.IdentityJobCountAsync(movie));
        Assert.Equal(0, await run.BatchFilesFailedAsync());
        Assert.Equal(1, await run.HoldCountAsync(movie));
        Assert.Equal(0, await run.ReviewCountAsync(movie, MediaInspectionFailedTrigger));
        Assert.Empty(run.Ffmpeg.ProbedPaths);
    }

    [Fact]
    public async Task A_song_scanned_without_the_media_tool_is_registered_and_queued_for_identity_not_failed()
    {
        var run = await ScanAsync(FakeFFmpegMode.Unavailable, new SeedFile("Wannabe.mp3", MediaType.Music));

        var song = run.Files.Single();
        Assert.Equal(0, await run.FailedLogCountAsync(song));
        Assert.Equal(1, await run.AssetCountAsync(song));
        Assert.Equal(1, await run.IdentityJobCountAsync(song));
        Assert.Equal(0, await run.BatchFilesFailedAsync());
        Assert.Equal(1, await run.HoldCountAsync(song));
    }

    [Fact]
    public async Task A_book_scanned_without_the_media_tool_is_registered_and_queued_for_identity_not_failed()
    {
        var run = await ScanAsync(FakeFFmpegMode.Unavailable, new SeedFile("Dune.epub", MediaType.Books));

        var book = run.Files.Single();
        Assert.Equal(0, await run.FailedLogCountAsync(book));
        Assert.Equal(1, await run.AssetCountAsync(book));
        Assert.Equal(1, await run.IdentityJobCountAsync(book));
        Assert.Equal(0, await run.BatchFilesFailedAsync());
        Assert.Equal(0, await run.HoldCountAsync(book));
    }

    [Fact]
    public async Task A_corrupt_video_goes_to_needs_review_and_the_good_video_in_the_same_batch_still_completes()
    {
        var run = await ScanAsync(
            FakeFFmpegMode.Available,
            new SeedFile("Corrupt.2020.mkv", MediaType.Movies, Corrupt: true),
            new SeedFile("Good.2021.mkv", MediaType.Movies));

        var corrupt = run.Files.Single(f => f.EndsWith("Corrupt.2020.mkv", StringComparison.Ordinal));
        var good = run.Files.Single(f => f.EndsWith("Good.2021.mkv", StringComparison.Ordinal));

        Assert.Equal(1, await run.AssetCountAsync(good));
        Assert.Equal(1, await run.IdentityJobCountAsync(good));
        Assert.Equal(0, await run.FailedLogCountAsync(good));

        // A registered file is never left with an asset row and no identity job.
        var corruptAssets = await run.AssetCountAsync(corrupt);
        var corruptJobs = await run.IdentityJobCountAsync(corrupt);
        Assert.False(corruptAssets > 0 && corruptJobs == 0, "the corrupt video was left registered with no identity job");
        Assert.Equal(1, await run.ReviewCountAsync(corrupt, MediaInspectionFailedTrigger));
        Assert.Equal(0, await run.HoldCountAsync(corrupt));
        Assert.Equal(0, await run.BatchFilesFailedAsync());
    }

    [Fact]
    public async Task A_corrupt_video_review_item_is_visible_in_needs_review_with_a_plain_reason()
    {
        var run = await ScanAsync(
            FakeFFmpegMode.Available,
            new SeedFile("Corrupt.2020.mkv", MediaType.Movies, Corrupt: true));

        var corrupt = run.Files.Single();
        var (readyAt, detail) = await run.ReviewFactsAsync(corrupt, MediaInspectionFailedTrigger);

        Assert.NotNull(readyAt);
        Assert.Equal("This file couldn't be read. It may be damaged or incomplete.", detail);
    }

    [Fact]
    public async Task Files_waiting_for_the_media_tool_are_inspected_when_it_appears_and_their_holds_clear()
    {
        var run = await ScanAsync(
            FakeFFmpegMode.Unavailable,
            new SeedFile("Dune.2021.mkv", MediaType.Movies),
            new SeedFile("Wannabe.mp3", MediaType.Music));
        Assert.Equal(2, await run.TotalHoldCountAsync());

        run.Ffmpeg.SetMode(FakeFFmpegMode.Available);
        await run.ResumeAsync(new MediaToolAvailability(true, false));

        Assert.Equal(0, await run.TotalHoldCountAsync());
        Assert.Equal(2, await run.InspectionCacheCountAsync());
        Assert.Equal(2, run.Ffmpeg.ProbedPaths.Count);
    }

    [Fact]
    public async Task Files_waiting_for_the_media_tool_stay_held_when_a_check_still_finds_no_tool()
    {
        var run = await ScanAsync(FakeFFmpegMode.Unavailable, new SeedFile("Dune.2021.mkv", MediaType.Movies));

        await run.ResumeAsync(new MediaToolAvailability(false, false));

        Assert.Equal(1, await run.TotalHoldCountAsync());
        Assert.Equal(0, await run.InspectionCacheCountAsync());
    }

    [Fact]
    public async Task A_waiting_file_that_the_tool_cannot_read_goes_to_needs_review_and_its_hold_clears()
    {
        var run = await ScanAsync(
            FakeFFmpegMode.Unavailable,
            new SeedFile("Corrupt.2020.mkv", MediaType.Movies, Corrupt: true),
            new SeedFile("Good.2021.mkv", MediaType.Movies));
        var corrupt = run.Files.Single(f => f.EndsWith("Corrupt.2020.mkv", StringComparison.Ordinal));

        run.Ffmpeg.SetMode(FakeFFmpegMode.Available);
        await run.ResumeAsync(new MediaToolAvailability(true, false));

        Assert.Equal(0, await run.TotalHoldCountAsync());
        Assert.Equal(1, await run.ReviewCountAsync(corrupt, MediaInspectionFailedTrigger));
        Assert.Equal(1, await run.InspectionCacheCountAsync());
    }

    [Fact]
    public async Task A_second_resume_while_one_is_running_returns_without_doing_anything()
    {
        var run = await ScanAsync(
            FakeFFmpegMode.Unavailable,
            new SeedFile("Dune.2021.mkv", MediaType.Movies),
            new SeedFile("Wannabe.mp3", MediaType.Music));
        run.Ffmpeg.SetMode(FakeFFmpegMode.Available);
        run.Ffmpeg.PauseNextProbe();
        var resumer = run.CreateResumer();

        await resumer.OnMediaToolsCheckedAsync(new MediaToolAvailability(true, false), CancellationToken.None);
        await run.Ffmpeg.WaitUntilProbeStartedAsync();
        var second = resumer.OnMediaToolsCheckedAsync(new MediaToolAvailability(true, false), CancellationToken.None);

        Assert.True(second.IsCompletedSuccessfully);
        Assert.Single(run.Ffmpeg.ProbedPaths);

        run.Ffmpeg.ReleaseProbe();
        await resumer.WhenIdleAsync();

        Assert.Equal(0, await run.TotalHoldCountAsync());
        Assert.Equal(2, run.Ffmpeg.ProbedPaths.Count);
    }

    [Fact]
    public async Task A_cancelled_resume_stops_and_leaves_the_remaining_files_held()
    {
        var run = await ScanAsync(
            FakeFFmpegMode.Unavailable,
            new SeedFile("Dune.2021.mkv", MediaType.Movies),
            new SeedFile("Wannabe.mp3", MediaType.Music));
        run.Ffmpeg.SetMode(FakeFFmpegMode.Available);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await run.ResumeAsync(new MediaToolAvailability(true, false), cancelled.Token);

        Assert.Equal(2, await run.TotalHoldCountAsync());
        Assert.Empty(run.Ffmpeg.ProbedPaths);
    }

    [Fact]
    public async Task Rereading_a_file_while_the_media_tool_is_missing_succeeds_and_keeps_it_waiting()
    {
        var run = await ScanAsync(FakeFFmpegMode.Unavailable, new SeedFile("Dune.2021.mkv", MediaType.Movies));
        var asset = await run.LoadAssetAsync(run.Files.Single());

        var outcome = await run.CreateWriter().InspectAsync(asset, CancellationToken.None);

        Assert.Equal(PlaybackInspectionOutcome.WaitingForTool, outcome);
        Assert.Equal(1, await run.TotalHoldCountAsync());
    }

    [Fact]
    public async Task A_file_that_failed_the_old_way_is_queued_and_held_when_it_is_scanned_again()
    {
        // The old failure left a registered file with no identity job, no hold, and its operation still "running".
        // A restart picks up unfinished operations and repairs the file through the same-path branch, which now
        // carries on past inspection instead of failing again.
        var run = await ScanAsync(
            FakeFFmpegMode.Unavailable,
            new SeedFile("Dune.2021.mkv", MediaType.Movies));
        var movie = run.Files.Single();
        await run.LeaveInTheOldFailedStateAsync(movie);
        Assert.Equal(0, await run.IdentityJobCountAsync(movie));
        Assert.Equal(0, await run.HoldCountAsync(movie));

        await RunEngineAsync(run, () => run.FileWasRedetectedAsync(movie));

        Assert.Equal(1, await run.IdentityJobCountAsync(movie));
        Assert.Equal(1, await run.HoldCountAsync(movie));
        Assert.Equal(0, await run.FailedLogCountAsync(movie));
    }

    [Fact]
    public async Task A_failed_file_closes_its_operation_instead_of_leaving_it_running()
    {
        // The hold store failing is an unexpected error in the middle of the file's steps.
        var run = await ScanAsync(FakeFFmpegMode.Unavailable, new ThrowingHoldRepository(), new SeedFile("Boom.2021.mkv", MediaType.Movies));
        var file = run.Files.Single();

        Assert.True(await run.FailedLogCountAsync(file) > 0);
        Assert.Equal(0, await run.RunningOperationCountAsync(file));
        Assert.Equal(1, await run.FailedTerminalOperationCountAsync(file));
    }

    // -- Harness -----------------------------------------------------------

    private sealed record SeedFile(string Name, MediaType Type, bool Corrupt = false);

    /// <summary>A hold store that fails: an unexpected error in the middle of a file's steps.</summary>
    private sealed class ThrowingHoldRepository : IMediaInspectionHoldRepository
    {
        public Task UpsertAsync(Guid assetId, string toolId, DateTimeOffset heldAt, CancellationToken ct = default)
            => throw new InvalidOperationException("The hold store is unavailable.");

        public Task DeleteAsync(Guid assetId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Guid>> ListAssetIdsByToolAsync(string toolId, int limit, int skip = 0, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<int> CountByToolAsync(string toolId, CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> ExistsAsync(Guid assetId, CancellationToken ct = default) => Task.FromResult(false);
    }

    private Task<ScanResult> ScanAsync(FakeFFmpegMode mode, params SeedFile[] seeds)
        => ScanAsync(mode, null, seeds);

    private async Task<ScanResult> ScanAsync(FakeFFmpegMode mode, IMediaInspectionHoldRepository? holdsOverride, params SeedFile[] seeds)
    {
        var h = new ImportFolderHarness();
        _disposables.Add(h);
        var ffmpeg = new FakeFFmpegService(mode);
        var processors = new TestProcessorLibraryItem();
        var libraries = new Dictionary<MediaType, LibraryFolderConfig>();
        var files = new List<string>();

        foreach (var seed in seeds)
        {
            var folder = Path.Combine(h.AsIsDir, seed.Type.ToString());
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, seed.Name);
            File.WriteAllText(path, $"bytes of {seed.Name}");
            files.Add(path);
            if (seed.Corrupt)
            {
                ffmpeg.CorruptFileNames.Add(seed.Name);
            }

            processors.SetResultForPath(path, new ProcessorResult
            {
                FilePath = path,
                DetectedType = seed.Type,
                Claims = [new ExtractedClaim { Key = "title", Value = Path.GetFileNameWithoutExtension(seed.Name), Confidence = 0.9 }],
            });

            if (!libraries.ContainsKey(seed.Type))
            {
                var index = libraries.Count + 1;
                libraries[seed.Type] = ImportFolderHarness.NewLibrary(
                    $"cccccccc-0000-4000-8000-00000000000{index}",
                    seed.Type.ToString(),
                    seed.Type.ToString(),
                    destinationId: null,
                    ImportFolderHarness.AsIs($"asis-{seed.Type}", folder));
            }
        }

        var options = new IngestionOptions
        {
            LibraryRoot = h.LibraryRoot,
            AutoOrganize = true,
            DuplicatePolicy = h.Options.DuplicatePolicy,
            ImportFolderPath = h.ImportDir,
            LibraryFolders = h.Config.Libraries.Libraries.Concat(libraries.Values).Select(LibraryFolderEntryFactory.Create).ToList(),
            PollIntervalSeconds = 0,
            IncludeSubdirectories = true,
            FswQuietPeriodSeconds = 1,
            LockProbeRetryMaxAttempts = 10,
            LockProbeRetryBaseDelaySeconds = 1,
        };

        var result = new ScanResult(h, files, ffmpeg, processors, options, holdsOverride);
        await RunEngineAsync(result, result.AllFilesSettledAsync);
        return result;
    }

    /// <summary>Starts an ingestion engine over the run's folders and data store and waits until <paramref name="settled"/> is true.</summary>
    private async Task RunEngineAsync(ScanResult result, Func<Task<bool>> settled)
    {
        var h = result.Harness;
        var db = h.Db.Connection;
        var publisher = new StubEventPublisher();
        var workRepo = new WorkRepository(db);
        var operationRepo = new MediaOperationRepository(db);
        var tracker = new MediaOperationTracker(
            operationRepo,
            new MediaOperationEventRepository(db),
            publisher,
            NullLogger<MediaOperationTracker>.Instance);
        var debounce = new DebounceQueue(new DebounceOptions
        {
            SettleDelay = TimeSpan.FromMilliseconds(1),
            ProbeInterval = TimeSpan.FromMilliseconds(20),
            MaxProbeAttempts = 2,
            MaxProbeDelay = TimeSpan.FromMilliseconds(50),
        });
        _disposables.Add(debounce);
        var monitor = new ReloadableOptionsMonitor<IngestionOptions>(result.Options);

        var engine = new IngestionEngine(
            new RecordingFileWatcher(),
            debounce,
            new TestAssetHasher(),
            result.Processors,
            new PriorityCascadeEngine(new StubConfigurationLoader(), NullLogger<PriorityCascadeEngine>.Instance),
            new StubFileOrganizer(),
            Enumerable.Empty<IMetadataTagger>(),
            h.Assets,
            new InlineBackgroundWorker(),
            publisher,
            Microsoft.Extensions.Options.Options.Create(result.Options),
            NullLogger<IngestionEngine>.Instance,
            new MetadataClaimRepository(db),
            h.Canonicals,
            new StubRecursiveIdentity(),
            new MediaEntityChainFactory(db, new HierarchyResolver(workRepo, null, new BookFormatSiblingFinder(db))),
            h.Reviews,
            h.Activity,
            new StubReconciliation(),
            new OrganizationGate(new ScoringConfiguration()),
            new IngestionLogRepository(db),
            new StubSmartLabeler(),
            new StubMediaTypeAdvisor(),
            new StubEntityTimelineRepository(),
            new ScoringConfiguration(),
            new IngestionBatchRepository(db),
            h.Jobs,
            new MediaTypeResolver(
                monitor,
                new LibraryFolderResolver(monitor),
                new StubMediaTypeAdvisor(),
                new MediaTypeExtensionCatalog(new StubConfigurationLoader()),
                NullLogger<MediaTypeResolver>.Instance,
                h.Importer),
            new DuplicateResolver(h.Assets),
            new IngestionLogScribe(new IngestionLogRepository(db), publisher, NullLogger<IngestionLogScribe>.Instance),
            new HashDedupeStageDependencies(null),
            new ScoreIdentifyStageDependencies(null),
            new OrganizeStageDependencies(null),
            new WriteBackStageDependencies(new EntityAssetRepository(db), new AssetPathService(h.LibraryRoot), workRepo, null),
            new IdentityJobStageDependencies(new IdentityPipelineSignal()),
            operationTracker: tracker,
            operationRepository: operationRepo,
            libraryFolderResolver: new LibraryFolderResolver(monitor),
            sourceMutationPolicyGate: new SourceMutationPolicyGate(),
            playbackInspection: result.CreateWriter(),
            importFolders: h.Importer,
            importFiler: h.Filer,
            optionsMonitor: monitor,
            inspectionReviews: result.CreateRecorder());
        _disposables.Add(engine);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await engine.StartAsync(cts.Token);
            // Every scanned file ends in a terminal ingestion state; wait for the whole batch, not just the first file.
            while (!cts.IsCancellationRequested && !await settled())
            {
                await Task.Delay(100, cts.Token);
            }

            await Task.Delay(500, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The wait ran out; the assertions say what was (not) stored.
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await engine.StopAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
                // A slow stop is acceptable in a test.
            }
        }
    }

    private sealed class ScanResult(
        ImportFolderHarness h,
        IReadOnlyList<string> files,
        FakeFFmpegService ffmpeg,
        TestProcessorLibraryItem processors,
        IngestionOptions options,
        IMediaInspectionHoldRepository? holdsOverride)
    {
        private readonly MediaInspectionHoldRepository _realHolds = new(h.Db.Connection);

        public ImportFolderHarness Harness => h;

        public IReadOnlyList<string> Files { get; } = files;

        public FakeFFmpegService Ffmpeg { get; } = ffmpeg;

        public TestProcessorLibraryItem Processors { get; } = processors;

        public IngestionOptions Options { get; } = options;

        public PlaybackInspectionWriter CreateWriter()
            => new(Ffmpeg, new PlaybackStateRepository(h.Db.Connection), holdsOverride ?? _realHolds, NullLogger<PlaybackInspectionWriter>.Instance);

        public MediaInspectionReviewRecorder CreateRecorder()
            => new(h.Reviews, holdsOverride ?? _realHolds, NullLogger<MediaInspectionReviewRecorder>.Instance);

        public async Task ResumeAsync(MediaToolAvailability availability, CancellationToken ct = default)
        {
            var resumer = CreateResumer();
            await resumer.OnMediaToolsCheckedAsync(availability, ct);
            await resumer.WhenIdleAsync();
        }

        public PendingMediaInspectionResumer CreateResumer()
            => new(_realHolds, h.Assets, CreateWriter(), CreateRecorder(), NullLogger<PendingMediaInspectionResumer>.Instance);

        public async Task<MediaEngine.Domain.Aggregates.MediaAsset> LoadAssetAsync(string file)
            => await h.Assets.FindByPathRootAsync(file) ?? throw new InvalidOperationException($"No asset is registered for {file}.");

        public async Task<bool> AllFilesSettledAsync()
        {
            using var connection = h.Db.Connection.CreateConnection();
            foreach (var file in Files)
            {
                var settled = await connection.ExecuteScalarAsync<int>(
                    """
                    SELECT COUNT(*) FROM ingestion_log
                    WHERE file_path = @file AND status IN ('failed', 'queued_identity', 'duplicate', 'no_match', 'review');
                    """,
                    new { file });
                if (settled == 0)
                {
                    return false;
                }
            }

            return true;
        }

        public async Task<bool> FileWasRedetectedAsync(string file)
        {
            using var connection = h.Db.Connection.CreateConnection();
            return await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM ingestion_log WHERE file_path = @file AND status = 'same_path_redetected';", new { file }) > 0;
        }

        /// <summary>What the old failure left behind: the asset row stays, but no identity job, no hold, and an unfinished operation.</summary>
        public async Task LeaveInTheOldFailedStateAsync(string file)
        {
            using var connection = h.Db.Connection.CreateConnection();
            await connection.ExecuteAsync(
                "DELETE FROM identity_jobs WHERE entity_id IN (SELECT id FROM media_assets WHERE file_path_root = @file);", new { file });
            await connection.ExecuteAsync(
                "DELETE FROM media_inspection_holds WHERE asset_id IN (SELECT id FROM media_assets WHERE file_path_root = @file);", new { file });
            await connection.ExecuteAsync(
                "UPDATE media_operations SET status = 'running', stage = 'scoring', completed_at = NULL WHERE source_path = @path;",
                new { path = Path.GetFullPath(file) });
        }

        public async Task<int> FailedLogCountAsync(string file)
        {
            using var connection = h.Db.Connection.CreateConnection();
            return await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM ingestion_log WHERE file_path = @file AND status = 'failed';", new { file });
        }

        public async Task<int> AssetCountAsync(string file)
        {
            using var connection = h.Db.Connection.CreateConnection();
            return await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM media_assets WHERE file_path_root = @file;", new { file });
        }

        public async Task<int> IdentityJobCountAsync(string file)
        {
            using var connection = h.Db.Connection.CreateConnection();
            return await connection.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM identity_jobs
                WHERE entity_id IN (SELECT id FROM media_assets WHERE file_path_root = @file);
                """,
                new { file });
        }

        public async Task<int> HoldCountAsync(string file)
        {
            using var connection = h.Db.Connection.CreateConnection();
            return await connection.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM media_inspection_holds
                WHERE asset_id IN (SELECT id FROM media_assets WHERE file_path_root = @file);
                """,
                new { file });
        }

        public async Task<int> TotalHoldCountAsync()
        {
            using var connection = h.Db.Connection.CreateConnection();
            return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM media_inspection_holds;");
        }

        public async Task<int> InspectionCacheCountAsync()
        {
            using var connection = h.Db.Connection.CreateConnection();
            return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM playback_inspection_cache;");
        }

        public async Task<int> ReviewCountAsync(string file, string trigger)
        {
            using var connection = h.Db.Connection.CreateConnection();
            return await connection.ExecuteScalarAsync<int>(
                """
                SELECT COUNT(*) FROM review_queue
                WHERE trigger = @trigger
                  AND entity_id IN (SELECT id FROM media_assets WHERE file_path_root = @file);
                """,
                new { file, trigger });
        }

        public async Task<(string? ReadyAt, string? Detail)> ReviewFactsAsync(string file, string trigger)
        {
            using var connection = h.Db.Connection.CreateConnection();
            var row = await connection.QuerySingleAsync<(string? ReadyAt, string? Detail)>(
                """
                SELECT review_ready_at AS ReadyAt, detail AS Detail FROM review_queue
                WHERE trigger = @trigger
                  AND entity_id IN (SELECT id FROM media_assets WHERE file_path_root = @file);
                """,
                new { file, trigger });
            return row;
        }

        public async Task<int> RunningOperationCountAsync(string file)
        {
            using var connection = h.Db.Connection.CreateConnection();
            return await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM media_operations WHERE source_path = @path AND completed_at IS NULL;",
                new { path = Path.GetFullPath(file) });
        }

        public async Task<int> FailedTerminalOperationCountAsync(string file)
        {
            using var connection = h.Db.Connection.CreateConnection();
            return await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM media_operations WHERE source_path = @path AND status = 'failed_terminal';",
                new { path = Path.GetFullPath(file) });
        }

        public async Task<int> BatchFilesFailedAsync()
        {
            using var connection = h.Db.Connection.CreateConnection();
            return await connection.ExecuteScalarAsync<int>("SELECT COALESCE(SUM(files_failed), 0) FROM ingestion_batches;");
        }
    }
}
