using System.Net;
using System.Text;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using StorageHttpClientConfig = MediaEngine.Domain.Configuration.HttpClientConfig;
using StorageProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Providers.Tests;

public sealed class ImageEnrichmentServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _libraryRoot;
    private readonly string _configRoot;
    private readonly DatabaseConnection _db;
    private readonly EntityAssetRepository _entityAssets;
    private readonly MediaAssetRepository _mediaAssets;
    private readonly CanonicalValueRepository _canonicals;
    private readonly WorkRepository _works;
    private readonly ImageCacheRepository _imageCache;
    private readonly AssetPathService _assetPaths;
    private readonly ConfigurationDirectoryLoader _configLoader;

    public ImageEnrichmentServiceTests()
    {
        DapperConfiguration.Configure();

        _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_image_enrichment_{Guid.NewGuid():N}");
        _libraryRoot = Path.Combine(_tempRoot, "library");
        _configRoot = Path.Combine(_tempRoot, "config");
        Directory.CreateDirectory(_libraryRoot);

        _db = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();

        _entityAssets = new EntityAssetRepository(_db);
        _mediaAssets = new MediaAssetRepository(_db);
        _canonicals = new CanonicalValueRepository(_db);
        _works = new WorkRepository(_db);
        _imageCache = new ImageCacheRepository(_db);
        _assetPaths = new AssetPathService(_libraryRoot);
        _configLoader = new ConfigurationDirectoryLoader(_configRoot);
        _configLoader.SaveProvider(new StorageProviderConfiguration
        {
            Name = "tmdb",
            Enabled = true,
            HttpClient = new StorageHttpClientConfig
            {
                ApiKey = "test-key",
                TimeoutSeconds = 10,
            },
        });
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task DiscoverComicArtwork_UsesIssueIdentityAndReturnsAllImagesWithoutDownloading()
    {
        _configLoader.SaveProvider(new StorageProviderConfiguration { Name = "comicvine", Enabled = true,
            HttpClient = new StorageHttpClientConfig { ApiKey = "test-key" } });
        var comic = await SeedStandaloneAssetAsync(MediaType.Comics, "Comics", "Comics", "Issue.cbz");
        await SeedCanonicalsAsync(comic.WorkId, (BridgeIdKeys.ComicVineId, "4000-42"));
        var requests = new List<string>();
        var service = CreateService(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal("/api/issue/4000-42/", request.RequestUri.AbsolutePath);
            return JsonResponse("""{"status_code":1,"results":{"image":{"original_url":"https://comicvine.gamespot.com/a/uploads/original/cover.jpg","small_url":"https://comicvine.gamespot.com/a/uploads/scale_small/cover.jpg"},"associated_images":[{"original_url":"https://comicvine.gamespot.com/a/uploads/original/variant.jpg"}]}}""");
        });
        var result = await service.DiscoverArtworkAsync(comic.AssetId, "issue", "Primary");
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, c => { Assert.Equal("Comic Vine", c.Provider); Assert.Contains("/scale_small/", c.ThumbnailUrl); });
        Assert.Single(requests);
        using var connection = _db.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM artwork_assets;"));
        var unsupported = await service.DiscoverArtworkAsync(comic.AssetId, "issue", "Logo");
        Assert.Empty(unsupported.Items);
        Assert.Single(requests);
    }

    [Fact]
    public void ProviderThumbnails_DoNotFallBackToUnknownOriginals()
    {
        Assert.Equal("https://covers.openlibrary.org/b/id/123-M.jpg", ProviderArtworkThumbnails.ForCover("https://covers.openlibrary.org/b/id/123-L.jpg"));
        Assert.Equal("https://m.media-amazon.com/images/I/example._SL300_.jpg", ProviderArtworkThumbnails.ForCover("https://m.media-amazon.com/images/I/example.jpg"));
        Assert.Empty(ProviderArtworkThumbnails.ForCover("https://provider.test/original.png"));
    }

    [Fact]
    public async Task DiscoverArtwork_ReturnsRoleSpecificThumbnailsWithoutDownloadingOrPersistingImages()
    {
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Example.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "42"));
        var requests = new List<string>();
        var service = CreateService(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal("/3/movie/42/images", request.RequestUri.AbsolutePath);
            return JsonResponse("""{"backdrops":[{"file_path":"/wide.jpg","width":1920,"height":1080}],"posters":[{"file_path":"/cover.jpg","width":600,"height":900}]}""");
        });
        var result = await service.DiscoverArtworkAsync(movie.AssetId, "item", "Background");
        var candidate = Assert.Single(result.Items);
        Assert.Equal("https://image.tmdb.org/t/p/w300/wide.jpg", candidate.ThumbnailUrl);
        Assert.Equal("https://image.tmdb.org/t/p/original/wide.jpg", candidate.Url);
        Assert.Equal(1920, candidate.Width);
        Assert.Single(requests);
        using var connection = _db.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM entity_assets;"));
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM artwork_assets;"));
    }

    [Fact]
    public async Task DiscoverArtwork_MissingIdentityDoesNotSearchByTitle()
    {
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Example.mkv");
        var service = CreateService(_ => throw new InvalidOperationException("No provider call expected"));
        var result = await service.DiscoverArtworkAsync(movie.AssetId, "item", "Primary");
        Assert.Empty(result.Items);
        Assert.Contains("Match & Identity", result.Message);
    }

    [Fact]
    public async Task RefreshTvEpisodeStill_ReportsMissingTmdbImageWithoutCreatingArtwork()
    {
        var requests = new List<string>();
        var service = CreateService(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return JsonResponse("""{"stills":[]}""");
        });

        var result = await service.RefreshTvEpisodeStillAsync(Guid.NewGuid(), "42", 2, 8, null);

        Assert.False(result.Changed);
        Assert.Contains("no still", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/3/tv/42/season/2/episode/8/images"], requests);
        using var connection = _db.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM entity_assets;"));
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_MovieArtwork_PreservesUserCoverOverride()
    {
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Arrival (2016).mkv");
        await SeedCanonicalsAsync(
            movie.WorkId,
            ("media_type", "Movies"),
            (BridgeIdKeys.TmdbId, "12345"));

        var existingCoverId = Guid.NewGuid();
        var existingCoverPath = _assetPaths.GetCentralAssetPath("Work", movie.WorkId, "CoverArt", existingCoverId, ".jpg");
        AssetPathService.EnsureDirectory(existingCoverPath);
        await File.WriteAllBytesAsync(existingCoverPath, [9, 9, 9]);

        await _entityAssets.UpsertAsync(new EntityAsset
        {
            Id = existingCoverId,
            EntityId = movie.WorkId.ToString(),
            EntityType = "Work",
            AssetTypeValue = "CoverArt",
            LocalImagePath = existingCoverPath,
            SourceProvider = "user_upload",
            AssetClassValue = "Artwork",
            StorageLocationValue = "Central",
            OwnerScope = "Work",
            IsPreferred = true,
            IsUserOverride = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/movie/12345/images?", StringComparison.OrdinalIgnoreCase))
            {
                var payload = """
                    {
                      "posters": [
                        { "file_path": "/movie-poster.jpg", "vote_average": 7.0, "vote_count": 11, "iso_639_1": null, "width": 1000, "height": 1500 }
                      ],
                      "logos": [],
                      "backdrops": []
                    }
                    """;
                return JsonResponse(payload);
            }

            return ImageResponse([1, 2, 3, 4]);
        });

        var result = await service.EnrichWorkImagesAsync(movie.AssetId, "Q12345");

        var coverAssets = await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt");
        var userCover = Assert.Single(coverAssets, asset => asset.IsUserOverride);
        Assert.True(userCover.IsPreferred);
        Assert.Contains(coverAssets, asset => !asset.IsUserOverride && !asset.IsPreferred && string.Equals(asset.SourceProvider, "tmdb", StringComparison.OrdinalIgnoreCase));

        Assert.True(result.Success);
        Assert.Equal(BridgeIdKeys.TmdbId, result.BridgeKey);
        Assert.Equal("12345", result.BridgeId);
        Assert.Equal(1, result.DownloadedCount);
        Assert.Equal(1, result.StoredVariantCounts["CoverArt"]);
        var diagnostics = await _canonicals.GetByEntityAsync(movie.WorkId);
        Assert.Contains(diagnostics, value => value.Key == "tmdb_artwork_status" && value.Value == "Completed");
        Assert.Contains(diagnostics, value => value.Key == "tmdb_artwork_bridge_id" && value.Value == "12345");
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_MusicIsIntentionallyUnsupported()
    {
        var album = await SeedStandaloneAssetAsync(MediaType.Music, "Music", "Music", Path.Combine("Artist", "Album", "01 - Track.flac"));
        await SeedCanonicalsAsync(
            album.WorkId,
            ("media_type", "Music"),
            ("musicbrainz_release_group_id", "rg-123"));

        var service = CreateService(_ => throw new InvalidOperationException("TMDB must not be called for music."));

        var result = await service.EnrichWorkImagesAsync(album.AssetId, "QALBUM");

        Assert.True(result.Skipped);
        Assert.Equal("unsupported_media_type", result.SkippedReason);
        Assert.Empty(await _entityAssets.GetByEntityAsync(album.WorkId.ToString(), "CoverArt"));
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_PrefersAdministratorApiKeyOverride()
    {
        var provider = _configLoader.LoadProvider("tmdb")!;
        provider.HttpClient!.ApiKeyOverride = "administrator-key";
        _configLoader.SaveProvider(provider);

        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Override.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "42"));

        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            Assert.Contains("api_key=administrator-key", url, StringComparison.Ordinal);
            Assert.DoesNotContain("api_key=test-key", url, StringComparison.Ordinal);
            return JsonResponse("""{ "posters": [], "logos": [], "backdrops": [] }""");
        });

        var result = await service.EnrichWorkImagesAsync(movie.AssetId, "Q42");

        Assert.Equal("NoImages", result.Status);
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_UsesConfiguredPosterAndLogoLanguageWithNeutralBackdrop()
    {
        var core = _configLoader.LoadCore();
        core.Language.Metadata = "en-US";
        _configLoader.SaveCore(core);
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Language.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "77"));

        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/movie/77/images?", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains("include_image_language=en,null", url, StringComparison.Ordinal);
                return JsonResponse("""
                    {
                      "posters": [
                        { "file_path": "/poster-en.jpg", "iso_639_1": "en", "width": 1000, "height": 1500, "vote_average": 10 },
                        { "file_path": "/poster-neutral.jpg", "iso_639_1": null, "width": 1000, "height": 1500, "vote_average": 5 }
                      ],
                      "logos": [
                        { "file_path": "/logo-neutral.png", "iso_639_1": null, "width": 1200, "height": 500, "vote_average": 9 },
                        { "file_path": "/logo-en.png", "iso_639_1": "en", "width": 1200, "height": 500, "vote_average": 10 }
                      ],
                      "backdrops": [
                        { "file_path": "/backdrop-en.jpg", "iso_639_1": "en", "width": 1920, "height": 1080, "vote_average": 10 },
                        { "file_path": "/backdrop-neutral.jpg", "iso_639_1": null, "width": 1920, "height": 1080, "vote_average": 5 }
                      ]
                    }
                    """);
            }

            return url.Contains("/logo-", StringComparison.OrdinalIgnoreCase)
                ? ImageResponse(CreateLogoPng(hasVisibleContent: true))
                : ImageResponse([1, 2, 3, 4]);
        });

        await service.EnrichWorkImagesAsync(movie.AssetId, "Q77");

        var posters = await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt");
        Assert.Single(posters);
        Assert.EndsWith("/poster-en.jpg", posters[0].ImageUrl, StringComparison.Ordinal);
        var preferredLogo = await _entityAssets.GetPreferredAsync(movie.WorkId.ToString(), "Logo");
        Assert.EndsWith("/logo-en.png", preferredLogo?.ImageUrl, StringComparison.Ordinal);
        var preferredBackdrop = await _entityAssets.GetPreferredAsync(movie.WorkId.ToString(), "Background");
        Assert.EndsWith("/backdrop-neutral.jpg", preferredBackdrop?.ImageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_RejectsUndecodableAndBlankLogosBeforeSelectingFallback()
    {
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Logo Validation.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "78"));

        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/movie/78/images?", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                    {
                      "posters": [],
                      "backdrops": [],
                      "logos": [
                        { "file_path": "/unsupported.svg", "iso_639_1": "en", "width": 1200, "height": 500, "vote_average": 10 },
                        { "file_path": "/transparent.png", "iso_639_1": "en", "width": 1200, "height": 500, "vote_average": 9 },
                        { "file_path": "/corrupt.png", "iso_639_1": "en", "width": 1200, "height": 500, "vote_average": 8 },
                        { "file_path": "/usable.png", "iso_639_1": "en", "width": 1200, "height": 500, "vote_average": 7 }
                      ]
                    }
                    """);
            }

            if (url.EndsWith("/unsupported.svg", StringComparison.OrdinalIgnoreCase))
            {
                return ImageResponse(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"));
            }

            if (url.EndsWith("/transparent.png", StringComparison.OrdinalIgnoreCase))
            {
                return ImageResponse(CreateLogoPng(hasVisibleContent: false));
            }

            return url.EndsWith("/usable.png", StringComparison.OrdinalIgnoreCase)
                ? ImageResponse(CreateLogoPng(hasVisibleContent: true))
                : ImageResponse([1, 2, 3, 4]);
        });

        var result = await service.EnrichWorkImagesAsync(movie.AssetId, "Q78");

        var logos = await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "Logo");
        var logo = Assert.Single(logos);
        Assert.True(logo.IsPreferred);
        Assert.EndsWith("/usable.png", logo.ImageUrl, StringComparison.Ordinal);
        Assert.Equal(1, result.StoredVariantCounts["Logo"]);
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_TvEnrichesEveryRepresentedSeasonFromOneEpisode()
    {
        var show = await _works.InsertParentAsync(MediaType.TV, "show:all-seasons", null, null);
        var seasonOne = await _works.InsertParentAsync(MediaType.TV, $"season:{show}:1", show, 1);
        var seasonTwo = await _works.InsertParentAsync(MediaType.TV, $"season:{show}:2", show, 2);
        var episode = await _works.InsertChildAsync(MediaType.TV, seasonOne, 1);
        var asset = await SeedAssetForExistingWorkAsync(episode, Path.Combine("TV", "All Seasons", "Season 01", "s01e01.mkv"));
        await SeedCanonicalsAsync(show, ("media_type", "TV"), (BridgeIdKeys.TmdbId, "88"));

        var requestedSeasons = new HashSet<int>();
        var rootRequestCount = 0;
        var seasonRequestCount = 0;
        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/tv/88/images?", StringComparison.OrdinalIgnoreCase))
            {
                rootRequestCount++;
                return JsonResponse("""{ "posters": [], "logos": [], "backdrops": [] }""");
            }

            if (url.Contains("/season/", StringComparison.OrdinalIgnoreCase))
            {
                var seasonNumber = url.Contains("/season/1/", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                seasonRequestCount++;
                requestedSeasons.Add(seasonNumber);
                return JsonResponse($$"""
                    { "posters": [{ "file_path": "/season-{{seasonNumber}}.jpg", "iso_639_1": null, "width": 1000, "height": 1500 }], "backdrops": [] }
                    """);
            }

            return ImageResponse([1, 2, 3, 4]);
        });

        await service.EnrichWorkImagesAsync(asset.AssetId, "Q88");
        await service.EnrichWorkImagesAsync(asset.AssetId, "Q88");

        Assert.Equal([1, 2], requestedSeasons.OrderBy(value => value));
        Assert.Equal(1, rootRequestCount);
        Assert.Equal(2, seasonRequestCount);
        Assert.Single(await _entityAssets.GetByEntityAsync(seasonOne.ToString(), "SeasonPoster"));
        Assert.Single(await _entityAssets.GetByEntityAsync(seasonTwo.ToString(), "SeasonPoster"));
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_StoresNetworkAndStudioLogosAsManagedAssets()
    {
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Studio Logos.mkv");
        await SeedCanonicalsAsync(
            movie.WorkId,
            (BridgeIdKeys.TmdbId, "84"),
            ("network_logo_url", "https://image.tmdb.org/t/p/original/network.png"),
            ("studio_logo_url", "https://image.tmdb.org/t/p/original/studio.png"));

        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            return url.Contains("/movie/84/images?", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse("""{ "posters": [], "logos": [], "backdrops": [] }""")
                : ImageResponse([1, 2, 3, 4]);
        });

        var result = await service.EnrichWorkImagesAsync(movie.AssetId, "Q84");

        Assert.Equal(2, result.DownloadedCount);
        Assert.Single(await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), AssetType.NetworkLogo.ToString()));
        Assert.Single(await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), AssetType.StudioLogo.ToString()));
        var canonicals = await _canonicals.GetByEntityAsync(movie.WorkId);
        Assert.Contains(canonicals, value => value.Key == "network_logo_url" && value.Value.StartsWith("/stream/artwork/", StringComparison.Ordinal));
        Assert.Contains(canonicals, value => value.Key == "studio_logo_url" && value.Value.StartsWith("/stream/artwork/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_ReusesSharedSourceUrlAcrossWorks()
    {
        var first = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "First Shared.mkv");
        var second = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Second Shared.mkv");
        await SeedCanonicalsAsync(first.WorkId, (BridgeIdKeys.TmdbId, "111"));
        await SeedCanonicalsAsync(second.WorkId, (BridgeIdKeys.TmdbId, "222"));

        var imageRequestCount = 0;
        const string sharedImageUrl = "https://image.tmdb.org/t/p/original/shared-poster.jpg";
        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/movie/111/images?", StringComparison.OrdinalIgnoreCase)
                || url.Contains("/movie/222/images?", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse($$"""
                    {
                      "posters": [ { "file_path": "/shared-poster.jpg", "vote_average": 7, "vote_count": 1, "iso_639_1": null } ],
                      "logos": [], "backdrops": []
                    }
                    """);
            }

            if (string.Equals(url, sharedImageUrl, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref imageRequestCount);
            }

            return ImageResponse([1, 2, 3, 4]);
        });

        await service.EnrichWorkImagesAsync(first.AssetId, "QFIRST");
        await service.EnrichWorkImagesAsync(second.AssetId, "QSECOND");

        Assert.Equal(1, imageRequestCount);
        Assert.Single(await _entityAssets.GetByEntityAsync(first.WorkId.ToString(), "CoverArt"));
        Assert.Single(await _entityAssets.GetByEntityAsync(second.WorkId.ToString(), "CoverArt"));
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_TvSeasonAndEpisodeArt_AttachesToResolvedChildWorks()
    {
        var show = await _works.InsertParentAsync(MediaType.TV, "show:the-expanse", null, null);
        var season = await _works.InsertParentAsync(MediaType.TV, $"season:{show}:1", show, 1);
        var episode = await _works.InsertChildAsync(MediaType.TV, season, 2);
        var asset = await SeedAssetForExistingWorkAsync(episode, Path.Combine("TV", "The Expanse", "Season 01", "The Expanse - s01e02 - Episode.mkv"));

        await SeedCanonicalsAsync(
            show,
            ("media_type", "TV"),
            ("tmdb_id", "54321"));
        await SeedCanonicalsAsync(season, (MetadataFieldConstants.SeasonNumber, "1"));

        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/tv/54321/images?", StringComparison.OrdinalIgnoreCase))
            {
                var payload = """
                    {
                      "posters": [], "logos": [], "backdrops": []
                    }
                    """;
                return JsonResponse(payload);
            }
            if (url.Contains("/tv/54321/season/1/images?", StringComparison.OrdinalIgnoreCase))
                return JsonResponse("""
                    {
                      "posters": [
                        { "file_path": "/season-poster-en.jpg", "iso_639_1": "en", "width": 1000, "height": 1500 },
                        { "file_path": "/season-poster-neutral.jpg", "iso_639_1": null, "width": 1000, "height": 1500 }
                      ],
                      "backdrops": [
                        { "file_path": "/season-thumb-en.jpg", "iso_639_1": "en", "width": 1920, "height": 1080, "vote_average": 10 },
                        { "file_path": "/season-thumb-neutral.jpg", "iso_639_1": null, "width": 1920, "height": 1080, "vote_average": 5 }
                      ]
                    }
                    """);

            return ImageResponse([7, 7, 7, 7]);
        });

        await service.EnrichWorkImagesAsync(asset.AssetId, "QSHOW");

        var seasonPoster = Assert.Single(await _entityAssets.GetByEntityAsync(season.ToString(), "SeasonPoster"));
        var seasonThumb = Assert.Single(await _entityAssets.GetByEntityAsync(season.ToString(), "SeasonThumb"));

        Assert.Equal("Season", seasonPoster.OwnerScope);
        Assert.Equal("Season", seasonThumb.OwnerScope);
        Assert.EndsWith("/season-poster-en.jpg", seasonPoster.ImageUrl, StringComparison.Ordinal);
        Assert.EndsWith("/season-thumb-neutral.jpg", seasonThumb.ImageUrl, StringComparison.Ordinal);
        Assert.True(File.Exists(seasonPoster.LocalImagePath));
        Assert.True(File.Exists(seasonThumb.LocalImagePath));
        Assert.Empty(await _entityAssets.GetByEntityAsync(episode.ToString(), "EpisodeStill"));
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_TvEpisodeStill_DoesNotGuessSeasonForDirectShowChild()
    {
        var show = await _works.InsertParentAsync(MediaType.TV, "show:direct-episode", null, null);
        var episode = await _works.InsertChildAsync(MediaType.TV, show, 2);
        var asset = await SeedAssetForExistingWorkAsync(episode, Path.Combine("TV", "Direct Episode Show", "Season 01", "Direct - s01e02.mkv"));

        await SeedCanonicalsAsync(
            show,
            ("media_type", "TV"),
            ("tmdb_id", "54321"));

        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/tv/54321/images?", StringComparison.OrdinalIgnoreCase))
            {
                var payload = """
                    {
                      "posters": [], "logos": [], "backdrops": []
                    }
                    """;
                return JsonResponse(payload);
            }

            return ImageResponse([8, 8, 8, 8]);
        });

        await service.EnrichWorkImagesAsync(asset.AssetId, "QSHOW");

        Assert.Empty(await _entityAssets.GetByEntityAsync(episode.ToString(), "EpisodeStill"));
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_TvEpisodeStill_DoesNotUseTmdbUrlCanonicalFallback()
    {
        var show = await _works.InsertParentAsync(MediaType.TV, "show:tmdb-fallback", null, null);
        var season = await _works.InsertParentAsync(MediaType.TV, $"season:{show}:1", show, 1);
        var episode = await _works.InsertChildAsync(MediaType.TV, season, 2);
        var asset = await SeedAssetForExistingWorkAsync(episode, Path.Combine("TV", "Fallback Show", "Season 01", "Fallback - s01e02.mkv"));

        await SeedCanonicalsAsync(
            show,
            ("media_type", "TV"),
            ("tmdb_id", "54321"));
        await SeedCanonicalsAsync(
            episode,
            ("episode_still_url", "https://images.test/tmdb-episode-still.jpg"));

        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/tv/54321/images?", StringComparison.OrdinalIgnoreCase))
            {
                var payload = """
                    {
                      "posters": [], "logos": [], "backdrops": []
                    }
                    """;
                return JsonResponse(payload);
            }

            return ImageResponse([9, 9, 9, 9]);
        });

        await service.EnrichWorkImagesAsync(asset.AssetId, "QSHOW");

        var episodeStills = await _entityAssets.GetByEntityAsync(episode.ToString(), "EpisodeStill");
        Assert.Empty(episodeStills);
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_MissingBridgeId_ReturnsStructuredSkipAndDiagnostics()
    {
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Missing Bridge.mkv");

        var service = CreateService(_ => throw new InvalidOperationException("TMDB should not be called without a bridge ID."));

        var result = await service.EnrichWorkImagesAsync(movie.AssetId, "Q123");

        Assert.True(result.Skipped);
        Assert.Equal("missing_bridge_id", result.SkippedReason);
        Assert.Equal("Movies", result.MediaType);
        Assert.Equal(0, result.DownloadedCount);

        var diagnostics = await _canonicals.GetByEntityAsync(movie.WorkId);
        Assert.Contains(diagnostics, value => value.Key == "tmdb_artwork_status" && value.Value == "Skipped");
        Assert.Contains(diagnostics, value => value.Key == "tmdb_artwork_skipped_reason" && value.Value == "missing_bridge_id");
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_ProviderNoResult_RecordsHttpDiagnostics()
    {
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "No Result.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "404"));

        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            return url.Contains("/movie/404/images?", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : ImageResponse([1, 2, 3]);
        });

        var result = await service.EnrichWorkImagesAsync(movie.AssetId, "Q404");

        Assert.True(result.Skipped);
        Assert.Equal("NoResult", result.Status);
        Assert.Equal("provider_no_result", result.SkippedReason);
        Assert.Equal(404, result.HttpStatusCode);

        var diagnostics = await _canonicals.GetByEntityAsync(movie.WorkId);
        Assert.Contains(diagnostics, value => value.Key == "tmdb_artwork_status" && value.Value == "NoResult");
        Assert.Contains(diagnostics, value => value.Key == "tmdb_artwork_http_status" && value.Value == "404");
    }

    private ImageEnrichmentService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        return new ImageEnrichmentService(
            _entityAssets,
            _mediaAssets,
            new StubCharacterPortraitRepository(),
            _canonicals,
            _works,
            new StubFictionalEntityRepository(),
            new StubPersonRepository(),
            new StubProviderConfigurationRepository(),
            _configLoader,
            _imageCache,
            _assetPaths,
            new StubAssetExportService(),
            new RoutingHttpClientFactory(responder),
            new StubFuzzyMatchingService(),
            NullLogger<ImageEnrichmentService>.Instance);
    }

    private async Task<(Guid WorkId, Guid AssetId)> SeedStandaloneAssetAsync(
        MediaType mediaType,
        string mediaTypeFolder,
        string canonicalMediaType,
        string relativeFilePath)
    {
        var workId = await _works.InsertStandaloneAsync(mediaType);
        var asset = await SeedAssetForExistingWorkAsync(workId, Path.Combine(mediaTypeFolder, relativeFilePath));
        await SeedCanonicalsAsync(workId, ("media_type", canonicalMediaType));
        return (workId, asset.AssetId);
    }

    private async Task<(Guid EditionId, Guid AssetId, string FilePath)> SeedAssetForExistingWorkAsync(Guid workId, string relativeFilePath)
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
        await _mediaAssets.InsertAsync(new MediaAsset
        {
            Id = assetId,
            EditionId = editionId,
            ContentHash = $"hash_{assetId:N}",
            FilePathRoot = filePath,
            Status = AssetStatus.Normal,
        });

        return (editionId, assetId, filePath);
    }

    private Task SeedCanonicalsAsync(Guid entityId, params (string Key, string Value)[] values)
    {
        return _canonicals.UpsertBatchAsync(values
            .Select(value => new CanonicalValue
            {
                EntityId = entityId,
                Key = value.Key,
                Value = value.Value,
                LastScoredAt = DateTimeOffset.UtcNow,
            })
            .ToList());
    }

    private static HttpResponseMessage JsonResponse(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage ImageResponse(byte[] bytes) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };

    private static byte[] CreateLogoPng(bool hasVisibleContent)
    {
        using var bitmap = new SKBitmap(320, 100, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        if (hasVisibleContent)
        {
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawRoundRect(new SKRect(30, 25, 290, 75), 8, 8, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
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

    private sealed class StubProviderConfigurationRepository : IProviderConfigurationRepository
    {
        public Task<IReadOnlyList<MediaEngine.Domain.Entities.ProviderConfiguration>> GetAllMaskedAsync(string providerId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MediaEngine.Domain.Entities.ProviderConfiguration>>([]);

        public Task<string?> GetDecryptedValueAsync(string providerId, string key, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task UpsertAsync(string providerId, string key, string plaintextValue, bool isSecret, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteAsync(string providerId, string key, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubCharacterPortraitRepository : ICharacterPortraitRepository
    {
        public Task<CharacterPortrait?> FindByIdAsync(Guid portraitId, CancellationToken ct = default)
            => Task.FromResult<CharacterPortrait?>(null);

        public Task<IReadOnlyList<CharacterPortrait>> GetByCharacterAsync(Guid fictionalEntityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CharacterPortrait>>([]);

        public Task<IReadOnlyList<CharacterPortrait>> GetByPersonAsync(Guid personId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CharacterPortrait>>([]);

        public Task<CharacterPortrait?> GetDefaultAsync(Guid fictionalEntityId, CancellationToken ct = default)
            => Task.FromResult<CharacterPortrait?>(null);

        public Task UpsertAsync(CharacterPortrait portrait, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task SetDefaultAsync(Guid portraitId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<CharacterPortrait>> GetByCharacterBatchAsync(IEnumerable<Guid> fictionalEntityIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CharacterPortrait>>([]);
    }

    private sealed class StubFictionalEntityRepository : IFictionalEntityRepository
    {
        public Task<FictionalEntity?> FindByQidAsync(string qid, CancellationToken ct = default) => Task.FromResult<FictionalEntity?>(null);
        public Task<IReadOnlyList<FictionalEntity>> FindByQidsAsync(IEnumerable<string> qids, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FictionalEntity>>([]);
        public Task<FictionalEntity?> FindByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<FictionalEntity?>(null);
        public Task<IReadOnlyList<FictionalEntity>> GetByUniverseAsync(string universeQid, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FictionalEntity>>([]);
        public Task<IReadOnlyList<FictionalEntity>> GetByUniverseAndTypeAsync(string universeQid, string entitySubType, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FictionalEntity>>([]);
        public Task<FictionalEntitySearchPage> SearchVisibleByUniverseAsync(string universeQid, IReadOnlyCollection<string> visibleWorkQids, string? entitySubType, string? search, int offset, int limit, CancellationToken ct = default) => Task.FromResult(new FictionalEntitySearchPage([], 0));
        public Task<IReadOnlyList<FictionalEntity>> GetByWorkQidAsync(string workQid, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FictionalEntity>>([]);
        public Task CreateAsync(FictionalEntity entity, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateEnrichmentAsync(Guid entityId, string? label, string? description, string? imageUrl, DateTimeOffset enrichedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateUserDetailsAsync(Guid entityId, string label, string? description, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateUniverseAsync(Guid entityId, string universeQid, string? universeLabel, CancellationToken ct = default) => Task.CompletedTask;
        public Task LinkToWorkAsync(FictionalEntityWorkLink appearance, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FictionalEntityWorkLink>> GetWorkLinksAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FictionalEntityWorkLink>>([]);
        public Task<IReadOnlyList<FictionalEntityWorkLink>> GetWorkLinksAsync(IEnumerable<Guid> entityIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FictionalEntityWorkLink>>([]);
        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task UpdateRevisionAsync(Guid entityId, long revisionId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FictionalEntity>> GetStaleEntitiesAsync(int staleAfterDays, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FictionalEntity>>([]);
    }

    private sealed class StubPersonRepository : IPersonRepository
    {
        public Task<Person?> FindByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<Person?>(null);
        public Task<IReadOnlyList<Person>> FindByNamesAsync(IEnumerable<string> names, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Person>>([]);
        public Task AddRoleAsync(Guid personId, string role, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetRolesAsync(Guid personId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<Dictionary<string, int>> GetRoleCountsAsync(CancellationToken ct = default) => Task.FromResult(new Dictionary<string, int>());
        public Task<Dictionary<Guid, Dictionary<string, int>>> GetPresenceBatchAsync(IEnumerable<Guid> personIds, CancellationToken ct = default) => Task.FromResult(new Dictionary<Guid, Dictionary<string, int>>());
        public Task<Person> CreateAsync(Person person, CancellationToken ct = default) => Task.FromResult(person);
        public Task UpdateEnrichmentAsync(Guid personId, string? wikidataQid, string? headshotUrl, string? biography, string? name, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateNameAsync(Guid personId, string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateSocialFieldsAsync(Guid personId, string? occupation, string? instagram, string? twitter, string? tiktok, string? mastodon, string? website, CancellationToken ct = default) => Task.CompletedTask;
        public Task LinkToMediaAssetAsync(Guid mediaAssetId, Guid personId, string role, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateLocalHeadshotPathAsync(Guid id, string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Person?> FindByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Person?>(null);
        public Task<IReadOnlyList<Person>> GetByMediaAssetAsync(Guid mediaAssetId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Person>>([]);
        public Task<IReadOnlyList<Person>> GetByMediaAssetsAsync(IEnumerable<Guid> mediaAssetIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Person>>([]);
        public Task<IReadOnlyList<Person>> ListAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Person>>([]);
        public Task<IReadOnlyList<Person>> ListPagedAsync(string? role, int offset, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Person>>([]);
        public Task<int> CountMediaLinksAsync(Guid personId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> CountGraphReferencesAsync(Guid personId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<Person?> FindByQidAsync(string qid, CancellationToken ct = default) => Task.FromResult<Person?>(null);
        public Task<IReadOnlyList<Person>> FindByQidsAsync(IEnumerable<string> qids, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Person>>([]);
        public Task DeleteAsync(Guid personId, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateBiographicalFieldsAsync(Guid personId, string? dateOfBirth, string? dateOfDeath, string? placeOfBirth, string? placeOfDeath, string? nationality, bool isPseudonym, bool isGroup = false, CancellationToken ct = default) => Task.CompletedTask;
        public Task LinkAliasAsync(Guid pseudonymPersonId, Guid realPersonId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Person>> FindAliasesAsync(Guid personId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Person>>([]);
        public Task LinkToCharacterAsync(Guid personId, Guid fictionalEntityId, string? workQid, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<(Guid FictionalEntityId, string? WorkQid)>> GetCharacterLinksAsync(Guid personId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(Guid FictionalEntityId, string? WorkQid)>>([]);
        public Task<IReadOnlyList<(Guid PersonId, Guid FictionalEntityId)>> GetCharacterLinksByWorkAsync(string workQid, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<(Guid PersonId, Guid FictionalEntityId)>>([]);
        public Task<IReadOnlyList<CharacterPerformerCredit>> GetCharacterPerformersAsync(IEnumerable<Guid> fictionalEntityIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CharacterPerformerCredit>>([]);
        public Task ReassignAllLinksAsync(Guid fromPersonId, Guid toPersonId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsPseudonymOrAliasAsync(Guid personId, CancellationToken ct = default) => Task.FromResult(false);
        public Task LinkGroupMemberAsync(Guid groupId, Guid memberId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubAssetExportService : IAssetExportService
    {
        public Task ReconcileAllArtworkAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ReconcileArtworkAsync(string entityId, string entityType, string assetType, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearArtworkExportAsync(string entityId, string entityType, string assetType, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubFuzzyMatchingService : IFuzzyMatchingService
    {
        public double ComputeTokenSetRatio(string a, string b) => 0d;
        public double ComputePartialRatio(string a, string b) => 0d;
        public FieldMatchResult ScoreCandidate(LocalMetadata local, CandidateMetadata candidate) => new();
    }
}
