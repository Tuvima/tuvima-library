using Dapper;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Services;
using MediaEngine.Intelligence.Models;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Tests.Helpers;

/// <summary>
/// A real folder conversion setup on temp folders and a real data store: a Movies library with one folder, the real
/// organiser, ledger, runner and previewer. Only the rename itself goes through a seam so a file in use, or a stop right
/// after a rename, can be staged on any operating system.
/// </summary>
internal sealed class FolderConversionHarness : IDisposable
{
    public const string LibraryId = "bbbbbbbb-0000-4000-8000-000000000001";

    public string Root { get; }
    public string SourceDir { get; }
    public Guid SourceId { get; } = Guid.NewGuid();
    public Guid OperationId { get; } = Guid.NewGuid();

    public StubConfigurationLoader Config { get; } = new();
    public TestDatabaseFactory Db { get; } = new();
    public IMediaAssetRepository Assets { get; }
    public ICanonicalValueRepository Canonicals { get; }
    public IReviewQueueRepository Reviews { get; }
    public ISystemActivityRepository Activity { get; }
    public IFolderOrganiseLogRepository Ledger { get; }
    public TestAssetHasher Hasher { get; } = new();
    public StubEventPublisher Publisher { get; } = new();
    public ScriptedMover Mover { get; }
    public RecordingIntake Intake { get; } = new();
    public IngestionOptions Options { get; }
    public FolderConversionOrganiser Organiser { get; }
    public FolderOrganiseRunner Runner { get; }
    public FolderConversionPreviewer Previewer { get; }
    public AutoOrganizeService Auto { get; }

    public FolderConversionHarness(
        string duplicatePolicy = LibraryDuplicatePolicies.KeepOne,
        bool managed = true)
    {
        Root = Path.Combine(Path.GetTempPath(), $"tuvima_convert_{Guid.NewGuid():N}");
        SourceDir = Path.Combine(Root, "Plex", "Movies");
        Directory.CreateDirectory(SourceDir);

        Config.Libraries = new LibrariesConfiguration
        {
            FileHandling = new FileHandlingConfig { DuplicatePolicy = duplicatePolicy },
            Libraries =
            [
                new LibraryFolderConfig
                {
                    Id = LibraryId,
                    Name = "Movies",
                    Kind = LibraryKinds.Catalogued,
                    MediaTypes = ["Movies"],
                    Sources = [Source(managed)],
                    PrimaryDestinationSourceId = managed ? SourceId.ToString("D") : null,
                },
            ],
        };

        var db = Db.Connection;
        Assets = new MediaAssetRepository(db);
        Canonicals = new CanonicalValueRepository(db);
        Reviews = new ReviewQueueRepository(db);
        Activity = new SystemActivityRepository(db);
        Ledger = new FolderOrganiseLogRepository(db);

        Options = new IngestionOptions
        {
            LibraryRoot = SourceDir,
            AutoOrganize = true,
            DuplicatePolicy = duplicatePolicy,
            WatchDirectories = [SourceDir],
            LibraryFolders = Config.Libraries.Libraries.Select(LibraryFolderEntryFactory.Create).ToList(),
            PollIntervalSeconds = 0,
            IncludeSubdirectories = true,
        };
        var monitor = new OptionsMonitorStub<IngestionOptions>(Options);

        Mover = new ScriptedMover();
        var organizer = new FileOrganizer(NullLogger<FileOrganizer>.Instance);
        var sidecars = new SidecarMover(Mover, NullLogger<SidecarMover>.Instance);
        Auto = new AutoOrganizeService(
            Assets,
            Canonicals,
            organizer,
            Activity,
            Reviews,
            Publisher,
            new OrganizationGate(new ScoringConfiguration()),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<AutoOrganizeService>.Instance,
            workRepo: new WorkRepository(db),
            libraryResolver: new LibraryFolderResolver(monitor),
            sourceMutationGate: new SourceMutationPolicyGate(),
            sidecars: sidecars);
        Organiser = new FolderConversionOrganiser(
            Auto,
            Assets,
            Canonicals,
            organizer,
            new OrganizationGate(new ScoringConfiguration()),
            Reviews,
            Activity,
            Ledger,
            Hasher,
            new SourceMutationPolicyGate(),
            sidecars,
            Mover,
            monitor,
            NullLogger<FolderConversionOrganiser>.Instance);
        Runner = new FolderOrganiseRunner(
            Config,
            Assets,
            Ledger,
            Organiser,
            NullLogger<FolderOrganiseRunner>.Instance,
            intake: Intake,
            timing: new FolderOrganiseTiming
            {
                RetryDelay = TimeSpan.FromMilliseconds(5),
                RetryRounds = 3,
                ProgressInterval = TimeSpan.Zero,
                SettingsRefresh = TimeSpan.Zero,
            });
        Previewer = new FolderConversionPreviewer(
            Config, Assets, Organiser, NullLogger<FolderConversionPreviewer>.Instance);
    }

    private LibrarySourceConfig Source(bool managed) => new()
    {
        Id = SourceId.ToString("D"),
        Path = SourceDir,
        ManagementMode = managed ? LibrarySourceManagementModes.ManagedByTuvima : LibrarySourceManagementModes.ExistingLibrary,
        AccessMode = managed ? LibrarySourceAccessModes.Writable : LibrarySourceAccessModes.ReadOnly,
    };

    public FolderOrganiseJob Job => new(OperationId, SourceId);

