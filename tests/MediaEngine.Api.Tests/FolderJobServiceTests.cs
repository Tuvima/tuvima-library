using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Services;
using MediaEngine.Intelligence.Models;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The folder job end to end on real folders and a real data store: one durable operation per conversion, a queue that
/// runs one at a time, pause / resume / cancel, resuming after the Engine restarts, and the log of every move.
/// </summary>
public sealed class FolderJobServiceTests : IDisposable
{
    private readonly FolderJobFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task Converting_a_folder_creates_one_durable_job_and_marks_the_folder_converting()
    {
        var folder = _f.AddAsIsFolder("Plex Movies", fileCount: 3);

        var status = await _f.Jobs.StartConversionAsync(folder.Id, default);

        Assert.Equal("queued", status.State);
        Assert.Equal(3, status.ItemsTotal);
        Assert.Equal(folder.Id, status.SourceId);
        var operation = await _f.Operations.GetByIdAsync(Guid.Parse(status.OperationId));
        Assert.NotNull(operation);
        Assert.Equal(MediaOperationType.OrganiseFolder, operation.OperationType);
        Assert.Equal(MediaOperationQueue.FolderJobs, operation.QueueName);
        Assert.Equal(Guid.Parse(folder.Id), operation.EntityId);

        var saved = _f.Source(folder.Id);
        Assert.Equal(LibrarySourceManagementModes.ManagedByTuvima, saved.ManagementMode);
        Assert.Equal(LibrarySourceAccessModes.Writable, saved.AccessMode);
        Assert.Equal(LibrarySourceConversionStates.Converting, saved.ConversionState);
        Assert.Equal(status.OperationId, saved.ConversionOperationId);
    }

    [Fact]
    public async Task Asking_again_while_a_job_is_open_returns_that_job_not_a_second_one()
    {
        var folder = _f.AddAsIsFolder("Plex Movies", fileCount: 2);

        var first = await _f.Jobs.StartConversionAsync(folder.Id, default);
        var second = await _f.Jobs.StartConversionAsync(folder.Id, default);

        Assert.Equal(first.OperationId, second.OperationId);
        Assert.Single(await _f.Operations.GetQueueAsync(MediaOperationQueue.FolderJobs, 50));
    }

    [Fact]
    public async Task A_second_conversion_waits_behind_the_first_and_says_which_folder_it_waits_for()
    {
        var first = _f.AddAsIsFolder("Library Movies", fileCount: 1);
        var second = _f.AddAsIsFolder("Plex Movies", fileCount: 1);
        var firstJob = await _f.Jobs.StartConversionAsync(first.Id, default);
        var secondJob = await _f.Jobs.StartConversionAsync(second.Id, default);

        // The worker takes one job at a time: the first is now running, the second still queued.
        var leased = await _f.Operations.LeaseNextAsync("test", [MediaOperationType.OrganiseFolder], 1, TimeSpan.FromMinutes(5));
        Assert.Equal(firstJob.OperationId, leased.Single().Id.ToString("D"));

        var waiting = await _f.Jobs.GetAsync(Guid.Parse(secondJob.OperationId), default);

        Assert.Equal("queued", waiting!.State);
        Assert.Equal("Library Movies", waiting.QueuedBehind);
        Assert.Equal("running", (await _f.Jobs.GetAsync(Guid.Parse(firstJob.OperationId), default))!.State);
    }

