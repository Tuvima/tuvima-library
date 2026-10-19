using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Activity;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>The Dashboard's reads and deletion for the Activity page: what is asked, and how each kind of refusal is told apart.</summary>
public sealed class EngineApiClientActivityListTests
{
    private static readonly string Key = new('d', 64);

    private static EngineApiClient Client(ScriptedHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:61495") };
        return new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
    }

    [Fact]
    public async Task TheList_AsksWithTheScopeAndEveryFilter_AndTurnsEveryPictureAddressIntoADashboardAddress()
    {
        var song = new PersonalActivityItemDto { Id = "song", Kind = PersonalActivityKindValues.Listen, ArtworkUrl = $"/api/v1/activity/artwork/{Key}" };
        var attempt = new PersonalActivityItemDto { Id = "attempt", ArtworkUrl = $"/api/v1/activity/artwork/{Key}?scope=everyone" };
        var item = new PersonalActivityItemDto
        {
            Id = "session",
            Kind = PersonalActivityKindValues.MusicSession,
            ArtworkUrl = "https://elsewhere.test/not-ours.jpg",
            CoverUrls = [$"/api/v1/activity/artwork/{Key}", "https://elsewhere.test/cover.jpg"],
            Songs = [song],
            Attempts = [attempt],
        };
        var handler = new ScriptedHandler(HttpStatusCode.OK, new PersonalActivityPageDto([item], "next", true));
        using var client = Client(handler);

        var outcome = await client.GetPersonalActivityAsync(new PersonalActivityRequest
        {
            Scope = "everyone",
            Search = "Tremors",
            Kinds = ["watch", "listen"],
            From = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            Outcome = "finished",
            Device = "Living room TV",
            GroupByTitle = true,
            ExpandSongs = true,
            Cursor = "c1",
            Limit = 50,
        });

        var path = Uri.UnescapeDataString(handler.Requests.Single().PathAndQuery);
        Assert.Equal(HttpMethod.Get, handler.Requests.Single().Method);
        Assert.StartsWith("/api/v1/activity?", path, StringComparison.Ordinal);
        foreach (var expected in new[]
        {
            "scope=everyone", "q=Tremors", "kind=watch,listen", "from=2026-09-01T00:00:00.0000000+00:00", "to=2026-10-01T00:00:00.0000000+00:00",
            "outcome=finished", "device=Living room TV", "group=title", "expand=songs", "cursor=c1", "limit=50",
        })
        {
            Assert.Contains(expected, path, StringComparison.Ordinal);
        }

        Assert.True(outcome.IsOk);
        Assert.True(outcome.Value!.IncludesOthers);
        var mapped = Assert.Single(outcome.Value.Items);
        Assert.Null(mapped.ArtworkUrl);
        Assert.Equal([$"/engine-activity-artwork/{Key}"], mapped.CoverUrls);
        Assert.Equal($"/engine-activity-artwork/{Key}", mapped.Songs![0].ArtworkUrl);
        // The scope the picture was asked for under stays on its address, so the Engine can check it.
        Assert.Equal($"/engine-activity-artwork/{Key}?scope=everyone", mapped.Attempts![0].ArtworkUrl);
    }

