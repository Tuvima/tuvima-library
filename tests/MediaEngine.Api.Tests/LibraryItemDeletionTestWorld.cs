using Dapper;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A real temp data store and real temp folders wired to the real deletion service, resolver and safety gate. Two library folders
/// exist: a managed one (Movies) and an as-is one (Old Rips); a third folder is outside every library. The merge ledger is the real
/// repository, so a merge original is recognised exactly as it is in the running Engine.
/// </summary>
internal sealed class LibraryItemDeletionTestWorld : IDisposable
{
    private static readonly IReadOnlySet<string> MediaExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".mp3", ".epub" };

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_delete_preview_{Guid.NewGuid():N}.db");
    private readonly IngestionOptions _options;

    public LibraryItemDeletionTestWorld()
    {
        DapperConfiguration.Configure();
        Root = Path.Combine(Path.GetTempPath(), $"tuvima_delete_preview_{Guid.NewGuid():N}");
        Managed = Directory.CreateDirectory(Path.Combine(Root, "managed")).FullName;
        AsIs = Directory.CreateDirectory(Path.Combine(Root, "as-is")).FullName;
        Outside = Directory.CreateDirectory(Path.Combine(Root, "outside")).FullName;

        Database = new DatabaseConnection(_databasePath);
        Database.InitializeSchema();
        Database.RunStartupChecks();

        _options = new IngestionOptions { LibraryFolders = BuildFolders(moviesIsAsIs: false) };

        MergeLedger = new FolderOrganiseLogRepository(Database);
        Service = new LibraryItemDeletionService(
            new LibraryItemDeletionRepository(Database),
            new LibraryFolderResolver(new StaticMonitor(_options)),
            new SourceMutationPolicyGate(MergeLedger),
            new FixedExtensionCatalog());
    }

    /// <summary>Switches the managed Movies folder to as-is, as a person can in Settings, without touching any file.</summary>
    public void SwitchMoviesFolderToAsIs() => _options.LibraryFolders = BuildFolders(moviesIsAsIs: true);

    private IReadOnlyList<LibraryFolderEntry> BuildFolders(bool moviesIsAsIs) =>
    [
        new LibraryFolderEntry
        {
            Id = "movies",
            Name = "Movies",
            PrimaryDestinationSourceId = ManagedSourceId.ToString("D"),
            Sources =
            [
                new LibrarySourceEntry
                {
                    Id = ManagedSourceId.ToString("D"),
                    Path = Managed,
                    ManagementMode = moviesIsAsIs
                        ? LibrarySourceManagementModes.ExistingLibrary
                        : LibrarySourceManagementModes.ManagedByTuvima,
                    AccessMode = moviesIsAsIs ? LibrarySourceAccessModes.ReadOnly : LibrarySourceAccessModes.Writable,
                },
            ],
        },
        new LibraryFolderEntry
        {
            Id = "old-rips",
            Name = "Old Rips",
            Sources =
            [
                new LibrarySourceEntry
                {
                    Id = AsIsSourceId.ToString("D"),
                    Path = AsIs,
                    ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
                    AccessMode = LibrarySourceAccessModes.ReadOnly,
                },
            ],
        },
    ];

    public Guid ManagedSourceId { get; } = Guid.NewGuid();
    public Guid AsIsSourceId { get; } = Guid.NewGuid();
    public string Root { get; }
    public string Managed { get; }
    public string AsIs { get; }
    public string Outside { get; }
    public DatabaseConnection Database { get; }
    public FolderOrganiseLogRepository MergeLedger { get; }
    public LibraryItemDeletionService Service { get; }

    /// <summary>Writes a real file (and its folder) and returns its full path.</summary>
    public string WriteFile(string folder, string relativePath, int bytes = 100)
    {
        var path = Path.Combine(folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>Adds a title with one file per path. The files are only recorded; the caller writes them with <see cref="WriteFile"/>.</summary>
    public (Guid WorkId, IReadOnlyList<Guid> AssetIds) AddWork(
        string title,
        IReadOnlyList<string> filePaths,
        string mediaType = "Movies",
        string workKind = "standalone",
        bool catalogOnly = false)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assets = new List<Guid>();
        using var connection = Database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind, is_catalog_only, ownership)
            VALUES (@workId, @mediaType, @workKind, @catalogOnly, @ownership);
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@workId, 'title', @title, @now);
            """,
            new
            {
                workId,
                editionId,
                mediaType,
                workKind,
                catalogOnly = catalogOnly ? 1 : 0,
                ownership = catalogOnly ? "Unowned" : "Owned",
                title,
                now = DateTimeOffset.UtcNow.ToString("O"),
            });
        foreach (var path in filePaths)
        {
            var assetId = Guid.NewGuid();
            assets.Add(assetId);
            AddAsset(connection, editionId, assetId, path);
        }

        return (workId, assets);
    }

    /// <summary>Adds another file to an existing title's edition.</summary>
    public Guid AddAssetTo(Guid workId, string path)
    {
        using var connection = Database.CreateConnection();
        var editionId = connection.ExecuteScalar<Guid>("SELECT id FROM editions WHERE work_id = @workId LIMIT 1;", new { workId });
        var assetId = Guid.NewGuid();
        AddAsset(connection, editionId, assetId, path);
        return assetId;
    }

    public Guid AddCollection(string name, bool manual, params Guid[] workIds)
    {
        var collectionId = Guid.NewGuid();
        using var connection = Database.CreateConnection();
        connection.Execute(
            "INSERT INTO collections (id, display_name, membership_mode) VALUES (@collectionId, @name, @mode);",
            new { collectionId, name, mode = manual ? "Manual" : "Smart" });
        foreach (var workId in workIds)
        {
            AddMember(collectionId, workId);
        }

        return collectionId;
    }

    public void AddMember(Guid collectionId, Guid workId)
    {
        using var connection = Database.CreateConnection();
        connection.Execute(
            "INSERT INTO collection_items (id, collection_id, work_id) VALUES (@id, @collectionId, @workId);",
            new { id = Guid.NewGuid(), collectionId, workId });
    }

    public Guid AddProfile(string name = "Someone")
    {
        var id = Guid.NewGuid();
        using var connection = Database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles (id, display_name, created_at) VALUES (@id, @name, @now);",
            new { id, name, now = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    public void AddIdentityJob(Guid entityId, string state)
    {
        using var connection = Database.CreateConnection();
        connection.Execute("""
            INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, state)
            VALUES (@id, @entityId, 'MediaAsset', 'Movies', @state);
            """,
            new { id = Guid.NewGuid(), entityId, state });
    }

    public Guid AddViewItem()
    {
        var id = Guid.NewGuid();
        var household = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = Database.CreateConnection();
        // A personal item in View lives in the Shared library; it is a local item, never a Work.
        connection.Execute("""
            INSERT INTO households (id, name, created_at) VALUES (@household, 'Test household', @now);
            INSERT INTO view_shared_library (household_id, library_id, created_at, updated_at) VALUES (@household, @libraryId, @now, @now);
            INSERT INTO local_items (id, scope_kind, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
            VALUES (@id, 'shared', @libraryId, 'image', 'holiday.jpg', 'image/jpeg', @now, @now);
            """,
            new { id, household, libraryId, now });
        return id;
    }

    public void Dispose()
    {
        Database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
        TestTemp.DeleteDirectory(Root);
    }

    private static void AddAsset(System.Data.IDbConnection connection, Guid editionId, Guid assetId, string path) =>
        connection.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @hash, @path, 'Normal');
            """,
            new { assetId, editionId, hash = Guid.NewGuid().ToString("N"), path });

    private sealed class StaticMonitor(IngestionOptions value) : IOptionsMonitor<IngestionOptions>
    {
        public IngestionOptions CurrentValue => value;

        public IngestionOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<IngestionOptions, string?> listener) => null;
    }

    /// <summary>Only the list of media extensions matters to the preview; the rest of the catalogue is not used.</summary>
    private sealed class FixedExtensionCatalog : IMediaTypeExtensionCatalog
    {
        public IReadOnlySet<string> GetAllMediaExtensions() => MediaExtensions;

        public IReadOnlySet<string> GetExtensionsFor(MediaType mediaType) => throw new NotSupportedException();

        public bool IsKnownMediaExtension(string? extension) => extension is not null && MediaExtensions.Contains(extension);

        public bool IsUnambiguousExtension(string? extension) => throw new NotSupportedException();

        public bool IsStrongFormatExtension(string? extension) => throw new NotSupportedException();

        public bool IsVideoExtension(string? extension) => throw new NotSupportedException();

        public MediaType ResolveMediaTypeFromExtension(string? extension) => throw new NotSupportedException();
    }
}
