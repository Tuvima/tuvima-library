using MediaEngine.Api.Services.Libraries;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.Vault;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Vault's images and galleries (Videos spec D44) end to end below the routes: finding images in the Vault folders, galleries of
/// every kind, pictures, tags, search, and the recycle bin with its protection of original source media. Real temp SQLite and a real
/// temp folder; the folder indexer, the folder resolver and the logger are hand-written spies.
/// </summary>
public sealed class VaultImageServiceTests : IDisposable
{
    private const string Secret = "secret-holiday-words";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-vault-image-service-{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-vault-image-service-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly LocalAssetRepository _assets;
    private readonly VaultImageRepository _images;
    private readonly VaultGalleryRepository _galleries;
    private readonly RecycleBinService _bin;
    private readonly CapturingLogger<VaultImageService> _logs = new();
    private readonly DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _libraryId = Guid.NewGuid().ToString("D");
    private readonly string _source;
    private readonly SpyIndexer _indexer;

    public VaultImageServiceTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "vault-source");
        Directory.CreateDirectory(_source);
        _database = new DatabaseConnection(_dbPath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _assets = new LocalAssetRepository(_database);
        _images = new VaultImageRepository(_database);
        _galleries = new VaultGalleryRepository(_database);
        _bin = new RecycleBinService(Path.Combine(_root, "data"), NullLogger.Instance);
        new VaultLibraryRepository(_database)
            .AddAsync(new VaultLibrary(_libraryId, VaultLookupSources.None, null, _now, null, _now, _now)).GetAwaiter().GetResult();
        _indexer = new SpyIndexer(_assets);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_dbPath);
        foreach (var file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ── Finding images ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_PutsEachImageInTheGalleryOfItsFolder_AndAScanAgainAddsNothing()
    {
        WriteImage("beach/one.jpg", 'a');
        WriteImage("beach/two.jpg", 'b');
        WriteImage("city/three.jpg", 'c');
        var service = Service(writable: true);

        var first = await service.ScanAsync();
        var second = await service.ScanAsync();
        var galleries = await service.ListGalleriesAsync();

        Assert.Equal(3, first.ImagesFound);
        Assert.Equal(3, first.ImagesAdded);
        Assert.Equal(0, second.ImagesAdded);
        Assert.Equal(["beach", "city"], galleries.Where(g => g.Kind == VaultImageGalleryKinds.Folder).Select(g => g.Name).ToArray());
        Assert.Equal(2, galleries.Single(g => g.Name == "beach").ImageCount);
        Assert.All(galleries.Where(g => g.Kind == VaultImageGalleryKinds.Folder), g => Assert.False(g.Editable));
        var inBeach = await service.ListAsync(new VaultImageListRequest(galleries.Single(g => g.Name == "beach").Id));
        Assert.Equal(2, inBeach!.Items.Count);
    }

    [Fact]
    public async Task Scan_LogsOnlyCounts_NeverANameOrAPath()
    {
        WriteImage($"{Secret}/{Secret}-photo.jpg", 'a');
        var service = Service(writable: true);

        await service.ScanAsync();

        Assert.NotEmpty(_logs.Messages);
        Assert.DoesNotContain(_logs.Messages, message => message.Contains(Secret, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_logs.Messages, message => message.Contains(_root, StringComparison.OrdinalIgnoreCase));
    }

    // ── Galleries ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AHandMadeGallery_TakesVaultImagesOnly_AndAFolderGalleryCannotBeChanged()
    {
        var ids = await ScanThree();
        var service = Service(writable: true);

        var created = await service.CreateGalleryAsync(new VaultImageGalleryRequest { Name = "Picks" }, null);
        var added = await service.AddToGalleryAsync(created.Value!.Id, [ids[0], Guid.NewGuid()]);
        var folder = (await service.ListGalleriesAsync()).First(g => g.Kind == VaultImageGalleryKinds.Folder);
        var changed = await service.UpdateGalleryAsync(folder.Id, new VaultImageGalleryRequest { Name = "Renamed" });
        var folderDelete = await service.DeleteGalleryAsync(folder.Id);

        Assert.True(created.Succeeded);
        Assert.Equal(1, added.Value);
        Assert.Equal(VaultImageOutcome.NotFound, changed.Outcome);
        Assert.False(folderDelete);
        Assert.Equal(ids[0], Assert.Single((await service.ListAsync(new VaultImageListRequest(created.Value.Id)))!.Items).Id);
    }

    [Fact]
    public async Task ASmartGallery_ListsOnlyVaultImagesThatMatchItsRule_AndRefusesRulesAboutViewsFields()
    {
        var ids = await ScanThree();
        var service = Service(writable: true);
        await service.SetFavoriteAsync(ids[1], true);

        var smart = await service.CreateGalleryAsync(
            new VaultImageGalleryRequest { Name = "Starred", Kind = VaultImageGalleryKinds.Smart, SmartRuleJson = Rule("favorite", "eq", "true") }, null);
        var refused = await service.CreateGalleryAsync(
            new VaultImageGalleryRequest { Name = "People", Kind = VaultImageGalleryKinds.Smart, SmartRuleJson = Rule("people", "eq", "Ana") }, null);
        var noHandPicks = await service.AddToGalleryAsync(smart.Value!.Id, [ids[0]]);

        Assert.True(smart.Succeeded);
        Assert.Equal(1, smart.Value.ImageCount);
        Assert.Equal(VaultImageOutcome.Rejected, refused.Outcome);
        Assert.Equal(VaultImageOutcome.Rejected, noHandPicks.Outcome);
        Assert.Equal(ids[1], Assert.Single((await service.ListAsync(new VaultImageListRequest(smart.Value.Id)))!.Items).Id);
    }

    [Fact]
    public async Task AGalleryThatDoesNotExist_ListsAsNotFound_NotAsEverything()
    {
        await ScanThree();

        Assert.Null(await Service(writable: true).ListAsync(new VaultImageListRequest(Guid.NewGuid())));
    }

    // ── Tags, favourites and search ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tags_AreSavedByName_FoundBySearch_AndFilterTheList()
    {
        var ids = await ScanThree();
        var service = Service(writable: true);

        Assert.True(await service.SetTagsAsync(ids[0], ["Sunset", "Pier"]));
        Assert.False(await service.SetTagsAsync(Guid.NewGuid(), ["Nope"]));
        var tag = (await service.ListTagsAsync()).Single(t => t.Name == "Sunset");
        var byTag = await service.ListAsync(new VaultImageListRequest(TagId: tag.Id));
        var bySearch = await service.SearchAsync(new VaultImageSearchRequest { Text = "pier" });
        var tooShort = await service.SearchAsync(new VaultImageSearchRequest { Text = "p" });

        Assert.Equal(ids[0], Assert.Single(byTag!.Items).Id);
        Assert.Equal(ids[0], Assert.Single(bySearch!.Items).Id);
        Assert.Equal(["Pier", "Sunset"], byTag.Items[0].Tags.Order().ToArray());
        Assert.Empty(tooShort!.Items);
    }

    [Fact]
    public async Task SearchText_IsNeverWrittenToTheLog()
    {
        await ScanThree();
        var service = Service(writable: true);

        await service.SearchAsync(new VaultImageSearchRequest { Text = Secret });
        await service.SetTagsAsync((await service.ListAsync(new VaultImageListRequest()))!.Items[0].Id, [Secret]);

        Assert.DoesNotContain(_logs.Messages, message => message.Contains(Secret, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ThePages_FollowTheCursor_NewestPictureFirst()
    {
        await ScanThree();
        var service = Service(writable: true);

        var first = await service.ListAsync(new VaultImageListRequest(Take: 2));
        var second = await service.ListAsync(new VaultImageListRequest(Cursor: first!.NextCursor, Take: 2));

        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Single(second!.Items);
        Assert.False(second.HasMore);
        Assert.Empty(first.Items.Select(i => i.Id).Intersect(second.Items.Select(i => i.Id)));
    }

    // ── Pictures ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AThumbnail_IsMadeOnce_AtAnOfferedSize_AndKeptApartFromViews()
    {
        var ids = await ScanThree();
        var service = Service(writable: true);

        var thumbnail = await service.OpenAsync(ids[0], 160);
        var preview = await service.OpenAsync(ids[0], 1280);
        var odd = await service.OpenAsync(ids[0], 777);
        var missing = await service.OpenAsync(Guid.NewGuid(), 160);

        Assert.NotNull(thumbnail);
        Assert.Contains("vault-image", thumbnail.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("image/jpeg", thumbnail.ContentType);
        using (var bitmap = SKBitmap.Decode(thumbnail.Path))
        {
            Assert.Equal(160, Math.Max(bitmap.Width, bitmap.Height));
        }

        Assert.NotNull(preview);
        Assert.Null(odd);
        Assert.Null(missing);
    }

    // ── Recycle bin ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_MovesEveryFileToTheBin_AndTheImageLeavesEveryList_UntilItIsPutBack()
    {
        var ids = await ScanThree();
        var service = Service(writable: true);
        var path = FilePathOf(ids[0]);

        var deleted = await service.DeleteAsync(ids[0]);

        Assert.True(deleted.Succeeded);
        Assert.False(File.Exists(path));
        Assert.Equal(2, (await service.ListAsync(new VaultImageListRequest()))!.Items.Count);
        Assert.DoesNotContain(ids[0], (await service.SearchAsync(new VaultImageSearchRequest { Text = "one" }))!.Items.Select(i => i.Id));
        Assert.Equal(ids[0], Assert.Single((await service.ListDeletedAsync(null, 20)).Items).Id);
        Assert.Single(_bin.List());

        var restored = await service.RestoreAsync(ids[0]);

        Assert.True(restored.Succeeded);
        Assert.True(File.Exists(path));
        Assert.Equal(3, (await service.ListAsync(new VaultImageListRequest()))!.Items.Count);
        Assert.Empty((await service.ListDeletedAsync(null, 20)).Items);
        Assert.Empty(_bin.List());
    }

    [Fact]
    public async Task Delete_InAFolderLibraryOnlyReads_IsRefused_AndNothingMoves()
    {
        var ids = await ScanThree();
        var service = Service(writable: false);
        var path = FilePathOf(ids[0]);

        var deleted = await service.DeleteAsync(ids[0]);

        Assert.Equal(VaultImageOutcome.Rejected, deleted.Outcome);
        Assert.True(File.Exists(path));
        Assert.Empty(_bin.List());
        Assert.Equal(3, (await service.ListAsync(new VaultImageListRequest()))!.Items.Count);
    }

    [Fact]
    public async Task Restore_NeverOverwritesAFileThatTookItsPlace()
    {
        var ids = await ScanThree();
        var service = Service(writable: true);
        var path = FilePathOf(ids[0]);
        await service.DeleteAsync(ids[0]);
        await File.WriteAllTextAsync(path, "someone else");

        var restored = await service.RestoreAsync(ids[0]);

        Assert.Equal(VaultImageOutcome.Rejected, restored.Outcome);
        Assert.Equal("someone else", await File.ReadAllTextAsync(path));
        Assert.Single((await service.ListDeletedAsync(null, 20)).Items);
    }

    [Fact]
    public async Task EmptyingTheBin_RemovesVaultImagesForGood_AndLeavesTheMainBinsOtherEntriesAlone()
    {
        var ids = await ScanThree();
        var service = Service(writable: true);
        var other = Path.Combine(_root, "ordinary.txt");
        await File.WriteAllTextAsync(other, "ordinary");
        var ordinary = _bin.MoveIn(other, "Ordinary", Guid.NewGuid().ToString("D"));
        await service.DeleteAsync(ids[0]);
        await service.DeleteAsync(ids[1]);

        var removed = await service.EmptyBinAsync();

        Assert.Equal(2, removed);
        Assert.Empty((await service.ListDeletedAsync(null, 20)).Items);
        Assert.Equal(ordinary.Id, Assert.Single(_bin.List()).Id);
        Assert.Null(_assets.Find(ids[0]));
        Assert.Single((await service.ListAsync(new VaultImageListRequest()))!.Items);
    }

    [Fact]
    public async Task TheBin_HidesVaultEntriesFromALockedCaller()
    {
        var ids = await ScanThree();
        var service = Service(writable: true);
        await service.DeleteAsync(ids[0]);

        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _libraryId };

        Assert.Empty(_bin.List(hidden));
        Assert.Equal(0, _bin.Empty(hidden));
        Assert.Single(_bin.List());
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<Guid>> ScanThree()
    {
        WriteImage("beach/one.jpg", 'a');
        WriteImage("beach/two.jpg", 'b');
        WriteImage("city/three.jpg", 'c');
        await Service(writable: true).ScanAsync();
        var page = Service(writable: true).ListAsync(new VaultImageListRequest()).GetAwaiter().GetResult()!;
        return page.Items.OrderBy(item => item.FileName, StringComparer.Ordinal).Select(item => item.Id).ToList();
    }

    private VaultImageService Service(bool writable) => new(
        new VaultLibraryRepository(_database), _images, _galleries, _assets, _indexer, new LibrariesLoader(_libraryId, _source),
        new FolderResolver(_libraryId, _source, writable), new SourceMutationPolicyGate(),
        new ViewThumbnailService(new AssetPathService(Path.Combine(_root, "data")), NullLogger<ViewThumbnailService>.Instance),
        _bin, _logs);

    private string FilePathOf(Guid itemId) => _assets.ResolveContent(itemId)!.FilePath;

    private void WriteImage(string relative, char seed)
    {
        var path = Path.Combine(_source, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(400, 300);
        bitmap.Erase(new SKColor((byte)seed, (byte)(seed * 2), (byte)(seed * 3)));
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Jpeg, 80);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static string Rule(string field, string op, string value) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1,
            groups = new[] { new { id = "g1", join_with_previous = "or", match_mode = "all", conditions = new[] { new { field, op, value } } } },
        });

    /// <summary>Hand-written spy: reads the images of a folder into the data store the way the real indexer would, and remembers nothing else.</summary>
    internal sealed class SpyIndexer(LocalAssetRepository assets) : IVaultImageIndexer
    {
        public async Task<VaultImageIndexResult> IndexFolderAsync(Guid libraryId, string root, bool includeSubdirectories, CancellationToken ct = default)
        {
            var added = 0;
            var indexed = new List<VaultIndexedImage>();
            var files = Directory.EnumerateFiles(root, "*.jpg", includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Order().ToList();
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file)));
                var result = await assets.UpsertAsync(new LocalAssetRegistration(
                    libraryId, null, null, LocalAssetMediaKinds.Image, Path.GetFileNameWithoutExtension(file),
                    new DateTimeOffset(2026, 8, 19, 19, 32, 0, TimeSpan.Zero).AddMinutes(indexed.Count),
                    [new LocalAssetFileRegistration(file, hash, info.Name, "image/jpeg", info.Length, info.LastWriteTimeUtc)],
                    Width: 400, Height: 300, ScopeKind: LocalAssetScopeKinds.Vault), ct);
                added += result.ItemAdded ? 1 : 0;
                indexed.Add(new VaultIndexedImage(result.ItemId, Path.GetDirectoryName(file)!, result.ItemAdded));
            }

            return new VaultImageIndexResult(files.Count, added, 0, indexed);
        }
    }

    /// <summary>Hand-written spy: one Vault library with one folder, either a folder the Library manages or one it only reads.</summary>
    internal sealed class FolderResolver : ILibraryFolderResolver
    {
        private readonly ResolvedLibrarySource _resolved;
        private readonly string _path;

        public FolderResolver(string libraryId, string path, bool writable)
        {
            _path = path;
            var source = new LibrarySourceEntry
            {
                Id = "source-1",
                Path = path,
                ManagementMode = writable ? LibrarySourceManagementModes.ManagedByTuvima : LibrarySourceManagementModes.ExistingLibrary,
                AccessMode = writable ? LibrarySourceAccessModes.Writable : LibrarySourceAccessModes.ReadOnly,
            };
            _resolved = new ResolvedLibrarySource(new LibraryFolderEntry { Id = libraryId, Name = "Vault", Sources = [source], PrimaryDestinationSourceId = writable ? "source-1" : null }, source);
        }

        public LibraryFolderEntry? ResolveById(string libraryId) => _resolved.Library.Id == libraryId ? _resolved.Library : null;

        public LibraryFolderEntry? ResolveForPath(string absolutePath) => ResolveSourceForPath(absolutePath)?.Library;

        public ResolvedLibrarySource? ResolveSourceForPath(string absolutePath) =>
            absolutePath.StartsWith(_path, StringComparison.OrdinalIgnoreCase) ? _resolved : null;

        public string? ResolveSourcePath(string absolutePath) => ResolveSourceForPath(absolutePath)?.Source.Path;
    }

    internal sealed class LibrariesLoader(string libraryId, string path) : IConfigurationLoader
    {
        private readonly LibrariesConfiguration _libraries = new()
        {
            Libraries =
            [
                new LibraryFolderConfig
                {
                    Id = libraryId,
                    Name = "Vault",
                    Area = LibraryAreas.Watch,
                    Sources = [new LibrarySourceConfig { Id = "source-1", Path = path }],
                },
            ],
        };

        public LibrariesConfiguration LoadLibraries() => _libraries;

        public string ConfigDirectoryPath => string.Empty;
        public CoreConfiguration LoadCore() => new();
        public void SaveCore(CoreConfiguration config) { }
        public ScoringSettings LoadScoring() => new();
        public void SaveScoring(ScoringSettings settings) { }
        public MaintenanceSettings LoadMaintenance() => new();
        public void SaveMaintenance(MaintenanceSettings settings) { }
        public HydrationSettings LoadHydration() => new();
        public void SaveHydration(HydrationSettings settings) { }
        public PipelineConfiguration LoadPipelines() => new();
        public void SavePipelines(PipelineConfiguration config) { }
        public DisambiguationSettings LoadDisambiguation() => new();
        public void SaveDisambiguation(DisambiguationSettings settings) { }
        public MediaTypeConfiguration LoadMediaTypes() => new();
        public void SaveMediaTypes(MediaTypeConfiguration config) { }
        public TranscodingSettings LoadTranscoding() => new();
        public void SaveTranscoding(TranscodingSettings settings) { }
        public FieldPriorityConfiguration LoadFieldPriorities() => new();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void SaveProvider(ProviderConfiguration config) { }
        public void DeleteProvider(string name) { }
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
        public T? LoadAi<T>() where T : class => default;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
    }

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + (exception?.Message ?? string.Empty));
    }
}