    [Fact]
    public async Task The_worker_runs_a_job_to_the_end_renames_the_files_and_the_folder_becomes_plain_Managed()
    {
        var folder = _f.AddAsIsFolder("Plex Movies", fileCount: 0);
        await _f.AddMovieAsync(folder, "raw dune.mkv", "Dune", "2021");
        await _f.AddMovieAsync(folder, "raw alien.mkv", "Alien", "1979");
        var job = await _f.Jobs.StartConversionAsync(folder.Id, default);

        await _f.RunWorkerUntilAsync(() => _f.IsFinishedAsync(job.OperationId));

        var done = await _f.Jobs.GetAsync(Guid.Parse(job.OperationId), default);
        Assert.Equal("completed", done!.State);
        Assert.Equal(2, done.ItemsCompleted);
        Assert.Equal(100, done.ProgressPercent);
        Assert.Equal("2 organised.", done.Message);
        Assert.True(done.HasLog);
        Assert.Equal(
            ["Movies/Alien (1979)/Alien (1979).mkv", "Movies/Dune (2021)/Dune (2021).mkv"],
            _f.FilesIn(folder));

        var saved = _f.Source(folder.Id);
        Assert.Equal(LibrarySourceConversionStates.Converted, saved.ConversionState);
        Assert.Equal(job.OperationId, saved.ConversionOperationId);

        // The log has every move, old path to new path, and exports as CSV.
        var log = await _f.Jobs.GetLogAsync(Guid.Parse(job.OperationId), 0, 100, default);
        Assert.Equal(2, log.Total);
        Assert.All(log.Entries, entry => Assert.Equal("moved", entry.Outcome));
        Assert.Contains(log.Entries, entry => entry.OldPath.EndsWith("raw dune.mkv", StringComparison.Ordinal)
            && entry.NewPath.EndsWith(Path.Combine("Dune (2021)", "Dune (2021).mkv"), StringComparison.Ordinal));
        using var csv = new MemoryStream();
        await _f.Jobs.WriteLogCsvAsync(Guid.Parse(job.OperationId), csv, default);
        var lines = System.Text.Encoding.UTF8.GetString(csv.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("﻿old_path,new_path,outcome,detail,at", lines[0]);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task A_paused_job_is_not_run_until_it_is_resumed()
    {
        var folder = _f.AddAsIsFolder("Plex Movies", fileCount: 0);
        await _f.AddMovieAsync(folder, "raw dune.mkv", "Dune", "2021");
        var job = await _f.Jobs.StartConversionAsync(folder.Id, default);

        var paused = await _f.Jobs.ControlAsync(Guid.Parse(job.OperationId), "pause", default);
        Assert.Equal("paused", paused.State);
        await _f.RunWorkerForAsync(TimeSpan.FromMilliseconds(600));
        Assert.Equal(["raw dune.mkv"], _f.FilesIn(folder));

        var resumed = await _f.Jobs.ControlAsync(Guid.Parse(job.OperationId), "resume", default);
        Assert.Equal("queued", resumed.State);
        await _f.RunWorkerUntilAsync(() => _f.IsFinishedAsync(job.OperationId));

        Assert.Equal(["Movies/Dune (2021)/Dune (2021).mkv"], _f.FilesIn(folder));
    }

    [Fact]
    public async Task Cancelling_a_job_that_has_not_started_keeps_the_folder_Managed_and_changes_no_file()
    {
        var folder = _f.AddAsIsFolder("Plex Movies", fileCount: 2);
        var job = await _f.Jobs.StartConversionAsync(folder.Id, default);

        var cancelled = await _f.Jobs.ControlAsync(Guid.Parse(job.OperationId), "cancel", default);

        Assert.Equal("cancelled", cancelled.State);
        Assert.Contains("the rest will be tidied gradually", cancelled.Message);
        var saved = _f.Source(folder.Id);
        Assert.Equal(LibrarySourceManagementModes.ManagedByTuvima, saved.ManagementMode);
        Assert.Equal(LibrarySourceConversionStates.Converted, saved.ConversionState);
        Assert.Equal(["file0.mkv", "file1.mkv"], _f.FilesIn(folder));
        await Assert.ThrowsAsync<FolderJobException>(() => _f.Jobs.ControlAsync(Guid.Parse(job.OperationId), "cancel", default));
    }

    [Fact]
    public async Task Cancelling_while_it_runs_stops_after_the_current_file_and_says_what_is_left()
    {
        var folder = _f.AddAsIsFolder("Plex Movies", fileCount: 0);
        for (var index = 0; index < 4; index++)
        {
            await _f.AddMovieAsync(folder, $"raw {index}.mkv", $"Film {index}", (2000 + index).ToString());
        }

        var job = await _f.Jobs.StartConversionAsync(folder.Id, default);
        var operationId = Guid.Parse(job.OperationId);
        var renames = 0;
        _f.Mover.AfterMove = (_, _) =>
        {
            if (++renames == 2)
            {
                _f.Jobs.ControlAsync(operationId, "cancel", default).GetAwaiter().GetResult();
            }
        };

        await _f.RunWorkerUntilAsync(() => _f.IsFinishedAsync(job.OperationId));

        var done = await _f.Jobs.GetAsync(operationId, default);
        Assert.Equal("cancelled", done!.State);
        Assert.Equal(2, _f.FilesIn(folder).Count(path => path.StartsWith("Movies/", StringComparison.Ordinal)));
        Assert.Equal(2, _f.FilesIn(folder).Count(path => path.StartsWith("raw ", StringComparison.Ordinal)));
        Assert.Equal(LibrarySourceConversionStates.Converted, _f.Source(folder.Id).ConversionState);
        Assert.Contains("Stopped after", done.Message);
    }

    [Fact]
    public async Task A_job_left_running_when_the_Engine_stopped_resumes_when_it_starts_again()
    {
        var folder = _f.AddAsIsFolder("Plex Movies", fileCount: 0);
        await _f.AddMovieAsync(folder, "raw dune.mkv", "Dune", "2021");
        await _f.AddMovieAsync(folder, "raw alien.mkv", "Alien", "1979");
        var job = await _f.Jobs.StartConversionAsync(folder.Id, default);

        // The Engine died with the job running: it holds a lease nobody will ever release.
        await _f.Operations.LeaseNextAsync("dead-engine", [MediaOperationType.OrganiseFolder], 1, TimeSpan.FromHours(1));
        await _f.Operations.UpdateStageAsync(Guid.Parse(job.OperationId), "organising", 10);
        Assert.Equal("running", (await _f.Jobs.GetAsync(Guid.Parse(job.OperationId), default))!.State);

        await _f.RunWorkerUntilAsync(() => _f.IsFinishedAsync(job.OperationId));

        Assert.Equal(
            ["Movies/Alien (1979)/Alien (1979).mkv", "Movies/Dune (2021)/Dune (2021).mkv"],
            _f.FilesIn(folder));
        Assert.Equal("completed", (await _f.Jobs.GetAsync(Guid.Parse(job.OperationId), default))!.State);
    }

    [Fact]
    public async Task Switching_the_folder_back_to_As_is_stops_the_job_and_leaves_every_file_where_it_is()
    {
        var folder = _f.AddAsIsFolder("Plex Movies", fileCount: 2);
        var job = await _f.Jobs.StartConversionAsync(folder.Id, default);

        // The person switches it back: saving the folder settings ends the conversion.
        var libraries = _f.Config.LoadLibraries();
        var source = libraries.Libraries[0].Sources.First(item => item.Id == folder.Id);
        source.ManagementMode = LibrarySourceManagementModes.ExistingLibrary;
        _f.Config.SaveLibraries(libraries);
        await _f.Jobs.StopJobsForUnmanagedFoldersAsync(libraries, default);

        var stopped = await _f.Jobs.GetAsync(Guid.Parse(job.OperationId), default);
        Assert.Equal("cancelled", stopped!.State);
        Assert.Contains("switched to As-is", stopped.Message);
        var saved = _f.Source(folder.Id);
        Assert.Equal(LibrarySourceConversionStates.None, saved.ConversionState);
        Assert.Null(saved.ConversionOperationId);
        await _f.RunWorkerForAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(["file0.mkv", "file1.mkv"], _f.FilesIn(folder));
        Assert.Empty(_f.Mover.Moves);
    }

    [Fact]
    public void A_saved_library_keeps_the_conversion_state_the_Engine_has_not_the_one_a_screen_last_read()
    {
        var current = new LibrariesConfiguration
        {
            Libraries =
            [
                new LibraryFolderConfig
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Sources =
                    [
                        new LibrarySourceConfig { Id = "a", ManagementMode = LibrarySourceManagementModes.ManagedByTuvima, ConversionState = LibrarySourceConversionStates.Converted, ConversionOperationId = "op" },
                    ],
                },
            ],
        };
        var proposed = new LibrariesConfiguration
        {
            Libraries =
            [
                new LibraryFolderConfig
                {
                    Id = current.Libraries[0].Id,
                    Sources =
                    [
                        new LibrarySourceConfig { Id = "a", ManagementMode = LibrarySourceManagementModes.ManagedByTuvima, ConversionState = LibrarySourceConversionStates.Converting, ConversionOperationId = "stale" },
                        new LibrarySourceConfig { Id = "new", ManagementMode = LibrarySourceManagementModes.ManagedByTuvima, ConversionState = LibrarySourceConversionStates.Converting, ConversionOperationId = "made-up" },
                        new LibrarySourceConfig { Id = "asis", ManagementMode = LibrarySourceManagementModes.ExistingLibrary, ConversionState = LibrarySourceConversionStates.Converting, ConversionOperationId = "x" },
                    ],
                },
            ],
        };

        FolderJobService.KeepEngineOwnedConversionState(current, proposed);

        var sources = proposed.Libraries[0].Sources;
        Assert.Equal((LibrarySourceConversionStates.Converted, "op"), (sources[0].ConversionState, sources[0].ConversionOperationId));
        Assert.Equal((LibrarySourceConversionStates.None, (string?)null), (sources[1].ConversionState, sources[1].ConversionOperationId));
        Assert.Equal((LibrarySourceConversionStates.None, (string?)null), (sources[2].ConversionState, sources[2].ConversionOperationId));
    }

    [Fact]
    public async Task A_folder_still_marked_converting_for_a_job_that_is_over_is_settled()
    {
        var folder = _f.AddAsIsFolder("Plex Movies", fileCount: 1);
        var job = await _f.Jobs.StartConversionAsync(folder.Id, default);
        // Cancelled through the general operations door, which knows nothing about folder settings.
        await _f.Operations.MarkCancelledAsync(Guid.Parse(job.OperationId), "Cancelled by user.");
        Assert.Equal(LibrarySourceConversionStates.Converting, _f.Source(folder.Id).ConversionState);

        await _f.Jobs.ReconcileAsync(default);

        Assert.Equal(LibrarySourceConversionStates.Converted, _f.Source(folder.Id).ConversionState);
    }

    [Fact]
    public async Task The_quick_preview_counts_files_without_changing_anything_and_the_estimate_uses_the_defaults()
    {
        var folder = _f.AddAsIsFolder("Plex Movies", fileCount: 4);

        var quick = await _f.Jobs.PreviewAsync(new FolderConversionPreviewRequest { SourceId = folder.Id, Quick = true }, default);

        Assert.Equal(4, quick.TotalFiles);
        Assert.Equal(4, quick.VideoFiles);
        Assert.Equal(20, quick.EstimatedSeconds); // about five seconds a video
        Assert.Equal("keep_one", quick.DuplicatePolicy);
        Assert.Equal(LibrarySourceManagementModes.ExistingLibrary, _f.Source(folder.Id).ManagementMode);
        Assert.Empty(await _f.Operations.GetQueueAsync(MediaOperationQueue.FolderJobs, 10));
    }

    // ── The fixture ──────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class FolderJobFixture : IDisposable
    {
        private readonly string _root;
        private readonly DatabaseConnection _database;
        private readonly IMediaAssetRepository _assets;
        private readonly ICanonicalValueRepository _canonicals;
        private readonly MediaEngine.Ingestion.Organise.FolderOrganiseRunner _runner;
        private string _libraryId = Guid.NewGuid().ToString("D");
        private readonly string _allowedRoot;

        public ConfigurationDirectoryLoader Config { get; }
        public IMediaOperationRepository Operations { get; }
        public FolderJobService Jobs { get; }
        public ScriptedMover Mover { get; } = new();
        public IFolderOrganiseLogRepository Ledger { get; }

        public FolderJobFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "tuvima-folder-jobs", Guid.NewGuid().ToString("N"));
            _allowedRoot = Path.Combine(_root, "allowed");
            Directory.CreateDirectory(_allowedRoot);
            Directory.CreateDirectory(Path.Combine(_root, "view"));
            DapperConfiguration.Configure();
            _database = new DatabaseConnection(Path.Combine(_root, "tuvima.db"));
            _database.InitializeSchema();
            _database.RunStartupChecks();

            Config = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
            Config.SaveLibraries(new LibrariesConfiguration
            {
                StorageLocations =
                [
                    new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = _allowedRoot, AllowWrite = true },
                    new ServerStorageLocationConfig { Id = "view", Label = "View", Path = Path.Combine(_root, "view"), AllowWrite = true },
                ],
                ViewStorage = new ViewStorageConfig { StorageLocationId = "view", RelativeRoot = "." },
                Libraries =
                [
                    new LibraryFolderConfig
                    {
                        Id = _libraryId,
                        Name = "Movies",
                        Category = "Movies",
                        Kind = LibraryKinds.Catalogued,
                        Area = LibraryAreas.Watch,
                        Presentation = LibraryPresentations.Catalogue,
                        MetadataPolicy = LibraryMetadataPolicies.Enriched,
                        MediaTypes = ["Movies"],
                        Sources = [],
                    },
                ],
            });

