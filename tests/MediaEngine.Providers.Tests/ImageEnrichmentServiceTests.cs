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
using MediaEngine.Providers.Tests.ProviderRecording;
using MediaEngine.Providers.Workers;
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _imageSizes = new(StringComparer.Ordinal);

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
    public async Task RefreshTvdbEpisode_StoresStillOnOwnedEpisodeWithBoundedRenditions()
    {
        ShippedProviderHarness.SaveShipped(_configLoader, "tvdb", "installation-key");
        var show = await _works.InsertParentAsync(MediaType.TV, "tvdb:fixture:show", null, null);
        var episode = await _works.InsertChildAsync(MediaType.TV, show, 1);
        var paths = new List<string>();
        const string stillUrl = "https://artworks.thetvdb.com/episode-still.png";
        var image = CreateLogoPng(hasVisibleContent: true);
        var factory = new RoutingHttpClientFactory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return path switch
            {
                "/v4/login" => JsonResponse("""{"data":{"token":"token"}}"""),
                "/v4/episodes/111/extended" => JsonResponse("""{"data":{"id":111,"seriesId":99,"image":"https://artworks.thetvdb.com/episode-still.png"}}"""),
                "/episode-still.png" => ImageResponse(image),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        var client = new ProviderApiClient(_configLoader, factory, new ProviderRateLimiterCoordinator());
        var service = new ImageEnrichmentService(
            _entityAssets, _mediaAssets, new StubCharacterPortraitRepository(), _canonicals, _works,
            new StubFictionalEntityRepository(), new StubPersonRepository(),
            new StubProviderConfigurationRepository(), _configLoader, _imageCache, _assetPaths,
            new StubAssetExportService(), factory, new StubFuzzyMatchingService(),
            NullLogger<ImageEnrichmentService>.Instance, tvdb: client);

        var result = await service.RefreshTvdbScopeImagesAsync(episode, "episode", "111");

        Assert.Equal("Completed", result.Status);
        Assert.Equal(1, result.DownloadedCount);
        Assert.Contains("/v4/episodes/111/extended", paths);
        Assert.Contains("/episode-still.png", paths);
        var still = Assert.Single(await _entityAssets.GetByEntityAsync(episode.ToString(), "EpisodeStill"));
        Assert.Equal("tvdb", still.SourceProvider);
        Assert.Equal(stillUrl, still.ImageUrl);
        Assert.True(still.IsPreferred);
        Assert.All(new[] { still.LocalImagePath, still.LocalImagePathSmall, still.LocalImagePathMedium, still.LocalImagePathLarge }, path => Assert.True(File.Exists(path)));
        Assert.Empty(await _entityAssets.GetByEntityAsync(show.ToString(), "EpisodeStill"));
        Assert.DoesNotContain(paths, path => path.Contains("tmdb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RefreshTvdbSeason_StoresManagedArtworkWithoutReplacingUserChoice()
    {
        ShippedProviderHarness.SaveShipped(_configLoader, "tvdb", "installation-key");
        var season = await SeedStandaloneAssetAsync(MediaType.TV, "TV", "TV", "Season.mkv");
        var chosen = new EntityAsset
        {
            Id = Guid.NewGuid(),
            EntityId = season.WorkId.ToString(),
            EntityType = "Work",
            AssetTypeValue = "SeasonPoster",
            ImageUrl = "https://example.test/user-poster.png",
            SourceProvider = "user_upload",
            IsPreferred = true,
            IsUserOverride = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _entityAssets.UpsertAsync(chosen);
        var image = CreateLogoPng(hasVisibleContent: true);
        var factory = new RoutingHttpClientFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/v4/login" => JsonResponse("""{"data":{"token":"token"}}"""),
            "/v4/artwork/types" => JsonResponse("""{"data":[{"id":1,"name":"Season Poster"},{"id":2,"name":"Season Background"}]}"""),
            "/v4/seasons/99/extended" => JsonResponse("""{"data":{"id":99,"artworks":[{"type":1,"image":"https://artworks.thetvdb.com/season-poster.png"},{"type":2,"image":"https://artworks.thetvdb.com/season-background.png"}]}}"""),
            _ => ImageResponse(image),
        });
        var client = new ProviderApiClient(_configLoader, factory, new ProviderRateLimiterCoordinator());
        var service = new ImageEnrichmentService(
            _entityAssets, _mediaAssets, new StubCharacterPortraitRepository(), _canonicals, _works,
            new StubFictionalEntityRepository(), new StubPersonRepository(),
            new StubProviderConfigurationRepository(), _configLoader, _imageCache, _assetPaths,
            new StubAssetExportService(), factory, new StubFuzzyMatchingService(),
            NullLogger<ImageEnrichmentService>.Instance, tvdb: client);

        var result = await service.RefreshTvdbScopeImagesAsync(season.WorkId, "season", "99");

        Assert.Equal("Completed", result.Status);
        Assert.Equal(2, result.DownloadedCount);
        var posters = await _entityAssets.GetByEntityAsync(season.WorkId.ToString(), "SeasonPoster");
        Assert.Contains(posters, asset => asset.Id == chosen.Id && asset.IsPreferred);
        Assert.Contains(posters, asset => asset.SourceProvider == "tvdb" && !asset.IsPreferred);
        Assert.Contains(await _entityAssets.GetByEntityAsync(season.WorkId.ToString(), "SeasonThumb"),
            asset => asset.SourceProvider == "tvdb" && asset.IsPreferred);
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_TvdbSiblingEpisodes_RecordTheSameHeldCountWhicheverRunsFirst()
    {
        var (service, show, first, second) = await SeedTvdbShowWithTwoEpisodesAsync();

        var firstResult = await service.EnrichWorkImagesAsync(first.AssetId, null);
        var secondResult = await service.EnrichWorkImagesAsync(second.AssetId, null);

        // The first episode downloads the shared show poster; the second finds it already stored.
        Assert.Equal(2, firstResult.DownloadedCount);
        Assert.Equal(1, secondResult.DownloadedCount);
        // Both still record what they hold: the show poster plus their own still.
        Assert.Equal(2, firstResult.HeldCount);
        Assert.Equal(2, secondResult.HeldCount);
        Assert.Equal("Completed", secondResult.Status);
        Assert.Equal("2", await GetCanonicalAsync(first.WorkId, "tvdb_artwork_held_count"));
        Assert.Equal("2", await GetCanonicalAsync(second.WorkId, "tvdb_artwork_held_count"));
        Assert.Equal("2", await GetCanonicalAsync(second.AssetId, "tvdb_artwork_held_count"));
        // The show records only its own artwork, so the last episode to finish cannot change it.
        Assert.Equal("1", await GetCanonicalAsync(show, "tvdb_artwork_held_count"));
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_ConcurrentTvdbSiblingEpisodes_StoreTheShowPosterOnce()
    {
        // Hold both passes at the show lookup until each has arrived, and slow the poster download,
        // so both read the show's stored artwork before either has saved the poster.
        using var bothArrived = new CountdownEvent(2);
        var (service, show, first, second) = await SeedTvdbShowWithTwoEpisodesAsync(bothArrived);

        var results = await Task.WhenAll(
            service.EnrichWorkImagesAsync(first.AssetId, null),
            service.EnrichWorkImagesAsync(second.AssetId, null));

        Assert.Single(await _entityAssets.GetByEntityAsync(show.ToString(), "CoverArt"));
        Assert.All(results, result => Assert.Equal(2, result.HeldCount));
        Assert.Equal(3, results.Sum(result => result.DownloadedCount));
    }

    [Fact]
    public async Task EnrichWorkImagesAsync_RepeatMoviePass_KeepsCompletedStatusAndHeldCount()
    {
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Repeat Pass.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "777"));
        var imagesRequests = 0;
        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/movie/777/images?", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref imagesRequests);
                return JsonResponse("""
                    {
                      "posters": [ { "file_path": "/repeat-poster.jpg", "vote_average": 7, "vote_count": 1, "iso_639_1": null } ],
                      "logos": [],
                      "backdrops": [ { "file_path": "/repeat-backdrop.jpg", "vote_average": 7, "vote_count": 1, "iso_639_1": null } ]
                    }
                    """);
            }

            return ImageResponse([1, 2, 3, 4]);
        });

        var first = await service.EnrichWorkImagesAsync(movie.AssetId, "QREPEAT");
        var repeat = await service.EnrichWorkImagesAsync(movie.AssetId, "QREPEAT");

        Assert.Equal(1, imagesRequests);
        Assert.Equal("Completed", first.Status);
        Assert.Equal(2, first.HeldCount);
        Assert.Equal("Completed", repeat.Status);
        Assert.Equal(0, repeat.DownloadedCount);
        Assert.Equal(2, repeat.HeldCount);
        Assert.Equal("Completed", await GetCanonicalAsync(movie.WorkId, "tmdb_artwork_status"));
        Assert.Equal("2", await GetCanonicalAsync(movie.WorkId, "tmdb_artwork_held_count"));
    }

    [Theory]
    [InlineData("cover,images")]
    [InlineData("images,cover")]
    [InlineData("cover,images,cover")]
    [InlineData("images,cover,cover")]
    public async Task CoverPassAndImagesPass_InEitherOrder_LeaveTheRankedPosterPreferred(string order)
    {
        // A maintenance sweep can run the images pass before Hydration's cover pass. Whichever runs
        // first, and however often the cover pass repeats ("already cached on disk"), the poster the
        // images pass ranked stays the preferred one rather than the smaller retail cover.
        const string retailCover = "https://image.tmdb.org/t/p/w500/retail-cover.jpg";
        const string rankedPoster = "https://image.tmdb.org/t/p/original/ranked-poster.jpg";
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Pass Order.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "555"), (MetadataFieldConstants.Cover, retailCover));
        var smallCover = CreatePosterJpeg(50, 75);
        var largePoster = CreatePosterJpeg(200, 300);
        HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/movie/555/images?", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                    {
                      "posters": [ { "file_path": "/ranked-poster.jpg", "vote_average": 8, "vote_count": 40, "iso_639_1": null, "width": 2000, "height": 3000 } ],
                      "logos": [],
                      "backdrops": []
                    }
                    """);
            }

            return ImageResponse(url.Contains("/w500/", StringComparison.Ordinal) ? smallCover : largePoster);
        }

        var images = CreateService(Respond);
        var coverArt = CreateCoverArtWorker(Respond);

        foreach (var pass in order.Split(','))
        {
            if (pass == "cover")
            {
                await coverArt.DownloadAndPersistAsync(movie.AssetId, wikidataQid: null, CancellationToken.None);
            }
            else
            {
                await images.EnrichWorkImagesAsync(movie.AssetId, null);
            }
        }

        var covers = await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt");
        var preferred = Assert.Single(covers, asset => asset.IsPreferred);
        Assert.Equal(rankedPoster, preferred.ImageUrl);
        Assert.Equal(200, preferred.WidthPx);
        Assert.Equal($"/stream/artwork/{preferred.Id}", await GetCanonicalAsync(movie.WorkId, MetadataFieldConstants.CoverUrl));
        Assert.Equal("present", await GetCanonicalAsync(movie.WorkId, MetadataFieldConstants.CoverState));
        Assert.Contains(covers, asset => asset.ImageUrl == retailCover && !asset.IsPreferred);
    }

    [Fact]
    public async Task TvdbImagesPass_ForAShow_StoresAStillForEveryOwnedEpisodeAndKeepsSeasonPostersOffTheShow()
    {
        // A show re-match starts from the show, so every owned episode needs its still from that one pass,
        // found by season and episode number when the episode has no TheTVDB id yet. The show's own artwork
        // list also carries its season posters and logos in other languages; neither may become the show's.
        ShippedProviderHarness.SaveShipped(_configLoader, "tvdb", "installation-key");
        var show = await _works.InsertParentAsync(MediaType.TV, "tvdb:fixture:stills-show", null, null);
        var season = await _works.InsertParentAsync(MediaType.TV, $"season:{show}:1", show, 1);
        var first = await _works.InsertChildAsync(MediaType.TV, season, 1);
        var second = await _works.InsertChildAsync(MediaType.TV, season, 2);
        var asset = await SeedAssetForExistingWorkAsync(first, Path.Combine("TV", "Stills", "Season 01", "Stills - s01e01.mkv"));
        await SeedCanonicalsAsync(show, ("media_type", "TV"), (MetadataFieldConstants.IdentityProvider, "tvdb"), (BridgeIdKeys.TvdbId, "55"));
        await SeedCanonicalsAsync(season, (MetadataFieldConstants.SeasonNumber, "1"));
        await SeedCanonicalsAsync(first, (MetadataFieldConstants.IdentityProvider, "tvdb"), (BridgeIdKeys.TvdbEpisodeId, "9001"));
        await SeedCanonicalsAsync(second, (MetadataFieldConstants.EpisodeNumber, "2"));
        var image = CreateLogoPng(hasVisibleContent: true);
        var factory = new RoutingHttpClientFactory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            return path switch
            {
                "/v4/login" => JsonResponse("""{"data":{"token":"token"}}"""),
                "/v4/artwork/types" => JsonResponse("""{"data":[{"id":2,"name":"Poster"},{"id":7,"name":"Season Poster"},{"id":23,"name":"Clear Logo"}]}"""),
                "/v4/series/55/extended" => JsonResponse("""{"data":{"id":55,"defaultSeasonType":1,"seasons":[{"id":66,"number":1,"type":{"id":1}}],"artworks":[{"type":7,"language":"eng","score":99999,"image":"https://artworks.thetvdb.com/season-poster.png"},{"type":2,"language":"eng","score":10,"image":"https://artworks.thetvdb.com/show-poster.png"},{"type":23,"language":"fra","score":99999,"image":"https://artworks.thetvdb.com/logo-fr.png"},{"type":23,"language":"eng","score":5,"image":"https://artworks.thetvdb.com/logo-en.png"}]}}"""),
                "/v4/seasons/66/extended" => JsonResponse("""{"data":{"id":66,"artworks":[{"type":7,"image":"https://artworks.thetvdb.com/season-one.png"}]}}"""),
                var episodes when episodes.StartsWith("/v4/series/55/episodes/") => JsonResponse("""{"data":{"episodes":[{"id":9001,"seasonNumber":1,"number":1,"image":"https://artworks.thetvdb.com/ep1.png"},{"id":9002,"seasonNumber":1,"number":2,"image":"https://artworks.thetvdb.com/ep2.png"}]},"links":{"next":null}}"""),
                _ => ImageResponse(image),
            };
        });
        var client = new ProviderApiClient(_configLoader, factory, new ProviderRateLimiterCoordinator());
        var service = new ImageEnrichmentService(
            _entityAssets, _mediaAssets, new StubCharacterPortraitRepository(), _canonicals, _works,
            new StubFictionalEntityRepository(), new StubPersonRepository(),
            new StubProviderConfigurationRepository(), _configLoader, _imageCache, _assetPaths,
            new StubAssetExportService(), factory, new StubFuzzyMatchingService(),
            NullLogger<ImageEnrichmentService>.Instance, tvdb: client);

        await service.EnrichWorkImagesAsync(asset.AssetId, null);

        var firstStill = Assert.Single(await _entityAssets.GetByEntityAsync(first.ToString(), "EpisodeStill"));
        Assert.Equal("https://artworks.thetvdb.com/ep1.png", firstStill.ImageUrl);
        var secondStill = Assert.Single(await _entityAssets.GetByEntityAsync(second.ToString(), "EpisodeStill"));
        Assert.Equal("https://artworks.thetvdb.com/ep2.png", secondStill.ImageUrl);
        Assert.Equal("9002", await GetCanonicalAsync(second, BridgeIdKeys.TvdbEpisodeId));
        var showPoster = Assert.Single(await _entityAssets.GetByEntityAsync(show.ToString(), "CoverArt"));
        Assert.Equal("https://artworks.thetvdb.com/show-poster.png", showPoster.ImageUrl);
        var logo = Assert.Single(await _entityAssets.GetByEntityAsync(show.ToString(), "Logo"));
        Assert.Equal("https://artworks.thetvdb.com/logo-en.png", logo.ImageUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoverPass_AfterReidentification_ReplacesTheEarlierFilmsRankedPoster(bool repeatImagesPassFirst)
    {
        // The ranked poster belongs to the identity the images pass chose it for. Once the item is identified
        // as a different film, the new retail cover takes over again, including after an images pass that
        // got no usable answer for the new film and so chose nothing for it.
        const string newCover = "https://image.tmdb.org/t/p/w500/other-film.jpg";
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Reidentified.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "601"));
        var poster = CreatePosterJpeg(200, 300);
        HttpResponseMessage Respond(HttpRequestMessage request) =>
            request.RequestUri?.ToString().Contains("/movie/601/images?", StringComparison.OrdinalIgnoreCase) == true
                ? JsonResponse("""
                    { "posters": [ { "file_path": "/first-film.jpg", "vote_average": 8, "vote_count": 4, "iso_639_1": null } ], "logos": [], "backdrops": [] }
                    """)
                : ImageResponse(poster);
        await CreateService(Respond).EnrichWorkImagesAsync(movie.AssetId, null);

        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "602"), (MetadataFieldConstants.Cover, newCover));
        if (repeatImagesPassFirst)
        {
            await CreateService(Respond).EnrichWorkImagesAsync(movie.AssetId, null);
            // The failed pass holds nothing for 602, so the held artwork is still recorded as the earlier film's.
            Assert.Equal("Error", await GetCanonicalAsync(movie.WorkId, "tmdb_artwork_status"));
            Assert.Equal("601", await GetCanonicalAsync(movie.WorkId, "tmdb_artwork_bridge_id"));
        }

        await CreateCoverArtWorker(Respond).DownloadAndPersistAsync(movie.AssetId, wikidataQid: null, CancellationToken.None);

        var preferred = Assert.Single(await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt"), asset => asset.IsPreferred);
        Assert.Equal(newCover, preferred.ImageUrl);
    }

    [Fact]
    public async Task CoverPass_KeepsAPreferredUserUpload()
    {
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "User Cover.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (MetadataFieldConstants.Cover, "https://image.tmdb.org/t/p/w500/retail.jpg"));
        var uploadId = Guid.NewGuid();
        var uploadPath = _assetPaths.GetCentralAssetPath("Work", movie.WorkId, "CoverArt", uploadId, ".jpg");
        AssetPathService.EnsureDirectory(uploadPath);
        await File.WriteAllBytesAsync(uploadPath, CreatePosterJpeg(100, 150));
        await _entityAssets.UpsertAsync(new EntityAsset
        {
            Id = uploadId,
            EntityId = movie.WorkId.ToString(),
            EntityType = "Work",
            AssetTypeValue = "CoverArt",
            LocalImagePath = uploadPath,
            SourceProvider = "user_upload",
            AssetClassValue = "Artwork",
            StorageLocationValue = "Central",
            OwnerScope = "Work",
            IsPreferred = true,
            IsUserOverride = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var poster = CreatePosterJpeg(50, 75);

        await CreateCoverArtWorker(_ => ImageResponse(poster)).DownloadAndPersistAsync(movie.AssetId, wikidataQid: null, CancellationToken.None);

        var covers = await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt");
        Assert.Equal(uploadId, Assert.Single(covers, asset => asset.IsPreferred).Id);
        Assert.Equal(2, covers.Count);
    }

    [Fact]
    public async Task OrdinaryImagesPass_AfterReidentification_RanksTheNewFilmsArtwork()
    {
        // Re-identification without a manual rematch changes tmdb_id only. The next ordinary pass asks TMDB
        // about the new film instead of repeating the earlier film's result, and only then repeats itself.
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Reranked.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "611"));
        var requested = new List<string>();
        var poster = CreatePosterJpeg(200, 300);
        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            foreach (var film in new[] { "611", "612" })
            {
                if (url.Contains($"/movie/{film}/images?", StringComparison.OrdinalIgnoreCase))
                {
                    requested.Add(film);
                    return JsonResponse($$"""
                        {
                          "posters": [ { "file_path": "/poster-{{film}}.jpg", "vote_average": 8, "vote_count": 4, "iso_639_1": null } ],
                          "logos": [],
                          "backdrops": [ { "file_path": "/backdrop-{{film}}.jpg", "vote_average": 8, "vote_count": 4, "iso_639_1": null } ]
                        }
                        """);
                }
            }

            return ImageResponse(poster);
        });
        await service.EnrichWorkImagesAsync(movie.AssetId, null);

        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "612"));
        var reidentified = await service.EnrichWorkImagesAsync(movie.AssetId, null);
        var repeat = await service.EnrichWorkImagesAsync(movie.AssetId, null);

        Assert.Equal(["611", "612"], requested);
        Assert.Equal("Completed", reidentified.Status);
        Assert.Equal(2, reidentified.DownloadedCount);
        Assert.Equal(0, repeat.DownloadedCount);
        Assert.EndsWith("/poster-612.jpg", Assert.Single(await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt"),
            asset => asset.IsPreferred).ImageUrl, StringComparison.Ordinal);
        Assert.EndsWith("/backdrop-612.jpg", Assert.Single(await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "Background"),
            asset => asset.IsPreferred).ImageUrl, StringComparison.Ordinal);
        Assert.Equal("612", await GetCanonicalAsync(movie.WorkId, ArtworkCanonicalHelper.PosterChoiceKey("tmdb")));
        Assert.Equal("612", await GetCanonicalAsync(movie.WorkId, "tmdb_artwork_bridge_id"));
    }

    [Fact]
    public async Task PickedVariant_SurvivesLaterCoverAndImagesPasses()
    {
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Picked.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "641"),
            (MetadataFieldConstants.Cover, "https://image.tmdb.org/t/p/w500/retail-641.jpg"));
        var respond = TwoPosterFilm("641");
        await CreateService(respond).EnrichWorkImagesAsync(movie.AssetId, null);
        var covers = await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt");
        var picked = Assert.Single(covers, asset => asset.ImageUrl!.EndsWith("/second-641.jpg", StringComparison.Ordinal));
        Assert.False(picked.IsPreferred);

        await _entityAssets.SetUserChoiceAsync(picked.Id);
        await CreateCoverArtWorker(respond).DownloadAndPersistAsync(movie.AssetId, wikidataQid: null, CancellationToken.None);
        await CreateService(respond).RefreshWorkImagesAsync(movie.AssetId, null);

        covers = await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt");
        var preferred = Assert.Single(covers, asset => asset.IsPreferred);
        Assert.Equal(picked.Id, preferred.Id);
        Assert.True(preferred.IsUserOverride);
        Assert.Contains(covers, asset => asset.ImageUrl == "https://image.tmdb.org/t/p/w500/retail-641.jpg");
        Assert.Equal($"/stream/artwork/{picked.Id}", await GetCanonicalAsync(movie.WorkId, MetadataFieldConstants.CoverUrl));

        // Picking another variant replaces the earlier pick, which goes back to provider-managed.
        var other = Assert.Single(covers, asset => asset.ImageUrl!.EndsWith("/first-641.jpg", StringComparison.Ordinal));
        await _entityAssets.SetUserChoiceAsync(other.Id);
        covers = await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt");
        Assert.Equal(other.Id, Assert.Single(covers, asset => asset.IsUserOverride).Id);
    }

    [Fact]
    public async Task CoverPass_KeepsAPickOfItsOwnRetailCover()
    {
        // The cover pass rewrites the variant that holds its own retail image. An ordinary pass keeps the
        // user's pick on it, so the ranked poster cannot take over again on the next images pass.
        const string retailCover = "https://image.tmdb.org/t/p/w500/retail-642.jpg";
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Picked Retail.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "642"), (MetadataFieldConstants.Cover, retailCover));
        var respond = TwoPosterFilm("642");
        await CreateService(respond).EnrichWorkImagesAsync(movie.AssetId, null);
        var coverArt = CreateCoverArtWorker(respond);
        await coverArt.DownloadAndPersistAsync(movie.AssetId, wikidataQid: null, CancellationToken.None);
        var retail = Assert.Single(await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt"),
            asset => asset.ImageUrl == retailCover);

        await _entityAssets.SetUserChoiceAsync(retail.Id);
        await coverArt.DownloadAndPersistAsync(movie.AssetId, wikidataQid: null, CancellationToken.None);
        await CreateService(respond).RefreshWorkImagesAsync(movie.AssetId, null);

        var preferred = Assert.Single(await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt"), asset => asset.IsPreferred);
        Assert.Equal(retail.Id, preferred.Id);
        Assert.True(preferred.IsUserOverride);
    }

    [Fact]
    public async Task RetailRematch_ReplacesAPickedProviderVariantButKeepsUploads()
    {
        const string rematchCover = "https://image.tmdb.org/t/p/w500/rematched.jpg";
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Rematched.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "643"));
        var respond = TwoPosterFilm("643");
        await CreateService(respond).EnrichWorkImagesAsync(movie.AssetId, null);
        var picked = (await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt")).First();
        await _entityAssets.SetUserChoiceAsync(picked.Id);
        var uploadId = await SeedUploadAsync(movie.WorkId);

        var result = await CreateCoverArtWorker(respond).ReplaceProviderArtworkAsync(
            movie.AssetId, rematchCover, "tmdb", WellKnownProviders.Tmdb, CancellationToken.None);

        Assert.True(result.CoverDownloaded);
        var covers = await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt");
        Assert.DoesNotContain(covers, asset => asset.Id == picked.Id);
        Assert.Equal(rematchCover, Assert.Single(covers, asset => asset.IsPreferred).ImageUrl);
        Assert.Contains(covers, asset => asset.Id == uploadId);
    }

    [Fact]
    public async Task OrdinaryImagesPass_AfterReidentification_RemovesTheEarlierFilmsArtwork()
    {
        // The earlier film's ranked posters and backdrops and its retail cover go; the user's upload and
        // pick stay, and the pick stays preferred.
        const string earlierCover = "https://image.tmdb.org/t/p/w500/retail-651.jpg";
        const string currentCover = "https://image.tmdb.org/t/p/w500/retail-652.jpg";
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Cleared.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "651"), (MetadataFieldConstants.Cover, earlierCover));
        var earlierFilm = TwoPosterFilm("651");
        var currentFilm = TwoPosterFilm("652");
        HttpResponseMessage Respond(HttpRequestMessage request) =>
            request.RequestUri!.ToString().Contains("652", StringComparison.Ordinal) ? currentFilm(request) : earlierFilm(request);

        await CreateCoverArtWorker(Respond).DownloadAndPersistAsync(movie.AssetId, wikidataQid: null, CancellationToken.None);
        await CreateService(Respond).EnrichWorkImagesAsync(movie.AssetId, null);
        var earlierFiles = (await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt")).Select(asset => asset.LocalImagePath!).ToList();
        var pickedBackdrop = Assert.Single(await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "Background"));
        await _entityAssets.SetUserChoiceAsync(pickedBackdrop.Id);
        var uploadId = await SeedUploadAsync(movie.WorkId);

        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "652"), (MetadataFieldConstants.Cover, currentCover));
        await CreateCoverArtWorker(Respond).DownloadAndPersistAsync(movie.AssetId, wikidataQid: null, CancellationToken.None);
        await CreateService(Respond).EnrichWorkImagesAsync(movie.AssetId, null);

        var covers = await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt");
        Assert.Equal(
            new[] { "/first-652.jpg", "/second-652.jpg", "/retail-652.jpg", "upload" }.Order(),
            covers.Select(asset => asset.Id == uploadId ? "upload" : asset.ImageUrl![asset.ImageUrl!.LastIndexOf('/')..]).Order());
        Assert.EndsWith("/first-652.jpg", Assert.Single(covers, asset => asset.IsPreferred).ImageUrl, StringComparison.Ordinal);
        Assert.All(earlierFiles, path => Assert.False(File.Exists(path)));
        var backdrops = await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "Background");
        Assert.Equal(pickedBackdrop.Id, Assert.Single(backdrops, asset => asset.IsPreferred).Id);
        Assert.Contains(backdrops, asset => asset.ImageUrl!.EndsWith("/backdrop-652.jpg", StringComparison.Ordinal));
        Assert.Equal(2, backdrops.Count);
    }

    [Fact]
    public async Task ArtworkPasses_WithoutReidentification_RemoveNothing()
    {
        // Guards ingestion baselines: ordinary, repeat and forced passes on an unchanged identity keep every variant.
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Unchanged.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "661"),
            (MetadataFieldConstants.Cover, "https://image.tmdb.org/t/p/w500/retail-661.jpg"));
        var respond = TwoPosterFilm("661");
        var images = CreateService(respond);

        await CreateCoverArtWorker(respond).DownloadAndPersistAsync(movie.AssetId, wikidataQid: null, CancellationToken.None);
        await images.EnrichWorkImagesAsync(movie.AssetId, null);
        var before = (await _entityAssets.GetByEntityAsync(movie.WorkId.ToString())).Select(asset => asset.Id).Order().ToList();
        await images.EnrichWorkImagesAsync(movie.AssetId, null);
        await images.RefreshWorkImagesAsync(movie.AssetId, null);
        await CreateCoverArtWorker(respond).DownloadAndPersistAsync(movie.AssetId, wikidataQid: null, CancellationToken.None);

        Assert.Equal(4, before.Count);
        Assert.Equal(before, (await _entityAssets.GetByEntityAsync(movie.WorkId.ToString())).Select(asset => asset.Id).Order().ToList());
    }

    [Fact]
    public async Task ArtworkLibraryChoice_SurvivesALaterPickAndARetailRematch()
    {
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Library Choice.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "662"));
        var respond = TwoPosterFilm("662");
        await CreateService(respond).EnrichWorkImagesAsync(movie.AssetId, null);
        // A picture linked from the artwork library keeps its provider as the source but has a local address.
        var libraryId = Guid.NewGuid();
        var libraryPath = _assetPaths.GetCentralAssetPath("ArtworkAsset", libraryId, "Primary", libraryId, ".jpg");
        AssetPathService.EnsureDirectory(libraryPath);
        await File.WriteAllBytesAsync(libraryPath, CreatePosterJpeg(120, 180));
        await _entityAssets.UpsertAsync(new EntityAsset
        {
            Id = libraryId,
            EntityId = movie.WorkId.ToString(),
            EntityType = "Work",
            AssetTypeValue = "CoverArt",
            ImageUrl = $"/api/v1/display/artwork/assets/{libraryId:N}/content",
            LocalImagePath = libraryPath,
            SourceProvider = "tmdb",
            AssetClassValue = "Artwork",
            StorageLocationValue = "Central",
            OwnerScope = "Work",
            IsUserOverride = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var provider = (await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt")).First(asset => asset.Id != libraryId);

        await _entityAssets.SetUserChoiceAsync(provider.Id);
        Assert.True((await _entityAssets.FindByIdAsync(libraryId))!.IsUserOverride);
        await CreateCoverArtWorker(respond).ReplaceProviderArtworkAsync(
            movie.AssetId, "https://image.tmdb.org/t/p/w500/rematched-662.jpg", "tmdb", WellKnownProviders.Tmdb, CancellationToken.None);

        var covers = await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "CoverArt");
        Assert.Contains(covers, asset => asset.Id == libraryId && asset.IsUserOverride);
        Assert.DoesNotContain(covers, asset => asset.Id == provider.Id);
        Assert.True(File.Exists(libraryPath));
    }

    [Fact]
    public async Task OrdinaryImagesPass_AfterReidentification_ClearsTheBackdropWhenNothingReplacesIt()
    {
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "No Backdrop.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "663"));
        var earlierFilm = TwoPosterFilm("663");
        var currentFilm = TwoPosterFilm("664", withBackdrop: false);
        HttpResponseMessage Respond(HttpRequestMessage request) =>
            request.RequestUri!.ToString().Contains("664", StringComparison.Ordinal) ? currentFilm(request) : earlierFilm(request);
        await CreateService(Respond).EnrichWorkImagesAsync(movie.AssetId, null);
        Assert.NotNull(await GetCanonicalAsync(movie.WorkId, "background"));

        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "664"));
        await CreateService(Respond).EnrichWorkImagesAsync(movie.AssetId, null);

        Assert.Empty(await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), "Background"));
        Assert.Null(await GetCanonicalAsync(movie.WorkId, "background"));
        Assert.Null(await GetCanonicalAsync(movie.WorkId, "background_url_m"));
    }

    [Fact]
    public async Task OrdinaryImagesPass_AfterReidentification_KeepsAFileAnotherItemStillUses()
    {
        // Artwork rows are content-addressed: the second film's identical poster is served through the first
        // film's file, so removing the first film's variant must leave that file in place.
        const string shared = "https://image.tmdb.org/t/p/original/shared.jpg";
        var sharedBytes = CreatePosterJpeg(222, 333);
        var first = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Shared First.mkv");
        var second = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Shared Second.mkv");
        await SeedCanonicalsAsync(first.WorkId, (BridgeIdKeys.TmdbId, "671"));
        await SeedCanonicalsAsync(second.WorkId, (BridgeIdKeys.TmdbId, "672"));
        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/images?", StringComparison.OrdinalIgnoreCase))
            {
                var poster = url.Contains("/movie/673/", StringComparison.Ordinal) ? "/replacement.jpg" : "/shared.jpg";
                return JsonResponse($$"""
                    { "posters": [ { "file_path": "{{poster}}", "vote_average": 8, "vote_count": 4, "iso_639_1": null } ], "logos": [], "backdrops": [] }
                    """);
            }

            return ImageResponse(url == shared ? sharedBytes : CreatePosterJpeg(240, 360));
        });
        await service.EnrichWorkImagesAsync(first.AssetId, null);
        await service.EnrichWorkImagesAsync(second.AssetId, null);
        var firstFile = Assert.Single(await _entityAssets.GetByEntityAsync(first.WorkId.ToString(), "CoverArt")).LocalImagePath!;

        await SeedCanonicalsAsync(first.WorkId, (BridgeIdKeys.TmdbId, "673"));
        await service.EnrichWorkImagesAsync(first.AssetId, null);

        Assert.DoesNotContain(await _entityAssets.GetByEntityAsync(first.WorkId.ToString(), "CoverArt"), asset => asset.ImageUrl == shared);
        Assert.True(File.Exists(firstFile));
        Assert.True(File.Exists(Assert.Single(await _entityAssets.GetByEntityAsync(second.WorkId.ToString(), "CoverArt")).LocalImagePath));
    }

    /// <summary>
    /// A film whose images list offers two posters (first ranks higher) and, unless told otherwise, one backdrop.
    /// Every image address serves its own bytes (retail covers small), so no two variants share a
    /// content-addressed artwork row by accident.
    /// </summary>
    private Func<HttpRequestMessage, HttpResponseMessage> TwoPosterFilm(string film, bool withBackdrop = true)
    {
        var backdrops = withBackdrop
            ? $$"""[ { "file_path": "/backdrop-{{film}}.jpg", "vote_average": 8, "vote_count": 4, "iso_639_1": null } ]"""
            : "[]";
        return request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains($"/movie/{film}/images?", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse($$"""
                    {
                      "posters": [
                        { "file_path": "/first-{{film}}.jpg", "vote_average": 9, "vote_count": 9, "iso_639_1": null },
                        { "file_path": "/second-{{film}}.jpg", "vote_average": 5, "vote_count": 1, "iso_639_1": null }
                      ],
                      "logos": [],
                      "backdrops": {{backdrops}}
                    }
                    """);
            }

            var step = _imageSizes.GetOrAdd(url, _ => _imageSizes.Count + 1);
            return ImageResponse(url.Contains("/w500/", StringComparison.Ordinal)
                ? CreatePosterJpeg(50 + step, 75)
                : CreatePosterJpeg(200 + step, 300));
        };
    }

    private async Task<Guid> SeedUploadAsync(Guid workId)
    {
        var uploadId = Guid.NewGuid();
        var uploadPath = _assetPaths.GetCentralAssetPath("Work", workId, "CoverArt", uploadId, ".jpg");
        AssetPathService.EnsureDirectory(uploadPath);
        await File.WriteAllBytesAsync(uploadPath, CreatePosterJpeg(100, 150));
        await _entityAssets.UpsertAsync(new EntityAsset
        {
            Id = uploadId,
            EntityId = workId.ToString(),
            EntityType = "Work",
            AssetTypeValue = "CoverArt",
            ImageUrl = $"/stream/artwork/{uploadId}",
            LocalImagePath = uploadPath,
            SourceProvider = "user_upload",
            AssetClassValue = "Artwork",
            StorageLocationValue = "Central",
            OwnerScope = "Work",
            IsUserOverride = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        return uploadId;
    }

    [Fact]
    public async Task OrdinaryImagesPass_AfterAFailedPass_AsksTmdbAgain()
    {
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Retried.mkv");
        await SeedCanonicalsAsync(movie.WorkId, (BridgeIdKeys.TmdbId, "621"));
        var imagesRequests = 0;
        var service = CreateService(request =>
        {
            if (request.RequestUri?.ToString().Contains("/movie/621/images?", StringComparison.OrdinalIgnoreCase) != true)
            {
                return ImageResponse(CreatePosterJpeg(200, 300));
            }

            return ++imagesRequests == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : JsonResponse("""
                    { "posters": [ { "file_path": "/retried.jpg", "vote_average": 8, "vote_count": 4, "iso_639_1": null } ], "logos": [], "backdrops": [] }
                    """);
        });

        var failed = await service.EnrichWorkImagesAsync(movie.AssetId, null);
        var retried = await service.EnrichWorkImagesAsync(movie.AssetId, null);

        Assert.Equal("Error", failed.Status);
        Assert.Equal(2, imagesRequests);
        Assert.Equal("Completed", retried.Status);
        Assert.Equal(1, retried.DownloadedCount);
    }

    [Fact]
    public async Task TvdbImagesPass_AfterShowReidentification_UsesTheNewShowsSeasonAndDropsTheEarlierShowsArtwork()
    {
        // The season kept the season id found under the show's earlier identity; that id is not one of the
        // current show's seasons, so the pass looks the season up again and records the new id. The earlier
        // show's poster and season poster go.
        ShippedProviderHarness.SaveShipped(_configLoader, "tvdb", "installation-key");
        var show = await _works.InsertParentAsync(MediaType.TV, "tvdb:fixture:reidentified-show", null, null);
        var season = await _works.InsertParentAsync(MediaType.TV, $"season:{show}:1", show, 1);
        var episode = await _works.InsertChildAsync(MediaType.TV, season, 1);
        var asset = await SeedAssetForExistingWorkAsync(episode, Path.Combine("TV", "Reidentified", "Season 01", "Reidentified - s01e01.mkv"));
        await SeedCanonicalsAsync(show, ("media_type", "TV"), (MetadataFieldConstants.IdentityProvider, "tvdb"), (BridgeIdKeys.TvdbId, "99"));
        await SeedCanonicalsAsync(season, (MetadataFieldConstants.SeasonNumber, "1"), (BridgeIdKeys.TvdbSeasonId, "500"));
        await SeedCanonicalsAsync(episode, (MetadataFieldConstants.IdentityProvider, "tvdb"));
        await SeedCanonicalsAsync(show, ("tvdb_artwork_bridge_id", "98"));
        foreach (var (owner, type, url) in new[]
                 {
                     (show, "CoverArt", "https://artworks.thetvdb.com/earlier-show.png"),
                     (season, "SeasonPoster", "https://artworks.thetvdb.com/earlier-season.png"),
                 })
        {
            await _entityAssets.UpsertAsync(new EntityAsset
            {
                Id = Guid.NewGuid(),
                EntityId = owner.ToString(),
                EntityType = "Work",
                AssetTypeValue = type,
                ImageUrl = url,
                SourceProvider = "tvdb",
                AssetClassValue = "Artwork",
                StorageLocationValue = "Central",
                OwnerScope = "Work",
                IsPreferred = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        var paths = new List<string>();
        var image = CreateLogoPng(hasVisibleContent: true);
        var factory = new RoutingHttpClientFactory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return path switch
            {
                "/v4/login" => JsonResponse("""{"data":{"token":"token"}}"""),
                "/v4/artwork/types" => JsonResponse("""{"data":[{"id":2,"name":"Poster"},{"id":7,"name":"Season Poster"}]}"""),
                "/v4/series/99/extended" => JsonResponse("""{"data":{"id":99,"defaultSeasonType":1,"seasons":[{"id":77,"number":1,"type":{"id":1}}],"artworks":[{"type":2,"image":"https://artworks.thetvdb.com/current-show.png"}]}}"""),
                "/v4/seasons/77/extended" => JsonResponse("""{"data":{"id":77,"artworks":[{"type":7,"image":"https://artworks.thetvdb.com/new-season.png"}]}}"""),
                _ => ImageResponse(image),
            };
        });
        var client = new ProviderApiClient(_configLoader, factory, new ProviderRateLimiterCoordinator());
        var service = new ImageEnrichmentService(
            _entityAssets, _mediaAssets, new StubCharacterPortraitRepository(), _canonicals, _works,
            new StubFictionalEntityRepository(), new StubPersonRepository(),
            new StubProviderConfigurationRepository(), _configLoader, _imageCache, _assetPaths,
            new StubAssetExportService(), factory, new StubFuzzyMatchingService(),
            NullLogger<ImageEnrichmentService>.Instance, tvdb: client);

        await service.EnrichWorkImagesAsync(asset.AssetId, null);

        Assert.DoesNotContain("/v4/seasons/500/extended", paths);
        Assert.Equal("77", await GetCanonicalAsync(season, BridgeIdKeys.TvdbSeasonId));
        var seasonPoster = Assert.Single(await _entityAssets.GetByEntityAsync(season.ToString(), "SeasonPoster"));
        Assert.Equal("https://artworks.thetvdb.com/new-season.png", seasonPoster.ImageUrl);
        Assert.True(seasonPoster.IsPreferred);
        var showPoster = Assert.Single(await _entityAssets.GetByEntityAsync(show.ToString(), "CoverArt"));
        Assert.Equal("https://artworks.thetvdb.com/current-show.png", showPoster.ImageUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatMoviePass_WithStudioLogo_HoldsTheSameCount(bool forceRefresh)
    {
        // The first pass rewrites studio_logo_url to the stored copy's /stream/artwork address;
        // a repeat pass still counts that logo as held.
        var movie = await SeedStandaloneAssetAsync(MediaType.Movies, "Movies", "Movies", "Studio Repeat.mkv");
        await SeedCanonicalsAsync(
            movie.WorkId,
            (BridgeIdKeys.TmdbId, "778"),
            ("studio_logo_url", "https://image.tmdb.org/t/p/original/studio-repeat.png"));
        var service = CreateService(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            return url.Contains("/movie/778/images?", StringComparison.OrdinalIgnoreCase)
                ? JsonResponse("""
                    {
                      "posters": [ { "file_path": "/studio-repeat-poster.jpg", "vote_average": 7, "vote_count": 1, "iso_639_1": null } ],
                      "logos": [],
                      "backdrops": []
                    }
                    """)
                : ImageResponse([1, 2, 3, 4]);
        });

        var first = await service.EnrichWorkImagesAsync(movie.AssetId, "QSTUDIO");
        Assert.StartsWith("/stream/artwork/", await GetCanonicalAsync(movie.WorkId, "studio_logo_url"), StringComparison.Ordinal);
        var repeat = forceRefresh
            ? await service.RefreshWorkImagesAsync(movie.AssetId, "QSTUDIO")
            : await service.EnrichWorkImagesAsync(movie.AssetId, "QSTUDIO");

        Assert.Equal(2, first.HeldCount);
        Assert.Equal(2, repeat.HeldCount);
        Assert.Equal(0, repeat.DownloadedCount);
        Assert.Equal("2", await GetCanonicalAsync(movie.WorkId, "tmdb_artwork_held_count"));
        var logo = Assert.Single(await _entityAssets.GetByEntityAsync(movie.WorkId.ToString(), AssetType.StudioLogo.ToString()));
        Assert.True(logo.IsPreferred);
        Assert.Equal($"/stream/artwork/{logo.Id}", await GetCanonicalAsync(movie.WorkId, "studio_logo_url"));
    }

    [Theory]
    [InlineData("https://image.tmdb.org/t/p/original/studio.png", false)]
    [InlineData("/stream/artwork/not-a-guid", false)]
    [InlineData(null, false)]
    [InlineData("/stream/artwork/{0}", true)]
    [InlineData("/stream/artwork/{0}?size=m", true)]
    public void StoredArtworkAddress_ParsesOnlyVariantIds(string? template, bool expected)
    {
        var id = Guid.NewGuid();
        var url = template?.Replace("{0}", id.ToString(), StringComparison.Ordinal);

        Assert.Equal(expected, ArtworkCanonicalHelper.TryParseStoredArtworkId(url, out var parsed));
        Assert.Equal(expected ? id : Guid.Empty, parsed);
    }

    private async Task<(ImageEnrichmentService Service, Guid Show, (Guid WorkId, Guid AssetId) First, (Guid WorkId, Guid AssetId) Second)>
        SeedTvdbShowWithTwoEpisodesAsync(CountdownEvent? showLookupGate = null)
    {
        ShippedProviderHarness.SaveShipped(_configLoader, "tvdb", "installation-key");
        var show = await _works.InsertParentAsync(MediaType.TV, "tvdb:fixture:shared-show", null, null);
        await SeedCanonicalsAsync(show,
            ("media_type", "TV"),
            (MetadataFieldConstants.IdentityProvider, "tvdb"),
            (BridgeIdKeys.TvdbId, "99"));
        var firstWork = await _works.InsertChildAsync(MediaType.TV, show, 1);
        var secondWork = await _works.InsertChildAsync(MediaType.TV, show, 2);
        var firstAsset = await SeedAssetForExistingWorkAsync(firstWork, Path.Combine("TV", "Shared Show", "Shared - s01e01.mkv"));
        var secondAsset = await SeedAssetForExistingWorkAsync(secondWork, Path.Combine("TV", "Shared Show", "Shared - s01e02.mkv"));
        await SeedCanonicalsAsync(firstWork, (MetadataFieldConstants.IdentityProvider, "tvdb"), (BridgeIdKeys.TvdbEpisodeId, "111"));
        await SeedCanonicalsAsync(secondWork, (MetadataFieldConstants.IdentityProvider, "tvdb"), (BridgeIdKeys.TvdbEpisodeId, "112"));

        var image = CreateLogoPng(hasVisibleContent: true);
        var factory = new RoutingHttpClientFactory(request => request.RequestUri!.AbsolutePath switch
        {
            "/v4/login" => JsonResponse("""{"data":{"token":"token"}}"""),
            "/v4/artwork/types" => JsonResponse("""{"data":[{"id":2,"name":"Poster"}]}"""),
            "/v4/series/99/extended" when showLookupGate is not null => GatedJson(showLookupGate,
                """{"data":{"id":99,"artworks":[{"type":2,"image":"https://artworks.thetvdb.com/show-poster.png"}]}}"""),
            "/show-poster.png" when showLookupGate is not null => DelayedImage(image),
            "/v4/series/99/extended" => JsonResponse("""{"data":{"id":99,"artworks":[{"type":2,"image":"https://artworks.thetvdb.com/show-poster.png"}]}}"""),
            "/v4/episodes/111/extended" => JsonResponse("""{"data":{"id":111,"seriesId":99,"image":"https://artworks.thetvdb.com/still-1.png"}}"""),
            "/v4/episodes/112/extended" => JsonResponse("""{"data":{"id":112,"seriesId":99,"image":"https://artworks.thetvdb.com/still-2.png"}}"""),
            _ => ImageResponse(image),
        });
        var client = new ProviderApiClient(_configLoader, factory, new ProviderRateLimiterCoordinator());
        var service = new ImageEnrichmentService(
            _entityAssets, _mediaAssets, new StubCharacterPortraitRepository(), _canonicals, _works,
            new StubFictionalEntityRepository(), new StubPersonRepository(),
            new StubProviderConfigurationRepository(), _configLoader, _imageCache, _assetPaths,
            new StubAssetExportService(), factory, new StubFuzzyMatchingService(),
            NullLogger<ImageEnrichmentService>.Instance, tvdb: client,
            imageDownloadCoordinator: new ImageDownloadCoordinator());
        return (service, show, (firstWork, firstAsset.AssetId), (secondWork, secondAsset.AssetId));
    }

    private static HttpResponseMessage GatedJson(CountdownEvent gate, string payload)
    {
        if (!gate.IsSet)
        {
            gate.Signal();
        }

        // A cached lookup on the second pass would never arrive; the timeout keeps the test from hanging.
        gate.Wait(TimeSpan.FromSeconds(5));
        return JsonResponse(payload);
    }

    private static HttpResponseMessage DelayedImage(byte[] image)
    {
        Thread.Sleep(200);
        return ImageResponse(image);
    }

    private async Task<string?> GetCanonicalAsync(Guid entityId, string key) =>
        (await _canonicals.GetByEntityAsync(entityId)).FirstOrDefault(value => value.Key == key)?.Value;

    [Fact]
    public async Task DiscoverComicArtwork_UsesIssueIdentityAndReturnsAllImagesWithoutDownloading()
    {
        _configLoader.SaveProvider(new StorageProviderConfiguration
        {
            Name = "comicvine",
            Enabled = true,
            HttpClient = new StorageHttpClientConfig { ApiKey = "test-key" }
        });
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
    public async Task EnrichWorkImagesAsync_LegacyTmdbTvIdentityDoesNotFetchArtwork()
    {
        var show = await _works.InsertParentAsync(MediaType.TV, "show:all-seasons", null, null);
        var seasonOne = await _works.InsertParentAsync(MediaType.TV, $"season:{show}:1", show, 1);
        var seasonTwo = await _works.InsertParentAsync(MediaType.TV, $"season:{show}:2", show, 2);
        var episode = await _works.InsertChildAsync(MediaType.TV, seasonOne, 1);
        var asset = await SeedAssetForExistingWorkAsync(episode, Path.Combine("TV", "All Seasons", "Season 01", "s01e01.mkv"));
        await SeedCanonicalsAsync(show, ("media_type", "TV"), (BridgeIdKeys.TmdbId, "88"));
        var service = CreateService(_ => throw new InvalidOperationException("TMDB must not be called for television."));

        var result = await service.EnrichWorkImagesAsync(asset.AssetId, "Q88");

        Assert.True(result.Skipped);
        Assert.Equal("tvdb", result.Provider);
        Assert.Equal("missing_tvdb_match_or_connection", result.SkippedReason);
        Assert.Empty(await _entityAssets.GetByEntityAsync(seasonOne.ToString(), "SeasonPoster"));
        Assert.Empty(await _entityAssets.GetByEntityAsync(seasonTwo.ToString(), "SeasonPoster"));
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
    public async Task EnrichWorkImagesAsync_TvSeasonWithOnlyTmdbIdentityIsNotRefreshed()
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

        var service = CreateService(_ => throw new InvalidOperationException("TMDB must not be called for television."));

        var result = await service.EnrichWorkImagesAsync(asset.AssetId, "QSHOW");

        Assert.True(result.Skipped);
        Assert.Empty(await _entityAssets.GetByEntityAsync(season.ToString(), "SeasonPoster"));
        Assert.Empty(await _entityAssets.GetByEntityAsync(season.ToString(), "SeasonThumb"));
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

    private CoverArtWorker CreateCoverArtWorker(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(
            _mediaAssets,
            _canonicals,
            _works,
            _imageCache,
            new RoutingHttpClientFactory(responder),
            _assetPaths,
            NullLogger<CoverArtWorker>.Instance,
            assetExportService: new StubAssetExportService(),
            entityAssetRepo: _entityAssets);

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

    private static byte[] CreatePosterJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(40, 90, 160));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

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
        public Task<IReadOnlySet<string>> GetLinkedWorkQidsAsync(IEnumerable<string> workQids, CancellationToken ct = default) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
        public Task<IReadOnlySet<Guid>> GetUniverseEnrichedEntityIdsAsync(IEnumerable<Guid> entityIds, CancellationToken ct = default) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<FictionalEntity>> FindByQidsAsync(IEnumerable<string> qids, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FictionalEntity>>([]);
        public Task<FictionalEntity?> FindByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<FictionalEntity?>(null);
        public Task<IReadOnlyList<FictionalEntity>> GetByUniverseAsync(string universeQid, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FictionalEntity>>([]);
        public Task<IReadOnlyList<FictionalEntity>> GetByUniverseAndTypeAsync(string universeQid, string entitySubType, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FictionalEntity>>([]);
        public Task<FictionalEntitySearchPage> SearchVisibleByUniverseAsync(string universeQid, IReadOnlyCollection<string> visibleWorkQids, string? entitySubType, string? search, int offset, int limit, CancellationToken ct = default) => Task.FromResult(new FictionalEntitySearchPage([], 0));
        public Task<IReadOnlyList<FictionalEntity>> GetByWorkQidAsync(string workQid, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FictionalEntity>>([]);
        public Task CreateAsync(FictionalEntity entity, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateEnrichmentAsync(Guid entityId, string? label, string? description, string? imageUrl, DateTimeOffset enrichedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateUserDetailsAsync(Guid entityId, string? label, string? description, CancellationToken ct = default) => Task.CompletedTask;
        public Task<(string? Label, string? Description)?> GetUserDetailsAsync(Guid entityId, CancellationToken ct = default) => Task.FromResult<(string? Label, string? Description)?>(null);
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
        public Task<IReadOnlyList<PersonPresenceCredit>> GetPresenceCreditsAsync(IEnumerable<Guid> personIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PersonPresenceCredit>>([]);
        public Task<IReadOnlyList<PersonCreditedFile>> GetCreditedFilesAsync(IEnumerable<Guid> personIds, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PersonCreditedFile>>([]);
        public Task<Person> CreateAsync(Person person, CancellationToken ct = default) => Task.FromResult(person);
        public Task UpdateEnrichmentAsync(Guid personId, string? wikidataQid, string? headshotUrl, string? biography, string? name, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateNameAsync(Guid personId, string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateSocialFieldsAsync(Guid personId, string? occupation, string? instagram, string? twitter, string? tiktok, string? mastodon, string? website, CancellationToken ct = default) => Task.CompletedTask;
        public Task LinkToMediaAssetAsync(Guid mediaAssetId, Guid personId, string role, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateLocalHeadshotPathAsync(Guid id, string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Person>> GetWithLocalHeadshotWithoutManagedAssetAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Person>>([]);
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
