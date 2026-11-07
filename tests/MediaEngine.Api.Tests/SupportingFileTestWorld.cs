using MediaEngine.Api.Services.Extras;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Extras;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// One show with supporting files for the browser tests (universal editor v5 WP4): a real temp data store and folder, the real file safety
/// gate, and hand-written spies for the title lookup, the library folders and the scanner. Seven places hold the files in turn: the title
/// itself and six seasons.
/// </summary>
internal sealed class SupportingFileTestWorld : IDisposable
{
    public const string ShowName = "The Last Signal";
    public const int GeneratedFiles = 238;

    public SupportingFileTestWorld(bool managedFolder = true, bool recycleBin = false)
    {
        DapperConfiguration.Configure();
        Root = Path.Combine(Path.GetTempPath(), $"tuvima_supporting_files_{Guid.NewGuid():N}");
        Show = Path.Combine(Root, ShowName);
        Directory.CreateDirectory(Show);
        Database = new DatabaseConnection(Path.Combine(Root, "library.db"));
        Database.InitializeSchema();
        Database.RunStartupChecks();
        Extras = new ItemExtraRepository(Database);
        Titles = new TitleSpy(Show);
        Scanner = new ScannerSpy(Show);
        Folders = new OneLibrary(Root, managedFolder, recycleBin);
        Excluded = new ExcludedSourceFileRepository(Database);
        Filter = new FilterSpy();
        var config = new ConfigSource();
        Disk = new SupportingFileDiskService(
            Folders, new SourceMutationPolicyGate(), Scanner, Excluded, Filter,
            new AssetPathService(Root, dataRoot: Path.Combine(Root, "data")), NullLogger<SupportingFileDiskService>.Instance);
        List = new SupportingFileListService(Extras, Titles, config);
        Bulk = new SupportingFileBulkService(Extras, Titles, Disk, config);
    }

    public string Root { get; }

    public string Show { get; }

    public DatabaseConnection Database { get; }

    public ItemExtraRepository Extras { get; }

    public TitleSpy Titles { get; }

    public ScannerSpy Scanner { get; }

    public OneLibrary Folders { get; }

    public ExcludedSourceFileRepository Excluded { get; }

    public FilterSpy Filter { get; }

    public SupportingFileDiskService Disk { get; }

    public SupportingFileListService List { get; }

    public SupportingFileBulkService Bulk { get; }

    public string SeasonFolder(int season) => Path.Combine(Show, $"Season {season:00}");

    /// <summary>
    /// 238 files: file number i belongs to the title itself when i mod 7 is 0, otherwise to season (i mod 7). The type goes
    /// document, picture, video, audio with i divided by 7, so every place holds 34 files: 9 documents, 9 pictures, 8 videos and 8 audio files.
    /// </summary>
    public async Task SeedGeneratedFilesAsync()
    {
        var rows = new List<ItemExtra>(GeneratedFiles);
        for (var number = 0; number < GeneratedFiles; number++)
        {
            var place = number % 7;
            var folder = place == 0 ? Show : SeasonFolder(place);
            var scope = place == 0 ? ExtraScope.Root : ExtraScope.ForSeason(place);
            rows.Add(((number / 7) % 4) switch
            {
                0 => Row(Path.Combine(folder, $"worksheet-{number:000}.pdf"), ItemExtraKinds.Document, $"Worksheet {number}", scope),
                1 => Row(Path.Combine(folder, $"photo-{number:000}.jpg"), ItemExtraKinds.Picture, $"Photo {number}", scope, groupKey: $"photo-{number:000}"),
                2 => Row(Path.Combine(folder, $"scene-{number:000}.mp4"), ItemExtraKinds.Clip, $"Scene {number}", scope),
                _ => Row(Path.Combine(folder, $"track-{number:000}.mp3"), ItemExtraKinds.Clip, $"Track {number}", scope),
            });
        }

        await Extras.SyncItemFolderAsync(Show, rows, []);
    }

    /// <summary>Stores one file that really exists on disk, at the title's own place (or the given one).</summary>
    public async Task<ItemExtra> SeedDiskFileAsync(string fileName, string title, ExtraScope? scope = null, string? folder = null)
    {
        var directory = folder ?? Show;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(path, "content");
        var existing = await Extras.ListAllInItemFolderAsync(Show);
        var row = Row(path, ItemExtraKinds.Document, title, scope ?? ExtraScope.Root);
        await Extras.SyncItemFolderAsync(Show, [.. existing, row], []);
        return (await Extras.GetByPathAsync(path))!;
    }

