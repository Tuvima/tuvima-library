using System.Net;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A plugin's web connection: it may only contact the places it declared in its manifest, redirects are checked one hop at a
/// time, and each place has a requests-a-minute limit that refuses (rather than holds for ever) a request that would wait
/// too long.
/// </summary>
public sealed class PluginWebConnectionTests : IDisposable
{
    private const string PluginId = HostServicesTestPlugin.DefaultId;

    private readonly List<IDisposable> _disposables = [];

    private static PluginNetworkDestination Destination(string host, int? perMinute = null) =>
        new() { Host = host, Sends = "Looks up titles by name", MaxRequestsPerMinute = perMinute };

    private (HostServicesHarness Host, BoundPluginHttpClient Client, ScriptedHandler Handler) Connect(
        ScriptedHandler handler,
        PluginHostLimits? limits = null,
        params PluginNetworkDestination[] destinations)
    {
        var plugin = new HostServicesTestPlugin(
            permissions: [PluginPermissionIds.NetworkHttp],
            network: destinations.Length == 0 ? [Destination("api.example.com")] : destinations);
        var host = new HostServicesHarness(limits, plugin);
        _disposables.Add(host);
        host.Enable();
        var effective = host.Limits;
        var client = new BoundPluginHttpClient(
            PluginId, host.Gate, new ScriptedClientFactory(handler), new PluginHttpRateLimiter(effective), effective, host.Statuses.Get(PluginId));
        return (host, client, handler);
    }

    private static HttpRequestMessage Get(string url) => new(HttpMethod.Get, url);

    [Fact]
    public async Task ARequestToADeclaredHost_GoesThrough()
    {
        var (_, client, handler) = Connect(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        using var response = await client.SendAsync(Get("https://api.example.com/titles?q=dune"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ARequestToAnUndeclaredHost_IsRefusedWithAPlainMessage_AndCountsAsTheLastProblem()
    {
        var (host, client, handler) = Connect(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SendAsync(Get("https://other.example.org/x")));

        Assert.Equal("Plugin 'test.host-services' did not say it would contact other.example.org, so the request was refused.", error.Message);
        Assert.Equal(0, handler.RequestCount);
        var description = host.Statuses.Get(PluginId).Describe();
        Assert.Equal("Tried to contact other.example.org, which it did not say it would contact", description.LastProblem);
        Assert.Equal("working", description.State);
    }

    [Fact]
    public async Task AWildcardHost_MatchesSubdomainsOnly()
    {
        var (_, client, handler) = Connect(
            new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), null, Destination("*.example.com"));

        (await client.SendAsync(Get("https://a.example.com/"))).Dispose();
        (await client.SendAsync(Get("https://deep.a.example.com/"))).Dispose();
        Assert.Equal(2, handler.RequestCount);

        foreach (var refused in new[] { "https://example.com/", "https://evilexample.com/", "https://example.com.evil.net/", "https://notexample.com/" })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SendAsync(Get(refused)));
        }

        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task AnExactHost_DoesNotCoverItsSubdomains()
    {
        var (_, client, handler) = Connect(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SendAsync(Get("https://www.api.example.com/")));

        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("*.example.com", "a.example.com", true)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("*.example.com", "A.EXAMPLE.COM", true)]
    [InlineData("api.example.com", "API.example.com", true)]
    [InlineData("api.example.com", "x.api.example.com", false)]
    [InlineData("*.example.com", "badexample.com", false)]
    [InlineData("*.example.com", ".example.com", false)]
    public void HostMatching_IsExactOrSubdomainsOfAWildcard(string pattern, string host, bool expected)
    {
        Assert.Equal(expected, PluginNetworkRules.HostMatches(pattern, host));
    }

    [Fact]
    public async Task TheLimiter_RefusesARequestThatWouldWaitTooLong_AndTheCardShowsNeedsAttentionWhileItKeepsHappening()
    {
        var limits = PluginHostLimits.Default with { HttpMaxRateLimitWait = TimeSpan.FromMilliseconds(300) };
        var (host, client, handler) = Connect(
            new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), limits, Destination("api.example.com", perMinute: 1));
        (await client.SendAsync(Get("https://api.example.com/1"))).Dispose();

        var error = await Assert.ThrowsAsync<PluginLimitExceededException>(() => client.SendAsync(Get("https://api.example.com/2")));

        Assert.Equal("Hit its limit of 1 requests a minute to api.example.com", error.Message);
        Assert.Equal(1, handler.RequestCount);
        var description = host.Statuses.Get(PluginId).Describe();
        Assert.Equal("needs_attention", description.State);
        Assert.Equal("Hit its limit of 1 requests a minute to api.example.com", description.Problem);

        host.Clock.Advance(limits.HttpProblemWindow + TimeSpan.FromSeconds(1));
        Assert.Equal("working", host.Statuses.Get(PluginId).Describe().State);
    }

    [Fact]
    public async Task ARequestOverTheLimit_WaitsBrieflyForItsTurnInsteadOfBeingRefused()
    {
        var limits = PluginHostLimits.Default with { HttpMaxRateLimitWait = TimeSpan.FromSeconds(10) };
        var (_, client, handler) = Connect(
            new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), limits, Destination("api.example.com", perMinute: 600));

        for (var i = 0; i < 8; i++)
        {
            (await client.SendAsync(Get($"https://api.example.com/{i}"))).Dispose();
        }

        // Burst is 5 and the rest wait about a tenth of a second each at 600 a minute; none is refused.
        Assert.Equal(8, handler.RequestCount);
    }

