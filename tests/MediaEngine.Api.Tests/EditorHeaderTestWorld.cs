using Dapper;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A small generated Library for the editor's v5 header reads (universal editor v5, WP3): a real temp SQLite data store, a real temp
/// folder for the title, and hand-written spies for the folder resolver and the Extras scanner. Nothing here touches real media.
/// </summary>
internal sealed class EditorHeaderTestWorld : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_editor_header_{Guid.NewGuid():N}.db");

    public EditorHeaderTestWorld(string titleFolderName, string accessMode = LibrarySourceAccessModes.Writable)
    {
        DapperConfiguration.Configure();
        Database = new DatabaseConnection(_databasePath);
        Database.InitializeSchema();
        Library = Path.Combine(Path.GetTempPath(), $"tuvima_editor_library_{Guid.NewGuid():N}");
        TitleFolder = Path.Combine(Library, titleFolderName);
        Directory.CreateDirectory(TitleFolder);
        Folders = new FolderResolverSpy(Library, accessMode);
        Scanner = new ScannerSpy(new ExtraTitleFolder("lib", TitleFolder));
        Scanner.PerFile = path => OtherTitleFolders.FirstOrDefault(folder => path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal)) is { } own
            ? new ExtraTitleFolder("lib", own)
            : null;
        Extras = new ItemExtraRepository(Database);
    }

    public DatabaseConnection Database { get; }

    /// <summary>The Library's folder; the title's folder sits inside it.</summary>
    public string Library { get; }

    public string TitleFolder { get; }

    public FolderResolverSpy Folders { get; }

    public ScannerSpy Scanner { get; }

    /// <summary>Further title folders of the same Library (one per book or film of a Series); a file under one resolves to it, any other to <see cref="TitleFolder"/>.</summary>
    public List<string> OtherTitleFolders { get; } = [];

    /// <summary>Makes another title folder of the Library (a book's or film's own) and returns its path.</summary>
    public string AddTitleFolder(string name)
    {
        var folder = Path.Combine(Library, name);
        Directory.CreateDirectory(folder);
        OtherTitleFolders.Add(folder);
        return folder;
    }

    public ItemExtraRepository Extras { get; }

    public ItemQuickFactsService QuickFacts => new(new ItemQuickFactsRepository(Database), Folders);

    public EditorHeaderReadService Header(IEditorConfigurationSource? config = null, ILogger<EditorHeaderReadService>? logger = null) => new(
        Database,
        new MediaEditorNavigationReadService(Database, null!, new HierarchyAlignmentService(Database, null!)),
        Extras,
        Scanner,
        config ?? new EditorConfigurationProvider(() => null),
        Folders,
        new ItemQuickFactsService(new ItemQuickFactsRepository(Database), Folders),
        new MediaEditorWorkVersionReadRepository(Database),
        null,
        logger);

    public EditorFolderTreeService FolderTree(IEditorConfigurationSource? config = null) => new(
        Header(config),
        Extras,
        Folders,
        new MediaTypeExtensionCatalog(),
        config ?? new EditorConfigurationProvider(() => null),
        Database);

    /// <summary>Adds a work with its title; the parent and ordinal place it in a family.</summary>
    public Guid Work(string mediaType, string kind, string title, Guid? parent = null, int? ordinal = null, Guid? id = null)
    {
        var workId = id ?? Guid.NewGuid();
        using var connection = Database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, ownership, curator_state)
            VALUES (@workId, @mediaType, @kind, @parent, @ordinal, 'Owned', 'accepted');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'title', @title, datetime('now'));
            """,
            new { workId, mediaType, kind, parent, ordinal, title });
        return workId;
    }

    /// <summary>Adds the work's one media file (under the title's folder, or <paramref name="folder"/> when given) and returns the asset id.</summary>
    public Guid File(Guid workId, string relativePath, double? durationSeconds = null, string? folder = null)
    {
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var hash = Guid.NewGuid().ToString("N");
        var path = Path.Combine(folder ?? TitleFolder, relativePath);
        using var connection = Database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, 'lib');
            """,
            new { editionId, workId, assetId, hash, path });
        if (durationSeconds is { } duration)
        {
            connection.Execute(
                "INSERT INTO playback_inspection_cache (asset_id, source_hash, inspected_at, duration_secs) VALUES (@assetId, @hash, datetime('now'), @duration);",
                new { assetId, hash, duration });
        }

        return assetId;
    }

    /// <summary>Writes a real file under the title's folder.</summary>
    public string Write(string relativePath, string text = "x")
    {
        var path = Path.Combine(TitleFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    public void Canonical(Guid entityId, string key, string value)
    {
        using var connection = Database.CreateConnection();
        connection.Execute(
            "INSERT OR REPLACE INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@entityId, @key, @value, datetime('now'));",
            new { entityId, key, value });
    }

    /// <summary>Stores a supporting file of the title at a place (a scope key such as <c>season:2</c>).</summary>
    public async Task<Guid> SupportingFileAsync(
        string relativePath, ExtraScope scope, string kind = ItemExtraKinds.Document, string role = ExtraRoles.Guide, string? folder = null,
        ExtraScope? alongside = null)
    {
        var id = Guid.NewGuid();
        var titleFolder = folder ?? TitleFolder;
        var path = Path.Combine(titleFolder, relativePath);
        var extra = new ItemExtra(
            id, "lib", titleFolder, path, kind, ItemExtraOrigins.Rule, Path.GetFileNameWithoutExtension(relativePath), null, 0, 100, null, 12,
            DateTimeOffset.UtcNow, InferredRole: role, InferredScope: scope, InferredAlongside: alongside);
        var existing = await Extras.ListAllInItemFolderAsync(titleFolder);
        await Extras.SyncItemFolderAsync(titleFolder, [.. existing, extra], []);
        return id;
    }

    public void Dispose()
    {
        Database.Dispose();
        TestTemp.ReleaseSqlitePool(_databasePath);
        TryDelete(() => File_Delete(_databasePath));
        TryDelete(() => Directory.Delete(Library, recursive: true));
    }

    private static void File_Delete(string path)
    {
        if (System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path);
        }
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A pooled SQLite handle can outlive disposal for a moment; the OS temp sweep removes what is left.
        }
    }

    internal sealed class FolderResolverSpy(string root, string access) : ILibraryFolderResolver
    {
        public LibraryFolderEntry? ResolveById(string libraryId) => null;

        public LibraryFolderEntry? ResolveForPath(string absolutePath) => ResolveSourceForPath(absolutePath)?.Library;

        public string? ResolveSourcePath(string absolutePath) => ResolveSourceForPath(absolutePath)?.Source.Path;

        public ResolvedLibrarySource? ResolveSourceForPath(string absolutePath) =>
            absolutePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || absolutePath == root
                ? new ResolvedLibrarySource(
                    new LibraryFolderEntry { Id = "lib" },
                    new LibrarySourceEntry { Id = "source", Path = root, AccessMode = access })
                : null;
    }

    internal sealed class ScannerSpy(ExtraTitleFolder titleFolder) : IItemExtrasScanner
    {
        /// <summary>When set, answers for a file first; null from it means the Library's default title folder.</summary>
        public Func<string, ExtraTitleFolder?>? PerFile { get; set; }

        public Task<ExtraTitleFolder?> ResolveTitleFolderAsync(string titleFilePath, CancellationToken ct = default) =>
            Task.FromResult<ExtraTitleFolder?>(PerFile?.Invoke(titleFilePath) ?? titleFolder);

        public Task<ItemExtra?> DescribeAsync(string filePath, string origin, ExtraDecision decision, CancellationToken ct = default) =>
            Task.FromResult<ItemExtra?>(null);

        public Task<ItemExtra?> DescribeAsync(string filePath, string origin, int? targetSeason, int? targetEpisode, CancellationToken ct = default) =>
            Task.FromResult<ItemExtra?>(null);

        public Task<bool> SyncItemFolderAsync(string itemFolder, CancellationToken ct = default) => Task.FromResult(true);

        public Task<IReadOnlyList<ExtraScope>> ListPlacesAsync(string itemFolder, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExtraScope>>([]);

        public Task<string?> ResolveScopeFolderAsync(string itemFolder, ExtraScope scope, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<ExtraEpisodePlacement?> GetEpisodePlacementAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<ExtraEpisodePlacement?>(null);

        public Task<bool> ClaimsFileAsync(string filePath, LibraryFolderEntry? library, CancellationToken ct = default) => Task.FromResult(false);

        public void NoteFile(string filePath)
        {
        }

        public Task<int> SyncQueuedAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> SyncAllAsync(CancellationToken ct = default) => Task.FromResult(0);
    }
}
