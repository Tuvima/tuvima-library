using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class ViewLibraryServiceTests
{
    [Fact]
    public async Task Upload_ResolvesOwnersPersonalSpaceWithoutAcceptingLibraryIdentity()
    {
        using var fixture = new ViewFixture();
        await using var content = new MemoryStream([1, 2, 3, 4]);

        var result = await fixture.Service.UploadAsync(
            fixture.OwnerProfileId,
            "phone-photo.jpg",
            content);

        var item = Assert.IsType<MediaEngine.Contracts.LocalAssets.LocalAssetDto>(
            fixture.Repository.Find(result.ItemId));
        Assert.Equal(fixture.OwnerProfileId, item.OwnerProfileId);
        Assert.Equal(fixture.PersonalLibraryId, item.LibraryId);
        Assert.NotEqual(Guid.Empty, item.PersonalSpaceId);
        var location = Assert.IsType<LocalAssetContentLocation>(fixture.Repository.ResolveContent(item.Id));
        var relative = Path.GetRelativePath(fixture.PersonalRoot, location.FilePath);
        Assert.Matches(@"managed-view[\\/]Profiles[\\/][^\\/]+[\\/]Timeline[\\/]\d{4}[\\/]\d{2} - [A-Z][a-z]{2}[\\/][A-Z][a-z]{2} \d{2} - \d{2}\.\d{2}\.\d{2}IMG\.jpg", relative);
    }

    [Fact]
    public async Task Scan_IndexesMixedPersonalMediaInPlaceAndSkipsCatalogueLibraries()
    {
        using var fixture = new ViewFixture();
        var imagePath = fixture.WritePersonal("lake.jpg", [1, 2, 3, 4]);
        fixture.WritePersonal("concert.mp4", [5, 6, 7]);
        fixture.WritePersonal("Road Trip Plan.txt", "Yellowstone itinerary and packing checklist");
        fixture.WritePersonal("voice-note.mp3", [8, 9, 10]);
        fixture.WritePersonal("ignored.bin", [11]);
        fixture.WriteCatalogue("provider-book.txt", "Must never enter the personal local index");
        var originalImage = System.IO.File.ReadAllBytes(imagePath);

        var result = Assert.IsType<MediaEngine.Contracts.LocalAssets.LocalAssetScanResultDto>(
            await fixture.Service.ScanAsync(fixture.PersonalLibraryId));

        Assert.Equal(4, result.FilesSeen);
        Assert.Equal(4, result.ItemsAdded);
        Assert.Equal(4, result.FilesAdded);
        Assert.Equal(4, result.SourcesAdded);
        Assert.Equal(0, result.Errors);
        Assert.Equal(originalImage, System.IO.File.ReadAllBytes(imagePath));

        var page = fixture.Repository.Query(new LocalAssetQuery(
            fixture.PersonalLibraryId,
            Limit: 20,
            IncludeHidden: true));
        Assert.Equal(4, page.Total);
        Assert.Contains(page.Items, item => item.MediaKind == LocalAssetMediaKinds.Image);
        Assert.Contains(page.Items, item => item.MediaKind == LocalAssetMediaKinds.Video);
        Assert.Contains(page.Items, item => item.MediaKind == LocalAssetMediaKinds.Document);
        Assert.Contains(page.Items, item => item.MediaKind == LocalAssetMediaKinds.Audio);
        Assert.Single(fixture.Repository.Query(new LocalAssetQuery(
            fixture.PersonalLibraryId,
            Search: "Yellowstone packing")).Items);
        Assert.Empty(fixture.Repository.Query(new LocalAssetQuery(
            fixture.CatalogueLibraryId,
            Limit: 20,
            IncludeHidden: true)).Items);

        var repeat = Assert.IsType<MediaEngine.Contracts.LocalAssets.LocalAssetScanResultDto>(
            await fixture.Service.ScanAsync(fixture.PersonalLibraryId));
        Assert.Equal(0, repeat.ItemsAdded);
        Assert.Equal(0, repeat.FilesAdded);
        Assert.Equal(0, repeat.SourcesAdded);
        Assert.Equal(4, fixture.Repository.Query(new LocalAssetQuery(
            fixture.PersonalLibraryId,
            Limit: 20,
            IncludeHidden: true)).Total);
    }

    [Fact]
    public async Task Scan_GroupsLivePhotosAndRawCompanionsIntoLogicalItems()
    {
        using var fixture = new ViewFixture();
        fixture.WritePersonal("IMG_1000.HEIC", [1, 1, 1]);
        fixture.WritePersonal("IMG_1000.MOV", [2, 2, 2]);
        fixture.WritePersonal("DSC_2000.NEF", [3, 3, 3]);
        fixture.WritePersonal("DSC_2000.JPG", [4, 4, 4]);
        fixture.WritePersonal("DSC_2000.XMP", [5, 5, 5]);

        var result = Assert.IsType<MediaEngine.Contracts.LocalAssets.LocalAssetScanResultDto>(
            await fixture.Service.ScanAsync(fixture.PersonalLibraryId));

        Assert.Equal(5, result.FilesSeen);
        Assert.Equal(2, result.ItemsAdded);
        Assert.Equal(5, result.FilesAdded);
        var items = fixture.Repository.Query(new LocalAssetQuery(
            fixture.PersonalLibraryId,
            Limit: 20,
            IncludeHidden: true)).Items;
        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Files.Any(file =>
            file.Role == LocalAssetFileRoles.LivePhotoVideo));
        Assert.Contains(items, item =>
            item.Files.Any(file => file.Role == LocalAssetFileRoles.Raw)
            && item.Files.Any(file => file.Role == LocalAssetFileRoles.Sidecar));
    }

    [Fact]
    public async Task Scan_GroupsAdjacentImgVidNamesAsLivePhotoPair()
    {
        using var fixture = new ViewFixture();
        fixture.WritePersonal("Sep 11 - 01.31.35IMG.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        fixture.WritePersonal("Sep 11 - 01.31.34VID.mov", [1, 2, 3]);

        var result = Assert.IsType<MediaEngine.Contracts.LocalAssets.LocalAssetScanResultDto>(
            await fixture.Service.ScanAsync(fixture.PersonalLibraryId));

        Assert.Equal(1, result.ItemsAdded);
        var item = Assert.Single(fixture.Repository.Query(new LocalAssetQuery(
            fixture.PersonalLibraryId,
            Limit: 20,
            IncludeHidden: true)).Items);
        Assert.Contains(item.Files, file => file.Role == LocalAssetFileRoles.LivePhotoVideo);
    }

    [Fact]
    public async Task IndexPath_IndexesOnlyTheUploadedViewItemWithoutCatalogueIngestion()
    {
        using var fixture = new ViewFixture();
        var uploaded = fixture.WritePersonal("uploaded-photo.jpg", [1, 2, 3]);
        fixture.WritePersonal("unrelated-photo.jpg", [4, 5, 6]);

        var result = await fixture.Service.IndexPathAsync(fixture.PersonalLibraryId, uploaded);

        Assert.NotNull(result);
        Assert.True(result.ItemAdded);
        var page = fixture.Repository.Query(new LocalAssetQuery(
            fixture.PersonalLibraryId,
            Limit: 20,
            IncludeHidden: true));
        var item = Assert.Single(page.Items);
        Assert.Equal("uploaded-photo.jpg", item.FileName);
        Assert.Equal(LocalAssetMediaKinds.Image, item.MediaKind);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(uploaded));
    }

    [Fact]
    public async Task IndexPath_IncludesAdjacentLivePhotoCompanion()
    {
        using var fixture = new ViewFixture();
        fixture.WritePersonal("IMG_4000.HEIC", [1, 1]);
        var uploaded = fixture.WritePersonal("IMG_4000.MOV", [2, 2]);

        var result = await fixture.Service.IndexPathAsync(fixture.PersonalLibraryId, uploaded);

        Assert.NotNull(result);
        Assert.Equal(2, result.FilesAdded);
        var item = Assert.Single(fixture.Repository.Query(new LocalAssetQuery(
            fixture.PersonalLibraryId,
            Limit: 20,
            IncludeHidden: true)).Items);
        Assert.Contains(item.Files, file => file.Role == LocalAssetFileRoles.LivePhotoVideo);
    }

    [Fact]
    public async Task IndexPath_RejectsPathsOutsideConfiguredViewSources()
    {
        using var fixture = new ViewFixture();
        var outside = fixture.WriteCatalogue("outside.jpg", "not a View source");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.IndexPathAsync(fixture.PersonalLibraryId, outside));
        Assert.Empty(fixture.Repository.Query(new LocalAssetQuery(
            fixture.PersonalLibraryId,
            Limit: 20,
            IncludeHidden: true)).Items);
    }

    [Fact]
    public async Task Scan_RejectsUnknownOrNonViewLibraryIds()
    {
        using var fixture = new ViewFixture();

        Assert.Null(await fixture.Service.ScanAsync(Guid.NewGuid()));
        Assert.Null(await fixture.Service.ScanAsync(fixture.CatalogueLibraryId));
    }

    private sealed class ViewFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"tuvima-view-api-{Guid.NewGuid():N}");
        private readonly string _databasePath;
        private readonly ConfigurationDirectoryLoader _configuration;
        private readonly DatabaseConnection _database;

        public ViewFixture()
        {
            PersonalRoot = Path.Combine(_root, "personal");
            CatalogueRoot = Path.Combine(_root, "catalogue");
            Directory.CreateDirectory(PersonalRoot);
            Directory.CreateDirectory(CatalogueRoot);
            _databasePath = Path.Combine(_root, "view.db");
            _database = new DatabaseConnection(_databasePath);
            _database.InitializeSchema();
            Repository = new LocalAssetRepository(_database);

            _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
            PersonalLibraryId = Guid.NewGuid();
            CatalogueLibraryId = Guid.NewGuid();
            OwnerProfileId = Guid.NewGuid();
            ProfileTestData.InsertAsync(_database, new Profile
            {
                Id = OwnerProfileId,
                DisplayName = "View owner",
                Role = ProfileRole.RestrictedProfile,
            }).GetAwaiter().GetResult();
            _configuration.SaveLibraries(new LibrariesConfiguration
            {
                StorageLocations =
                [
                    new ServerStorageLocationConfig
                    {
                        Id = "personal",
                        Label = "Personal",
                        Path = PersonalRoot,
                        AllowWrite = true,
                    },
                    new ServerStorageLocationConfig
                    {
                        Id = "catalogue",
                        Label = "Catalogue",
                        Path = CatalogueRoot,
                        AllowWrite = true,
                    },
                ],
                ViewStorage = new ViewStorageConfig
                {
                    StorageLocationId = "personal",
                    RelativeRoot = "managed-view",
                },
                Libraries =
                [
                    CatalogueLibrary(CatalogueLibraryId, CatalogueRoot),
                ],
            });
            var spaces = new ViewPersonalSpaceRepository(_database);
            var space = spaces.CreateAsync(OwnerProfileId, PersonalLibraryId).GetAwaiter().GetResult();
            var now = DateTimeOffset.UtcNow;
            spaces.UpsertSourceAsync(new ViewSource(
                Guid.NewGuid(), space.Id, ViewSourceType.Folder, "Existing personal files", "test:personal",
                null, now, now, ViewSourceStorageMode.Linked, ExternalPath: PersonalRoot,
                IncludeSubdirectories: true, Enabled: true)).GetAwaiter().GetResult();
            var storage = new ViewStorageService(_configuration, spaces, new ViewSharedLibraryRepository(_database));
            Service = new ViewLibraryService(
                Repository,
                _configuration,
                spaces,
                storage,
                NullLogger<ViewLibraryService>.Instance);
        }

        public Guid PersonalLibraryId { get; }
        public Guid CatalogueLibraryId { get; }
        public Guid OwnerProfileId { get; }
        public string PersonalRoot { get; }
        public string CatalogueRoot { get; }
        public LocalAssetRepository Repository { get; }
        public ViewLibraryService Service { get; }

        public string WritePersonal(string fileName, byte[] content) =>
            Write(PersonalRoot, fileName, content);

        public string WritePersonal(string fileName, string content) =>
            Write(PersonalRoot, fileName, System.Text.Encoding.UTF8.GetBytes(content));

        public string WriteCatalogue(string fileName, string content) =>
            Write(CatalogueRoot, fileName, System.Text.Encoding.UTF8.GetBytes(content));

        private static string Write(string root, string fileName, byte[] content)
        {
            var path = Path.Combine(root, fileName);
            System.IO.File.WriteAllBytes(path, content);
            return path;
        }

        private static LibraryFolderConfig PersonalLibrary(Guid id, Guid ownerProfileId, string path)
        {
            var sourceId = Guid.NewGuid().ToString("D");
            return new LibraryFolderConfig
            {
                Id = id.ToString("D"),
                Name = "Shy's Phone Photos",
                Kind = LibraryKinds.Personal,
                Area = LibraryAreas.View,
                Presentation = LibraryPresentations.MixedGallery,
                MetadataPolicy = LibraryMetadataPolicies.LocalOnly,
                MediaTypes = ["Images", "ShortVideos", "Documents", "AudioNotes"],
                OwnerProfileId = ownerProfileId.ToString("D"),
                Visibility = LibraryVisibility.Private,
                AcceptedIntakeModes = [LibraryIntakeModes.BrowserUpload],
                OrganizationPolicy = new LibraryOrganizationPolicyConfig
                {
                    Mode = LibraryOrganizationModes.KeepOriginalFolders,
                    WriteTags = false,
                },
                Sources =
            [
                new LibrarySourceConfig
                {
                    Id = sourceId,
                    Path = path,
                    ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                    SourceType = LibrarySourceTypes.LocalFolder,
                    IncludeSubdirectories = true,
                    AccessMode = LibrarySourceAccessModes.Writable,
                },
            ],
                PrimaryDestinationSourceId = sourceId,
            };
        }

        private static LibraryFolderConfig CatalogueLibrary(Guid id, string path) => new()
        {
            Id = id.ToString("D"),
            Name = "Books",
            Category = "Books",
            Kind = LibraryKinds.Catalogued,
            Area = LibraryAreas.Read,
            Presentation = LibraryPresentations.Catalogue,
            MetadataPolicy = LibraryMetadataPolicies.Enriched,
            MediaTypes = ["Books"],
            Visibility = LibraryVisibility.Household,
            Sources =
            [
                new LibrarySourceConfig
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Path = path,
                    ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
                    SourceType = LibrarySourceTypes.LocalFolder,
                    IncludeSubdirectories = true,
                    AccessMode = LibrarySourceAccessModes.ReadOnly,
                },
            ],
        };

        public void Dispose()
        {
            _configuration.Dispose();
            _database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
