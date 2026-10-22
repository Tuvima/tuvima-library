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
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Tests.Helpers;

/// <summary>
/// A real import setup on temp folders: an Import folder with its six per-type folders, a Movies library with one managed
/// folder, a real SQLite data store, the real organiser and the real services under test. Nothing is mocked on disk.
/// </summary>
internal sealed class ImportFolderHarness : IDisposable
{
    public const string MoviesLibraryId = "aaaaaaaa-0000-4000-8000-000000000001";
    public const string ComicsLibraryId = "aaaaaaaa-0000-4000-8000-000000000002";

    public string Root { get; }
    public string ImportDir { get; }
    public string MoviesDir { get; }
    public string ElsewhereDir { get; }
    public string AsIsDir { get; }
    public string LibraryRoot { get; }

    public StubConfigurationLoader Config { get; } = new();
    public TestDatabaseFactory Db { get; } = new();
    public IMediaAssetRepository Assets { get; }
    public ICanonicalValueRepository Canonicals { get; }
    public IReviewQueueRepository Reviews { get; }
    public ISystemActivityRepository Activity { get; }
    public IIdentityJobRepository Jobs { get; }
    public StubEventPublisher Publisher { get; } = new();
    public TestAssetHasher Hasher { get; } = new();
    public IngestionOptions Options { get; }

    public ImportFolderService Importer { get; }
    public ImportFileMover Mover { get; }
    public ImportFilingJournal Journal { get; }
    public AutoOrganizeService Organizer { get; }
    public ImportFolderFiler Filer { get; }
    public ImportFolderWaitingReader Waiting { get; }

    public ImportFolderHarness(
        string duplicatePolicy = LibraryDuplicatePolicies.KeepOne,
        bool createMoviesFolder = true,
        bool alwaysCopyAndVerify = false)
    {
        Root = Path.Combine(Path.GetTempPath(), $"tuvima_import_{Guid.NewGuid():N}");
        ImportDir = Path.Combine(Root, "Import");
        MoviesDir = Path.Combine(Root, "library", "Movies");
        ElsewhereDir = Path.Combine(Root, "library", "Elsewhere");
        AsIsDir = Path.Combine(Root, "asis");
        LibraryRoot = Path.Combine(Root, "library");

        Directory.CreateDirectory(ImportDir);
        Directory.CreateDirectory(ElsewhereDir);
        Directory.CreateDirectory(AsIsDir);
        if (createMoviesFolder)
        {
            Directory.CreateDirectory(MoviesDir);
        }

        Config.Libraries = new LibrariesConfiguration
        {
            FileHandling = new FileHandlingConfig { DuplicatePolicy = duplicatePolicy },
            StorageLocations =
            [
                new ServerStorageLocationConfig
                {
                    Id = ImportFolderRules.LocationId,
                    Label = "Import folder",
                    Path = ImportDir,
                    AllowWrite = true,
                },
            ],
            Libraries =
            [
                // A second managed folder that is NOT marked New media: nothing may ever be filed there.
                NewLibrary(
                    MoviesLibraryId,
                    "Movies",
                    "Movies",
                    destinationId: "movies-new",
                    Managed("movies-other", ElsewhereDir),
                    Managed("movies-new", MoviesDir)),
            ],
        };

        var db = Db.Connection;
        Assets = new MediaAssetRepository(db);
        Canonicals = new CanonicalValueRepository(db);
        Reviews = new ReviewQueueRepository(db);
        Activity = new SystemActivityRepository(db);
        Jobs = new IdentityJobRepository(db);

        Options = new IngestionOptions
        {
            LibraryRoot = LibraryRoot,
            AutoOrganize = true,
            DuplicatePolicy = duplicatePolicy,
            WatchDirectories = [ImportDir, MoviesDir],
            ImportFolderPath = ImportDir,
            LibraryFolders = Config.Libraries.Libraries.Select(LibraryFolderEntryFactory.Create).ToList(),
            PollIntervalSeconds = 0,
            IncludeSubdirectories = true,
        };

        Importer = new ImportFolderService(Config, NullLogger<ImportFolderService>.Instance);
        Mover = new ImportFileMover(Hasher, NullLogger<ImportFileMover>.Instance) { AlwaysCopyAndVerify = alwaysCopyAndVerify };
        Journal = new ImportFilingJournal(Importer, NullLogger<ImportFilingJournal>.Instance);
        Organizer = new AutoOrganizeService(
            Assets,
            Canonicals,
            new FileOrganizer(NullLogger<FileOrganizer>.Instance),
            Activity,
            Reviews,
            Publisher,
            new OrganizationGate(new ScoringConfiguration()),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<AutoOrganizeService>.Instance,
            workRepo: new WorkRepository(db),
            libraryResolver: new LibraryFolderResolver(new OptionsMonitorStub<IngestionOptions>(Options)),
            sourceMutationGate: new SourceMutationPolicyGate(),
            importFolders: Importer,
            importMover: Mover,
            importJournal: Journal,
            sidecars: new SidecarMover(new SystemOrganiseFileMover(), NullLogger<SidecarMover>.Instance));
        Filer = new ImportFolderFiler(
            Importer,
            Assets,
            Jobs,
            Canonicals,
            Organizer,
            new FileOrganizer(NullLogger<FileOrganizer>.Instance),
            new SourceMutationPolicyGate(),
            Mover,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<ImportFolderFiler>.Instance,
            Journal);
        Waiting = new ImportFolderWaitingReader(
            Importer, Assets, Canonicals, NullLogger<ImportFolderWaitingReader>.Instance);
    }

