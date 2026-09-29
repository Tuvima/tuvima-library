using System.Net;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class ProviderConnectionClientTests
{
    [Fact]
    public async Task SettingsAuthorizationFailure_IsNotReportedAsProviderConnectivityFailure()
    {
        var handler = new ResponseHandler(HttpStatusCode.Forbidden);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        var key = "private-provider-key";

        var result = await client.SaveProviderCredentialsAsync("subdl", Request(key));

        Assert.Equal("engine_unauthorized", result?.Status);
        Assert.Contains("administrator session", result!.Message);
        Assert.DoesNotContain(key, result.Message);
        Assert.Equal("/settings/providers/subdl/credentials", handler.LastPath);
    }

    [Fact]
    public async Task SetupAuthorizationFailure_IdentifiesExpiredSetupSession()
    {
        var handler = new ResponseHandler(HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.SaveSetupProviderCredentialsAsync(
            "tvdb", Request("private-provider-key"), "setup-session");

        Assert.Equal("engine_unauthorized", result?.Status);
        Assert.Contains("setup session expired", result!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/setup/v1/providers/tvdb/credentials", handler.LastPath);
        Assert.Equal("setup-session", handler.LastSetupSession);
    }

    [Fact]
    public async Task ConfiguredTestAuthorizationFailure_DoesNotExposeProblemBodyAsProviderFailure()
    {
        var handler = new ResponseHandler(HttpStatusCode.Forbidden);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://engine.test") };
        using var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);

        var result = await client.TestProviderAsync("subdl");

        Assert.False(result!.Success);
        Assert.Equal("engine_unauthorized", result.Status);
        Assert.Contains("administrator session", result.Message);
        Assert.Equal("/settings/providers/subdl/test", handler.LastPath);
    }

    private static ProviderCredentialWriteRequest Request(string key) => new()
    {
        Credentials = new Dictionary<string, string> { ["api_key"] = key },
    };

    private sealed class ResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        public string? LastSetupSession { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastSetupSession = request.Headers.TryGetValues("X-Tuvima-Setup-Session", out var values)
                ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