            _assets = new MediaAssetRepository(_database);
            _canonicals = new CanonicalValueRepository(_database);
            Operations = new MediaOperationRepository(_database);
            Ledger = new FolderOrganiseLogRepository(_database);
            var tracker = new MediaOperationTracker(
                Operations, new MediaOperationEventRepository(_database), new NullPublisher(), NullLogger<MediaOperationTracker>.Instance);

            var options = new IngestionOptions { PollIntervalSeconds = 0, IncludeSubdirectories = true };
            var monitor = new StaticMonitor(options);
            var organizer = new FileOrganizer(NullLogger<FileOrganizer>.Instance);
            var hasher = new Sha256Hasher();
            var sidecars = new SidecarMover(Mover, NullLogger<SidecarMover>.Instance);
            var auto = new AutoOrganizeService(
                _assets,
                _canonicals,
                organizer,
                new SystemActivityRepository(_database),
                new ReviewQueueRepository(_database),
                new NullPublisher(),
                new OrganizationGate(new ScoringConfiguration()),
                Microsoft.Extensions.Options.Options.Create(options),
                NullLogger<AutoOrganizeService>.Instance,
                workRepo: new WorkRepository(_database),
                libraryResolver: new LibraryFolderResolver(monitor),
                sourceMutationGate: new SourceMutationPolicyGate(),
                sidecars: sidecars);
            var organiser = new FolderConversionOrganiser(
                auto, _assets, _canonicals, organizer, new OrganizationGate(new ScoringConfiguration()),
                new ReviewQueueRepository(_database), new SystemActivityRepository(_database), Ledger, hasher,
                new SourceMutationPolicyGate(), sidecars, Mover, monitor, NullLogger<FolderConversionOrganiser>.Instance);
            _runner = new FolderOrganiseRunner(
                Config, _assets, Ledger, organiser, NullLogger<FolderOrganiseRunner>.Instance,
                timing: new FolderOrganiseTiming { RetryDelay = TimeSpan.FromMilliseconds(5), ProgressInterval = TimeSpan.Zero });
            var previewer = new FolderConversionPreviewer(Config, _assets, organiser, NullLogger<FolderConversionPreviewer>.Instance);
            Jobs = new FolderJobService(
                Config, Operations, tracker, Ledger, previewer,
                new ServerFolderBrowserService(Config, NullLogger<ServerFolderBrowserService>.Instance),
                NullLogger<FolderJobService>.Instance);
            Worker = new FolderOrganiseJobWorker(Operations, tracker, _runner, Jobs, NullLogger<FolderOrganiseJobWorker>.Instance);
        }

        private FolderOrganiseJobWorker Worker { get; }

        /// <summary>Adds an As-is folder to the Movies library with plain media files in it that the library does not know.</summary>
        public LibrarySourceConfig AddAsIsFolder(string name, int fileCount)
        {
            var path = Path.Combine(_allowedRoot, name);
            Directory.CreateDirectory(path);
            for (var index = 0; index < fileCount; index++)
            {
                File.WriteAllText(Path.Combine(path, $"file{index}.mkv"), $"bytes {name} {index}");
            }

            var libraries = Config.LoadLibraries();
            var source = new LibrarySourceConfig
            {
                Id = Guid.NewGuid().ToString("D"),
                Path = path,
                Notes = name,
                ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
                AccessMode = LibrarySourceAccessModes.ReadOnly,
            };
            libraries.Libraries[0].Sources.Add(source);
            Config.SaveLibraries(libraries);
            return source;
        }

        public LibrarySourceConfig Source(string id) =>
            Config.LoadLibraries().Libraries[0].Sources.First(source => source.Id == id);

        public IReadOnlyList<string> FilesIn(LibrarySourceConfig folder) =>
            Directory.EnumerateFiles(folder.Path, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(folder.Path, path).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>Writes a media file and registers it in the library the way ingestion would have.</summary>
        public async Task AddMovieAsync(LibrarySourceConfig folder, string fileName, string title, string year)
        {
            var path = Path.Combine(folder.Path, fileName);
            File.WriteAllText(path, $"{title} {year} bytes");
            var hash = (await new Sha256Hasher().ComputeAsync(path)).Hex;
            var workId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            using (var connection = _database.CreateConnection())
            {
                connection.Execute("""
                    INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@workId, 'Movies', 'standalone', 'Owned');
                    INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                    INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id)
                    VALUES (@assetId, @editionId, @hash, @path, 'Normal', @libraryId);
                    """,
                    new { workId, editionId, assetId, hash, path, libraryId = _libraryId });
            }

            await _canonicals.UpsertBatchAsync(
            [
                new CanonicalValue { EntityId = assetId, Key = "title", Value = title, LastScoredAt = DateTimeOffset.UtcNow },
                new CanonicalValue { EntityId = assetId, Key = "media_type", Value = "Movies", LastScoredAt = DateTimeOffset.UtcNow },
                new CanonicalValue { EntityId = assetId, Key = "year", Value = year, LastScoredAt = DateTimeOffset.UtcNow },
            ]);
        }

        public async Task<bool> IsFinishedAsync(string operationId)
        {
            var operation = await Operations.GetByIdAsync(Guid.Parse(operationId));
            return operation is not null && operation.Status is MediaOperationStatus.Succeeded
                or MediaOperationStatus.Cancelled or MediaOperationStatus.FailedTerminal;
        }

        public async Task RunWorkerUntilAsync(Func<Task<bool>> done, int timeoutSeconds = 30)
        {
            await Worker.StartAsync(CancellationToken.None);
            try
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
                while (!await done() && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(25);
                }

                Assert.True(await done(), "The folder job did not finish in time.");
            }
            finally
            {
                await Worker.StopAsync(CancellationToken.None);
            }
        }

        public async Task RunWorkerForAsync(TimeSpan duration)
        {
            await Worker.StartAsync(CancellationToken.None);
            await Task.Delay(duration);
            await Worker.StopAsync(CancellationToken.None);
        }

        public void Dispose()
        {
            try
            {
                _database.Dispose();
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Removed by the next run's cleanup of the tuvima-folder-jobs folder.
                _ = ex;
            }
        }
    }

    private sealed class ScriptedMover : IOrganiseFileMover
    {
        public List<(string From, string To)> Moves { get; } = [];

        public Action<string, string>? AfterMove { get; set; }

        public void Move(string from, string to)
        {
            File.Move(from, to, overwrite: false);
            Moves.Add((from, to));
            AfterMove?.Invoke(from, to);
        }
    }

    private sealed class Sha256Hasher : IAssetHasher
    {
        public Task<HashResult> ComputeAsync(string filePath, CancellationToken ct = default)
        {
            using var stream = File.OpenRead(filePath);
            return Task.FromResult(new HashResult
            {
                FilePath = filePath,
                Hex = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)),
                FileSize = new FileInfo(filePath).Length,
                Elapsed = TimeSpan.FromMilliseconds(1),
            });
        }
    }

    private sealed class NullPublisher : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull => Task.CompletedTask;
    }

    private sealed class StaticMonitor(IngestionOptions value) : IOptionsMonitor<IngestionOptions>
    {
        public IngestionOptions CurrentValue => value;

        public IngestionOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<IngestionOptions, string?> listener) => null;
    }
}
