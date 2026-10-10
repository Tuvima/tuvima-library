using System.Net;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace MediaEngine.Providers.Tests;

public sealed class CoverArtWorkerCentralStorageTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _libraryRoot;
    private readonly DatabaseConnection _db;
    private readonly MediaAssetRepository _assetRepo;
    private readonly CanonicalValueRepository _canonicalRepo;
    private readonly WorkRepository _workRepo;
    private readonly EntityAssetRepository _entityAssetRepo;
    private readonly AssetPathService _assetPaths;

    public CoverArtWorkerCentralStorageTests()
    {
        DapperConfiguration.Configure();

        _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_cover_worker_{Guid.NewGuid():N}");
        _libraryRoot = Path.Combine(_tempRoot, "library");
        Directory.CreateDirectory(_libraryRoot);

        _db = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();

        _assetRepo = new MediaAssetRepository(_db);
        _canonicalRepo = new CanonicalValueRepository(_db);
        _workRepo = new WorkRepository(_db);
        _entityAssetRepo = new EntityAssetRepository(_db);
        _assetPaths = new AssetPathService(_libraryRoot);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task DownloadAndPersistAsync_WritesCentralCoverRenditionsAndPalette()
    {
        var workId = await _workRepo.InsertStandaloneAsync(MediaType.Movies);
        var assetId = await SeedAssetForExistingWorkAsync(workId, Path.Combine("Movies", "Arrival (2016).mkv"));
        await SeedCanonicalsAsync(
            workId,
            ("cover", "https://images.test/poster.jpg"),
            ("title", "Arrival"),
            ("wikidata_qid", "QMOVIE"));

        var worker = new CoverArtWorker(
            _assetRepo,
            _canonicalRepo,
            _workRepo,
            new NoOpImageCacheRepository(),
            new RoutingHttpClientFactory(_ => ImageResponse(CreateTestImageBytes())),
            _assetPaths,
            NullLogger<CoverArtWorker>.Instance,
            assetExportService: null,
            coverArtHash: null,
            entityAssetRepo: _entityAssetRepo);

        await worker.DownloadAndPersistAsync(assetId, null, CancellationToken.None);

        var lineage = await _workRepo.GetLineageByAssetAsync(assetId);
        var ownerEntityId = lineage?.TargetForParentScope ?? assetId;
        var coverAsset = Assert.Single(await _entityAssetRepo.GetByEntityAsync(ownerEntityId.ToString(), "CoverArt"));

        Assert.True(File.Exists(coverAsset.LocalImagePath));
        Assert.True(File.Exists(coverAsset.LocalImagePathSmall));
        Assert.True(File.Exists(coverAsset.LocalImagePathMedium));
        Assert.True(File.Exists(coverAsset.LocalImagePathLarge));
        Assert.Equal(ArtworkAspectClasses.Square, coverAsset.AspectClass);
        Assert.False(string.IsNullOrWhiteSpace(coverAsset.PrimaryHex));
        Assert.False(string.IsNullOrWhiteSpace(coverAsset.SecondaryHex));
        Assert.False(string.IsNullOrWhiteSpace(coverAsset.AccentHex));
        Assert.False(File.Exists(_assetPaths.GetCentralDerivedPath("Work", ownerEntityId, "hero", "hero.jpg")));
    }

    [Fact]
    public async Task DownloadAndPersistAsync_ConcurrentEntitiesDownloadSharedUrlOnce()
    {
        var firstWorkId = await _workRepo.InsertStandaloneAsync(MediaType.Movies);
        var firstAssetId = await SeedAssetForExistingWorkAsync(firstWorkId, Path.Combine("Movies", "First.mkv"));
        var secondWorkId = await _workRepo.InsertStandaloneAsync(MediaType.Movies);
        var secondAssetId = await SeedAssetForExistingWorkAsync(secondWorkId, Path.Combine("Movies", "Second.mkv"));
        const string sharedUrl = "https://images.test/shared-poster.jpg";
        await SeedCanonicalsAsync(firstWorkId, ("cover", sharedUrl), ("title", "First"));
        await SeedCanonicalsAsync(secondWorkId, ("cover", sharedUrl), ("title", "Second"));

        var httpFactory = new CountingImageHttpClientFactory(CreateTestImageBytes());
        var worker = new CoverArtWorker(
            _assetRepo,
            _canonicalRepo,
            _workRepo,
            new ImageCacheRepository(_db),
            httpFactory,
            _assetPaths,
            NullLogger<CoverArtWorker>.Instance,
            assetExportService: null,
            coverArtHash: null,
            entityAssetRepo: _entityAssetRepo,
            imageDownloadCoordinator: new MediaEngine.Providers.Services.ImageDownloadCoordinator());

        await Task.WhenAll(
            worker.DownloadAndPersistAsync(firstAssetId, null, CancellationToken.None),
            worker.DownloadAndPersistAsync(secondAssetId, null, CancellationToken.None));

        Assert.Equal(1, httpFactory.RequestCount);
        var firstCover = Assert.Single(await _entityAssetRepo.GetByEntityAsync(firstWorkId.ToString(), "CoverArt"));
        var secondCover = Assert.Single(await _entityAssetRepo.GetByEntityAsync(secondWorkId.ToString(), "CoverArt"));
        Assert.True(File.Exists(firstCover.LocalImagePath));
        Assert.True(File.Exists(secondCover.LocalImagePath));
    }

    [Fact]
    public async Task DownloadAndPersistAsync_ComicIssueReadsCoverFromSelfScopedWork()
    {
        var parentWorkId = await _workRepo.InsertParentAsync(
            MediaType.Comics,
            "comic:saga",
            grandparentWorkId: null,
            ordinal: null);
        var issueWorkId = await _workRepo.InsertChildAsync(MediaType.Comics, parentWorkId, ordinal: 1);
        var assetId = await SeedAssetForExistingWorkAsync(issueWorkId, Path.Combine("Comics", "Saga", "Saga 001.cbz"));
        await SeedCanonicalsAsync(
            issueWorkId,
            ("cover", "https://images.test/saga-001.jpg"),
            ("issue_title", "Chapter One"));

        var worker = new CoverArtWorker(
            _assetRepo,
            _canonicalRepo,
            _workRepo,
            new NoOpImageCacheRepository(),
            new RoutingHttpClientFactory(_ => ImageResponse(CreateTestImageBytes())),
            _assetPaths,
            NullLogger<CoverArtWorker>.Instance,
            assetExportService: null,
            coverArtHash: null,
            entityAssetRepo: _entityAssetRepo);

        await worker.DownloadAndPersistAsync(assetId, null, CancellationToken.None);

        var coverAsset = Assert.Single(await _entityAssetRepo.GetByEntityAsync(issueWorkId.ToString(), "CoverArt"));
        Assert.True(File.Exists(coverAsset.LocalImagePath));
        Assert.True(File.Exists(coverAsset.LocalImagePathSmall));
        Assert.True(File.Exists(coverAsset.LocalImagePathMedium));
        Assert.True(File.Exists(coverAsset.LocalImagePathLarge));
        Assert.False(string.IsNullOrWhiteSpace(coverAsset.PrimaryHex));

        var parentAssets = await _entityAssetRepo.GetByEntityAsync(parentWorkId.ToString(), "CoverArt");
        Assert.Empty(parentAssets);
    }

    [Fact]
    public async Task DownloadAndPersistAsync_MusicTrackReadsEnrichmentCoverFromAlbumWork()
    {
        var albumWorkId = await _workRepo.InsertParentAsync(
            MediaType.Music,
            "music:album:a-night-at-the-opera",
            grandparentWorkId: null,
            ordinal: null);
        var trackWorkId = await _workRepo.InsertChildAsync(MediaType.Music, albumWorkId, ordinal: 11);
        var assetId = await SeedAssetForExistingWorkAsync(trackWorkId, Path.Combine("Music", "Queen", "Bohemian Rhapsody.flac"));
        await SeedCanonicalsAsync(
            albumWorkId,
            ("cover_url", "https://images.test/apple-album-cover.jpg"),
            ("album", "A Night at the Opera"));

        var worker = new CoverArtWorker(
            _assetRepo,
            _canonicalRepo,
            _workRepo,
            new NoOpImageCacheRepository(),
            new RoutingHttpClientFactory(_ => ImageResponse(CreateTestImageBytes())),
            _assetPaths,
            NullLogger<CoverArtWorker>.Instance,
            assetExportService: null,
            coverArtHash: null,
            entityAssetRepo: _entityAssetRepo);

        await worker.DownloadAndPersistAsync(assetId, null, CancellationToken.None);

        var coverAsset = Assert.Single(await _entityAssetRepo.GetByEntityAsync(albumWorkId.ToString(), "CoverArt"));
        Assert.Equal("https://images.test/apple-album-cover.jpg", coverAsset.ImageUrl);
        Assert.True(File.Exists(coverAsset.LocalImagePath));
        Assert.True(File.Exists(coverAsset.LocalImagePathSmall));
        Assert.True(File.Exists(coverAsset.LocalImagePathMedium));
        Assert.True(File.Exists(coverAsset.LocalImagePathLarge));
        Assert.False(string.IsNullOrWhiteSpace(coverAsset.PrimaryHex));

        var trackAssets = await _entityAssetRepo.GetByEntityAsync(trackWorkId.ToString(), "CoverArt");
        Assert.Empty(trackAssets);
    }

    [Fact]
    public async Task DownloadAndPersistAsync_TvShowWithManagedTvdbPosterAndNoCoverClaim_IsNotMarkedMissing()
    {
        // TheTVDB emits no show `cover` claim; its poster is stored as an entity asset by
        // ImageEnrichmentService. The claim-less cover pass must not overwrite that with "missing".
        var showWorkId = await _workRepo.InsertParentAsync(MediaType.TV, "tv:solo-leveling", grandparentWorkId: null, ordinal: null);
        var seasonWorkId = await _workRepo.InsertParentAsync(MediaType.TV, "tv:solo-leveling:s1", grandparentWorkId: showWorkId, ordinal: 1);
        var episodeWorkId = await _workRepo.InsertChildAsync(MediaType.TV, seasonWorkId, ordinal: 1);
        var assetId = await SeedAssetForExistingWorkAsync(episodeWorkId, Path.Combine("TV", "Solo Leveling", "S01E01.mkv"));
        await SeedCanonicalsAsync(showWorkId, ("tvdb_id", "389597"), ("show_name", "Solo Leveling"));

        var posterId = Guid.NewGuid();
        var posterPath = _assetPaths.GetCentralAssetPath("Work", showWorkId, "CoverArt", posterId, ".jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(posterPath)!);
        await File.WriteAllBytesAsync(posterPath, CreateTestImageBytes());
        await _entityAssetRepo.UpsertAsync(new EntityAsset
        {
            Id = posterId,
            EntityId = showWorkId.ToString(),
            EntityType = "Work",
            AssetTypeValue = "CoverArt",
            ImageUrl = "https://artworks.thetvdb.com/banners/v4/series/389597/posters/a.jpg",
            LocalImagePath = posterPath,
            SourceProvider = "tvdb",
            IsPreferred = true,
        });
        await SeedCanonicalsAsync(showWorkId, ("cover_state", "missing"), ("cover_source", "none"));

        var worker = new CoverArtWorker(
            _assetRepo,
            _canonicalRepo,
            _workRepo,
            new NoOpImageCacheRepository(),
            new RoutingHttpClientFactory(_ => ImageResponse(CreateTestImageBytes())),
            _assetPaths,
            NullLogger<CoverArtWorker>.Instance,
            assetExportService: null,
            coverArtHash: null,
            entityAssetRepo: _entityAssetRepo);

        await worker.DownloadAndPersistAsync(assetId, null, CancellationToken.None);

        var canonicals = await _canonicalRepo.GetByEntityAsync(showWorkId);
        Assert.Equal("present", canonicals.Single(value => value.Key == MetadataFieldConstants.CoverState).Value);
        Assert.Equal("provider", canonicals.Single(value => value.Key == MetadataFieldConstants.CoverSource).Value);
        Assert.Equal(
            $"/stream/artwork/{posterId}",
            canonicals.Single(value => value.Key == MetadataFieldConstants.CoverUrl).Value);
    }

    [Fact]
    public async Task DownloadAndPersistAsync_ClaimLessOwnerWithoutStoredCover_IsMarkedMissing()
    {
        var workId = await _workRepo.InsertStandaloneAsync(MediaType.Movies);
        var assetId = await SeedAssetForExistingWorkAsync(workId, Path.Combine("Movies", "NoArt.mkv"));
        await SeedCanonicalsAsync(workId, ("title", "No Art"));

        var worker = new CoverArtWorker(
            _assetRepo,
            _canonicalRepo,
            _workRepo,
            new NoOpImageCacheRepository(),
            new RoutingHttpClientFactory(_ => ImageResponse(CreateTestImageBytes())),
            _assetPaths,
            NullLogger<CoverArtWorker>.Instance,
            assetExportService: null,
            coverArtHash: null,
            entityAssetRepo: _entityAssetRepo);

        await worker.DownloadAndPersistAsync(assetId, null, CancellationToken.None);

        var canonicals = await _canonicalRepo.GetByEntityAsync(workId);
        Assert.Equal("missing", canonicals.Single(value => value.Key == MetadataFieldConstants.CoverState).Value);
        Assert.Equal("none", canonicals.Single(value => value.Key == MetadataFieldConstants.CoverSource).Value);
    }

    [Fact]
    public async Task ReplaceProviderArtworkAsync_BookReplacesOldManagedArtworkAndKeepsUserUpload()
    {
        var seriesWorkId = await _workRepo.InsertParentAsync(
            MediaType.Books,
            "books:lord-of-the-rings",
            grandparentWorkId: null,
            ordinal: null);
        var bookWorkId = await _workRepo.InsertChildAsync(MediaType.Books, seriesWorkId, ordinal: 2);
        var assetId = await SeedAssetForExistingWorkAsync(bookWorkId, Path.Combine("Books", "The Two Towers.epub"));
        await SeedCanonicalsAsync(
            bookWorkId,
            ("cover", "https://images.test/old-two-towers.jpg"),
            ("cover_source", "apple_books"),
            ("title", "The Two Towers"));
        await SeedCanonicalsAsync(
            assetId,
            ("cover", "https://images.test/stale-asset-scope-two-towers.jpg"),
            ("cover_source", "apple_books"));

        var worker = new CoverArtWorker(
            _assetRepo,
            _canonicalRepo,
            _workRepo,
            new NoOpImageCacheRepository(),
            new RoutingHttpClientFactory(_ => ImageResponse(CreateTestImageBytes())),
            _assetPaths,
            NullLogger<CoverArtWorker>.Instance,
            assetExportService: null,
            coverArtHash: null,
            entityAssetRepo: _entityAssetRepo);

        await worker.DownloadAndPersistAsync(assetId, null, CancellationToken.None);
        var oldCover = Assert.Single(await _entityAssetRepo.GetByEntityAsync(bookWorkId.ToString(), "CoverArt"));
        var oldCoverPath = oldCover.LocalImagePath!;

        var staleBackgroundId = Guid.NewGuid();
        var staleBackgroundPath = _assetPaths.GetCentralAssetPath("Work", bookWorkId, "Background", staleBackgroundId, ".jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(staleBackgroundPath)!);
        await File.WriteAllBytesAsync(staleBackgroundPath, CreateTestImageBytes());
        await _entityAssetRepo.UpsertAsync(new EntityAsset
        {
            Id = staleBackgroundId,
            EntityId = bookWorkId.ToString(),
            EntityType = "Work",
            AssetTypeValue = "Background",
            ImageUrl = "https://images.test/old-background.jpg",
            LocalImagePath = staleBackgroundPath,
            SourceProvider = "wikidata",
            IsPreferred = true,
            IsUserOverride = false,
        });
        await SeedCanonicalsAsync(bookWorkId, ("background_url", $"/stream/artwork/{staleBackgroundId}"));

        var userLogoId = Guid.NewGuid();
        var userLogoPath = _assetPaths.GetCentralAssetPath("Work", bookWorkId, "Logo", userLogoId, ".jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(userLogoPath)!);
        await File.WriteAllBytesAsync(userLogoPath, CreateTestImageBytes());
        await _entityAssetRepo.UpsertAsync(new EntityAsset
        {
            Id = userLogoId,
            EntityId = bookWorkId.ToString(),
            EntityType = "Work",
            AssetTypeValue = "Logo",
            LocalImagePath = userLogoPath,
            SourceProvider = "user_upload",
            IsPreferred = true,
            IsUserOverride = true,
        });

        var result = await worker.ReplaceProviderArtworkAsync(
            assetId,
            "https://images.test/new-two-towers.jpg",
            "open_library",
            WellKnownProviders.OpenLibrary,
            CancellationToken.None);

        Assert.True(result.ArtworkChanged);
        Assert.True(result.CoverDownloaded);
        Assert.Equal(bookWorkId, result.OwnerEntityId);
        Assert.Equal(2, result.RemovedVariantCount);

        var covers = await _entityAssetRepo.GetByEntityAsync(bookWorkId.ToString(), "CoverArt");
        var replacement = Assert.Single(covers);
        Assert.Equal("https://images.test/new-two-towers.jpg", replacement.ImageUrl);
        Assert.True(replacement.IsPreferred);
        Assert.True(File.Exists(replacement.LocalImagePath));
        Assert.False(File.Exists(oldCoverPath));
        Assert.False(File.Exists(staleBackgroundPath));
        Assert.Empty(await _entityAssetRepo.GetByEntityAsync(bookWorkId.ToString(), "Background"));
        Assert.Single(await _entityAssetRepo.GetByEntityAsync(bookWorkId.ToString(), "Logo"));
        Assert.True(File.Exists(userLogoPath));

        var canonicals = await _canonicalRepo.GetByEntityAsync(bookWorkId);
        Assert.Equal(
            "https://images.test/new-two-towers.jpg",
            canonicals.Single(value => value.Key == MetadataFieldConstants.Cover).Value);
        Assert.Equal(
            $"/stream/artwork/{replacement.Id}",
            canonicals.Single(value => value.Key == MetadataFieldConstants.CoverUrl).Value);
        Assert.DoesNotContain(canonicals, value => value.Key == "background_url");

        var assetCanonicals = await _canonicalRepo.GetByEntityAsync(assetId);
        Assert.DoesNotContain(assetCanonicals, value => value.Key == MetadataFieldConstants.Cover);
        Assert.DoesNotContain(assetCanonicals, value => value.Key == MetadataFieldConstants.CoverUrl);
    }

    private async Task<Guid> SeedAssetForExistingWorkAsync(Guid workId, string relativeFilePath)
    {
        var editionId = Guid.NewGuid();
        var filePath = Path.Combine(_libraryRoot, relativeFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, [0, 1, 2]);

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO editions (id, work_id) VALUES (@EditionId, @WorkId);",
            new
            {
                EditionId = editionId,
                WorkId = workId,
            });

        var assetId = Guid.NewGuid();
        await _assetRepo.InsertAsync(new MediaAsset
        {
            Id = assetId,
            EditionId = editionId,
            ContentHash = $"hash_{assetId:N}",
            FilePathRoot = filePath,
            Status = AssetStatus.Normal,
        });

        return assetId;
    }

    private Task SeedCanonicalsAsync(Guid entityId, params (string Key, string Value)[] values)
    {
        return _canonicalRepo.UpsertBatchAsync(values
            .Select(value => new CanonicalValue
            {
                EntityId = entityId,
                Key = value.Key,
                Value = value.Value,
                LastScoredAt = DateTimeOffset.UtcNow,
            })
            .ToList());
    }

    private static HttpResponseMessage ImageResponse(byte[] bytes) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };

    private static byte[] CreateTestImageBytes()
    {
        using var bitmap = new SKBitmap(16, 16);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.DarkOrange);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private sealed class NoOpImageCacheRepository : IImageCacheRepository
    {
        public Task<string?> FindByHashAsync(string contentHash, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task InsertAsync(string contentHash, string filePath, string? sourceUrl = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsUserOverrideAsync(string contentHash, CancellationToken ct = default) => Task.FromResult(false);
        public Task<string?> FindBySourceUrlAsync(string sourceUrl, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetUserOverrideAsync(string contentHash, bool isOverride, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetPerceptualHashAsync(string contentHash, ulong phash, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ulong?> GetPerceptualHashAsync(string contentHash, CancellationToken ct = default) => Task.FromResult<ulong?>(null);
    }

    private sealed class RoutingHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RoutingHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        public HttpClient CreateClient(string name)
            => new(new RoutingHttpMessageHandler(_responder), disposeHandler: true);
    }

    private sealed class RoutingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RoutingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class CountingImageHttpClientFactory(byte[] imageBytes) : IHttpClientFactory
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public HttpClient CreateClient(string name) =>
            new(new CountingImageHttpMessageHandler(this, imageBytes), disposeHandler: true);

        private sealed class CountingImageHttpMessageHandler(
            CountingImageHttpClientFactory owner,
            byte[] bytes) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._requestCount);
                await Task.Delay(50, cancellationToken);
                return ImageResponse(bytes);
            }
        }
    }
}