    [Fact]
    public void WithNoNumberDeclared_TheLibrarysDefaultOfSixtyARequestsMinuteApplies()
    {
        var limiter = new PluginHttpRateLimiter(PluginHostLimits.Default);

        Assert.Equal(60, limiter.RequestsPerMinute(Destination("api.example.com")));
        Assert.Equal(12, limiter.RequestsPerMinute(Destination("api.example.com", perMinute: 12)));
        Assert.Equal(TimeSpan.FromSeconds(30), PluginHostLimits.Default.HttpMaxRateLimitWait);
    }

    [Fact]
    public async Task EachDeclaredPlaceHasItsOwnLimit()
    {
        var limits = PluginHostLimits.Default with { HttpMaxRateLimitWait = TimeSpan.FromMilliseconds(200) };
        var (_, client, handler) = Connect(
            new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)),
            limits,
            Destination("slow.example.com", perMinute: 1),
            Destination("fast.example.com", perMinute: 600));
        (await client.SendAsync(Get("https://slow.example.com/1"))).Dispose();
        await Assert.ThrowsAsync<PluginLimitExceededException>(() => client.SendAsync(Get("https://slow.example.com/2")));

        (await client.SendAsync(Get("https://fast.example.com/1"))).Dispose();

        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ARedirectToAnUndeclaredHost_IsRefused_AndNothingIsSentThere()
    {
        var (host, client, handler) = Connect(new ScriptedHandler(request => Redirect(HttpStatusCode.Found, "https://evil.example.net/steal")));

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SendAsync(Get("https://api.example.com/start")));

        Assert.Contains("evil.example.net", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("Tried to contact evil.example.net, which it did not say it would contact", host.Statuses.Get(PluginId).Describe().LastProblem);
    }

    [Fact]
    public async Task ARedirectToADeclaredHost_IsFollowed_AndALoginIsNotCarriedToAnotherHost()
    {
        var (_, client, handler) = Connect(
            new ScriptedHandler(request => request.RequestUri!.AbsolutePath switch
            {
                "/start" => Redirect(HttpStatusCode.MovedPermanently, "/next"),
                "/next" => Redirect(HttpStatusCode.TemporaryRedirect, "https://cdn.example.com/file"),
                _ => new HttpResponseMessage(HttpStatusCode.OK),
            }),
            null,
            Destination("api.example.com"),
            Destination("cdn.example.com"));
        using var request = Get("https://api.example.com/start");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["https://api.example.com/start", "https://api.example.com/next", "https://cdn.example.com/file"], handler.Urls);
        Assert.Equal([true, true, false], handler.HadAuthorization);
        Assert.All(handler.AcceptHeaders, accept => Assert.Equal("application/json", accept));
    }

    [Fact]
    public async Task ARedirectFromHttpsToPlainHttp_IsRefused()
    {
        var (_, client, handler) = Connect(new ScriptedHandler(_ => Redirect(HttpStatusCode.Found, "http://api.example.com/plain")));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SendAsync(Get("https://api.example.com/start")));

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ARedirectLoop_IsStoppedAfterTheMostRedirectsAllowed()
    {
        var limits = PluginHostLimits.Default with { HttpMaxRedirects = 3 };
        var (_, client, handler) = Connect(new ScriptedHandler(_ => Redirect(HttpStatusCode.Found, "/again")), limits);

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SendAsync(Get("https://api.example.com/start")));

        Assert.Contains("more than 3 times", error.Message, StringComparison.Ordinal);
        Assert.Equal(4, handler.RequestCount);
    }

    [Fact]
    public async Task ARedirectAnswerToAPost_IsHandedBackAsIs_NotFollowed()
    {
        var (_, client, handler) = Connect(new ScriptedHandler(_ => Redirect(HttpStatusCode.Found, "https://api.example.com/next")));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/submit") { Content = new StringContent("{}") };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ASwitchedOffPlugin_CannotConnect()
    {
        var (host, client, handler) = Connect(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        host.Disable();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SendAsync(Get("https://api.example.com/")));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void ThePluginHttpClient_IsRegisteredThroughTheSharedHelper_WithoutAutomaticRedirects()
    {
        var source = File.ReadAllText(RepoPath("src/MediaEngine.Api/DependencyInjection/TuvimaProviderServiceCollectionExtensions.cs"));

        Assert.Contains("AddTuvimaHttpClient(\"plugin_http\"", source, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", source, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private static string RepoPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), relative);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private sealed class ScriptedClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("plugin_http", name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<string> _urls = [];
        private readonly List<bool> _hadAuthorization = [];
        private readonly List<string?> _accept = [];

        public int RequestCount => _urls.Count;
        public IReadOnlyList<string> Urls => _urls;
        public IReadOnlyList<bool> HadAuthorization => _hadAuthorization;
        public IReadOnlyList<string?> AcceptHeaders => _accept;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _urls.Add(request.RequestUri!.ToString());
            _hadAuthorization.Add(request.Headers.Contains("Authorization"));
            _accept.Add(request.Headers.TryGetValues("Accept", out var accept) ? string.Join(",", accept) : null);
            return Task.FromResult(respond(request));
        }
    }
}
