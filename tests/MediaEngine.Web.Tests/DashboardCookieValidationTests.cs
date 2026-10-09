using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class DashboardCookieValidationTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    public async Task OnlyProvenInvalidSessionsInvalidateTheCookie(HttpStatusCode status, bool invalid)
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(status))) { BaseAddress = new Uri("http://localhost") };
        var identity = new DashboardIdentityClient(new Factory(http));
        var result = await identity.ValidateCookieAsync("test-session", ClientIngressValues.HomeNetwork);
        Assert.Null(result.Response);
        Assert.Equal(invalid, result.Invalid);
    }

    [Fact]
    public async Task HomeSessionUsedFromOutside_IsRefusedWithoutErasingTheCookie()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = JsonContent.Create(new { reason = ClientIngressValues.SignInAgainHere }),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var identity = new DashboardIdentityClient(new Factory(http));

        var result = await identity.ValidateCookieAsync("test-session", ClientIngressValues.Remote);

        Assert.Null(result.Response);
        Assert.False(result.Invalid);
    }

    [Fact]
    public async Task ValidationTellsTheEngineWhereTheRequestCameFrom()
    {
        string? sent = null;
        var handler = new Handler(request =>
        {
            sent = request.Headers.TryGetValues(ClientIngressValues.ValidateHeader, out var values) ? values.Single() : null;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var identity = new DashboardIdentityClient(new Factory(http));

        await identity.ValidateCookieAsync("test-session", ClientIngressValues.HomeNetwork);

        Assert.Equal(ClientIngressValues.HomeNetwork, sent);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }
    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
