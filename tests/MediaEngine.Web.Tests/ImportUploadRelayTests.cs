using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Dashboard's upload relay (<c>/import-uploads</c>) carries a resumable upload from the browser to the Engine (Library setup spec
/// D44). These run the real routes, a real Kestrel host, the production Engine client chain and a stand-in Engine.
/// </summary>
public sealed class ImportUploadRelayTests
{
    private const string SignedInHeader = "X-Test-Signed-In";
    private const string NoSessionClaim = "no-session";
    private const string SessionTokenFromClaim = "session-token-from-claim";
    private const string ServiceSecret = "dashboard-service-secret";
    private const string TokenHeader = "RequestVerificationToken";
    private static readonly Guid UploadId = Guid.Parse("50000000-0000-0000-0000-000000000005");
    private const int MiB = 1024 * 1024;

    [Fact]
    public async Task Start_IsPassedToTheEngineAsTheSignedInPerson_WithTheDashboardsServiceCredentialBesideTheSession()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address, realHandlers: true);
        const string body = """{"kind_key":"movies","file_name":"Film.mkv","total_bytes":1000}""";

        using var response = await dashboard.SendAsync(Request(HttpMethod.Post, "/import-uploads", new StringContent(body, Encoding.UTF8, "application/json"), withToken: await dashboard.TokenAsync()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = Assert.Single(engine.Requests);
        Assert.Equal("POST", seen.Method);
        Assert.Equal("/imports/uploads", seen.PathAndQuery);
        Assert.Equal(body, Encoding.UTF8.GetString(seen.Body));
        Assert.StartsWith("application/json", seen.ContentType, StringComparison.Ordinal);
        Assert.Equal(SessionTokenFromClaim, seen.Session);
        Assert.Equal(ServiceSecret, seen.ServiceKey);
        Assert.Null(seen.Cookie);
        Assert.Null(seen.Authorization);
        Assert.Null(seen.AntiforgeryToken);
    }

    [Fact]
    public async Task APiece_IsStreamedToTheEngineUnchanged_AtItsOwnIndex()
    {
        var piece = new byte[3 * MiB + 17];
        Random.Shared.NextBytes(piece);
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address, realHandlers: true);

        using var response = await dashboard.SendAsync(Request(HttpMethod.Put, $"/import-uploads/{UploadId:D}/pieces/7", new ByteArrayContent(piece), withToken: await dashboard.TokenAsync()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = Assert.Single(engine.Requests);
        Assert.Equal("PUT", seen.Method);
        Assert.Equal($"/imports/uploads/{UploadId:D}/pieces/7", seen.PathAndQuery);
        Assert.Equal("application/octet-stream", seen.ContentType);
        Assert.Equal(piece.Length, seen.Body.Length);
        Assert.Equal(SHA256.HashData(piece), SHA256.HashData(seen.Body));
        Assert.Equal(SessionTokenFromClaim, seen.Session);
    }

    [Fact]
    public async Task StatusCompleteAndCancel_AreRelayedToTheirEngineRoutes_AndTheEnginesAnswerComesBack()
    {
        await using var engine = await StartEngineAsync(answer: seen => (200, $$"""{"seen":"{{seen.Method}} {{seen.PathAndQuery}}"}"""));
        await using var dashboard = await StartDashboardAsync(engine.Address, realHandlers: true);
        var token = await dashboard.TokenAsync();

        using var status = await dashboard.SendAsync(Request(HttpMethod.Get, $"/import-uploads/{UploadId:D}", null, withToken: null));
        using var complete = await dashboard.SendAsync(Request(HttpMethod.Post, $"/import-uploads/{UploadId:D}/complete", null, withToken: token));
        using var cancel = await dashboard.SendAsync(Request(HttpMethod.Delete, $"/import-uploads/{UploadId:D}", null, withToken: token));

        Assert.Equal(
            [
                ("GET", $"/imports/uploads/{UploadId:D}"),
                ("POST", $"/imports/uploads/{UploadId:D}/complete"),
                ("DELETE", $"/imports/uploads/{UploadId:D}"),
            ],
            engine.Requests.Select(seen => (seen.Method, seen.PathAndQuery)));
        Assert.Contains("\"seen\":\"GET /imports/uploads/", await status.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.All([status, complete, cancel], response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
    }

    [Theory]
    [InlineData(409, """{"title":"Conflict.","status":409,"detail":"Set an Import folder first.","code":"no_import_folder"}""")]
    [InlineData(409, """{"title":"Conflict.","status":409,"detail":"Large uploads can fail.","code":"large_upload_needs_confirm","import_folder_path":"E:\\Import\\Movies"}""")]
    [InlineData(403, """{"title":"Access denied.","status":403,"detail":"Only an administrator can add files this way."}""")]
    [InlineData(404, """{"title":"Not found.","status":404,"code":"upload_not_found"}""")]
    [InlineData(507, """{"title":"Insufficient disk space.","status":507,"code":"not_enough_space"}""")]
    public async Task AnEngineRefusal_ReachesTheBrowserWithTheSameStatusAndBody(int status, string problem)
    {
        await using var engine = await StartEngineAsync(answer: _ => (status, problem), contentType: "application/problem+json");
        await using var dashboard = await StartDashboardAsync(engine.Address);

        using var response = await dashboard.SendAsync(Request(HttpMethod.Post, "/import-uploads", new StringContent("{}", Encoding.UTF8, "application/json"), withToken: await dashboard.TokenAsync()));

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(problem, await response.Content.ReadAsStringAsync());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ASignedOutBrowser_ReachesNothing_AndTheEngineIsNeverCalled()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);

        using var response = await dashboard.SendAsync(Request(HttpMethod.Get, $"/import-uploads/{UploadId:D}", null, withToken: null, signedIn: false));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(engine.Requests);
    }

    [Fact]
    public async Task ASignedInCookieWithoutAnEngineSession_IsTurnedAwayBeforeTheEngineIsCalled_SoTheServiceCredentialNeverActsAlone()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address, realHandlers: true);
        var token = await dashboard.TokenAsync();

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, "/import-uploads"),
                     (HttpMethod.Get, $"/import-uploads/{UploadId:D}"),
                     (HttpMethod.Put, $"/import-uploads/{UploadId:D}/pieces/0"),
                     (HttpMethod.Post, $"/import-uploads/{UploadId:D}/complete"),
                     (HttpMethod.Delete, $"/import-uploads/{UploadId:D}"),
                 })
        {
            using var response = await dashboard.SendAsync(Request(method, path, method == HttpMethod.Put ? new ByteArrayContent(new byte[8]) : null, withToken: token, signedIn: NoSessionClaim));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Empty(engine.Requests);
    }

    [Fact]
    public async Task EverythingThatChangesSomething_NeedsThePagesAntiforgeryToken_ButReadingStatusDoesNot()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);

        using var start = await dashboard.SendAsync(Request(HttpMethod.Post, "/import-uploads", new StringContent("{}", Encoding.UTF8, "application/json"), withToken: null));
        using var piece = await dashboard.SendAsync(Request(HttpMethod.Put, $"/import-uploads/{UploadId:D}/pieces/0", new ByteArrayContent(new byte[8]), withToken: null));
        using var complete = await dashboard.SendAsync(Request(HttpMethod.Post, $"/import-uploads/{UploadId:D}/complete", null, withToken: null));
        using var cancel = await dashboard.SendAsync(Request(HttpMethod.Delete, $"/import-uploads/{UploadId:D}", null, withToken: null));
        using var wrongToken = await dashboard.SendAsync(Request(HttpMethod.Delete, $"/import-uploads/{UploadId:D}", null, withToken: "not-a-token"));

        Assert.All([start, piece, complete, cancel, wrongToken], response => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode));
        using var refusal = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        Assert.Equal("antiforgery_failed", refusal.RootElement.GetProperty("code").GetString());
        Assert.Empty(engine.Requests);
        using var status = await dashboard.SendAsync(Request(HttpMethod.Get, $"/import-uploads/{UploadId:D}", null, withToken: null));
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
    }

    [Fact]
    public async Task OnlyTheSessionAndServiceCredentialTheDashboardAddsReachTheEngine_NeverWhatTheBrowserSent()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address, realHandlers: true);
        var request = Request(HttpMethod.Put, $"/import-uploads/{UploadId:D}/pieces/0", new ByteArrayContent(new byte[8]), withToken: await dashboard.TokenAsync());
        request.Headers.TryAddWithoutValidation("X-Tuvima-Session", "browser-session");
        request.Headers.TryAddWithoutValidation("X-Tuvima-Service-Key", "browser-service-key");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer browser-token");
        request.Headers.TryAddWithoutValidation("X-Api-Key", "browser-supplied");

        using var response = await dashboard.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = Assert.Single(engine.Requests);
        Assert.Equal(SessionTokenFromClaim, seen.Session);
        Assert.Equal(ServiceSecret, seen.ServiceKey);
        Assert.Null(seen.Authorization);
        Assert.Null(seen.Cookie);
    }

    [Fact]
    public async Task APieceWithoutAKnownLength_IsAskedToSendOne_AndNothingReachesTheEngine()
    {
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);
        var chunked = new StreamContent(new UnknownLengthStream(new byte[64]));

        using var response = await dashboard.SendAsync(Request(HttpMethod.Put, $"/import-uploads/{UploadId:D}/pieces/0", chunked, withToken: await dashboard.TokenAsync()));

        Assert.Equal(HttpStatusCode.LengthRequired, response.StatusCode);
        Assert.Empty(engine.Requests);
    }

    [Fact]
    public async Task OnlyThePieceRoute_TakesABodyBiggerThanTheNormalRequestLimit()
    {
        var piece = new byte[31 * MiB];
        await using var engine = await StartEngineAsync();
        await using var dashboard = await StartDashboardAsync(engine.Address);
        var token = await dashboard.TokenAsync();

        using var accepted = await dashboard.SendAsync(Request(HttpMethod.Put, $"/import-uploads/{UploadId:D}/pieces/0", new ByteArrayContent(piece), withToken: token));
        HttpStatusCode? other = null;
        var cutOff = await Record.ExceptionAsync(async () =>
        {
            var start = new ByteArrayContent(piece) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
            using var response = await dashboard.SendAsync(Request(HttpMethod.Post, "/import-uploads", start, withToken: token));
            other = response.StatusCode;
        });

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(piece.Length, Assert.Single(engine.Requests).Body.Length);
        Assert.True(other == HttpStatusCode.RequestEntityTooLarge || cutOff is HttpRequestException, $"The start route took a {piece.Length}-byte body (answer {other}).");
    }

    [Fact]
    public async Task AnUnreachableEngine_IsABadGatewayWithAReasonCode_NotACrash()
    {
        await using var dashboard = await StartDashboardAsync(new Uri("http://127.0.0.1:1"));

        using var response = await dashboard.SendAsync(Request(HttpMethod.Get, $"/import-uploads/{UploadId:D}", null, withToken: null));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("engine_unreachable", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void APiece_HasAboutFiveMinutes_AndTheUploadClientHasNoSharedHundredSecondLimit()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), ImportUploadRelayEndpoint.PieceTimeout);
        Assert.True(ImportUploadRelayEndpoint.ControlTimeout < ImportUploadRelayEndpoint.PieceTimeout);
        var program = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Program.cs"));
        var registration = program[program.IndexOf("AddHttpClient(ImportUploadRelayEndpoint.EngineClientName", StringComparison.Ordinal)..];
        registration = registration[..registration.IndexOf("ViewProfileAssertionHandler>();", StringComparison.Ordinal)];
        Assert.Contains("Timeout.InfiniteTimeSpan", registration, StringComparison.Ordinal);
        Assert.Contains("AddHttpMessageHandler<DashboardEngineAuthenticationHandler>", registration, StringComparison.Ordinal);
        Assert.Contains("app.MapImportUploadRelay();", program, StringComparison.Ordinal);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content, string? withToken, object? signedIn = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (signedIn is not (false or null) || signedIn is null)
        {
            request.Headers.TryAddWithoutValidation(SignedInHeader, signedIn as string ?? "1");
        }

        if (withToken is not null)
        {
            request.Headers.TryAddWithoutValidation(TokenHeader, withToken);
        }

        return request;
    }

    /// <param name="realHandlers">Wires the production Engine client chain (service credential from a protected bundle on disk, session from the signed-in cookie's claim).</param>
    private static async Task<Dashboard> StartDashboardAsync(Uri engine, bool realHandlers = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, SignedInHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery();
        string? tempDirectory = null;
        if (realHandlers)
        {
            tempDirectory = Path.Combine(Path.GetTempPath(), $"tuvima-import-relay-{Guid.NewGuid():N}");
            var configDirectory = Path.Combine(tempDirectory, "config");
            var protection = DataProtectionProvider.Create(Directory.CreateDirectory(Path.Combine(tempDirectory, "keys")));
            Directory.CreateDirectory(Path.Combine(configDirectory, ".secrets"));
            var bundle = new DashboardServiceCredentialBundle(
                Guid.NewGuid().ToString("N"),
                protection.CreateProtector("Tuvima.DashboardEngineCredential.v1").Protect(ServiceSecret),
                DateTimeOffset.UtcNow);
            File.WriteAllText(
                Path.Combine(configDirectory, ".secrets", "dashboard-engine.credential.json"),
                JsonSerializer.Serialize(bundle));

            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton(protection);
            builder.Services.AddSingleton(new DashboardServiceCredentialProviderOptions(configDirectory));
            builder.Services.AddSingleton<DashboardServiceCredentialProvider>();
            builder.Services.AddScoped<DashboardSessionAccessor>();
            builder.Services.AddScoped<ActiveProfileAccessor>();
            builder.Services.AddScoped<IActiveProfileAccessor>(services => services.GetRequiredService<ActiveProfileAccessor>());
            builder.Services.AddTransient<DashboardEngineAuthenticationHandler>();
            builder.Services.AddTransient<ViewProfileAssertionHandler>(services => new ViewProfileAssertionHandler(
                services.GetRequiredService<IActiveProfileAccessor>()));
            builder.Services.AddHttpClient(ImportUploadRelayEndpoint.EngineClientName, client =>
                {
                    client.BaseAddress = engine;
                    client.Timeout = Timeout.InfiniteTimeSpan;
                })
                .AddHttpMessageHandler<DashboardEngineAuthenticationHandler>()
                .AddHttpMessageHandler<ViewProfileAssertionHandler>();
        }
        else
        {
            builder.Services.AddHttpClient(ImportUploadRelayEndpoint.EngineClientName, client =>
            {
                client.BaseAddress = engine;
                client.Timeout = Timeout.InfiniteTimeSpan;
            });
        }

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/test/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty));
        app.MapImportUploadRelay();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new Dashboard(app, new Uri(addresses!.Addresses.Single()), tempDirectory);
    }

    /// <param name="answer">What the stand-in Engine answers for a request (status and body); the default is 200 with a small JSON body.</param>
    private static async Task<Engine> StartEngineAsync(Func<SeenRequest, (int Status, string Body)>? answer = null, string contentType = "application/json")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * MiB);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var seen = new List<SeenRequest>();
        app.MapMethods("/imports/uploads/{**rest}", [HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Delete], Handle);
        app.MapPost("/imports/uploads", Handle);
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new Engine(app, new Uri(addresses!.Addresses.Single()), seen);

        async Task Handle(HttpContext context)
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            var request = new SeenRequest(
                context.Request.Method,
                context.Request.Path + context.Request.QueryString,
                context.Request.ContentType,
                body.ToArray(),
                context.Request.Headers.Cookie.ToString().NullIfEmpty(),
                context.Request.Headers.Authorization.ToString().NullIfEmpty(),
                context.Request.Headers["X-Tuvima-Service-Key"].ToString().NullIfEmpty(),
                context.Request.Headers["X-Tuvima-Session"].ToString().NullIfEmpty(),
                context.Request.Headers[TokenHeader].ToString().NullIfEmpty());
            lock (seen)
            {
                seen.Add(request);
            }

            var (status, text) = answer?.Invoke(request) ?? (200, """{"ok":true}""");
            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            await context.Response.WriteAsync(text);
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record SeenRequest(
        string Method,
        string PathAndQuery,
        string? ContentType,
        byte[] Body,
        string? Cookie,
        string? Authorization,
        string? ServiceKey,
        string? Session,
        string? AntiforgeryToken);

    /// <summary>A body whose length the sender cannot know up front, so it goes out chunked.</summary>
    private sealed class UnknownLengthStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SignedInHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(SignedInHeader, out var mode))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            // "no-session" is a signed-in person whose cookie carries no Engine session token.
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "administrator") };
            if (mode != NoSessionClaim)
            {
                claims.Add(new Claim(DashboardEngineAuthenticationHandler.SessionTokenClaim, SessionTokenFromClaim));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class Engine(WebApplication app, Uri address, List<SeenRequest> seen) : IAsyncDisposable
    {
        public Uri Address { get; } = address;

        public IReadOnlyList<SeenRequest> Requests
        {
            get
            {
                lock (seen)
                {
                    return seen.ToArray();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class Dashboard(WebApplication app, Uri address, string? tempDirectory) : IAsyncDisposable
    {
        // One client, so the antiforgery cookie the page set is sent back with the token, as a browser does.
        private readonly HttpClient _client = new(new SocketsHttpHandler { UseCookies = true }) { BaseAddress = address, Timeout = TimeSpan.FromMinutes(2) };

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _client.SendAsync(request);

        /// <summary>The antiforgery token a signed-in page would hold; fetching it also sets the antiforgery cookie.</summary>
        public async Task<string> TokenAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/test/antiforgery");
            request.Headers.TryAddWithoutValidation(SignedInHeader, "1");
            using var response = await _client.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            if (tempDirectory is not null && Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
