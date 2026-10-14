using Dapper;
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

    [Fact]
    public async Task Scan_ReadsPdfAndOfficeTextInTheBackgroundAndSearchFindsIt()
    {
        using var fixture = new ViewFixture();
        fixture.WritePersonal("lease.pdf", DocumentFixtures.Pdf("zeppelin hangar rental"));
        fixture.WritePersonal("minutes.docx", DocumentFixtures.Docx("quarterly gazebo budget"));
        fixture.WritePersonal("ledger.xlsx", DocumentFixtures.Xlsx("marmalade invoices"));
        fixture.WritePersonal("pitch.pptx", DocumentFixtures.Pptx("lighthouse keeper roadmap"));
        fixture.WritePersonal("letter.rtf", @"{\rtf1\ansi Dear \b trombone\b0  society\par}");

        var scan = await fixture.Service.ScanAsync(fixture.PersonalLibraryId);

        Assert.Equal(5, scan!.ItemsAdded);
        Assert.Equal(0, fixture.Search("zeppelin"));
        await fixture.DrainTextQueueAsync();
        Assert.Equal(1, fixture.Search("zeppelin hangar"));
        Assert.Equal(1, fixture.Search("gazebo"));
        Assert.Equal(1, fixture.Search("marmalade"));
        Assert.Equal(1, fixture.Search("lighthouse"));
        Assert.Equal(1, fixture.Search("trombone"));
        Assert.All(
            fixture.Repository.Query(new LocalAssetQuery(fixture.PersonalLibraryId, Limit: 20, IncludeHidden: true)).Items,
            item => Assert.Equal(LocalAssetTextSources.Embedded, fixture.TextSource(item.Id)));
    }

    [Fact]
    public async Task TextRead_IsIdempotentAndRescanDoesNotQueueTwice()
    {
        using var fixture = new ViewFixture();
        fixture.WritePersonal("lease.pdf", DocumentFixtures.Pdf("zeppelin hangar"));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);

        Assert.Equal(0, await fixture.Worker.RunBatchAsync(CancellationToken.None));
        var item = Assert.Single(fixture.Repository.Query(new LocalAssetQuery(fixture.PersonalLibraryId, Limit: 5)).Items);
        Assert.True((await fixture.TextReader.ReadItemAsync(item.Id, CancellationToken.None)).Status == DocumentTextReadStatus.Read);
        Assert.True((await fixture.TextReader.ReadItemAsync(item.Id, CancellationToken.None)).Status == DocumentTextReadStatus.Read);
        Assert.Equal(1, fixture.Search("zeppelin"));
    }

    [Fact]
    public async Task EditedDocumentAtTheSamePathUpdatesTheSameItemAndReplacesOldWords()
    {
        using var fixture = new ViewFixture();
        var path = fixture.WritePersonal("plan.pdf", DocumentFixtures.Pdf("zeppelin hangar"));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();
        var original = Assert.Single(fixture.Repository.Query(new LocalAssetQuery(fixture.PersonalLibraryId, Limit: 5)).Items);
        await fixture.Repository.SetFlagsAsync(original.Id, favorite: true, hidden: null);
        await fixture.Repository.ReplaceTagsAsync(original.Id, ["keep"]);

        System.IO.File.WriteAllBytes(path, DocumentFixtures.Pdf("submarine garage"));
        var rescan = await fixture.Service.ScanAsync(fixture.PersonalLibraryId);

        Assert.Equal(0, rescan!.ItemsAdded);
        Assert.Equal(1, fixture.ItemCount());
        Assert.Equal(0, fixture.Search("zeppelin"));
        await fixture.DrainTextQueueAsync();
        Assert.Equal(0, fixture.Search("zeppelin"));
        Assert.Equal(1, fixture.Search("submarine garage"));
        var updated = Assert.IsType<MediaEngine.Contracts.LocalAssets.LocalAssetDto>(fixture.Repository.Find(original.Id));
        Assert.True(updated.Favorite);
        Assert.Contains("keep", updated.Tags);
        Assert.Equal(
            LocalAssetContentHashFor(path),
            fixture.Repository.ResolveContent(original.Id)!.ContentHash,
            ignoreCase: true);
    }

    [Fact]
    public async Task CorruptEncryptedAndEmptyDocumentsRecordNoneWithoutStoppingTheBatch()
    {
        using var fixture = new ViewFixture();
        fixture.WritePersonal("broken.pdf", "%PDF-1.4 this is not really a pdf"u8.ToArray());
        fixture.WritePersonal("locked.docx", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 1, 2, 3]);
        fixture.WritePersonal("blank.pdf", DocumentFixtures.Pdf(string.Empty));
        fixture.WritePersonal("good.docx", DocumentFixtures.Docx("persimmon harvest"));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);

        await fixture.DrainTextQueueAsync();

        var items = fixture.Repository.Query(new LocalAssetQuery(fixture.PersonalLibraryId, Limit: 20, IncludeHidden: true)).Items;
        string Source(string name) => fixture.TextSource(items.Single(item => item.Title == name).Id)!;
        Assert.Equal(LocalAssetTextSources.None, Source("broken"));
        Assert.Equal(LocalAssetTextSources.None, Source("locked"));
        Assert.Equal(LocalAssetTextSources.None, Source("blank"));
        Assert.Equal(LocalAssetTextSources.Embedded, Source("good"));
        var locked = items.Single(item => item.Title == "locked");
        Assert.Contains("\"document_encrypted\":true", fixture.Metadata(locked.Id));
        Assert.Equal(1, fixture.Search("persimmon"));
    }

    [Fact]
    public async Task LongDocumentTextIsTruncatedAndFlagged()
    {
        using var fixture = new ViewFixture();
        var path = fixture.WritePersonal("long.docx", DocumentFixtures.Docx(string.Join(' ', Enumerable.Repeat("filler", 400)) + " needleword"));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);

        var result = DocumentTextReader.Read(path, new DocumentTextReadLimits(100, 10, TimeSpan.FromSeconds(30)), CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Equal(100, result.Text!.Length);
    }

    [Fact]
    public void StartupChecksCanRunTwiceAndLeaveTheTextSourceColumn()
    {
        using var fixture = new ViewFixture();
        using var first = new DatabaseConnection(fixture.DatabasePath);
        first.RunStartupChecks();
        first.RunStartupChecks();
        using var connection = first.CreateConnection();
        Assert.Contains(
            connection.Query<string>("SELECT name FROM pragma_table_info('local_item_metadata');"),
            name => name == "text_source");
    }

    [Fact]
    public async Task DocumentTimeline_SearchHighlightsWordsInSnippetsAndPagesByCursor()
    {
        using var fixture = new ViewFixture();
        fixture.WritePersonal("one.pdf", DocumentFixtures.Pdf("the zeppelin hangar rental agreement for spring"));
        fixture.WritePersonal("two.docx", DocumentFixtures.Docx("another zeppelin schedule for the summer"));
        fixture.WritePersonal("three.txt", "a zeppelin note without a table");
        fixture.WritePersonal("photo.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();

        var query = new LocalAssetTimelineQuery(
            [fixture.PersonalLibraryId], Limit: 2, Search: "zeppelin", MediaKinds: [LocalAssetMediaKinds.Document]);
        var first = fixture.Repository.QueryTimeline(query);

        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);
        Assert.All(first.Items, item =>
        {
            Assert.NotNull(item.Snippet);
            Assert.Contains("zeppelin", item.Snippet!, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(item.DocumentInfo);
            Assert.Equal(LocalAssetTextSources.Embedded, item.DocumentInfo!.TextSource);
            Assert.True(item.DocumentInfo.HasText);
        });

        var second = fixture.Repository.QueryTimeline(query with
        {
            BeforeEffectiveAt = first.NextCursor!.EffectiveAt,
            BeforeItemId = first.NextCursor.ItemId,
        });
        var all = first.Items.Concat(second.Items).Select(item => item.Id).ToList();
        Assert.Single(second.Items);
        Assert.False(second.HasMore);
        Assert.Equal(3, all.Distinct().Count());
    }

    [Fact]
    public async Task DocumentTimeline_WithoutSearchHasNoSnippetsAndReportsTextFlags()
    {
        using var fixture = new ViewFixture();
        fixture.WritePersonal("memo.pdf", DocumentFixtures.Pdf("quarterly gazebo budget"));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();

        var page = fixture.Repository.QueryTimeline(new LocalAssetTimelineQuery(
            [fixture.PersonalLibraryId], MediaKinds: [LocalAssetMediaKinds.Document]));

        var item = Assert.Single(page.Items);
        Assert.Null(item.Snippet);
        Assert.False(item.DocumentInfo!.Encrypted);
        Assert.False(item.DocumentInfo.TextTruncated);
    }

    [Fact]
    public async Task DocumentText_IsReturnedBoundedAndOnlyForDocuments()
    {
        using var fixture = new ViewFixture();
        var path = fixture.WritePersonal("memo.txt", string.Join(' ', Enumerable.Repeat("filler", 100)) + " needleword");
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();
        var item = Assert.Single(fixture.Repository.Query(new LocalAssetQuery(fixture.PersonalLibraryId, Limit: 5)).Items);

        var cut = fixture.Repository.GetDocumentText(item.Id, 50);
        var whole = fixture.Repository.GetDocumentText(item.Id, 100_000);

        Assert.True(fixture.Repository.IsDocument(item.Id));
        Assert.False(fixture.Repository.IsDocument(Guid.NewGuid()));
        Assert.Equal(50, cut!.Value.Text.Length);
        Assert.True(cut.Value.Cut);
        Assert.Contains("needleword", whole!.Value.Text);
        Assert.False(whole.Value.Cut);
        Assert.NotNull(path);
    }

    private static string LocalAssetContentHashFor(string path) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(path)));

    internal sealed class ViewFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"tuvima-view-api-{Guid.NewGuid():N}");
        private readonly string _databasePath;
        private readonly ConfigurationDirectoryLoader _configuration;
        private readonly DatabaseConnection _database;

        public ViewFixture(MediaEngine.Api.Services.LocalAssets.Ocr.IOcrService? ocr = null, bool withFiling = false)
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
            Spaces = spaces;
            var space = spaces.CreateAsync(OwnerProfileId, PersonalLibraryId).GetAwaiter().GetResult();
            PersonalSpaceId = space.Id;
            var now = DateTimeOffset.UtcNow;
            spaces.UpsertSourceAsync(new ViewSource(
                Guid.NewGuid(), space.Id, ViewSourceType.Folder, "Existing personal files", "test:personal",
                null, now, now, ViewSourceStorageMode.Linked, ExternalPath: PersonalRoot,
                IncludeSubdirectories: true, Enabled: true)).GetAwaiter().GetResult();
            var storage = new ViewStorageService(_configuration, spaces, new ViewSharedLibraryRepository(_database));
            Operations = new MediaOperationRepository(_database);
            if (ocr is not null)
            {
                OcrQueue = new MediaEngine.Api.Services.LocalAssets.Ocr.DocumentOcrQueue(
                    Operations, Repository, ocr, _configuration,
                    NullLogger<MediaEngine.Api.Services.LocalAssets.Ocr.DocumentOcrQueue>.Instance);
            }

            if (withFiling)
            {
                FilingRepository = new DocumentFilingRepository(_database);
                RulesRepository = new DocumentRuleRepository(_database);
                Catalogue = new ViewCatalogueRepository(_database);
                RulesQueue = new MediaEngine.Api.Services.LocalAssets.Filing.DocumentRulesQueue(
                    Operations, FilingRepository,
                    NullLogger<MediaEngine.Api.Services.LocalAssets.Filing.DocumentRulesQueue>.Instance);
                FilingWorker = new MediaEngine.Api.Services.LocalAssets.Filing.DocumentApplyRulesOperationWorker(
                    Operations,
                    new MediaEngine.Api.Services.LocalAssets.Filing.DocumentFilingService(
                        FilingRepository, NullLogger<MediaEngine.Api.Services.LocalAssets.Filing.DocumentFilingService>.Instance),
                    NullLogger<MediaEngine.Api.Services.LocalAssets.Filing.DocumentApplyRulesOperationWorker>.Instance);
            }

            Service = new ViewLibraryService(
                Repository,
                _configuration,
                spaces,
                storage,
                NullLogger<ViewLibraryService>.Instance,
                operations: Operations,
                ocrQueue: OcrQueue,
                rulesQueue: RulesQueue);
            TextReader = new DocumentTextReadService(
                Repository, _configuration, NullLogger<DocumentTextReadService>.Instance, OcrQueue, RulesQueue);
            Worker = new DocumentTextReadOperationWorker(
                Operations, TextReader, NullLogger<DocumentTextReadOperationWorker>.Instance);
        }

        public MediaOperationRepository Operations { get; }
        public ViewPersonalSpaceRepository Spaces { get; }
        public Guid PersonalSpaceId { get; }
        public DocumentFilingRepository? FilingRepository { get; }
        public DocumentRuleRepository? RulesRepository { get; }
        public ViewCatalogueRepository? Catalogue { get; }
        public MediaEngine.Api.Services.LocalAssets.Filing.DocumentRulesQueue? RulesQueue { get; }
        public MediaEngine.Api.Services.LocalAssets.Filing.DocumentApplyRulesOperationWorker? FilingWorker { get; }

        /// <summary>Runs the auto-filing worker until its queue is empty.</summary>
        public async Task DrainFilingQueueAsync()
        {
            while (await FilingWorker!.RunBatchAsync(CancellationToken.None) > 0)
            {
            }
        }

        public MediaEngine.Api.Services.LocalAssets.Ocr.DocumentOcrQueue? OcrQueue { get; }
        public ConfigurationDirectoryLoader Configuration => _configuration;
        public DocumentTextReadService TextReader { get; }
        public DocumentTextReadOperationWorker Worker { get; }

        public string DatabasePath => _databasePath;

        public int Search(string words) => Repository.Query(new LocalAssetQuery(
            PersonalLibraryId, Search: words, Limit: 20, IncludeHidden: true)).Items.Count;

        public string? TextSource(Guid itemId)
        {
            using var connection = _database.CreateConnection();
            return connection.ExecuteScalar<string?>(
                "SELECT text_source FROM local_item_metadata WHERE item_id = @itemId;", new { itemId });
        }

        public string? Metadata(Guid itemId)
        {
            using var connection = _database.CreateConnection();
            return connection.ExecuteScalar<string?>(
                "SELECT metadata_json FROM local_item_metadata WHERE item_id = @itemId;", new { itemId });
        }

        public int ItemCount()
        {
            using var connection = _database.CreateConnection();
            return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM local_items;");
        }

        /// <summary>Runs the text-read worker until the queue is empty.</summary>
        public async Task DrainTextQueueAsync()
        {
            while (await Worker.RunBatchAsync(CancellationToken.None) > 0)
            {
            }
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
