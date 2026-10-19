using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The signed-in account's "Not now" choices: each account reads and changes only its own, and only known reminders
/// are accepted. Runs the real self-service route handlers (in memory, no network) against a temp data store.
/// </summary>
public sealed class AccountNoticeEndpointTests : IAsyncLifetime
{
    private const string AccountHeader = "X-Test-Account";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-account-notices-{Guid.NewGuid():N}.db");
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
        services.AddAuthorization(options => options.AddPolicy(
            AuthPolicies.HumanSelfService, policy => policy.RequireAssertion(_ => true)));
        services.AddSingleton<IDatabaseConnection>(_database);
        services.AddSingleton<IAccountNoticeDismissalRepository, AccountNoticeDismissalRepository>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AccountNoticeService>();
        services.AddScoped<IRequestAuthorityResolver, HeaderAuthorityResolver>();
        services.AddScoped<ISelfServiceAuthorizationService, SelfAccountOnlyDecisions>();
        // The other self-service routes are mapped too; they are never called here.
        services.AddScoped<IAccountRepository>(_ => null!);
        services.AddScoped<IIdentityRepository>(_ => null!);
        services.AddScoped<IProfileRepository>(_ => null!);
        services.AddScoped<IAccountExternalLoginService>(_ => null!);
        services.AddScoped<IAccountSignInMethodRepository>(_ => null!);
        services.AddScoped<IAuthorizationAuditWriter>(_ => null!);
        services.AddScoped<IGrantAdminUnlockService>(_ => null!);
        services.AddScoped<IAccountAccessMutationService>(_ => null!);
        services.AddScoped<IConfigurationLoader>(_ => null!);
        services.AddSingleton<AuthenticationPolicyMutationGate>();
        services.AddSingleton<AuthenticationProviderConfigurationService>();
        services.AddSingleton<UserManager<MediaEngine.Domain.Entities.Account>>(_ => null!);

        _app = builder.Build();
        _app.MapAccountEndpoints();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _database.Dispose();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task DismissingTheSecureAccountReminder_IsRememberedForTheSignedInAccount()
    {
        var account = SeedAccount();

        Assert.Empty(await DismissedAsync(account));
        var status = await DismissAsync(account, "secure-account");

        Assert.Equal(StatusCodes.Status204NoContent, status);
        Assert.Equal(["secure-account"], await DismissedAsync(account));
    }

    [Fact]
    public async Task DismissingAnUnknownReminder_IsRefusedAndNothingIsSaved()
    {
        var account = SeedAccount();

        var status = await DismissAsync(account, "not-a-real-reminder");

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Empty(await DismissedAsync(account));
        using var conn = _database.CreateConnection();
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM account_notice_dismissals;"));
    }

    [Fact]
    public async Task AnotherAccount_CannotReadOrChangeYourDismissals()
    {
        var mine = SeedAccount();
        var someoneElse = SeedAccount();

        Assert.Equal(StatusCodes.Status204NoContent, await DismissAsync(mine, "secure-account"));

        Assert.Equal(["secure-account"], await DismissedAsync(mine));
        Assert.Empty(await DismissedAsync(someoneElse));

        // Dismissing as the other account changes only that account's own list.
        Assert.Equal(StatusCodes.Status204NoContent, await DismissAsync(someoneElse, "secure-account"));
        Assert.Equal(["secure-account"], await DismissedAsync(someoneElse));
        using var conn = _database.CreateConnection();
        Assert.Equal(2, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM account_notice_dismissals;"));
    }

    [Fact]
    public void TheNoticeRoutes_TakeNoAccountFromTheRequest()
    {
        var routes = Endpoints()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/access/self-service/notices", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(2, routes.Length);
        Assert.All(routes, endpoint =>
        {
            Assert.DoesNotContain(endpoint.RoutePattern.Parameters, parameter =>
                parameter.Name.Contains("account", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(endpoint.Metadata.OfType<AuthorityRequirementMetadata>(),
                metadata => metadata.Requirement == "human_self_service");
        });
    }

    private IEnumerable<RouteEndpoint> Endpoints() =>
        ((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>();

    private async Task<IReadOnlyList<string>> DismissedAsync(Guid account)
    {
        var (status, body) = await SendAsync("GET", "/access/self-service/notices", account);
        Assert.Equal(StatusCodes.Status200OK, status);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("dismissed").EnumerateArray().Select(item => item.GetString()!).ToList();
    }

    private async Task<int> DismissAsync(Guid account, string key) =>
        (await SendAsync("POST", "/access/self-service/notices/{key}/dismiss", account, key)).Status;

    /// <summary>Runs the mapped route's handler as the given signed-in account and returns the status and body.</summary>
    private async Task<(int Status, string Body)> SendAsync(string method, string route, Guid account, string? key = null)
    {
        var endpoint = Endpoints().Single(candidate => candidate.RoutePattern.RawText == route
            && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));
        await using var scope = _app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = method;
        context.Request.Headers[AccountHeader] = account.ToString("D");
        if (key is not null)
        {
            context.Request.RouteValues["key"] = key;
        }

        using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context);
        body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(body).ReadToEndAsync());
    }

    private Guid SeedAccount()
    {
        var id = Guid.NewGuid();
        using var conn = _database.CreateConnection();
        conn.Execute(
            "INSERT INTO accounts (id, created_at, updated_at) VALUES (@id, @now, @now);",
            new { id, now = DateTimeOffset.UtcNow.ToString("O") });
        return id;
    }

    private sealed class HeaderAuthorityResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(context.Request.Headers.TryGetValue(AccountHeader, out var value)
                && Guid.TryParse(value.ToString(), out var account)
                    ? new RequestAuthority(PrincipalKind.Human, true, AccountId: account, ActiveProfileId: Guid.NewGuid())
                    : new RequestAuthority(PrincipalKind.Anonymous, false));
    }

    private sealed class SelfAccountOnlyDecisions : ISelfServiceAuthorizationService
    {
        public ValueTask<AuthorizationDecision> EvaluateAccountAsync(
            RequestAuthority authority, Guid accountId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(authority.AccountId == accountId ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.Unauthenticated));

        public ValueTask<AuthorizationDecision> EvaluateProfileAsync(
            RequestAuthority authority, Guid profileId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
