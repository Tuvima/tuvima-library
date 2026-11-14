using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The pictures on the Activity page (spec library-activity, issue 690): the Engine answers its own cover addresses, and the Dashboard
/// turns every one into an address on its own origin (the image proxy) before the page draws it, so none comes out as a broken image.
/// </summary>
public sealed class EngineApiClientActivityPicturesTests
{
    private static readonly Guid Work = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly string EngineCover = $"/stream/entity/work/{Work:D}/cover?size=s";
    private static readonly string ProxiedCover = $"/engine-image/stream/entity/work/{Work:D}/cover?size=s";

    [Fact]
    public async Task TheSmallCoversOfEveryHistoryEntry_AreServedThroughTheImageProxy()
    {
        var run = new ActivityRunDto(
            Guid.NewGuid(), "library", "Library", ActivityRunKindNames.Added, ActivityRunStatusNames.Completed,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 3, [new ActivityRunCountDto("Movies", 3)],
            [new ActivityCoverDto(EngineCover, "Movies", "Dune"), new ActivityCoverDto(null, "Movies", "No art")]);
        using var client = Client(new ActivityRunsDto([run], false, null));

        var runs = await client.GetActivityRunsAsync(null, null, null, null, null, true, 0, 50);

        var covers = Assert.Single(runs!.Runs).Covers;
        Assert.Equal(ProxiedCover, covers[0].Url);
        Assert.Null(covers[1].Url);
    }

    [Fact]
    public async Task TheTilesOfAnOpenedBatch_AreServedThroughTheImageProxy()
    {
        var group = new ActivityGroupDto("movie:dune", "Dune", "Movies", 1, EngineCover, null);
        using var client = Client(new ActivityRunGroupsDto(Guid.NewGuid(), [group], false));

        var groups = await client.GetActivityRunGroupsAsync(Guid.NewGuid(), null, null, 0, 24);

        Assert.Equal(ProxiedCover, Assert.Single(groups!.Groups).CoverUrl);
    }

    [Fact]
    public async Task ASearchedTitlesCover_IsServedThroughTheImageProxy_AndALinkElsewhereIsLeftAlone()
    {
        var engineCard = new ActivityTitleCardDto("show:expanse", "The Expanse", "TV", EngineCover, null, 12, []);
        var elsewhere = new ActivityTitleCardDto("show:other", "Other", "TV", "https://elsewhere.test/cover.jpg", null, 1, []);
        using var client = Client(new ActivityTitleSearchDto([engineCard, elsewhere]));

        var found = await client.SearchActivityTitlesAsync("the", null, null, null, null);

        Assert.Equal(ProxiedCover, found!.Titles[0].CoverUrl);
        Assert.Equal("https://elsewhere.test/cover.jpg", found.Titles[1].CoverUrl);
    }

    [Fact]
    public async Task WhenTheEngineCannotBeReached_TheHistoryReadsAnswerNothingRatherThanThrowing()
    {
        using var client = Client(null, HttpStatusCode.InternalServerError);

        Assert.Null(await client.GetActivityRunsAsync(null, null, null, null, null, true, 0, 50));
        Assert.Null(await client.GetActivityRunGroupsAsync(Guid.NewGuid(), null, null, 0, 24));
        Assert.Null(await client.SearchActivityTitlesAsync("the", null, null, null, null));
    }

    private static EngineApiClient Client(object? body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var http = new HttpClient(new Handler(status, body)) { BaseAddress = new Uri("http://localhost:61495") };
        return new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
    }

    private sealed class Handler(HttpStatusCode status, object? body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = JsonContent.Create(body, body.GetType());
            }

            return Task.FromResult(response);
        }
    }
}
