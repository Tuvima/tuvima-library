using Dapper;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
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
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// The Import folder through the real ingestion engine: a watch of the folder, the media type prior from the per-type
/// folder, classification of a file at the root, the global duplicate rule, and a file that is still being written.
/// </summary>
public sealed class ImportFolderPipelineTests : IDisposable
{
    private readonly List<ImportFolderHarness> _harnesses = [];

    public void Dispose()
    {
        foreach (var harness in _harnesses)
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task A_file_in_the_Movies_folder_takes_the_Movies_type_and_library_from_its_folder()
    {
        var h = NewHarness();
        var path = h.WriteImportFile(Path.Combine("Movies", "Dune.2021.mkv"), "dune");
        var processors = new TestProcessorLibraryItem();
        processors.SetResultForPath(path, new ProcessorResult
        {
            FilePath = path,
            DetectedType = MediaType.Unknown,
            Claims = [new ExtractedClaim { Key = "title", Value = "Dune", Confidence = 0.9 }],
        });

        await RunEngineAsync(h, processors, () => HasAssetsAsync(h, 1));

        var asset = await h.Assets.FindByPathRootAsync(path);
        Assert.NotNull(asset);
        Assert.Equal(ImportFolderHarness.MoviesLibraryId, asset.LibraryId);
        var canonicals = await h.Canonicals.GetByEntityAsync(asset.Id);
        Assert.Equal("Movies", canonicals.First(c => c.Key == "media_type").Value);
        Assert.True(File.Exists(path), "the file stays put until its identity work is done");
    }

    [Fact]
    public async Task A_file_at_the_root_of_the_Import_folder_is_classified_first()
    {
        var h = NewHarness();
        var path = h.WriteImportFile("Dune.epub", "dune book");
        var processors = new TestProcessorLibraryItem();
        processors.SetResultForPath(path, new ProcessorResult
        {
            FilePath = path,
            DetectedType = MediaType.Books,
            Claims = [new ExtractedClaim { Key = "title", Value = "Dune", Confidence = 0.9 }],
        });

        await RunEngineAsync(h, processors, () => HasAssetsAsync(h, 1));

        var asset = await h.Assets.FindByPathRootAsync(path);
        Assert.NotNull(asset);
        var canonicals = await h.Canonicals.GetByEntityAsync(asset.Id);
        Assert.Equal("Books", canonicals.First(c => c.Key == "media_type").Value);
    }

    [Fact]
    public async Task A_root_file_with_an_ambiguous_extension_goes_to_Review_and_stays_in_the_Import_folder()
    {
        var h = NewHarness();
        var path = h.WriteImportFile("Dune.mkv", "dune");
        var processors = new TestProcessorLibraryItem();
        processors.SetResultForPath(path, new ProcessorResult
        {
            FilePath = path,
            DetectedType = MediaType.Movies,
            MediaTypeCandidates =
            [
                new MediaTypeCandidate { Type = MediaType.Movies, Confidence = 0.8, Reason = "video" },
                new MediaTypeCandidate { Type = MediaType.TV, Confidence = 0.7, Reason = "video" },
            ],
            Claims = [new ExtractedClaim { Key = "title", Value = "Dune", Confidence = 0.9 }],
        });

        await RunEngineAsync(h, processors, () => HasAssetsAsync(h, 1));

        var asset = await h.Assets.FindByPathRootAsync(path);
        Assert.NotNull(asset);
        var reviews = await h.Reviews.GetPendingByEntityAsync(asset.Id);
        Assert.Contains(reviews, review => review.Trigger == ReviewTrigger.RootWatchFolder);

        // Identity matching could still settle the type and clear this on its own; once it is handed to a person it blocks filing.
        await h.Reviews.MarkPendingReadyByEntityAsync(asset.Id);
        await h.Organizer.TryAutoOrganizeAsync(asset.Id);
        Assert.True(File.Exists(path));
        Assert.True(ImportFolderRules.IsInside(h.ImportDir, asset.FilePathRoot));
    }

    [Fact]
    public async Task Keep_one_copy_removes_only_the_import_copy_of_an_exact_duplicate()
    {
        var h = NewHarness(LibraryDuplicatePolicies.KeepOne);
        var (held, _) = await SeedHeldCopyAsync(h, "same bytes");
        var copy = h.WriteImportFile(Path.Combine("Movies", "Dune Copy.mkv"), "same bytes");
        var processors = new TestProcessorLibraryItem();

        await RunEngineAsync(h, processors, () => Task.FromResult(!File.Exists(copy)));

        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(held), "the copy already held is never deleted");
        Assert.True(Directory.Exists(Path.Combine(h.ImportDir, "Movies")));
        Assert.Equal(1, await AssetCountAsync(h));
        Assert.Single(h.FilesUnder(h.MoviesDir));
    }

