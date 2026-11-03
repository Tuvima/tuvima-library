using System.Net;
using System.Text;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientAudiobookBookmarkOutcomeTests
{
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, AudiobookBookmarkOperationOutcome.DefiniteFailure)]
    [InlineData(HttpStatusCode.InternalServerError, AudiobookBookmarkOperationOutcome.Unknown)]
    public async Task CreateOutcomeClassifiesHttpFailureWithoutRetry(HttpStatusCode status, AudiobookBookmarkOperationOutcome expected)
    {
        var handler = new StaticHandler(status, "{}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.CreateAudiobookBookmarkWithOutcomeAsync(Guid.NewGuid(),
            new CreateAudiobookBookmarkRequestDto { AssetId = Guid.NewGuid(), PositionSeconds = 12 }, Guid.NewGuid());

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SuccessfulCreateWithUnparseableResponseIsUnknownAfterOnePost()
    {
        var handler = new StaticHandler(HttpStatusCode.Created, "not-json");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.CreateAudiobookBookmarkWithOutcomeAsync(Guid.NewGuid(),
            new CreateAudiobookBookmarkRequestDto { AssetId = Guid.NewGuid(), PositionSeconds = 12 }, Guid.NewGuid());

        Assert.Equal(AudiobookBookmarkOperationOutcome.Unknown, result.Outcome);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, AudiobookBookmarkOperationOutcome.DefiniteFailure)]
    [InlineData(HttpStatusCode.NotFound, AudiobookBookmarkOperationOutcome.DefiniteFailure)]
    [InlineData(HttpStatusCode.BadGateway, AudiobookBookmarkOperationOutcome.Unknown)]
    public async Task AdministratorBookmarkCallsKeepRefusalsApartFromUnconfirmedOutcomes(HttpStatusCode status, AudiobookBookmarkOperationOutcome expected)
    {
        var handler = new StaticHandler(status, "{}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        var profile = Guid.NewGuid();

        Assert.Equal(expected, (await client.GetEveryonesBookmarksWithOutcomeAsync(Guid.NewGuid(), profile)).Outcome);
        Assert.Equal(expected, (await client.PromoteAudiobookBookmarkWithOutcomeAsync(Guid.NewGuid(), "Name", profile)).Outcome);
        Assert.Equal(expected, (await client.UpdateGlobalAudiobookBookmarkWithOutcomeAsync(Guid.NewGuid(), new UpdateAudiobookBookmarkRequestDto { Label = "x" }, profile)).Outcome);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task PromoteSendsOnePostToThePromoteAddressWithTheName_AndReadsTheGlobalBookmarkBack()
    {
        var bookmark = new AudiobookBookmarkDto { Id = Guid.NewGuid(), IsGlobal = true, PositionSeconds = 30, Label = "Scene" };
        var handler = new StaticHandler(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(bookmark));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.PromoteAudiobookBookmarkWithOutcomeAsync(bookmark.Id, "Scene", Guid.NewGuid());

        Assert.Equal(AudiobookBookmarkOperationOutcome.Success, result.Outcome);
        Assert.True(result.Value!.IsGlobal);
        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
