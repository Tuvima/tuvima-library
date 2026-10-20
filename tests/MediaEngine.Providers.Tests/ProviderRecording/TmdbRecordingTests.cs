using System.Net;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Tests.ProviderRecording;

/// <summary>
/// Records the exact requests and claims the shipped TMDB provider produces. The golden files
/// pin behaviour across the WP9 conversion of TMDB's name-keyed enrichment to JSON.
/// </summary>
public sealed class TmdbRecordingTests
{
    private const string MovieSearch = """
        {"results":[{"id":78,"title":"Blade Runner","overview":"A future noir.","release_date":"1982-06-25","poster_path":"/poster.jpg","vote_average":8.1,"original_language":"en"}]}
        """;

    private const string MovieDetail = """
        {
          "id": 78, "title": "Blade Runner", "overview": "A detail overview.", "tagline": "Man has made his match.", "runtime": 117,
          "belongs_to_collection": { "id": 422837, "name": "Blade Runner Collection" },
          "production_companies": [
            { "name": "Warner Bros.", "logo_path": "/wb.png" }, { "name": "The Ladd Company", "logo_path": null },
            { "name": "" , "logo_path": "/blank.png" }, { "name": "Shaw Brothers" }, { "name": "Blade Runner Partnership" },
            { "name": "Sixth Co" }, { "name": "Seventh Co" }
          ],
          "external_ids": { "imdb_id": "tt0083658", "tvdb_id": null, "wikidata_id": "Q184843" },
          "release_dates": { "results": [
            { "iso_3166_1": "DE", "release_dates": [ { "certification": "16" } ] },
            { "iso_3166_1": "US", "release_dates": [ { "certification": "" }, { "certification": "R" } ] } ] },
          "credits": {
            "cast": [
              { "id": 3, "name": "Rutger Hauer", "character": "Roy Batty", "order": 1, "profile_path": "/rh.jpg" },
              { "id": 2, "name": "Harrison Ford", "character": "Deckard", "order": 0, "profile_path": "/hf.jpg" },
              { "id": 9, "name": "zed", "character": "X", "order": 1, "profile_path": null },
              { "id": 10, "name": "Alpha", "character": "Y" },
              { "id": 11, "name": "", "character": "Nameless", "order": 2 }
            ],
            "crew": [
              { "id": 5, "name": "Ridley Scott", "job": "Director", "profile_path": "/rs.jpg" },
              { "id": 6, "name": "Hampton Fancher", "job": "Screenplay" },
              { "id": 7, "name": "Vangelis", "job": "Original Music Composer", "profile_path": "/v.jpg" },
              { "id": 8, "name": "Michael Deeley", "job": "Producer" },
              { "id": 12, "name": "Gaffer Gus", "job": "Gaffer" },
              { "id": 13, "name": "Multi Job", "jobs": [ { "job": "" }, { "job": "Executive Producer" } ] },
              { "id": 14, "name": "Story Teller", "job": "Story" }
            ] }
        }
        """;

    private const string Collection = """
        {"id":422837,"name":"Blade Runner Collection","parts":[
          {"id":335984,"title":"Blade Runner 2049","release_date":"2017-10-06"},
          {"id":78,"title":"Blade Runner","release_date":"1982-06-25"},
          {"id":999,"title":"Blade Runner: Black Out 2022"},
          {"id":0,"title":"Zero"}]}
        """;