    [Fact]
    public async Task OwnScope_IsNotSentAtAll_SoTheDefaultIsTheEnginesNotATypedAddress()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, new PersonalActivityPageDto([], null, false));
        using var client = Client(handler);

        await client.GetPersonalActivityAsync(new PersonalActivityRequest { Limit = 25 });

        Assert.Equal("/api/v1/activity?limit=25", handler.Requests.Single().PathAndQuery);
    }

    [Fact]
    public async Task ThePeriods_AreAskedWithTheFiltersButNeverPagingOrGrouping()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, new PersonalActivityPeriodsDto([new(2026, 3, [new(10, 3)])]));
        using var client = Client(handler);

        var outcome = await client.GetPersonalActivityPeriodsAsync(new PersonalActivityRequest
        {
            Scope = "household:3f2b2a1e-7a5c-4f43-8e0e-111111111111",
            Kinds = ["read"],
            GroupByTitle = true,
            Cursor = "c1",
            Limit = 50,
        });

        var path = Uri.UnescapeDataString(handler.Requests.Single().PathAndQuery);
        Assert.StartsWith("/api/v1/activity/periods?", path, StringComparison.Ordinal);
        Assert.Contains("scope=household:3f2b2a1e-7a5c-4f43-8e0e-111111111111", path, StringComparison.Ordinal);
        Assert.Contains("kind=read", path, StringComparison.Ordinal);
        Assert.DoesNotContain("group", path, StringComparison.Ordinal);
        Assert.DoesNotContain("cursor", path, StringComparison.Ordinal);
        Assert.DoesNotContain("limit", path, StringComparison.Ordinal);
        Assert.Equal(3, outcome.Value!.Years.Single().Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "admin_unlock_required", PersonalActivityOutcomeStatus.UnlockRequired)]
    [InlineData(HttpStatusCode.Forbidden, null, PersonalActivityOutcomeStatus.Forbidden)]
    [InlineData(HttpStatusCode.Forbidden, "confirm_its_you", PersonalActivityOutcomeStatus.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, null, PersonalActivityOutcomeStatus.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, null, PersonalActivityOutcomeStatus.Invalid)]
    [InlineData(HttpStatusCode.InternalServerError, null, PersonalActivityOutcomeStatus.Unavailable)]
    public async Task ARefusal_KeepsItsKindAndTheEnginesSentence_SoThePageCanTellThemApart(HttpStatusCode status, string? code, PersonalActivityOutcomeStatus expected)
    {
        var problem = new Dictionary<string, object?> { ["title"] = "Access denied.", ["detail"] = "Plain sentence from the Engine.", ["status"] = (int)status };
        if (code is not null)
        {
            problem["code"] = code;
        }

        using var client = Client(new ScriptedHandler(status, problem));

        var outcome = await client.GetPersonalActivityAsync(new PersonalActivityRequest { Scope = "everyone" });

        Assert.Equal(expected, outcome.Status);
        Assert.False(outcome.IsOk);
        Assert.Null(outcome.Value);
        if (expected != PersonalActivityOutcomeStatus.Unavailable)
        {
            Assert.Equal("Plain sentence from the Engine.", outcome.Message);
        }
    }

    [Fact]
    public async Task ARefusalWithNoReadableBody_IsStillARefusal()
    {
        using var client = Client(new ScriptedHandler(HttpStatusCode.Forbidden, null));

        var outcome = await client.GetPersonalActivityAsync(new PersonalActivityRequest());

        Assert.Equal(PersonalActivityOutcomeStatus.Forbidden, outcome.Status);
        Assert.Null(outcome.Message);
    }

    [Fact]
    public async Task APreview_AsksWhichScopeAndDate_AndOwnScopeIsOmitted()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, new PersonalActivityDeletePreviewDto(12));
        using var client = Client(handler);

        var preview = await client.PreviewPersonalActivityDeletionAsync("profile:3f2b2a1e-7a5c-4f43-8e0e-111111111111", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await client.PreviewPersonalActivityDeletionAsync("me", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(12, preview.Value!.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        var first = Uri.UnescapeDataString(handler.Requests[0].PathAndQuery);
        Assert.StartsWith("/api/v1/activity/delete-preview?", first, StringComparison.Ordinal);
        Assert.Contains("before=2026-01-01T00:00:00.0000000+00:00", first, StringComparison.Ordinal);
        Assert.Contains("scope=profile:3f2b2a1e-7a5c-4f43-8e0e-111111111111", first, StringComparison.Ordinal);
        Assert.DoesNotContain("scope", handler.Requests[1].PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_IsADeleteRequestOnTheActivityRoute_AndTellsAnUnlockRefusalApart()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, new PersonalActivityDeleteResultDto(7));
        using var client = Client(handler);

        var deleted = await client.DeletePersonalActivityAsync("everyone", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(7, deleted.Value!.Deleted);
        Assert.Equal(HttpMethod.Delete, handler.Requests.Single().Method);
        Assert.StartsWith("/api/v1/activity?", handler.Requests.Single().PathAndQuery, StringComparison.Ordinal);

        using var locked = Client(new ScriptedHandler(HttpStatusCode.Forbidden, new Dictionary<string, object?> { ["code"] = "admin_unlock_required", ["detail"] = "Unlock." }));
        var refused = await locked.DeletePersonalActivityAsync("everyone", DateTimeOffset.UtcNow);
        Assert.Equal(PersonalActivityOutcomeStatus.UnlockRequired, refused.Status);
    }

    [Fact]
    public async Task TheSummary_IsTheCallersLifetimeTotals_AndNullWhenTheEngineRefuses()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, new PersonalActivitySummaryDto(7200, 600, 9000, 3600, 5400, 12, 3, "2026-01-01T00:00:00Z"));
        using var client = Client(handler);

        var summary = await client.GetPersonalActivitySummaryAsync();

        Assert.Equal("/api/v1/activity/summary", handler.Requests.Single().PathAndQuery);
        Assert.Equal(12, summary!.FinishedCount);
        Assert.Equal(3, summary.InProgressCount);
        Assert.Equal(9000, summary.ListenSeconds);

        using var refused = Client(new ScriptedHandler(HttpStatusCode.Forbidden, null));
        Assert.Null(await refused.GetPersonalActivitySummaryAsync());
    }

    [Fact]
    public void TheExportAddress_CarriesTheScopeAndTheFilters_ButNotPagingGroupingOrSongs()
    {
        var request = new PersonalActivityRequest
        {
            Scope = "everyone",
            Search = "Tremors & co",
            Kinds = ["watch", "read"],
            Outcome = "not_finished",
            GroupByTitle = true,
            ExpandSongs = true,
            Cursor = "c1",
            Limit = 50,
        };

        var href = request.ExportHref();

        Assert.StartsWith("/engine-activity-export?", href, StringComparison.Ordinal);
        Assert.Equal(
            "/engine-activity-export?scope=everyone&q=Tremors%20%26%20co&kind=watch%2Cread&outcome=not_finished",
            href);
        Assert.Equal("/engine-activity-export", new PersonalActivityRequest().ExportHref());
        Assert.All(PersonalActivityRequest.FilterKeys, key => Assert.DoesNotContain(key, new[] { "cursor", "limit", "group", "expand" }));
    }

    private sealed class ScriptedHandler(HttpStatusCode status, object? body) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string PathAndQuery)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = JsonContent.Create(body, body.GetType());
            }

            return Task.FromResult(response);
        }
    }
}