    // ── Originals left behind by a cancelled merge ────────────────────────

    [Fact]
    public async Task Keep_one_copy_leaves_the_original_of_a_cancelled_merge_on_disk_beside_its_copy()
    {
        var h = NewHarness(LibraryDuplicatePolicies.KeepOne);
        var (held, _) = await SeedHeldCopyAsync(h, "same bytes");
        // The merged folder is a plain managed folder again after the cancel; its original sits beside the copy the merge made.
        var original = Path.Combine(h.ElsewhereDir, "Dune.2021.mkv");
        File.WriteAllText(original, "same bytes");
        h.Options.WatchDirectories = [.. h.Options.WatchDirectories, h.ElsewhereDir];
        await new FolderOrganiseLogRepository(h.Db.Connection).UpsertAsync(new FolderOrganiseLogEntry(
            Guid.NewGuid(), Guid.NewGuid(), original, held, FolderOrganiseOutcomes.Copied, DateTimeOffset.UtcNow, null));

        await RunEngineAsync(
            h, new TestProcessorLibraryItem(), () => HasIngestionStatusAsync(h, "duplicate"), withMergeLedger: true);

        Assert.True(File.Exists(original), "the original of a merge is never removed as a spare copy");
        Assert.True(File.Exists(held));
        Assert.Equal(1, await AssetCountAsync(h));
        using var connection = h.Db.Connection.CreateConnection();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM review_queue;"));
    }

    [Fact]
    public async Task Keep_one_copy_still_removes_an_ordinary_duplicate_that_no_merge_listed()
    {
        var h = NewHarness(LibraryDuplicatePolicies.KeepOne);
        var (held, _) = await SeedHeldCopyAsync(h, "same bytes");
        var spare = Path.Combine(h.ElsewhereDir, "Dune.2021.mkv");
        File.WriteAllText(spare, "same bytes");
        h.Options.WatchDirectories = [.. h.Options.WatchDirectories, h.ElsewhereDir];

        await RunEngineAsync(
            h, new TestProcessorLibraryItem(), () => Task.FromResult(!File.Exists(spare)), withMergeLedger: true);

        Assert.False(File.Exists(spare));
        Assert.True(File.Exists(held));
        Assert.Equal(1, await AssetCountAsync(h));
    }