    [Fact]
    public async Task MovieWithCollectionCreditsAndRatings_RecordsRequestsAndClaims()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Url.Contains("/search/movie?", StringComparison.Ordinal))
            {
                return RecordingHandler.Json(MovieSearch);
            }

            if (request.Url.Contains("/movie/78?", StringComparison.Ordinal))
            {
                return RecordingHandler.Json(MovieDetail);
            }

            return request.Url.Contains("/collection/422837?", StringComparison.Ordinal)
                ? RecordingHandler.Json(Collection)
                : RecordingHandler.Status(HttpStatusCode.NotFound);
        });

        var claims = await ShippedProviderHarness.Tmdb(handler).FetchAsync(MovieRequest());

        RecordingSnapshot.Verify("tmdb-movie-collection",
            RecordingText.Section("requests", handler.Lines),
            RecordingText.Section("claims", [RecordingText.Claims(claims)]));
    }

    [Fact]
    public async Task MovieDetailNotFound_KeepsSearchClaimsOnly()
    {
        var handler = new RecordingHandler(request => request.Url.Contains("/search/movie?", StringComparison.Ordinal)
            ? RecordingHandler.Json(MovieSearch)
            : RecordingHandler.Status(HttpStatusCode.NotFound));

        var claims = await ShippedProviderHarness.Tmdb(handler).FetchAsync(MovieRequest());

        RecordingSnapshot.Verify("tmdb-movie-detail-404",
            RecordingText.Section("requests", handler.Lines),
            RecordingText.Section("claims", [RecordingText.Claims(claims)]));
    }

    [Fact]
    public async Task MovieDetailServerError_KeepsSearchClaimsOnly()
    {
        var handler = new RecordingHandler(request => request.Url.Contains("/search/movie?", StringComparison.Ordinal)
            ? RecordingHandler.Json(MovieSearch)
            : RecordingHandler.Status(HttpStatusCode.InternalServerError));

        var claims = await ShippedProviderHarness.Tmdb(handler).FetchAsync(MovieRequest());

        RecordingSnapshot.Verify("tmdb-movie-detail-500",
            RecordingText.Section("requests", handler.Lines),
            RecordingText.Section("claims", [RecordingText.Claims(claims)]));
    }

    [Fact]
    public async Task MovieCollectionServerError_KeepsDetailClaimsWithoutSequence()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Url.Contains("/search/movie?", StringComparison.Ordinal))
            {
                return RecordingHandler.Json(MovieSearch);
            }

            return request.Url.Contains("/movie/78?", StringComparison.Ordinal)
                ? RecordingHandler.Json(MovieDetail)
                : RecordingHandler.Status(HttpStatusCode.InternalServerError);
        });

        var claims = await ShippedProviderHarness.Tmdb(handler).FetchAsync(MovieRequest());

        RecordingSnapshot.Verify("tmdb-movie-collection-500",
            RecordingText.Section("requests", handler.Lines),
            RecordingText.Section("claims", [RecordingText.Claims(claims)]));
    }

    [Fact]
    public async Task TvShowWithAggregateCreditsAndNetworks_RecordsRequestsAndClaims()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Url.Contains("/search/tv?", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""
                    {"results":[{"id":95396,"name":"Severance","overview":"Work-life separation.","first_air_date":"2022-02-18","poster_path":"/sev.jpg","original_language":"en"}]}
                    """);
            }

            return request.Url.Contains("/tv/95396?", StringComparison.Ordinal)
                ? RecordingHandler.Json("""
                    {
                      "id": 95396, "name": "Severance", "overview": "Work-life separation.", "tagline": "",
                      "networks": [ { "name": "", "logo_path": "/none.png" }, { "name": "Apple TV+", "logo_path": "/atv.png" }, { "name": "Second Net", "logo_path": "/n2.png" } ],
                      "production_companies": [ { "name": "Fifth Season", "logo_path": "/fs.png" }, { "name": "Red Hour" } ],
                      "external_ids": { "imdb_id": "tt11280740", "tvdb_id": 371980, "wikidata_id": null },
                      "content_ratings": { "results": [ { "iso_3166_1": "FR", "rating": "12" }, { "iso_3166_1": "GB", "rating": "15" }, { "iso_3166_1": "US", "rating": "TV-MA" } ] },
                      "aggregate_credits": {
                        "cast": [
                          { "id": 1, "name": "Adam Scott", "order": 0, "profile_path": "/as.jpg", "roles": [
                              { "character": "Mark S.", "episode_count": 5 }, { "character": "Mark Scout", "episode_count": 5 },
                              { "character": "Mark", "episode_count": 9 }, { "character": "mark s.", "episode_count": 5 }, { "character": "Mark X", "episode_count": 5 } ] },
                          { "id": 2, "name": "Britt Lower", "order": 1, "roles": [ { "character": "Helly R." } ], "character": "Fallback" },
                          { "id": 3, "name": "Zach Cherry", "order": 1, "roles": [], "character": "Dylan G." }
                        ],
                        "crew": [
                          { "id": 20, "name": "Ben Stiller", "jobs": [ { "job": "Director" } ] },
                          { "id": 21, "name": "Dan Erickson", "jobs": [ { "job": "Writer" } ], "profile_path": "/de.jpg" },
                          { "id": 22, "name": "Theodore Shapiro", "jobs": [ { "job": "Music" } ] }
                        ]
                      }
                    }
                    """)
                : RecordingHandler.Status(HttpStatusCode.NotFound);
        });

        // tmdb.json lists only Movies under can_handle, so TV requests never reach the adapter in
        // production; the TV enrichment path is still exercised by widening can_handle here.
        var claims = await ShippedProviderHarness.Tmdb(handler, config => config.CanHandle!.MediaTypes.Add("TV")).FetchAsync(new ProviderLookupRequest
        {
            EntityId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.TV,
            Title = "Severance",
            Year = "2022",
            Language = "en",
            Country = "US",
        });

        RecordingSnapshot.Verify("tmdb-tv-aggregate-credits",
            RecordingText.Section("requests", handler.Lines),
            RecordingText.Section("claims", [RecordingText.Claims(claims)]));
    }

    [Fact]
    public async Task MovieSearch_RecordsNonEnglishLocaleRequests()
    {
        var handler = new RecordingHandler(request => request.Url.Contains("/search/movie?", StringComparison.Ordinal)
            ? RecordingHandler.Json(MovieSearch)
            : request.Url.Contains("/movie/78?", StringComparison.Ordinal)
                ? RecordingHandler.Json(MovieDetail)
                : RecordingHandler.Json(Collection));

        var claims = await ShippedProviderHarness.Tmdb(handler).FetchAsync(MovieRequest("fr", "ca"));

        RecordingSnapshot.Verify("tmdb-movie-locale-fr-ca",
            RecordingText.Section("requests", handler.Lines),
            RecordingText.Section("claims", [RecordingText.Claims(claims)]));
    }

    private static ProviderLookupRequest MovieRequest(string language = "en", string country = "US") => new()
    {
        EntityId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        EntityType = EntityType.MediaAsset,
        MediaType = MediaType.Movies,
        Title = "Blade Runner",
        Year = "1982",
        Language = language,
        Country = country,
    };
}