    /// <summary>Switches the saved folder between As-is and Managed the way saving the folder settings would.</summary>
    public void SetManaged(bool managed)
    {
        var library = Config.Libraries.Libraries[0];
        library.Sources[0] = Source(managed);
        library.PrimaryDestinationSourceId = managed ? SourceId.ToString("D") : null;
        Options.LibraryFolders = Config.Libraries.Libraries.Select(LibraryFolderEntryFactory.Create).ToList();
    }

    public string Write(string relativePath, string content = "media bytes")
    {
        var path = Path.Combine(SourceDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public IEnumerable<string> Files() => Directory.EnumerateFiles(SourceDir, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(SourceDir, path).Replace('\\', '/'))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a file the way ingestion would have (work, edition, asset, title, type), without moving it.</summary>
    public async Task<Guid> RegisterAsync(string path, string title, string? year = null, MediaType type = MediaType.Movies)
    {
        var hash = (await Hasher.ComputeAsync(path)).Hex;
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var connection = Db.Connection.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO works (id, media_type, work_kind, ownership) VALUES (@workId, @mediaType, 'standalone', 'Owned');
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id)
                VALUES (@assetId, @editionId, @contentHash, @path, 'Normal', @libraryId);
                """,
                new { workId, mediaType = type.ToString(), editionId, assetId, contentHash = hash, path, libraryId = LibraryId });
        }

        var values = new List<CanonicalValue>
        {
            new() { EntityId = assetId, Key = "title", Value = title, LastScoredAt = DateTimeOffset.UtcNow },
            new() { EntityId = assetId, Key = "media_type", Value = type.ToString(), LastScoredAt = DateTimeOffset.UtcNow },
        };
        if (year is not null)
        {
            values.Add(new CanonicalValue { EntityId = assetId, Key = "year", Value = year, LastScoredAt = DateTimeOffset.UtcNow });
        }

        await Canonicals.UpsertBatchAsync(values);
        return assetId;
    }

    public async Task AddPendingReviewAsync(Guid assetId)
    {
        await Reviews.InsertAsync(new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = assetId,
            EntityType = "MediaAsset",
            Trigger = MediaEngine.Domain.Constants.ReviewTrigger.LowConfidence,
            ConfidenceScore = 0.2,
            Detail = "Needs a person to confirm the match.",
            ReviewReadyAt = DateTimeOffset.UtcNow,
            AutomationCompletedAt = DateTimeOffset.UtcNow,
        });
    }

    public Task<FolderOrganiseRunResult> RunAsync(TestHost? host = null, CancellationToken ct = default) =>
        Runner.RunAsync(Job, host ?? new TestHost(), ct);

    public void Dispose()
    {
        Db.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked temp folder is removed by the next run's cleanup of tuvima_convert_* folders.
            _ = ex;
        }
    }
}

/// <summary>A rename that can be told to fail like a file in use, or to stop the process right after it worked.</summary>
internal sealed class ScriptedMover : IOrganiseFileMover
{
    private readonly Dictionary<string, int> _lockedFor = new(StringComparer.OrdinalIgnoreCase);

    public List<(string From, string To)> Moves { get; } = [];

    /// <summary>Raised after a rename succeeded, before the caller can record it. Throw to simulate the process ending.</summary>
    public Action<string, string>? AfterMove { get; set; }

    /// <summary>The file counts as open for playback for this many attempts.</summary>
    public void LockFor(string path, int attempts) => _lockedFor[Path.GetFullPath(path)] = attempts;

    /// <summary>
    /// Stands in for a drive that ignores letter case: when set and it returns true, the two names count as one file.
    /// When it is not set the real file identity check runs.
    /// </summary>
    public Func<string, string, bool>? SameFile { get; set; }

    public bool IsSameFile(string first, string second) => SameFile?.Invoke(first, second) ?? FileIdentity.AreSameFile(first, second);

    public void Move(string from, string to)
    {
        var key = Path.GetFullPath(from);
        if (_lockedFor.TryGetValue(key, out var remaining) && remaining > 0)
        {
            _lockedFor[key] = remaining - 1;
            throw new IOException("The process cannot access the file because it is being used by another process.");
        }

        File.Move(from, to, overwrite: false);
        Moves.Add((from, to));
        AfterMove?.Invoke(from, to);
    }
}

internal sealed class RecordingIntake : IFileIntake
{
    public List<string> Paths { get; } = [];

    public Task EnqueueAsync(string path, string libraryId, string sourceId, CancellationToken ct = default)
    {
        Paths.Add(path);
        return Task.CompletedTask;
    }
}

/// <summary>Stands in for the worker: hands out control signals and remembers the progress it was told.</summary>
internal sealed class TestHost : IFolderOrganiseHost
{
    public Func<int, FolderJobSignal> Signal { get; set; } = _ => FolderJobSignal.Continue;

    public List<FolderOrganiseProgress> Progress { get; } = [];

    private int _asked;

    public Task<FolderJobSignal> GetSignalAsync(CancellationToken ct) => Task.FromResult(Signal(_asked++));

    public Task ReportProgressAsync(FolderOrganiseProgress progress, CancellationToken ct)
    {
        Progress.Add(progress);
        return Task.CompletedTask;
    }
}

/// <summary>Raised by a test to stand in for the Engine being killed: nothing in the job catches it.</summary>
internal sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException() : base("The Engine was stopped.") { }
}
