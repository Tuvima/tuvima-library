using System.Security.Claims;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Settings "Data store version" line reads the latest recorded upgrade step through
/// <c>GET /system/data-store-version</c>, which only administrators (or an application granted backup read) may call.
/// </summary>
public sealed class DataStoreVersionEndpointTests : IDisposable
{
    private const string Route = "/system/data-store-version";
    private const string PrincipalClaim = "test-principal";

    private static readonly Guid Account = Guid.NewGuid();
    private static readonly Guid Profile = Guid.NewGuid();
    private static readonly Guid Household = Guid.NewGuid();
    private static readonly Guid NativeApp = BuiltInApplicationIds.NativeClient;
    private static readonly Guid BackupApp = Guid.NewGuid();
    private static readonly Guid PlainApp = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<string, RequestAuthority> Principals = new Dictionary<string, RequestAuthority>
    {
        ["server-admin"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, GrantEnabled: true, AccountIsAdministrator: true, GrantAdminEnabled: true),
        ["household-member"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, GrantEnabled: true, AccountHouseholdId: Household),
        ["household-admin"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, GrantEnabled: true, AccountHouseholdId: Household, AccountIsHouseholdAdmin: true),
        // A paired native app (a device token for a signed-in administrator) that was not granted backup.read.
        ["native-app-without-backup-read"] = new(PrincipalKind.DelegatedUserClient, true, Account, Profile, NativeApp, null, Guid.NewGuid(),
            AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true, AccountIsAdministrator: true, GrantAdminEnabled: true),
        // A paired native app that does hold backup.read still acts for a person who is not an administrator.
        ["native-app-with-backup-read"] = new(PrincipalKind.DelegatedUserClient, true, Account, Profile, BackupApp, null, Guid.NewGuid(),
            AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true),
        ["service-without-backup-read"] = new(PrincipalKind.ServiceApplication, true, null, null, PlainApp,
            ApplicationEnabled: true),
        ["service-with-backup-read"] = new(PrincipalKind.ServiceApplication, true, null, null, BackupApp,
            ApplicationEnabled: true),
    };

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-store-version-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public DataStoreVersionEndpointTests()
    {
        _database = new DatabaseConnection(_databasePath);
    }