    [Fact]
    public async Task Keep_both_files_the_import_copy_beside_the_held_copy_with_a_unique_name()
    {
        var h = NewHarness(LibraryDuplicatePolicies.KeepBoth);
        var (held, _) = await SeedHeldCopyAsync(h, "same bytes");
        var copy = h.WriteImportFile(Path.Combine("Movies", "Dune Copy.mkv"), "same bytes");
        var processors = new TestProcessorLibraryItem();

        await RunEngineAsync(h, processors, () => Task.FromResult(!File.Exists(copy)));

        Assert.False(File.Exists(copy));
        Assert.True(File.Exists(held));
        var files = h.FilesUnder(h.MoviesDir).ToList();
        Assert.Equal(2, files.Count);
        Assert.Equal(2, files.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(files, file => Assert.Equal("same bytes", File.ReadAllText(file)));
        Assert.Equal(1, await AssetCountAsync(h));
    }

    [Fact]
    public async Task Keep_both_leaves_the_import_copy_alone_until_its_destination_is_ready_and_then_files_it()
    {
        var h = NewHarness(LibraryDuplicatePolicies.KeepBoth);
        var (held, heldId) = await SeedHeldCopyAsync(h, "same bytes", h.ElsewhereDir);
        Directory.Delete(h.MoviesDir); // the New media folder is offline
        var copy = h.WriteImportFile(Path.Combine("Movies", "Dune Copy.mkv"), "same bytes");
        var processors = new TestProcessorLibraryItem();

        await RunEngineAsync(h, processors, () => Task.FromResult(false), timeout: TimeSpan.FromSeconds(3));

        Assert.True(File.Exists(copy));
        Assert.False(Directory.Exists(h.MoviesDir), "nothing is created on a folder that is away");
        Assert.Equal(held, Assert.Single(h.FilesUnder(h.ElsewhereDir)));

        Directory.CreateDirectory(h.MoviesDir);
        var hash = (await h.Hasher.ComputeAsync(copy)).Hex;
        Assert.NotNull(await h.Filer.FileDuplicateCopyAsync(copy, heldId, hash));

        Assert.False(File.Exists(copy));
        Assert.Equal("same bytes", File.ReadAllText(Assert.Single(h.FilesUnder(h.MoviesDir))));
        Assert.Equal(1, await AssetCountAsync(h));
    }

    [Fact]
    public async Task Keep_one_copy_retries_when_the_import_copy_is_locked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Only Windows refuses to delete a file that is open; elsewhere the delete simply succeeds.
        }

        var h = NewHarness(LibraryDuplicatePolicies.KeepOne);
        await SeedHeldCopyAsync(h, "same bytes");
        var copy = h.WriteImportFile(Path.Combine("Movies", "Dune Copy.mkv"), "same bytes");
        var processors = new TestProcessorLibraryItem();