    public ItemExtra Row(string path, string kind, string title, ExtraScope scope, string? groupKey = null) => new(
        Guid.NewGuid(), "lib", Show, path, kind, ItemExtraOrigins.Rule,
        title, groupKey, 0, 10, null, null, DateTimeOffset.UtcNow,
        InferredRole: ExtraRoles.DefaultFor(kind),
        InferredScope: scope);

    public void Dispose()
    {
        Database.Dispose();
        TestTemp.ReleaseSqlitePool(Path.Combine(Root, "library.db"));
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
    }

    public sealed class TitleSpy(string folder) : ISupportingFileTitleResolver
    {
        public bool HasFolder { get; set; } = true;

        public Task<IReadOnlyList<string>> ResolveItemFoldersAsync(Guid titleId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(HasFolder ? [folder] : []);
    }

    public sealed class ScannerSpy(string show) : IItemExtrasScanner
    {
        public Task<string?> ResolveScopeFolderAsync(string itemFolder, ExtraScope scope, CancellationToken ct = default) =>
            Task.FromResult<string?>(scope.Level switch
            {
                ExtraScopeLevel.Root => show,
                ExtraScopeLevel.Season when scope.Season is { } season => Path.Combine(show, $"Season {season:00}"),
                _ => null,
            });

        public Task<bool> ClaimsFileAsync(string filePath, LibraryFolderEntry? library, CancellationToken ct = default) => Task.FromResult(false);

        public void NoteFile(string filePath)
        {
        }

        public Task<int> SyncQueuedAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<int> SyncAllAsync(CancellationToken ct = default) => Task.FromResult(0);

        public Task<bool> SyncItemFolderAsync(string itemFolder, CancellationToken ct = default) => Task.FromResult(true);

        public Task<ItemExtra?> DescribeAsync(string filePath, string origin, int? targetSeason, int? targetEpisode, CancellationToken ct = default) =>
            Task.FromResult<ItemExtra?>(null);

        public Task<ItemExtra?> DescribeAsync(string filePath, string origin, ExtraDecision decision, CancellationToken ct = default) =>
            Task.FromResult<ItemExtra?>(null);

        public Task<ExtraTitleFolder?> ResolveTitleFolderAsync(string titleFilePath, CancellationToken ct = default) =>
            Task.FromResult<ExtraTitleFolder?>(null);

        public Task<ExtraEpisodePlacement?> GetEpisodePlacementAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<ExtraEpisodePlacement?>(null);
    }

    public sealed class FilterSpy : IExcludedSourceFileFilter
    {
        public int Invalidated { get; private set; }

        public Task<bool> ShouldSkipAsync(string fullPath, CancellationToken ct = default) => Task.FromResult(false);

        public void Invalidate() => Invalidated++;
    }

    /// <summary>One library ("lib") with one source folder ("src") at the test root: managed and writable, or a read-only existing folder.</summary>
    public sealed class OneLibrary : ILibraryFolderResolver
    {
        private readonly LibraryFolderEntry _library;
        private readonly LibrarySourceEntry _source;
        private readonly string _root;

        public OneLibrary(string root, bool managed, bool recycleBin)
        {
            _root = root;
            _source = new LibrarySourceEntry
            {
                Id = "src",
                Path = root,
                ManagementMode = managed ? LibrarySourceManagementModes.ManagedByTuvima : LibrarySourceManagementModes.ExistingLibrary,
                AccessMode = managed ? LibrarySourceAccessModes.Writable : LibrarySourceAccessModes.ReadOnly,
            };
            _library = new LibraryFolderEntry { Id = "lib", Name = "Shows", Sources = [_source], RecycleBin = recycleBin };
        }

        public LibraryFolderEntry? ResolveById(string libraryId) => libraryId == "lib" ? _library : null;

        public LibraryFolderEntry? ResolveForPath(string absolutePath) => ResolveSourceForPath(absolutePath)?.Library;

        public ResolvedLibrarySource? ResolveSourceForPath(string absolutePath) =>
            Path.GetFullPath(absolutePath).StartsWith(Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase)
                ? new ResolvedLibrarySource(_library, _source)
                : null;

        public string? ResolveSourcePath(string absolutePath) => ResolveSourceForPath(absolutePath)?.Source.Path;
    }

    private sealed class ConfigSource : IExtrasConfigurationSource
    {
        public ExtrasConfiguration Current => ExtrasConfigurationProvider.Active;
    }
}
