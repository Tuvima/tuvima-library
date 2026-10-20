using System.Net;
using MediaEngine.Domain;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Tests.ProviderRecording;

/// <summary>
/// The adapter must not know which provider a file describes. The shipped TMDB and ComicVine
/// files, renamed, produce the same requests and claims as the recordings (apart from the
/// provider's own name inside the manifest).
/// </summary>
public sealed class RenamedProviderTests
{
    [Fact]
    public async Task TmdbFileUnderAnotherName_RecordsTheSameRequestsAndClaims()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Url.Contains("/search/movie?", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""{"results":[{"id":78,"title":"Blade Runner","overview":"A future noir.","release_date":"1982-06-25","poster_path":"/poster.jpg","vote_average":8.1,"original_language":"en"}]}""");
            }

            if (request.Url.Contains("/movie/78?", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""
                    {"id":78,"title":"Blade Runner","overview":"A detail overview.","tagline":"Man has made his match.","runtime":117,
                     "belongs_to_collection":{"id":422837,"name":"Blade Runner Collection"},
                     "production_companies":[{"name":"Warner Bros.","logo_path":"/wb.png"},{"name":"The Ladd Company"}],
                     "external_ids":{"imdb_id":"tt0083658"},
                     "release_dates":{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"R"}]}]},
                     "credits":{"cast":[{"id":2,"name":"Harrison Ford","character":"Deckard","order":0}],"crew":[{"id":5,"name":"Ridley Scott","job":"Director"}]}}
                    """);
            }

            return request.Url.Contains("/collection/422837?", StringComparison.Ordinal)
                ? RecordingHandler.Json("""{"id":422837,"parts":[{"id":335984,"title":"Blade Runner 2049","release_date":"2017-10-06"},{"id":78,"title":"Blade Runner","release_date":"1982-06-25"}]}""")
                : RecordingHandler.Status(HttpStatusCode.NotFound);
        });

        var claims = await ShippedProviderHarness.Tmdb(handler, config => config.Name = "any_movie_catalog").FetchAsync(
            new ProviderLookupRequest
            {
                EntityId = Guid.NewGuid(),
                EntityType = EntityType.MediaAsset,
                MediaType = MediaType.Movies,
                Title = "Blade Runner",
                Year = "1982",
                Language = "en",
                Country = "US",
            });

        Assert.Contains("GET https://api.themoviedb.org/3/movie/78?language=en-US&append_to_response=credits,release_dates,external_ids,images", handler.Lines);
        Assert.Contains("GET https://api.themoviedb.org/3/collection/422837?language=en-US", handler.Lines);
        Assert.Contains(claims, claim => claim is { Key: "series_position", Value: "1" });
        Assert.Contains(claims, claim => claim is { Key: "director", Value: "Ridley Scott" });
        var manifest = Assert.Single(claims, claim => claim.Key == "sequence_manifest_json");
        Assert.Contains("\"provider\":\"any_movie_catalog\"", manifest.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComicVineFileUnderAnotherName_RecordsTheSameRequestsAndClaims()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Url.Contains("/volume/4050-900/", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""{"results":{"id":900,"name":"Saga","count_of_issues":12,"start_year":"1992","publisher":{"name":"Other Publisher"}}}""");
            }

            if (request.Url.Contains("/volume/4050-1234/", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""{"results":{"id":1234,"name":"Saga","count_of_issues":72,"start_year":"2012","publisher":{"name":"Image Comics"}}}""");
            }

            return request.Url.Contains("field_list=", StringComparison.Ordinal)
                ? RecordingHandler.Json("""{"number_of_total_results":1,"results":[{"id":222,"issue_number":"1","name":"Chapter One","cover_date":"2012-03-14"}]}""")
                : RecordingHandler.Json("""
                    {"results":[
                      {"name":"Chapter One","issue_number":"1","id":111,"cover_date":"2012-03-14","volume":{"id":900,"name":"Saga"}},
                      {"name":"Chapter One","issue_number":"1","id":222,"cover_date":"2012-03-14","volume":{"id":1234,"name":"Saga"}}]}
                    """);
        });

        var claims = await ShippedProviderHarness.ConfigDriven(
            "comicvine", handler, tweak: config => config.Name = "any_comic_catalog").FetchAsync(new ProviderLookupRequest
            {
                EntityId = Guid.NewGuid(),
                EntityType = EntityType.MediaAsset,
                MediaType = MediaType.Comics,
                Title = "Saga #1",
                Series = "Saga",
                Year = "2012",
                BaseUrl = "https://comicvine.gamespot.com/api",
                Hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [MetadataFieldConstants.Series] = "Saga",
                    [MetadataFieldConstants.SeriesPosition] = "1",
                    [MetadataFieldConstants.PublisherField] = "Image Comics",
                },
            });

        Assert.Contains(claims, claim => claim is { Key: "comic_vine_id", Value: "222" });
        Assert.Contains(claims, claim => claim is { Key: "comic_vine_volume_id", Value: "1234" });
        Assert.Contains(claims, claim => claim is { Key: "sequence_total", Value: "72" });
        Assert.Contains(claims, claim => claim.Key == "sequence_manifest_json");
        Assert.Equal(2, handler.Lines.Count(line => line.Contains("/volume/4050-", StringComparison.Ordinal)));
    }
}