        using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await RunEngineAsync(h, processors, () => Task.FromResult(false), timeout: TimeSpan.FromSeconds(4));
            Assert.True(File.Exists(copy), "a locked copy is not removed");
        }

        // The operation stayed open, so the next pass removes it once the lock is gone.
        await RunEngineAsync(h, processors, () => Task.FromResult(!File.Exists(copy)));
        Assert.False(File.Exists(copy));
        Assert.Equal(1, await AssetCountAsync(h));
    }

    [Fact]
    public async Task A_file_still_being_written_is_not_picked_up_until_it_has_settled()
    {
        var h = NewHarness();
        var path = Path.Combine(h.ImportDir, "Movies", "Big.Film.2021.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var processors = new TestProcessorLibraryItem();
        processors.SetResultForPath(path, new ProcessorResult
        {
            FilePath = path,
            DetectedType = MediaType.Movies,
            Claims = [new ExtractedClaim { Key = "title", Value = "Big Film", Confidence = 0.9 }],
        });

        var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        try
        {
            await writer.WriteAsync(new byte[1024]);
            await writer.FlushAsync();

            var released = false;
            var seenWhileWriting = false;
            await RunEngineAsync(
                h,
                processors,
                async () =>
                {
                    if (!released)
                    {
                        seenWhileWriting |= await AssetCountAsync(h) > 0;
                        if (seenWhileWriting)
                        {
                            return true;
                        }

                        await Task.Delay(900);
                        await writer.WriteAsync(new byte[1024]);
                        await writer.DisposeAsync();
                        released = true;
                        return false;
                    }

                    return await AssetCountAsync(h) > 0;
                },
                timeout: TimeSpan.FromSeconds(30));

            Assert.False(seenWhileWriting, "a file that is still being written must not be registered");
            Assert.Equal(1, await AssetCountAsync(h));
        }
        finally
        {
            await writer.DisposeAsync();
        }
    }

    [Fact]
    public async Task Working_files_in_the_import_data_folder_are_never_ingested()
    {
        var h = NewHarness();
        h.WriteImportFile(Path.Combine(".data", "staging", "half.mkv"), "temporary");
        var processors = new TestProcessorLibraryItem();

        await RunEngineAsync(h, processors, () => Task.FromResult(false), timeout: TimeSpan.FromSeconds(2));

        Assert.Equal(0, await AssetCountAsync(h));
    }

    // ── Harness ───────────────────────────────────────────────────────────

    private ImportFolderHarness NewHarness(string duplicatePolicy = LibraryDuplicatePolicies.KeepOne)
    {
        var harness = new ImportFolderHarness(duplicatePolicy);
        _harnesses.Add(harness);
        return harness;
    }

    /// <summary>A file the library already holds (registered and filed), so a copy dropped in Import is an exact duplicate.</summary>
    private static async Task<(string Path, Guid AssetId)> SeedHeldCopyAsync(
        ImportFolderHarness h, string content, string? folder = null)
    {
        var held = Path.Combine(folder ?? h.MoviesDir, "Movies", "Dune (2021)", "Dune (2021).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(held)!);
        File.WriteAllText(held, content);
        var id = await h.RegisterAsync(held, MediaType.Movies, "Dune", "2021");
        return (held, id);
    }

    private static async Task<int> AssetCountAsync(ImportFolderHarness h)
    {
        using var connection = h.Db.Connection.CreateConnection();
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM media_assets;");
    }

    private static async Task<bool> HasIngestionStatusAsync(ImportFolderHarness h, string status)
    {
        using var connection = h.Db.Connection.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM ingestion_log WHERE status = @status;", new { status }) > 0;
    }

    private static async Task<bool> HasAssetsAsync(ImportFolderHarness h, int expected)
        => await AssetCountAsync(h) >= expected;

    private async Task RunEngineAsync(
        ImportFolderHarness h,
        TestProcessorLibraryItem processors,
        Func<Task<bool>> done,
        TimeSpan? timeout = null,
        bool withMergeLedger = false)
    {
        var db = h.Db.Connection;
        var publisher = new StubEventPublisher();
        var workRepo = new WorkRepository(db);
        var operationRepo = new MediaOperationRepository(db);
        var tracker = new MediaOperationTracker(
            operationRepo,
            new MediaOperationEventRepository(db),
            publisher,
            NullLogger<MediaOperationTracker>.Instance);
        var options = h.Options;
        options.LockProbeRetryMaxAttempts = 10;
        options.LockProbeRetryBaseDelaySeconds = 1;

        using var debounce = new DebounceQueue(new DebounceOptions
        {
            SettleDelay = TimeSpan.FromMilliseconds(1),
            ProbeInterval = TimeSpan.FromMilliseconds(20),
            MaxProbeAttempts = 2,
            MaxProbeDelay = TimeSpan.FromMilliseconds(50),
        });
        var monitor = new OptionsMonitorStub<IngestionOptions>(options);
        var assetHasher = new TestAssetHasher();
        // The running Engine's gate reads the merge ledger; the Import tests keep the plain gate they always had.
        var gate = new SourceMutationPolicyGate(withMergeLedger ? new FolderOrganiseLogRepository(db) : null);

        using var engine = new IngestionEngine(
            new StubFileWatcher(),
            debounce,
            assetHasher,
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
            sourceMutationPolicyGate: gate,
            importFolders: h.Importer,
            importFiler: h.Filer);

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
        try
        {
            await engine.StartAsync(cts.Token);
            while (!cts.IsCancellationRequested && !await done())
            {
                await Task.Delay(100, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The wait ran out; the assertions in the test say whether that was expected.
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
}
