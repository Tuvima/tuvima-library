using System.Net;
using System.Text;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// What the Dashboard concludes when a delete or a preview does not get a normal answer: a failure to connect proves nothing was
/// deleted, but a timeout after the delete was sent does not.
/// </summary>
public sealed class EngineApiClientLibraryDeletionTests
{
    private static readonly Guid[] Ids = [Guid.Parse("11111111-aaaa-bbbb-cccc-000000000001")];

    private static EngineApiClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        new(new HttpClient(new Handler(respond)) { BaseAddress = new Uri("http://localhost:61495") }, NullLogger<EngineApiClient>.Instance);

    [Fact]
    public async Task ADeleteThatTimesOutAfterBeingSent_IsUnconfirmedNotUnavailable()
    {
        var client = Client(_ => throw new TaskCanceledException("The request timed out.", new TimeoutException()));

        var outcome = await client.DeleteLibraryItemsAsync(Ids, "fp-1");

        Assert.Equal(LibraryItemDeletionOutcomeStatus.Unconfirmed, outcome.Status);
    }

    [Fact]
    public async Task ADeleteThatIsCancelledAfterBeingSent_IsUnconfirmed()
    {
        using var cancelled = new CancellationTokenSource();
        var client = Client(_ =>
        {
            cancelled.Cancel();
            throw new OperationCanceledException(cancelled.Token);
        });

        var outcome = await client.DeleteLibraryItemsAsync(Ids, "fp-1", cancelled.Token);

        Assert.Equal(LibraryItemDeletionOutcomeStatus.Unconfirmed, outcome.Status);
    }

    [Fact]
    public async Task ADeleteThatCouldNotConnect_IsUnavailableBecauseNothingReachedTheEngine()
    {
        var client = Client(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused"));

        var outcome = await client.DeleteLibraryItemsAsync(Ids, "fp-1");

        Assert.Equal(LibraryItemDeletionOutcomeStatus.Unavailable, outcome.Status);
    }

    [Fact]
    public async Task ADeleteWhoseConnectionDroppedAfterTheRequestWentOut_IsUnconfirmed()
    {
        var client = Client(_ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely"));

        var outcome = await client.DeleteLibraryItemsAsync(Ids, "fp-1");

        Assert.Equal(LibraryItemDeletionOutcomeStatus.Unconfirmed, outcome.Status);
    }

    [Fact]
    public async Task ASuccessfulDeleteWhoseAnswerCannotBeRead_IsUnconfirmedBecauseTheDeleteRan()
    {
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not json</html>", Encoding.UTF8, "application/json"),
        }));

        var outcome = await client.DeleteLibraryItemsAsync(Ids, "fp-1");

        Assert.Equal(LibraryItemDeletionOutcomeStatus.Unconfirmed, outcome.Status);
    }

    [Fact]
    public async Task APreviewThatTimesOut_IsUnavailableBecauseNothingWasDeleted()
    {
        var client = Client(_ => throw new TaskCanceledException("The request timed out.", new TimeoutException()));

        var outcome = await client.GetLibraryItemDeletionPreviewAsync(Ids);

        Assert.Equal(LibraryItemDeletionOutcomeStatus.Unavailable, outcome.Status);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