    [Fact]
    public async Task FreshDataStore_ReportsTheLatestUpgradeStepTheDataStoreHasRecorded()
    {
        _database.InitializeSchema();
        await using var app = BuildApplication();
        var (latestNumber, latestName) = LatestRecordedStep();

        var (status, body) = await GetAsync(app);

        Assert.Equal(StatusCodes.Status200OK, status);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.True(latestNumber >= 1);
        Assert.Equal(latestNumber, root.GetProperty("step_number").GetInt32());
        Assert.Equal(latestName, root.GetProperty("step_name").GetString());
        Assert.True(root.GetProperty("applied_at").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    // What a fresh start recorded, read straight from the table, so the test keeps passing when more steps are added.
    private (int Number, string Name) LatestRecordedStep()
    {
        using var connection = _database.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT number, name FROM data_store_upgrades ORDER BY number DESC LIMIT 1;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return (reader.GetInt32(0), reader.GetString(1));
    }

    [Fact]
    public async Task DataStoreWithNoRecordedStep_ReportsStepZeroAndNoDateInsteadOfNotFound()
    {
        using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE data_store_upgrades (
                    number INTEGER NOT NULL PRIMARY KEY, name TEXT NOT NULL, fingerprint TEXT NOT NULL,
                    applied_at TEXT NOT NULL, engine_version TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();
        }

        await using var app = BuildApplication();

        var (status, body) = await GetAsync(app);

        Assert.Equal(StatusCodes.Status200OK, status);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(0, root.GetProperty("step_number").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("applied_at").ValueKind);
    }

    [Fact]
    public async Task Route_IsRefusedToNonAdministrators_ExactlyLikeTheBackupsList()
    {
        _database.InitializeSchema();
        await using var app = BuildApplication();

        var version = RequirementOf(app, Route);
        var backups = RequirementOf(app, "/system/backups");

        // Only the server administrator (or an application granted backup read) passes; a household administrator
        // does not, because the route does not opt in to household administrators.
        Assert.Equal(ApplicationPermissionIds.BackupRead, version.Permission);
        Assert.False(version.AllowHouseholdAdministrator);
        Assert.Equal(backups.Permission, version.Permission);
        Assert.Equal(backups.AllowHouseholdAdministrator, version.AllowHouseholdAdministrator);
    }

    [Theory]
    [InlineData("household-member")]
    [InlineData("household-admin")]
    [InlineData("native-app-without-backup-read")]
    [InlineData("native-app-with-backup-read")]
    [InlineData("service-without-backup-read")]
    public async Task Route_IsRefusedToEveryoneButAServerAdministratorOrABackupApplication(string principal)
    {
        _database.InitializeSchema();
        await using var app = BuildApplication();

        Assert.False(await PassesAsync(app, Route, principal), $"{principal} must not read the data store version.");
        // The backups list refuses the same principals, so the new route is exactly as private.
        Assert.False(await PassesAsync(app, "/system/backups", principal), $"{principal} must not list backups.");
    }

    [Theory]
    [InlineData("server-admin")]
    [InlineData("service-with-backup-read")]
    public async Task Route_ServesAServerAdministratorAndAnApplicationGrantedBackupRead(string principal)
    {
        _database.InitializeSchema();
        await using var app = BuildApplication();

        Assert.True(await PassesAsync(app, Route, principal), $"{principal} must read the data store version.");
        Assert.True(await PassesAsync(app, "/system/backups", principal), $"{principal} must list backups.");
    }

    private WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new PrincipalClaimResolver());
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new AdministratorDecisions());
        // Only the applications that hold backup.read are allowed that permission.
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator>(new BackupReadEvaluator(BackupApp));
        builder.Services.AddScoped<IAuthorizationHandler, AdministratorOrApplicationHandler>();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<DataStoreVersionRepository>();
        // The other system routes are mapped too; their services only need to be known so they bind as services.
        builder.Services.AddSingleton<IConfigurationLoader>(_ => null!);
        builder.Services.AddSingleton<StartupReadinessService>(_ => null!);
        builder.Services.AddSingleton<IScheduledTaskRegistry>(_ => null!);
        builder.Services.AddSingleton<IScheduledTaskRunRepository>(_ => null!);
        builder.Services.AddSingleton<IMediaOperationRepository>(_ => null!);
        builder.Services.AddSingleton<IFileWatcher>(_ => null!);
        builder.Services.AddSingleton<AssetStoreCleanupService>(_ => null!);
        builder.Services.AddSingleton<DatabaseBackupService>(_ => null!);
        var app = builder.Build();
        app.MapSystemEndpoints();
        return app;
    }

    private static RouteEndpoint EndpointOf(WebApplication app, string route) =>
        Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), candidate =>
                candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);

    private static AdministratorOrApplicationRequirement RequirementOf(WebApplication app, string route) =>
        Assert.Single(EndpointOf(app, route).Metadata.OfType<AuthorizationPolicy>()
            .SelectMany(policy => policy.Requirements)
            .OfType<AdministratorOrApplicationRequirement>());

    // What the authorization middleware does for an endpoint: combine its policies, then evaluate them for the principal.
    private static async Task<bool> PassesAsync(WebApplication app, string route, string principal)
    {
        var endpoint = EndpointOf(app, route);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(PrincipalClaim, principal)], "test"));
        var context = new DefaultHttpContext { RequestServices = app.Services, User = user };
        var provider = app.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(
            provider,
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
        Assert.NotNull(policy);
        var result = await app.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, context, policy);
        return result.Succeeded;
    }

    private static async Task<(int Status, string Text)> GetAsync(WebApplication app)
    {
        var responseBody = new MemoryStream();
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            Request = { Method = "GET", Path = Route },
            Response = { Body = responseBody },
        };

        await EndpointOf(app, Route).RequestDelegate!(context);
        responseBody.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(responseBody).ReadToEndAsync());
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }

    private sealed class PrincipalClaimResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(Principals[context.User.FindFirstValue(PrincipalClaim)!]);
    }

    /// <summary>Allows the backup.read permission only to the applications named; everything else is denied.</summary>
    private sealed class BackupReadEvaluator(params Guid[] applicationsWithBackupRead) : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                requirement.ApplicationPermission == ApplicationPermissionIds.BackupRead
                    && authority.ApplicationId is { } application
                    && applicationsWithBackupRead.Contains(application)
                    ? AuthorizationDecision.Allow()
                    : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }
}
