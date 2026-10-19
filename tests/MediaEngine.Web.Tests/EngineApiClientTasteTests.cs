using System.Net;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class EngineApiClientTasteTests
{
    [Theory]
    [InlineData("drama", "/api/v1/taste/items/genres/drama")]
    [InlineData("rhythm and blues/soul", "/api/v1/taste/items/genres/rhythm%20and%20blues/soul")]
    [InlineData("sci-fi/fantasy/space opera", "/api/v1/taste/items/genres/sci-fi/fantasy/space%20opera")]
    public async Task RemovingATasteValue_SendsEachSlashSeparatedPartEscapedOnItsOwn_NeverAnEncodedSlash(string value, string expectedPath)
    {
        string? sent = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            sent = request.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }))
        {
            BaseAddress = new Uri("http://localhost:61495/"),
        };
        var client = new EngineApiClient(httpClient, NullLogger<EngineApiClient>.Instance);

        Assert.True(await client.RemovePersonalTasteItemAsync("genres", value));

        Assert.Equal(expectedPath, sent);
        Assert.DoesNotContain("%2F", sent, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