    public static LibrarySourceConfig Managed(string id, string path) => new()
    {
        Id = id,
        Path = path,
        ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
        AccessMode = LibrarySourceAccessModes.Writable,
    };

    public static LibrarySourceConfig AsIs(string id, string path) => new()
    {
        Id = id,
        Path = path,
        ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
        AccessMode = LibrarySourceAccessModes.ReadOnly,
    };

    public static LibraryFolderConfig NewLibrary(
        string id, string name, string mediaType, string? destinationId, params LibrarySourceConfig[] sources) => new()
        {
            Id = id,
            Name = name,
            Kind = LibraryKinds.Catalogued,
            KindKey = LibraryKindsConfiguration.BuiltIn().First(kind => kind.Available && LibraryKindRules.TokenMatchesFormat(mediaType, kind.Format)).Key,
            MediaTypes = [mediaType],
            Sources = [.. sources],
            PrimaryDestinationSourceId = destinationId,
        };

    /// <summary>Adds (or replaces) a library for a media type, the way saving the folder settings would.</summary>
    public void SetLibrary(LibraryFolderConfig library)
    {
        Config.Libraries.Libraries.RemoveAll(existing => existing.Id == library.Id);
        Config.Libraries.Libraries.Add(library);
    }

    public string WriteImportFile(string relativePath, string content = "media bytes")
    {
        var path = Path.Combine(ImportDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public IEnumerable<string> FilesUnder(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        : [];

    /// <summary>
    /// Registers a file that is already in the Import folder the way the pipeline does (work, edition, asset, title and type),
    /// with its identity work finished, so the next step is filing.
    /// </summary>
    public async Task<Guid> RegisterAsync(
        string path,
        MediaType type,
        string title,
        string? year = null,
        string? libraryId = MoviesLibraryId,
        IdentityJobState jobState = IdentityJobState.Ready)
    {
        var hash = (await Hasher.ComputeAsync(path)).Hex;
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var connection = Db.Connection.CreateConnection())
        {
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
                    mediaType = type.ToString(),
                    editionId,
                    assetId,
                    contentHash = hash,
                    path,
                    libraryId,
                });
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
        await Jobs.CreateAsync(new IdentityJob
        {
            EntityId = assetId,
            EntityType = "MediaAsset",
            MediaType = type.ToString(),
            State = jobState.ToString(),
        });
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

    public void Dispose()
    {
        Db.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked temp folder is removed by the next run's cleanup of tuvima_import_* folders.
            _ = ex;
        }
    }
}
