using Dapper;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A Shared (household) View folder added as a linked folder is read where it is and its files belong to the Shared library,
/// exactly as a personal folder's belong to a Personal Space.
/// </summary>
public sealed class ViewSharedFolderIndexingTests : IDisposable
{
    private readonly ViewLibraryServiceTests.ViewFixture _fixture = new();
    private readonly ViewSharedLibraryRepository _shared;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"tuvima-shared-folder-{Guid.NewGuid():N}");

    public ViewSharedFolderIndexingTests()
    {
        Directory.CreateDirectory(_folder);
        _shared = new ViewSharedLibraryRepository(_fixture.Database);
    }

    [Fact]
    public async Task Scan_OfALinkedSharedPhotosFolder_IndexesItsPicturesAsSharedLibraryItems()
    {
        var library = await _shared.GetAsync();
        var source = await AddLinkedFolderAsync(library.LibraryId, ViewContentKinds.Photos);
        var heic = Write("2024/2024-06-01/IMG_0001.heic", [1, 1, 1]);
        Write("2024/2024-06-02/IMG_0002.jpg", [2, 2, 2]);
        Write("2025/2025-01-09/IMG_0003.jpg", [3, 3, 3]);
        var originalBytes = File.ReadAllBytes(heic);

        var result = Assert.IsType<LocalAssetScanResultDto>(await _fixture.Service.ScanAsync(library.LibraryId));

        Assert.Equal(3, result.FilesSeen);
        Assert.Equal(3, result.ItemsAdded);
        Assert.Equal(0, result.Errors);
        var page = _fixture.Repository.Query(new LocalAssetQuery(library.LibraryId, Limit: 20, IncludeHidden: true));
        Assert.Equal(3, page.Total);
        Assert.All(page.Items, item =>
        {
            Assert.Equal("shared", item.ScopeKind);
            Assert.Equal(library.LibraryId, item.LibraryId);
            Assert.Null(item.PersonalSpaceId);
            Assert.Null(item.OwnerProfileId);
            Assert.Equal(ViewContentKinds.Photos, item.ContentKind);
        });
        Assert.Equal(originalBytes, File.ReadAllBytes(heic));
        Assert.Equal(3, Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories).Count());
        Assert.False((await _shared.GetSourcesAsync()).Single(candidate => candidate.Id == source.Id).FolderOffline);
    }

    [Fact]
    public async Task Scan_OfALinkedSharedPhotosFolder_CountsWhatDoesNotFitAsSkippedAndLeavesItAlone()
    {
        var library = await _shared.GetAsync();
        var source = await AddLinkedFolderAsync(library.LibraryId, ViewContentKinds.Photos);
        Write("a/IMG_0001.jpg", [1, 1, 1]);
        var clip = Write("a/CLIP_0002.mp4", [2, 2, 2]);
        var note = Write("a/notes.txt", [3, 3, 3]);

        var result = Assert.IsType<LocalAssetScanResultDto>(await _fixture.Service.ScanAsync(library.LibraryId));

        Assert.Equal(1, result.ItemsAdded);
        Assert.Equal(2, (await _shared.GetSourcesAsync()).Single(candidate => candidate.Id == source.Id).SkippedFileCount);
        Assert.True(File.Exists(clip));
        Assert.True(File.Exists(note));
    }

    [Fact]
    public async Task Scan_OfASharedFolderAgain_AddsNothingNew()
    {
        var library = await _shared.GetAsync();
        await AddLinkedFolderAsync(library.LibraryId, ViewContentKinds.Photos);
        Write("IMG_0001.jpg", [1, 1, 1]);
        await _fixture.Service.ScanAsync(library.LibraryId);

        var repeat = Assert.IsType<LocalAssetScanResultDto>(await _fixture.Service.ScanAsync(library.LibraryId));

        Assert.Equal(0, repeat.ItemsAdded);
        Assert.Equal(0, repeat.FilesAdded);
        Assert.Equal(1, _fixture.Repository.Query(new LocalAssetQuery(library.LibraryId, Limit: 20, IncludeHidden: true)).Total);
    }

    [Fact]
    public async Task Scan_OfASharedFolder_DoesNotTouchAPersonalSpacesItems()
    {
        var library = await _shared.GetAsync();
        await AddLinkedFolderAsync(library.LibraryId, ViewContentKinds.Photos);
        Write("IMG_0001.jpg", [1, 1, 1]);

        await _fixture.Service.ScanAsync(library.LibraryId);

        Assert.Equal(0, _fixture.Repository.Query(new LocalAssetQuery(_fixture.PersonalLibraryId, Limit: 20, IncludeHidden: true)).Total);
    }

    [Fact]
    public async Task Scan_OfALibraryThatIsNeitherPersonalNorShared_ReturnsNothing()
    {
        Assert.Null(await _fixture.Service.ScanAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Scan_OfASharedLibrary_LeavesTheTransferOwnedFoldersAlone()
    {
        var library = await _shared.GetAsync();
        var timeline = Path.Combine(_folder, "timeline");
        Directory.CreateDirectory(timeline);
        File.WriteAllBytes(Path.Combine(timeline, "IMG_0001.jpg"), [1, 1, 1]);
        var now = DateTimeOffset.UtcNow;
        await _shared.UpsertSourceAsync(new ViewSharedSource(
            Guid.NewGuid(), library.LibraryId, ViewSourceType.Folder, "Timeline", "shared:timeline", null, now, now,
            ViewSourceStorageMode.Linked, ExternalPath: timeline, ContentKind: ViewContentKinds.Photos));

        var result = Assert.IsType<LocalAssetScanResultDto>(await _fixture.Service.ScanAsync(library.LibraryId));

        Assert.Equal(0, result.FilesSeen);
        Assert.Equal(0, _fixture.Repository.Query(new LocalAssetQuery(library.LibraryId, Limit: 20, IncludeHidden: true)).Total);
    }

    [Fact]
    public async Task IndexPath_ForAFileThatArrivesInASharedFolder_IndexesItIntoTheSharedLibrary()
    {
        var library = await _shared.GetAsync();
        await AddLinkedFolderAsync(library.LibraryId, ViewContentKinds.Photos);
        var arrived = Write("2025/2025-02-02/IMG_0009.jpg", [9, 9, 9]);

        var upserted = await _fixture.Service.IndexPathAsync(library.LibraryId, arrived);

        Assert.NotNull(upserted);
        Assert.True(upserted.ItemAdded);
        Assert.Equal("shared", _fixture.Repository.Find(upserted.ItemId)!.ScopeKind);
    }

    [Fact]
    public async Task IndexPath_OutsideEverySharedFolder_IsRefused()
    {
        var library = await _shared.GetAsync();
        await AddLinkedFolderAsync(library.LibraryId, ViewContentKinds.Photos);
        var outside = Path.Combine(_fixture.PersonalRoot, "outside.jpg");
        File.WriteAllBytes(outside, [1]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _fixture.Service.IndexPathAsync(library.LibraryId, outside));
    }

    [Fact]
    public async Task SharedItemsFromAFolder_AreVisibleToTheHouseholdsSharedScope()
    {
        var library = await _shared.GetAsync();
        await AddLinkedFolderAsync(library.LibraryId, ViewContentKinds.Photos);
        Write("IMG_0001.jpg", [1, 1, 1]);
        await _fixture.Service.ScanAsync(library.LibraryId);
        using (var connection = _fixture.Database.CreateConnection())
        {
            connection.Execute("""
                UPDATE local_item_metadata SET latitude = 48.8566, longitude = 2.3522
                 WHERE item_id IN (SELECT id FROM local_items WHERE library_id = @libraryId);
                """, new { libraryId = library.LibraryId });
        }

        var places = new ViewDiscoveryRepository(_fixture.Database).QueryPlaces(
            new ViewPlaceDiscoveryQuery([library.LibraryId], IncludeSharedLibraryAssets: true));
        var otherHousehold = new ViewDiscoveryRepository(_fixture.Database).QueryPlaces(
            new ViewPlaceDiscoveryQuery([Guid.NewGuid()], IncludeSharedLibraryAssets: true));
        var itemId = Assert.Single(_fixture.Repository.Query(new LocalAssetQuery(library.LibraryId, Limit: 5, IncludeHidden: true)).Items).Id;
        var descriptor = await new ViewResourcePersistenceService(
            _fixture.Repository, new ViewGalleryRepository(_fixture.Database), _fixture.Spaces, _fixture.Database)
            .FindAsync(ViewResourceKind.Asset, itemId, Guid.Empty);

        Assert.True(places.HasEligibleData);
        Assert.False(otherHousehold.HasEligibleData);
        Assert.True(descriptor!.IsSharedLibraryAsset);
    }

    [Fact]
    public async Task ASharedFolderFileDeletedOutsideLibrary_IsMarkedMissingByTheFolderCheck()
    {
        var library = await _shared.GetAsync();
        var source = await AddLinkedFolderAsync(library.LibraryId, ViewContentKinds.Photos);
        var kept = Write("IMG_0001.jpg", [1, 1, 1]);
        var gone = Write("IMG_0002.jpg", [2, 2, 2]);
        await _fixture.Service.ScanAsync(library.LibraryId);
        File.Delete(gone);
        var sweeper = new ViewMissingFileService(
            _fixture.Storage, new ViewMissingFileRepository(_fixture.Database), _fixture.Configuration,
            new SystemActivityRepository(_fixture.Database), TimeProvider.System, NullLogger<ViewMissingFileService>.Instance);

        var changed = await sweeper.SweepAsync(library.LibraryId);

        Assert.Equal(1, changed);
        Assert.True(File.Exists(kept));
        Assert.False((await _shared.GetSourcesAsync()).Single(candidate => candidate.Id == source.Id).FolderOffline);
    }

    private async Task<ViewSharedSource> AddLinkedFolderAsync(Guid sharedLibraryId, string contentKind)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        return await _shared.UpsertSourceAsync(new ViewSharedSource(
            id, sharedLibraryId, ViewSourceType.Folder, "Family photos", $"shared:linked:{id:N}", null, now, now,
            ViewSourceStorageMode.Linked, ExternalPath: _folder, IncludeSubdirectories: true,
            Enabled: true, ContentKind: contentKind));
    }

    private string Write(string relativePath, byte[] content)
    {
        var path = Path.Combine(_folder, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        _fixture.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
