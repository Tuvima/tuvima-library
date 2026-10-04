using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientLibraryWorksTests
{
    [Fact]
    public async Task HomeSpotlightsNormalizeEveryRenditionAndPreviewThroughAuthenticatedImageProxy()
    {
        const string id = "11111111-1111-1111-1111-111111111111";
        var json = $$"""
            { "key":"home", "title":"Home", "shelves":[], "catalog":[],
              "spotlights":[{
                "title":"Album", "artwork":{
                  "coverUrl":"/stream/artwork/{{id}}",
                  "coverSmallUrl":"/stream/artwork/{{id}}?size=s",
                  "coverMediumUrl":"/stream/artwork/{{id}}?size=m",
                  "coverLargeUrl":"/stream/artwork/{{id}}?size=l",
                  "backgroundUrl":"http://localhost:61495/stream/artwork/{{id}}?size=l"
                }, "actions":[], "previewItems":[{
                  "title":"Owned track", "imageUrl":"/stream/artwork/{{id}}?size=s", "shape":"square"
                }]
              }]
            }
            """;
        using var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        var page = await client.GetDisplayHomeAsync();
        var hero = Assert.Single(page!.Spotlights);
        var expected = $"/engine-image/stream/artwork/{id}";
        Assert.Equal(expected, hero.Artwork.CoverUrl);
        Assert.Equal(expected + "?size=s", hero.Artwork.CoverSmallUrl);
        Assert.Equal(expected + "?size=m", hero.Artwork.CoverMediumUrl);
        Assert.Equal(expected + "?size=l", hero.Artwork.CoverLargeUrl);
        Assert.Equal(expected + "?size=l", hero.Artwork.BackgroundUrl);
        Assert.Equal(expected + "?size=s", Assert.Single(hero.PreviewItems).ImageUrl);
    }

    [Fact]
    public async Task GetDetailPageAsync_BuildsExpectedUrlAndNormalizesArtwork()
    {
        HttpRequestMessage? capturedRequest = null;
        const string json = """
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "entityType": 0,
              "presentationContext": 3,
              "editorTarget": {
                "entityId": "33333333-3333-3333-3333-333333333333",
                "entityKind": "Work",
                "containerMode": "Canonical",
                "initialTab": "episodes",
                "initialScope": "series"
              },
              "title": "Dune",
              "artwork": {
                "coverUrl": "/stream/cover",
                "backdropUrl": "/stream/backdrop",
                "relatedArtworkUrls": ["/stream/related"],
                "dominantColors": ["#c9922e"],
                "presentationMode": 1,
                "source": 2
              },
              "ownedFormats": [
                {
                  "id": "22222222-2222-2222-2222-222222222222",
                  "formatType": 0,
                  "displayName": "Ebook",
                  "coverUrl": "/stream/ebook-cover",
                  "actions": []
                }
              ],
              "multiFormatState": 0,
              "metadata": [],
              "primaryActions": [],
              "secondaryActions": [],
              "overflowActions": [],
              "contributorGroups": [],
              "previewContributors": [],
              "characterGroups": [],
              "previewCharacters": [],
              "relationshipStrip": [],
              "tabs": [],
              "mediaGroups": [],
              "identityStatus": 0,
              "libraryStatus": 1,
              "isAdminView": false
            }
            """;

        using var httpClient = CreateHttpClient(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);
        var detail = await client.GetDetailPageAsync(
            DetailEntityType.Work,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            DetailPresentationContext.Read);

        Assert.NotNull(detail);
        Assert.NotNull(capturedRequest);
        Assert.Equal(
            "http://localhost:61495/api/v1/details/work/11111111-1111-1111-1111-111111111111?context=read",
            capturedRequest!.RequestUri!.ToString());
        Assert.Equal("http://localhost:61495/stream/cover", detail!.Artwork.CoverUrl);
        Assert.Equal("http://localhost:61495/stream/backdrop", detail.Artwork.BackdropUrl);
        Assert.Equal("http://localhost:61495/stream/related", Assert.Single(detail.Artwork.RelatedArtworkUrls));
        Assert.Equal("http://localhost:61495/stream/ebook-cover", Assert.Single(detail.OwnedFormats).CoverUrl);
        Assert.NotNull(detail.EditorTarget);
        Assert.Equal("33333333-3333-3333-3333-333333333333", detail.EditorTarget!.EntityId);
        Assert.Equal("Canonical", detail.EditorTarget.ContainerMode);
        Assert.Equal("episodes", detail.EditorTarget.InitialTab);
        Assert.Equal("series", detail.EditorTarget.InitialScope);
    }

    [Fact]
    public async Task GetDetailPageAsync_FillsStreamingServiceHeroBrandWhenEngineHasNoLogo()
    {
        const string json = """
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "entityType": 3,
              "presentationContext": 1,
              "title": "Shogun",
              "artwork": {
                "logoUrl": "/stream/show-logo",
                "heroArtwork": {
                  "url": "/stream/backdrop",
                  "mode": 0,
                  "hasImage": true
                }
              },
              "heroBrand": {
                "label": "FX on Hulu"
              },
              "ownedFormats": [],
              "multiFormatState": 0,
              "metadata": [],
              "primaryActions": [],
              "secondaryActions": [],
              "overflowActions": [],
              "contributorGroups": [],
              "previewContributors": [],
              "characterGroups": [],
              "previewCharacters": [],
              "relationshipStrip": [],
              "tabs": [],
              "mediaGroups": [],
              "identityStatus": 0,
              "libraryStatus": 1,
              "isAdminView": false
            }
            """;

        using var httpClient = CreateHttpClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var detail = await client.GetDetailPageAsync(
            DetailEntityType.TvShow,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            DetailPresentationContext.Watch);

        Assert.NotNull(detail);
        Assert.Equal("/images/streaming-services/hulu.png", detail!.HeroBrand?.ImageUrl);
        Assert.Equal("http://localhost:61495/stream/show-logo", detail.Artwork.LogoUrl);
    }

    [Fact]
    public async Task GetDetailPageAsync_KeepsEngineHeroBrandLogoWhenProvided()
    {
        const string json = """
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "entityType": 3,
              "presentationContext": 1,
              "title": "The Last of Us",
              "artwork": {},
              "heroBrand": {
                "label": "Max",
                "imageUrl": "/stream/network-logo/max"
              },
              "ownedFormats": [],
              "multiFormatState": 0,
              "metadata": [],
              "primaryActions": [],
              "secondaryActions": [],
              "overflowActions": [],
              "contributorGroups": [],
              "previewContributors": [],
              "characterGroups": [],
              "previewCharacters": [],
              "relationshipStrip": [],
              "tabs": [],
              "mediaGroups": [],
              "identityStatus": 0,
              "libraryStatus": 1,
              "isAdminView": false
            }
            """;

        using var httpClient = CreateHttpClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var detail = await client.GetDetailPageAsync(
            DetailEntityType.TvShow,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            DetailPresentationContext.Watch);

        Assert.NotNull(detail);
        Assert.Equal("http://localhost:61495/stream/network-logo/max", detail!.HeroBrand?.ImageUrl);
    }

    [Fact]
    public async Task GetLibraryWorksAsync_MapsReturnedItemsForHomePage()
    {
        const string json = """
            {
              "items": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "collectionId": "99999999-9999-9999-9999-999999999999",
                  "rootWorkId": "33333333-3333-3333-3333-333333333333",
                  "mediaType": "Books",
                  "workKind": "leaf",
                  "ordinal": 1,
                  "wikidataQid": "Q123",
                  "assetId": "22222222-2222-2222-2222-222222222222",
                  "createdAt": "2026-04-17T12:00:00Z",
                  "coverUrl": "/stream/22222222-2222-2222-2222-222222222222/cover",
                  "backgroundUrl": "/stream/22222222-2222-2222-2222-222222222222/background",
                  "bannerUrl": "/stream/22222222-2222-2222-2222-222222222222/banner",
                  "logoUrl": "/stream/22222222-2222-2222-2222-222222222222/logo",
                  "canonicalValues": {
                    "title": "Dune",
                    "author": "Frank Herbert",
                    "square_url": "/stream/artwork/44444444-4444-4444-4444-444444444444",
                    "cover_url_s": "/stream/artwork/22222222-2222-2222-2222-222222222222?size=s"
                  }
                }
              ],
              "offset": 0,
              "limit": 500,
              "has_more": false,
              "total_count": 1,
              "next_cursor": null
            }
            """;

        using var httpClient = CreateHttpClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var results = await client.GetLibraryWorksAsync();

        var work = Assert.Single(results);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), work.Id);
        Assert.Equal(Guid.Parse("99999999-9999-9999-9999-999999999999"), work.CollectionId);
        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), work.RootWorkId);
        Assert.Equal("Books", work.MediaType);
        Assert.Equal("leaf", work.WorkKind);
        Assert.Equal(new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero), work.CreatedAt);
        Assert.Equal("Dune", work.Title);
        Assert.Equal("Frank Herbert", work.Author);
        Assert.Equal("/engine-image/stream/22222222-2222-2222-2222-222222222222/cover", work.CoverUrl);
        Assert.Equal("/engine-image/stream/22222222-2222-2222-2222-222222222222/background", work.BackgroundUrl);
        Assert.Equal("/engine-image/stream/22222222-2222-2222-2222-222222222222/banner", work.BannerUrl);
        Assert.Null(work.HeroUrl);
        Assert.Equal("/engine-image/stream/22222222-2222-2222-2222-222222222222/logo", work.LogoUrl);
        Assert.Equal("/engine-image/stream/artwork/44444444-4444-4444-4444-444444444444", work.SquareUrl);
        Assert.Equal("/engine-image/stream/artwork/22222222-2222-2222-2222-222222222222?size=s", work.CoverUrlSmall);
    }

    [Fact]
    public async Task GetLibraryWorksAsync_RecordsFailureStateWhenRequestFails()
    {
        using var httpClient = CreateHttpClient(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("Engine warming up", Encoding.UTF8, "text/plain"),
            });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var results = await client.GetLibraryWorksAsync();

        Assert.Empty(results);
        Assert.Equal("GET /library/works", client.LastFailedEndpoint);
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, client.LastStatusCode);
        Assert.Equal("http_failure", client.LastFailureKind);
        Assert.Contains("Engine warming up", client.LastError);
    }

    [Fact]
    public async Task GetLibraryWorksAsync_NormalizesRootRelativeAndRootlessArtworkUrls()
    {
        const string json = """
            {
              "items": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "mediaType": "Books",
                  "assetId": "22222222-2222-2222-2222-222222222222",
                  "coverUrl": "stream/22222222-2222-2222-2222-222222222222/cover",
                  "canonicalValues": {
                    "title": "Dune",
                    "square_url": "stream/artwork/22222222-2222-2222-2222-222222222222",
                    "background_url_m": "stream/artwork/33333333-3333-3333-3333-333333333333?size=m"
                  }
                }
              ],
              "offset": 0,
              "limit": 500,
              "has_more": false,
              "total_count": 1,
              "next_cursor": null
            }
            """;

        using var httpClient = CreateHttpClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var results = await client.GetLibraryWorksAsync();

        var work = Assert.Single(results);
        Assert.Equal("/engine-image/stream/22222222-2222-2222-2222-222222222222/cover", work.CoverUrl);
        Assert.Null(work.HeroUrl);
        Assert.Equal("/engine-image/stream/artwork/22222222-2222-2222-2222-222222222222", work.SquareUrl);
        Assert.Equal("/engine-image/stream/artwork/33333333-3333-3333-3333-333333333333?size=m", work.BackgroundUrlMedium);
    }

    [Fact]
    public async Task GetContentGroupsAsync_MapsRichArtworkAndContextFields()
    {
        const string json = """
            [
              {
                "collection_id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "display_name": "Funny AF with Kevin Hart",
                "primary_media_type": "TV",
                "work_count": 6,
                "distinct_title_count": 4,
                "preview_items": [
                  {
                    "work_id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                    "title": "Episode One",
                    "image_url": "/stream/preview",
                    "shape": "portrait",
                    "position": "1"
                  }
                ],
                "cover_url": "/stream/cover",
                "background_url": "/stream/background",
                "banner_url": "/stream/banner",
                "logo_url": "/stream/logo",
                "cover_aspect_class": "Portrait",
                "square_aspect_class": "Square",
                "background_aspect_class": "LandscapeWide",
                "banner_aspect_class": "BannerStrip",
                "description": "Competition series",
                "tagline": "New episodes coming Monday",
                "creator": "Kevin Hart",
                "director": "Leslie Small",
                "writer": "Writers Room",
                "release_date": "2026-04-14",
                "network": "Netflix",
                "year": "2026",
                "earliest_year": 2024,
                "latest_year": 2026,
                "season_count": 2,
                "created_at": "2026-04-10T12:00:00Z"
              }
            ]
            """;

        using var httpClient = CreateHttpClient(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var groups = await client.GetContentGroupsAsync();

        var group = Assert.Single(groups);
        Assert.Equal("Funny AF with Kevin Hart", group.DisplayName);
        Assert.Equal("http://localhost:61495/stream/cover", group.CoverUrl);
        Assert.Equal("http://localhost:61495/stream/background", group.BackgroundUrl);
        Assert.Equal("http://localhost:61495/stream/banner", group.BannerUrl);
        Assert.Equal("http://localhost:61495/stream/logo", group.LogoUrl);
        Assert.Equal("Portrait", group.CoverAspectClass);
        Assert.Equal("Square", group.SquareAspectClass);
        Assert.Equal("LandscapeWide", group.BackgroundAspectClass);
        Assert.Equal("BannerStrip", group.BannerAspectClass);
        Assert.Equal("Competition series", group.Description);
        Assert.Equal("New episodes coming Monday", group.Tagline);
        Assert.Equal("Kevin Hart", group.Creator);
        Assert.Equal("Leslie Small", group.Director);
        Assert.Equal("Writers Room", group.Writer);
        Assert.Equal("2026-04-14", group.ReleaseDate);
        Assert.Equal("Netflix", group.Network);
        Assert.Equal("2026", group.Year);
        Assert.Equal(2024, group.EarliestYear);
        Assert.Equal(2026, group.LatestYear);
        Assert.Equal(2, group.SeasonCount);
        Assert.Equal(4, group.DistinctTitleCount);
        Assert.Equal("http://localhost:61495/stream/preview", Assert.Single(group.PreviewItems).ImageUrl);
    }

    [Fact]
    public async Task GetSystemViewGroupsAsync_UsesRequestedGroupingAndMapsLogo()
    {
        HttpRequestMessage? capturedRequest = null;
        const string json = """
            [
              {
                "collection_id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                "display_name": "The Record",
                "primary_media_type": "Music",
                "work_count": 12,
                "cover_url": "/stream/album-cover",
                "logo_url": "/stream/album-logo",
                "description": "Album group",
                "tagline": "Fresh in your library",
                "creator": "boygenius",
                "person_id": "cccccccc-cccc-cccc-cccc-cccccccccccc",
                "person_photo_url": "/persons/cccccccc-cccc-cccc-cccc-cccccccccccc/headshot",
                "person_roles": ["Artist"],
                "year": "2023",
                "earliest_year": 2021,
                "latest_year": 2023,
                "created_at": "2026-04-10T12:00:00Z"
              }
            ]
            """;

        using var httpClient = CreateHttpClient(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var groups = await client.GetSystemViewGroupsAsync(mediaType: "Music", groupField: "album");

        Assert.NotNull(capturedRequest);
        Assert.Equal(
            "http://localhost:61495/collections/system-views?mediaType=Music&groupField=album",
            capturedRequest!.RequestUri!.ToString());

        var group = Assert.Single(groups);
        Assert.Equal("The Record", group.DisplayName);
        Assert.Equal("http://localhost:61495/stream/album-cover", group.CoverUrl);
        Assert.Equal("http://localhost:61495/stream/album-logo", group.LogoUrl);
        Assert.Equal("Album group", group.Description);
        Assert.Equal(2021, group.EarliestYear);
        Assert.Equal(2023, group.LatestYear);
        Assert.Equal("Fresh in your library", group.Tagline);
        Assert.Equal(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), group.PersonId);
        Assert.Equal(
            "/engine-image/persons/cccccccc-cccc-cccc-cccc-cccccccccccc/headshot",
            group.PersonPhotoUrl);
        Assert.Equal(["Artist"], group.PersonRoles);
    }

    [Fact]
    public async Task GetSystemViewGroupDetailAsync_IncludesArtistFilterWhenProvided()
    {
        HttpRequestMessage? capturedRequest = null;
        const string json = """
            {
              "collection_id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
              "display_name": "The Record",
              "background_url": "/stream/album-background",
              "banner_url": "/stream/album-banner",
              "hero_url": "/stream/album-hero",
              "logo_url": "/stream/album-logo",
              "works": [],
              "seasons": [],
              "top_cast": []
            }
            """;

        using var httpClient = CreateHttpClient(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        var detail = await client.GetSystemViewGroupDetailAsync("album", "The Record", mediaType: "Music", artistName: "boygenius");

        Assert.NotNull(detail);
        Assert.Equal("http://localhost:61495/stream/album-background", detail!.BackgroundUrl);
        Assert.Equal("http://localhost:61495/stream/album-banner", detail.BannerUrl);
        Assert.Equal("http://localhost:61495/stream/album-hero", detail.HeroUrl);
        Assert.Equal("http://localhost:61495/stream/album-logo", detail.LogoUrl);
        Assert.NotNull(capturedRequest);
        Assert.Equal(
            "http://localhost:61495/collections/system-view-detail?groupField=album&groupValue=The Record&mediaType=Music&artistName=boygenius",
            capturedRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task UploadScopeArtworkFromUrlAsync_PostsToScopedArtworkEndpoint()
    {
        HttpRequestMessage? capturedRequest = null;

        using var httpClient = CreateHttpClient(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });

        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);
        var result = await client.UploadScopeArtworkFromUrlAsync(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "series",
            "CoverArt",
            "https://example.test/poster.jpg");

        Assert.True(result);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal(
            "http://localhost:61495/metadata/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/artwork/series/CoverArt/from-url",
            capturedRequest.RequestUri!.ToString());

        var payload = await capturedRequest.Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(payload);
        Assert.Equal("https://example.test/poster.jpg", json.RootElement.GetProperty("image_url").GetString());
    }

    private static HttpClient CreateHttpClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new StubHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("http://localhost:61495"),
        };

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
