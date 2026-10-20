using System.Text;
using MediaEngine.Domain;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Tests.ProviderRecording;

/// <summary>
/// Records the exact requests and claims the shipped ComicVine provider produces. The golden files
/// pin behaviour across the WP9 conversion of ComicVine's name-keyed hooks to JSON.
/// </summary>
public sealed class ComicVineRecordingTests
{
    private static ProviderLookupRequest Request(
        string title, string series, string issue, string? year = "2012", string? publisher = null,
        string? comicVineId = null)
    {
        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Series] = series,
            [MetadataFieldConstants.SeriesPosition] = issue,
        };
        if (publisher is not null)
        {
            hints[MetadataFieldConstants.PublisherField] = publisher;
        }

        if (comicVineId is not null)
        {
            hints[BridgeIdKeys.ComicVineId] = comicVineId;
        }

        return new ProviderLookupRequest
        {
            EntityId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            EntityType = EntityType.MediaAsset,
            MediaType = MediaType.Comics,
            Title = title,
            Series = series,
            Year = year,
            BaseUrl = "https://comicvine.gamespot.com/api",
            Hints = hints,
        };
    }

    private static async Task RecordAsync(string name, RecordingHandler handler, ProviderLookupRequest request)
    {
        var claims = await ShippedProviderHarness.ComicVine(handler).FetchAsync(request);
        RecordingSnapshot.Verify(name,
            RecordingText.Section("requests", handler.Lines),
            RecordingText.Section("claims", [RecordingText.Claims(claims)]));
    }

    [Fact]
    public async Task SameNameRuns_ScoredByVolumeFacts()
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

            return RecordingHandler.Json("""
                {"results":[
                  {"name":"Chapter One","issue_number":"1","id":111,"cover_date":"2012-03-14","volume":{"id":900,"name":"Saga"},"image":{"original_url":"https://example.test/saga-wrong.jpg"}},
                  {"name":"Chapter One","issue_number":"1","id":222,"cover_date":"2012-03-14","volume":{"id":1234,"name":"Saga"},"image":{"original_url":"https://example.test/saga-right.jpg"}}]}
                """);
        });

        await RecordAsync("comicvine-same-name-runs", handler,
            Request("Saga #1", "Saga", "1", publisher: "Image Comics"));
    }

    [Fact]
    public async Task LocalizedRun_FallsBackToRunScopedVolumeSearch()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Url.Contains("resources=volume", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""
                    {"results":[
                      {"id":46568,"name":"Saga","count_of_issues":72,"start_year":"2012","publisher":{"name":"Image"}},
                      {"id":110032,"name":"Saga","count_of_issues":12,"start_year":"2013","publisher":{"name":"Cross Cult"}},
                      {"id":777,"name":"Saga of Tanya","count_of_issues":3,"start_year":"2020"}]}
                    """);
            }

            if (request.Url.Contains("/issues/", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""
                    {"results":[{"name":"Chapter Two","issue_number":"2","id":321316,"cover_date":"2012-04-01","volume":{"id":46568,"name":"Saga"},"image":{"original_url":"https://example.test/saga-original.jpg"}}]}
                    """);
            }

            if (request.Url.Contains("/volume/4050-46568/", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""{"results":{"id":46568,"name":"Saga","count_of_issues":72,"start_year":"2012","publisher":{"name":"Image"}}}""");
            }

            if (request.Url.Contains("/volume/4050-110032/", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""{"results":{"id":110032,"name":"Saga","count_of_issues":12,"start_year":"2013","publisher":{"name":"Cross Cult"}}}""");
            }

            return RecordingHandler.Json("""
                {"results":[{"name":null,"issue_number":"2","id":667131,"cover_date":"2013-12-05","volume":{"id":110032,"name":"Saga"},
                  "description":"<p>Die fantastische Weltraum-Opera geht in die zweite Runde und das ist nicht von einem Anderen.</p>",
                  "image":{"original_url":"https://example.test/saga-localized.jpg"}}]}
                """);
        });

        await RecordAsync("comicvine-localized-run-scoped", handler, Request("Saga #2", "Saga", "2"));
    }

    [Fact]
    public async Task PagedVolumeManifest_RecordsEveryPageRequestAndAuthoritativeManifest()
    {
        var firstPage = string.Join(",", Enumerable.Range(1, 100).Select(n =>
            $$"""{"id":{{1000 + n}},"issue_number":"{{n}}","name":"Issue {{n}}","cover_date":"2012-03-14"}"""));
        var secondPage = string.Join(",", Enumerable.Range(101, 5).Select(n =>
            $$"""{"id":{{1000 + n}},"issue_number":"{{n}}","name":null,"cover_date":"2013-01-02"}"""));
        var handler = new RecordingHandler(request =>
        {
            if (request.Url.Contains("field_list=", StringComparison.Ordinal))
            {
                return RecordingHandler.Json(request.Url.Contains("offset=0", StringComparison.Ordinal)
                    ? $$"""{"number_of_total_results":105,"results":[{{firstPage}}]}"""
                    : $$"""{"number_of_total_results":105,"results":[{{secondPage}}]}""");
            }

            if (request.Url.Contains("/volume/4050-900/", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""{"results":{"id":900,"name":"Saga","count_of_issues":105,"start_year":"2012"}}""");
            }

            return RecordingHandler.Json("""
                {"results":[{"name":"Chapter One","issue_number":"1","id":1001,"cover_date":"2012-03-14","volume":{"id":900,"name":"Saga"}}]}
                """);
        });

        await RecordAsync("comicvine-paged-manifest", handler, Request("Saga #1", "Saga", "1"));
    }

    [Fact]
    public async Task NoIssueMatch_UsesVolumeSearchStrategyAndKeepsVolumeIdentity()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Url.Contains("resources=volume", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""{"results":[{"id":555,"name":"Akira","count_of_issues":6,"start_year":"1982","publisher":{"name":"Kodansha"}}]}""");
            }

            return request.Url.Contains("/volume/4050-555/", StringComparison.Ordinal)
                ? RecordingHandler.Json("""{"results":{"id":555,"name":"Akira","count_of_issues":6,"start_year":"1982","publisher":{"name":"Kodansha"}}}""")
                : RecordingHandler.Json("""{"results":[]}""");
        });

        await RecordAsync("comicvine-volume-search-strategy", handler,
            Request("Akira Vol. 1", "Akira", "1", "1982", "Kodansha"));
    }

    [Fact]
    public async Task StructuredCreatorCredits_BecomeCreatorClaims()
    {
        var handler = new RecordingHandler(request => request.Url.Contains("/volume/4050-1234/", StringComparison.Ordinal)
            ? RecordingHandler.Json("""{"results":{"id":1234,"name":"Saga","count_of_issues":72,"start_year":"2012","publisher":{"name":"Image Comics"}}}""")
            : RecordingHandler.Json("""
                {"results":[{"name":"Chapter One","issue_number":"1","id":222,"cover_date":"2012-03-14","volume":{"id":1234,"name":"Saga"},
                  "person_credits":[
                    {"name":"Brian K. Vaughan","role":"Writer"},{"name":"Fiona Staples","role":"Penciller"},
                    {"name":"Brian K. Vaughan","role":"Story, Script"},{"person":{"name":"Inky Ink"},"role":"inker"},
                    {"name":"Letter Person","role":"Letterer"},{"name":"No Role"}],
                  "description":"<p>The war goes on.</p>",
                  "image":{"original_url":"https://example.test/saga-en.jpg"}}]}
                """));

        await RecordAsync("comicvine-creator-credits", handler, Request("Saga #1", "Saga", "1"));
    }

    [Fact]
    public async Task ExactIdLookup_UsesIssueIdStrategyFirst()
    {
        var handler = new RecordingHandler(request => request.Url.Contains("/volume/4050-1234/", StringComparison.Ordinal)
            ? RecordingHandler.Json("""{"results":{"id":1234,"name":"Saga","count_of_issues":72,"start_year":"2012","publisher":{"name":"Image Comics"}}}""")
            : RecordingHandler.Json("""
                {"results":[{"name":"Chapter One","issue_number":"1","id":222,"cover_date":"2012-03-14","volume":{"id":1234,"name":"Saga"}}]}
                """));

        await RecordAsync("comicvine-issue-id-lookup", handler,
            Request("Saga #1", "Saga", "1", comicVineId: "222"));
    }

    [Fact]
    public async Task GermanDescription_IsDroppedForEnglishRequests()
    {
        var german = "Die fantastische Weltraum-Oper ist nicht von einem Anderen und mit der Familie von der Erde.";
        var handler = new RecordingHandler(request => request.Url.Contains("/volume/4050-1234/", StringComparison.Ordinal)
            ? RecordingHandler.Json("""{"results":{"id":1234,"name":"Saga","count_of_issues":72,"start_year":"2012"}}""")
            : RecordingHandler.Json(new StringBuilder()
                .Append("""{"results":[{"name":"Chapter One","issue_number":"1","id":222,"cover_date":"2012-03-14","volume":{"id":1234,"name":"Saga"},"description":"<p>""")
                .Append(german)
                .Append("""</p>"}]}""")
                .ToString()));

        await RecordAsync("comicvine-non-english-description", handler, Request("Saga #1", "Saga", "1"));
    }
}
