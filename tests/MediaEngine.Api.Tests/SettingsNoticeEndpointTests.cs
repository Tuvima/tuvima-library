using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Settings attention lines a server administrator dismissed with the X: each profile reads and changes only its
/// own, only known lines are accepted, and the profile is never taken from the request. Runs the real route handlers
/// (in memory, no network) against a temp data store.
/// </summary>
public sealed class SettingsNoticeEndpointTests : IAsyncLifetime
{
    private const string ProfileHeader = "X-Test-Profile";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-settings-notices-{Guid.NewGuid():N}.db");
    private DatabaseConnection _database = null!;
    private WebApplication _app = null!;

    public Task InitializeAsync()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var services = builder.Services;
        services.AddAuthorization();
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddSingleton<IDatabaseConnection>(_database);
        services.AddSingleton<IProfileNoticeDismissalRepository, ProfileNoticeDismissalRepository>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ProfileNoticeService>();
        services.AddScoped<IRequestAuthorityResolver, HeaderAuthorityResolver>();

        _app = builder.Build();
        _app.MapSettingsNoticeEndpoints();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _database.Dispose();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task DismissingTheNeedsReviewLine_IsRememberedWithItsFingerprintForTheActiveProfile()
    {
        var profile = SeedProfile();

        Assert.Empty(await DismissedAsync(profile));
        var status = await DismissAsync(profile, "needs-review", "3");

        Assert.Equal(StatusCodes.Status204NoContent, status);
        var dismissed = Assert.Single(await DismissedAsync(profile));
        Assert.Equal(("needs-review", "3"), dismissed);
    }

    [Fact]
    public async Task DismissingTheMissingToolsLine_StoresTheSortedToolIds()
    {
        var profile = SeedProfile();

        Assert.Equal(StatusCodes.Status204NoContent, await DismissAsync(profile, "missing-tools", "ffmpeg,image_engine"));

        Assert.Equal([("missing-tools", "ffmpeg,image_engine")], await DismissedAsync(profile));
    }

    [Fact]
    public async Task DismissingTheSameLineAgain_ReplacesTheEarlierFingerprint()
    {
        var profile = SeedProfile();

        await DismissAsync(profile, "needs-review", "3");
        await DismissAsync(profile, "needs-review", "8");

        Assert.Equal([("needs-review", "8")], await DismissedAsync(profile));
    }

    [Fact]
    public async Task DismissingAnUnknownLine_IsRefusedAndNothingIsSaved()
    {
        var profile = SeedProfile();

        var status = await DismissAsync(profile, "not-a-real-notice", "1");

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Empty(await DismissedAsync(profile));
        using var conn = _database.CreateConnection();
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM profile_notice_dismissals;"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DismissingWithoutAFingerprint_IsRefused(string fingerprint)
    {
        var profile = SeedProfile();

        Assert.Equal(StatusCodes.Status400BadRequest, await DismissAsync(profile, "needs-review", fingerprint));
        Assert.Empty(await DismissedAsync(profile));
    }

    [Fact]
    public async Task DismissingWithAnOversizedFingerprint_IsRefused()
    {
        var profile = SeedProfile();

        Assert.Equal(StatusCodes.Status400BadRequest,
            await DismissAsync(profile, "missing-tools", new string('x', ProfileNoticeService.MaxFingerprintLength + 1)));
        Assert.Empty(await DismissedAsync(profile));
    }

    [Fact]
    public async Task AnotherProfile_CannotReadOrChangeYourDismissals()
    {
        var mine = SeedProfile();
        var someoneElse = SeedProfile();

        Assert.Equal(StatusCodes.Status204NoContent, await DismissAsync(mine, "needs-review", "3"));

        Assert.Equal([("needs-review", "3")], await DismissedAsync(mine));
        Assert.Empty(await DismissedAsync(someoneElse));

        // Dismissing as the other profile changes only that profile's own list.
        Assert.Equal(StatusCodes.Status204NoContent, await DismissAsync(someoneElse, "needs-review", "9"));
        Assert.Equal([("needs-review", "9")], await DismissedAsync(someoneElse));
        Assert.Equal([("needs-review", "3")], await DismissedAsync(mine));
    }

    [Fact]
    public async Task WithoutAnActiveProfile_BothRoutesAnswerUnauthorized()
    {
        Assert.Equal(StatusCodes.Status401Unauthorized, (await SendAsync("GET", "/settings/notices/", null)).Status);
        Assert.Equal(StatusCodes.Status401Unauthorized,
            (await SendAsync("POST", "/settings/notices/{key}/dismiss", null, "needs-review", "{\"fingerprint\":\"3\"}")).Status);
    }

    [Fact]
    public void TheNoticeRoutes_AreAdministratorOnly_AndTakeNoProfileFromTheRequest()
    {
        var routes = Endpoints().ToArray();

        Assert.Equal(2, routes.Length);
        Assert.All(routes, endpoint =>
        {
            Assert.DoesNotContain(endpoint.RoutePattern.Parameters, parameter =>
                parameter.Name.Contains("profile", StringComparison.OrdinalIgnoreCase));
            var policy = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => !string.IsNullOrEmpty(data.Policy));
            Assert.Equal(AuthPolicies.Administrator, policy.Policy);
        });
    }

    private IEnumerable<RouteEndpoint> Endpoints() =>
        ((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/settings/notices", StringComparison.Ordinal) == true);

    private async Task<IReadOnlyList<(string Key, string Fingerprint)>> DismissedAsync(Guid profile)
    {
        var (status, body) = await SendAsync("GET", "/settings/notices/", profile);
        Assert.Equal(StatusCodes.Status200OK, status);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("dismissed").EnumerateArray()
            .Select(item => (item.GetProperty("key").GetString()!, item.GetProperty("fingerprint").GetString()!))
            .ToList();
    }

    private async Task<int> DismissAsync(Guid profile, string key, string fingerprint) =>
        (await SendAsync("POST", "/settings/notices/{key}/dismiss", profile, key,
            JsonSerializer.Serialize(new { fingerprint }))).Status;

    /// <summary>Runs the mapped route's handler as the given active profile and returns the status and body.</summary>
    private async Task<(int Status, string Body)> SendAsync(
        string method, string route, Guid? profile, string? key = null, string? json = null)
    {
        var endpoint = Endpoints().Single(candidate => candidate.RoutePattern.RawText == route
            && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));
        await using var scope = _app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = method;
        if (profile is { } active)
        {
            context.Request.Headers[ProfileHeader] = active.ToString("D");
        }

        if (key is not null)
        {
            context.Request.RouteValues["key"] = key;
        }

        if (json is not null)
        {
            context.Request.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());
        }

        using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context);
        body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(body).ReadToEndAsync());
    }

    private Guid SeedProfile()
    {
        var id = Guid.NewGuid();
        using var conn = _database.CreateConnection();
        conn.Execute(
            "INSERT INTO profiles (id, display_name, created_at) VALUES (@id, 'Test', @now);",
            new { id, now = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    private sealed class HeaderAuthorityResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(context.Request.Headers.TryGetValue(ProfileHeader, out var value)
                && Guid.TryParse(value.ToString(), out var profile)
                    ? new RequestAuthority(PrincipalKind.Human, true, AccountId: Guid.NewGuid(), ActiveProfileId: profile)
                    : new RequestAuthority(PrincipalKind.Anonymous, false));
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
