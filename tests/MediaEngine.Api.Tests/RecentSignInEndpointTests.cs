using System.Security.Claims;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Sensitive account actions ask the person to confirm it's them once the sign-in is more than ten minutes old, and
/// "secure this account" moves the server from This computer to Home network only for the first administrator.
/// </summary>
public sealed class RecentSignInEndpointTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-recent-sign-in-{Guid.NewGuid():N}.db");
    private readonly string _configPath = Path.Combine(Path.GetTempPath(), $"tuvima-recent-sign-in-cfg-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly AccountRepository _accounts;
    private readonly FirstPartyIdentityService _identity;
    private readonly ClockStub _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    public RecentSignInEndpointTests()
    {
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _configuration = new ConfigurationDirectoryLoader(_configPath);
        _accounts = new AccountRepository(_database);
        _identity = new FirstPartyIdentityService(
            new IdentityRepository(_database), _accounts, new ProfileRepository(_database),
            new PasswordHasher<AccountCredential>(), new PasswordHasher<ProfileCredential>(),
            _clock, new FixedPolicy());
    }

    [Fact]
    public async Task ChangePassword_ElevenMinutesAfterSignIn_AsksToConfirmFirst_AndSucceedsAfterConfirming()
    {
        var issued = await _identity.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "device", "Browser", "Dashboard");
        await using var app = BuildApplication();
        var user = HumanUser(issued);
        var body = new { new_password = "a brand new password" };

        _clock.Advance(TimeSpan.FromMinutes(11));
        var stale = await SendAsync(app, "POST", "/auth/password/change", user, body);

        Assert.Equal(StatusCodes.Status403Forbidden, stale.Status);
        Assert.Equal("confirm_its_you", stale.Json.GetProperty("code").GetString());
        Assert.True((await _identity.AuthenticatePasswordAsync("owner@example.com", Password, "d2", "Other", "Dashboard")).Succeeded);

        Assert.True(await _identity.ConfirmWithPasswordAsync(issued.Account.Id, issued.Session.Id, Password));
        var confirmed = await SendAsync(app, "POST", "/auth/password/change", user, body);

        Assert.Equal(StatusCodes.Status204NoContent, confirmed.Status);
        Assert.True((await _identity.AuthenticatePasswordAsync("owner@example.com", "a brand new password", "d3", "Other", "Dashboard")).Succeeded);
    }

    [Fact]
    public async Task ChangePassword_RightAfterSignIn_NeedsNoConfirmation()
    {
        var issued = await _identity.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "device", "Browser", "Dashboard");
        await using var app = BuildApplication();

        _clock.Advance(TimeSpan.FromMinutes(9));
        var result = await SendAsync(app, "POST", "/auth/password/change", HumanUser(issued), new { new_password = "a brand new password" });

        Assert.Equal(StatusCodes.Status204NoContent, result.Status);
    }

    [Theory]
    [InlineData("POST", "/auth/password/recovery-codes")]
    [InlineData("DELETE", "/auth/sessions/others")]
    [InlineData("GET", "/auth/confirm/recent")]
    public async Task OtherSensitiveActions_AskToConfirmWhenTheSignInIsStale_AndRunWhenItIsRecent(string method, string route)
    {
        var issued = await _identity.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "device", "Browser", "Dashboard");
        await using var app = BuildApplication();
        var user = HumanUser(issued);

        _clock.Advance(TimeSpan.FromMinutes(11));
        var stale = await SendAsync(app, method, route, user);
        Assert.Equal(StatusCodes.Status403Forbidden, stale.Status);
        Assert.Equal("confirm_its_you", stale.Json.GetProperty("code").GetString());

        Assert.True(await _identity.ConfirmWithPasswordAsync(issued.Account.Id, issued.Session.Id, Password));
        var recent = await SendAsync(app, method, route, user);
        Assert.InRange(recent.Status, 200, 299);
    }

    [Fact]
    public async Task ACallerWithNoSession_CannotConfirm_SoItIsRefused()
    {
        await _identity.BootstrapAdministratorAsync("owner@example.com", Password, "Owner", "device", "Browser", "Dashboard");
        await using var app = BuildApplication();
        var noSession = new ClaimsPrincipal(new ClaimsIdentity([new Claim(TuvimaClaimTypes.AccountId, Guid.NewGuid().ToString("D"))], "test"));

        var result = await SendAsync(app, "DELETE", "/auth/sessions/others", noSession);

        Assert.Equal(StatusCodes.Status403Forbidden, result.Status);
        Assert.Equal("confirm_its_you", result.Json.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SecuringTheFirstAdministrator_MovesTheServerFromThisComputerToHomeNetwork()
    {
        var issued = await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "device", "Browser", "Dashboard");
        SetWhoCanConnect(WhoCanConnectModes.ThisComputer);

        var moved = await AuthenticationEndpoints.MoveToHomeNetworkIfFirstAdministratorAsync(
            _accounts, _configuration, NullLogger.Instance, issued.Account.Id, CancellationToken.None);

        Assert.True(moved);
        Assert.Equal(WhoCanConnectModes.HomeNetwork, _configuration.LoadNetwork().WhoCanConnect);
    }

    [Fact]
    public async Task SecuringAnotherAdministrator_LeavesWhoCanConnectAlone()
    {
        await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "device", "Browser", "Dashboard");
        var second = new Account
        {
            Id = Guid.NewGuid(),
            Email = "second@example.com",
            NormalizedEmail = "SECOND@EXAMPLE.COM",
            IsEnabled = true,
            IsAdministrator = true,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(1),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(1),
        };
        await _accounts.InsertAsync(second);
        SetWhoCanConnect(WhoCanConnectModes.ThisComputer);

        var moved = await AuthenticationEndpoints.MoveToHomeNetworkIfFirstAdministratorAsync(
            _accounts, _configuration, NullLogger.Instance, second.Id, CancellationToken.None);

        Assert.False(moved);
        Assert.Equal(WhoCanConnectModes.ThisComputer, _configuration.LoadNetwork().WhoCanConnect);
    }

    [Theory]
    [InlineData(WhoCanConnectModes.HomeNetwork)]
    [InlineData(WhoCanConnectModes.Anywhere)]
    public async Task SecuringTheFirstAdministrator_NeverNarrowsOrChangesAWiderSetting(string current)
    {
        var issued = await _identity.BootstrapThisComputerAdministratorAsync("owner@example.com", "Owner", "device", "Browser", "Dashboard");
        SetWhoCanConnect(current);

        var moved = await AuthenticationEndpoints.MoveToHomeNetworkIfFirstAdministratorAsync(
            _accounts, _configuration, NullLogger.Instance, issued.Account.Id, CancellationToken.None);

        Assert.False(moved);
        Assert.Equal(current, _configuration.LoadNetwork().WhoCanConnect);
    }

    private void SetWhoCanConnect(string mode)
    {
        var network = _configuration.LoadNetwork();
        network.WhoCanConnect = mode;
        _configuration.SaveNetwork(network);
    }

    private static ClaimsPrincipal HumanUser(SessionIssueResult issued) => new(new ClaimsIdentity(
    [
        new Claim(TuvimaClaimTypes.AccountId, issued.Account.Id.ToString("D")),
        new Claim(TuvimaClaimTypes.SessionId, issued.Session.Id.ToString("D")),
    ], "test"));

    private WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddSingleton<IFirstPartyIdentityService>(_identity);
        builder.Services.AddScoped<RecentSignInGuard>();
        // The other sign-in routes are mapped too; their services only need to be known so they bind as services.
        builder.Services.AddSingleton<DashboardAuthorityProjector>(_ => null!);
        builder.Services.AddSingleton<IConfigurationLoader>(_ => null!);
        builder.Services.AddSingleton<AuthenticationProviderConfigurationService>(_ => null!);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(_ => null!);
        builder.Services.AddSingleton<ExternalIdentityTransactionService>(_ => null!);
        builder.Services.AddSingleton<IAccountExternalLoginService>(_ => null!);
        builder.Services.AddSingleton<ISelfServiceAuthorizationService>(_ => null!);
        builder.Services.AddSingleton<IAccountRepository>(_ => null!);
        builder.Services.AddSingleton<IPasskeyHandler<Account>>(_ => null!);
        builder.Services.AddSingleton<UserManager<Account>>(_ => null!);
        builder.Services.AddSingleton<IAccountSignInMethodRepository>(_ => null!);
        builder.Services.AddSingleton<AuthenticationPolicyMutationGate>(_ => null!);
        builder.Services.AddSingleton<IIdentityRepository>(_ => null!);
        builder.Services.AddSingleton<IntercomTokenService>(_ => null!);
        builder.Services.AddSingleton<TimeProvider>(_clock);
        var app = builder.Build();
        app.MapAuthenticationEndpoints();
        return app;
    }

    private static async Task<(int Status, JsonElement Json, string Text)> SendAsync(
        WebApplication app, string method, string pattern, ClaimsPrincipal user, object? body = null)
    {
        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), candidate =>
                candidate.RoutePattern.RawText == pattern &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
        var requestBody = new MemoryStream(body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType()));
        var responseBody = new MemoryStream();
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = user,
            Request =
            {
                Method = method,
                Path = pattern,
                ContentType = body is null ? null : "application/json",
                ContentLength = body is null ? null : requestBody.Length,
                Body = requestBody,
            },
            Response = { Body = responseBody },
        };
        if (body is not null)
        {
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());
        }

        await endpoint.RequestDelegate!(context);
        responseBody.Position = 0;
        var text = await new StreamReader(responseBody).ReadToEndAsync();
        var json = text.Length > 0 && text.TrimStart().StartsWith('{')
            ? JsonDocument.Parse(text).RootElement.Clone()
            : default;
        return (context.Response.StatusCode, json, text);
    }

    public void Dispose()
    {
        _configuration.Dispose();
        _database.Dispose();
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }

        try
        {
            if (Directory.Exists(_configPath))
            {
                Directory.Delete(_configPath, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class ClockStub(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FixedPolicy : IAuthenticationPolicyProvider
    {
        public AuthSettings GetCurrent() => new();
    }
}
