using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
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

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-store-version-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;

    public DataStoreVersionEndpointTests()
    {
        _database = new DatabaseConnection(_databasePath);
    }

    [Fact]
    public async Task FreshDataStore_ReportsTheBaselineStepAsStepOne()
    {
        _database.InitializeSchema();
        await using var app = BuildApplication();

        var (status, body) = await GetAsync(app);

        Assert.Equal(StatusCodes.Status200OK, status);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(1, root.GetProperty("step_number").GetInt32());
        Assert.Equal("baseline", root.GetProperty("step_name").GetString());
        Assert.True(root.GetProperty("applied_at").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
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

    private WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<DataStoreVersionRepository>();
        // The other system routes are mapped too; their services only need to be known so they bind as services.
        builder.Services.AddSingleton<IConfigurationLoader>(_ => null!);
        builder.Services.AddSingleton<StartupReadinessService>(_ => null!);
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
}
