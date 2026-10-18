using System.Net;
using System.Net.Http;
using System.Text;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The detail load tells the page why it failed, so a missing item, a refused account and an
/// unreachable Engine each get their own state instead of one generic "not found".
/// </summary>
public sealed class EngineApiClientDetailLoadTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task LoadDetailPageAsync_ReturnsThePageWhenTheEngineAnswersOk()
    {
        const string json = """
            { "id": "11111111-1111-1111-1111-111111111111", "entityType": 0, "title": "Dune",
              "artwork": { "coverUrl": "/stream/cover" } }
            """;
        var client = ClientReplying(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") });

        var result = await client.LoadDetailPageAsync(DetailEntityType.Work, Id);

        Assert.Equal(DetailPageLoadFailure.None, result.Failure);
        Assert.Equal("Dune", result.Detail!.Title);
        Assert.Equal("http://localhost:61495/stream/cover", result.Detail.Artwork.CoverUrl);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, DetailPageLoadFailure.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, DetailPageLoadFailure.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized, DetailPageLoadFailure.Unreachable)]
    [InlineData(HttpStatusCode.Forbidden, DetailPageLoadFailure.NotAllowed)]
    [InlineData(HttpStatusCode.InternalServerError, DetailPageLoadFailure.Unreachable)]
    [InlineData(HttpStatusCode.BadGateway, DetailPageLoadFailure.Unreachable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, DetailPageLoadFailure.Unreachable)]
    public async Task LoadDetailPageAsync_ClassifiesTheEngineStatusIntoADistinctFailure(
        HttpStatusCode status, DetailPageLoadFailure expected)
    {
        var client = ClientReplying(_ => new HttpResponseMessage(status));

        var result = await client.LoadDetailPageAsync(DetailEntityType.Work, Id);

        Assert.Null(result.Detail);
        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public async Task LoadDetailPageAsync_ReportsUnreachableWhenTheConnectionFails()
    {
        var client = ClientReplying(_ => throw new HttpRequestException("connection refused"));

        var result = await client.LoadDetailPageAsync(DetailEntityType.Work, Id);

        Assert.Null(result.Detail);
        Assert.Equal(DetailPageLoadFailure.Unreachable, result.Failure);
    }

    [Fact]
    public async Task LoadDetailPageAsync_ReportsUnreachableWhenTheRequestTimesOut()
    {
        // HttpClient reports a timeout as a TaskCanceledException that the caller's token did not cause.
        var client = ClientReplying(_ => throw new TaskCanceledException("timed out"));

        var result = await client.LoadDetailPageAsync(DetailEntityType.Work, Id);

        Assert.Null(result.Detail);
        Assert.Equal(DetailPageLoadFailure.Unreachable, result.Failure);
    }

    [Fact]
    public async Task LoadDetailPageAsync_ReturnsWithoutAPageWhenTheCallerCancels()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var client = ClientReplying(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await client.LoadDetailPageAsync(DetailEntityType.Work, Id, ct: cts.Token);

        Assert.Null(result.Detail);
        Assert.NotEqual(DetailPageLoadFailure.Unreachable, result.Failure);
    }

    [Fact]
    public async Task GetDetailPageAsync_StillReturnsNullForEveryFailure()
    {
        var client = ClientReplying(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        Assert.Null(await client.GetDetailPageAsync(DetailEntityType.Work, Id));
    }

    [Fact]
    public async Task GetDetailPageAsync_LeavesNoErrorBehindForOtherScreens()
    {
        var client = ClientReplying(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await client.GetDetailPageAsync(DetailEntityType.Work, Id);

        Assert.Null(client.LastError);
    }

    [Fact]
    public async Task LoadDetailPageAsync_RecordsTheFailureForTheDetailPage()
    {
        var client = ClientReplying(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await client.LoadDetailPageAsync(DetailEntityType.Work, Id);

        Assert.NotNull(client.LastError);
    }

    [Fact]
    public async Task LoadDetailPageAsync_SendsNoProfileParameter()
    {
        HttpRequestMessage? captured = null;
        var client = ClientReplying(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        await client.LoadDetailPageAsync(DetailEntityType.TvShow, Id, DetailPresentationContext.Watch, sequence: SequenceWindowQuery.Windowed);

        Assert.NotNull(captured);
        Assert.DoesNotContain("profileId", captured!.RequestUri!.Query, StringComparison.OrdinalIgnoreCase);
    }

    private static EngineApiClient ClientReplying(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new HttpClient(new StubHandler(responder)) { BaseAddress = new Uri("http://localhost:61495") },
            NullLogger<EngineApiClient>.Instance);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
