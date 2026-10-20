using System.Net;
using System.Text;
using System.Text.Json;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>What the Dashboard's client makes of each answer to POST /view/trash/empty. Emptying a Trash deletes personal files for good, so "nothing was deleted" is only ever claimed when the request provably never reached the Engine.</summary>
public sealed class EngineApiClientViewTrashEmptyTests
{
    [Fact]
    public async Task ASuccessfulEmpty_ReturnsWhatWasDeleted_AndSendsTheFingerprint()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, """
            {"deleted_count":126,"bytes_freed":4509715661,"skipped_count":3,
             "failed":[{"item_id":"11111111-2222-3333-4444-555555555555","reason":"Couldn't delete: the file is in use."}]}
            """);
        using var client = ClientFor(handler);

        var outcome = await client.EmptyViewTrashAsync("fingerprint-1");

        Assert.True(outcome.IsOk);
        Assert.Equal(126, outcome.Value!.DeletedCount);
        Assert.Equal(4509715661, outcome.Value.BytesFreed);
        Assert.Equal(3, outcome.Value.SkippedCount);
        Assert.Equal("Couldn't delete: the file is in use.", Assert.Single(outcome.Value.Failed).Reason);
        Assert.Equal("POST", handler.Method);
        Assert.Equal("/view/trash/empty", handler.Path);
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal("fingerprint-1", sent.RootElement.GetProperty("fingerprint").GetString());
    }

    [Fact]
    public async Task AChangedTrash_IsAConflict_CarryingTheFreshPreview_AndNothingWasDeleted()
    {
        var handler = new StaticHandler(HttpStatusCode.Conflict, """
            {"message":"Something changed since you opened this. Here's the updated list.",
             "preview":{"items":[],"galleries":[],"delete_count":4,"photo_count":4,"video_count":0,"document_count":0,"other_count":0,
                        "skipped":[],"bytes_freed":10,"has_compound":false,"fingerprint":"fresh"}}
            """);
        using var client = ClientFor(handler);

        var outcome = await client.EmptyViewTrashAsync("old");

        Assert.False(outcome.IsOk);
        Assert.Equal(ViewTrashOutcomeStatus.Conflict, outcome.Status);
        Assert.Equal("Something changed since you opened this. Here's the updated list.", outcome.Message);
        Assert.Equal("fresh", outcome.FreshPreview!.Fingerprint);
        Assert.Equal(4, outcome.FreshPreview.DeleteCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ARefusal_IsForbidden_AndCarriesNoPreview(HttpStatusCode status)
    {
        using var client = ClientFor(new StaticHandler(status, """{"detail":"Only a signed-in person can manage their Trash."}"""));

        var outcome = await client.EmptyViewTrashAsync("x");

        Assert.False(outcome.IsOk);
        Assert.Equal(ViewTrashOutcomeStatus.Forbidden, outcome.Status);
        Assert.Null(outcome.FreshPreview);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, ViewTrashOutcomeStatus.Unconfirmed)]
    [InlineData(HttpStatusCode.BadGateway, ViewTrashOutcomeStatus.Unconfirmed)]
    [InlineData(HttpStatusCode.BadRequest, ViewTrashOutcomeStatus.Unavailable)]
    public async Task AServerError_IsNeverClaimedAsNothingDeleted(HttpStatusCode status, ViewTrashOutcomeStatus expected)
    {
        using var client = ClientFor(new StaticHandler(status, "{}"));

        var outcome = await client.EmptyViewTrashAsync("x");

        Assert.False(outcome.IsOk);
        Assert.Equal(expected, outcome.Status);
    }

    [Fact]
    public async Task AFailureToConnect_MeansTheRequestNeverReachedTheEngine()
    {
        using var client = ClientFor(new ThrowingHandler(
            () => new HttpRequestException(HttpRequestError.ConnectionError, "no route")));

        var outcome = await client.EmptyViewTrashAsync("x");

        Assert.Equal(ViewTrashOutcomeStatus.Unavailable, outcome.Status);
    }

    [Fact]
    public async Task AConnectionThatDropsAfterSending_IsUnconfirmed()
    {
        using var client = ClientFor(new ThrowingHandler(
            () => new HttpRequestException(HttpRequestError.ResponseEnded, "connection reset")));

        var outcome = await client.EmptyViewTrashAsync("x");

        Assert.Equal(ViewTrashOutcomeStatus.Unconfirmed, outcome.Status);
    }

    [Fact]
    public async Task ATimeout_IsUnconfirmed_BecauseTheEngineMayStillBeDeleting()
    {
        using var client = ClientFor(new ThrowingHandler(() => new TaskCanceledException("timed out")));

        var outcome = await client.EmptyViewTrashAsync("x");

        Assert.Equal(ViewTrashOutcomeStatus.Unconfirmed, outcome.Status);
    }

    [Fact]
    public async Task AnAnswerThatCannotBeRead_AfterAYes_IsUnconfirmed()
    {
        using var client = ClientFor(new StaticHandler(HttpStatusCode.OK, "not json"));

        var outcome = await client.EmptyViewTrashAsync("x");

        Assert.Equal(ViewTrashOutcomeStatus.Unconfirmed, outcome.Status);
    }

    private static EngineApiClient ClientFor(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") }, NullLogger<EngineApiClient>.Instance);

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Method { get; private set; }

        public string? Path { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ThrowingHandler(Func<Exception> failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw failure();
    }
}
