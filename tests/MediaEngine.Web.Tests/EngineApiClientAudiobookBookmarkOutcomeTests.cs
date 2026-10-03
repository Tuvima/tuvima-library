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
