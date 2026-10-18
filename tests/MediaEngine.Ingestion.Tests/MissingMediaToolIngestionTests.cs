using Dapper;
using MediaEngine.Domain.Configuration;
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
    // ReviewTrigger.MediaInspectionFailed, added in WP3
    private const string MediaInspectionFailedTrigger = "MediaInspectionFailed";

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
    }

    // -- Harness -----------------------------------------------------------

    private sealed record SeedFile(string Name, MediaType Type, bool Corrupt = false);

    private async Task<ScanResult> ScanAsync(FakeFFmpegMode mode, params SeedFile[] seeds)
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
        var monitor = new ReloadableOptionsMonitor<IngestionOptions>(options);

        var engine = new IngestionEngine(
            new RecordingFileWatcher(),
            debounce,
            new TestAssetHasher(),
            processors,
            new PriorityCascadeEngine(new StubConfigurationLoader(), NullLogger<PriorityCascadeEngine>.Instance),
            new StubFileOrganizer(),
            Enumerable.Empty<IMetadataTagger>(),
            h.Assets,
            new InlineBackgroundWorker(),
            publisher,
            Microsoft.Extensions.Options.Options.Create(options),
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
            playbackInspection: new PlaybackInspectionWriter(ffmpeg, new PlaybackStateRepository(db)),
            importFolders: h.Importer,
            importFiler: h.Filer,
            optionsMonitor: monitor);
        _disposables.Add(engine);

        var result = new ScanResult(h, files, ffmpeg);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await engine.StartAsync(cts.Token);
            // Every scanned file ends in a terminal ingestion state; wait for the whole batch, not just the first file.
            while (!cts.IsCancellationRequested && !await result.AllFilesSettledAsync())
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

        return result;
    }

    private sealed class ScanResult(ImportFolderHarness h, IReadOnlyList<string> files, FakeFFmpegService ffmpeg)
    {
        public IReadOnlyList<string> Files { get; } = files;

        public FakeFFmpegService Ffmpeg { get; } = ffmpeg;

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

        public async Task<int> BatchFilesFailedAsync()
        {
            using var connection = h.Db.Connection.CreateConnection();
            return await connection.ExecuteScalarAsync<int>("SELECT COALESCE(SUM(files_failed), 0) FROM ingestion_batches;");
        }
    }
}
