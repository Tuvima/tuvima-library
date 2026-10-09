using System.Net;
using System.Text;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientHistoryStatusTests
{
    [Fact]
    public async Task EmptyHistoryAndForbiddenHistoryHaveDifferentStates()
    {
        using var emptyHttp = new HttpClient(new StaticHandler(HttpStatusCode.OK, "[]"))
        { BaseAddress = new Uri("http://engine.test") };
        using var emptyClient = new EngineApiClient(emptyHttp, NullLogger<EngineApiClient>.Instance);
        var empty = await emptyClient.GetItemHistoryWithStatusAsync(Guid.NewGuid());
        Assert.Empty(empty.Items);
        Assert.Null(empty.Error);

        using var forbiddenHttp = new HttpClient(new StaticHandler(HttpStatusCode.Forbidden, "{}"))
        { BaseAddress = new Uri("http://engine.test") };
        using var forbiddenClient = new EngineApiClient(forbiddenHttp, NullLogger<EngineApiClient>.Instance);
        var forbidden = await forbiddenClient.GetItemHistoryWithStatusAsync(Guid.NewGuid());
        Assert.Empty(forbidden.Items);
        Assert.Contains("permission", forbidden.Error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
