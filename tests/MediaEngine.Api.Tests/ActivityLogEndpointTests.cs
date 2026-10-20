using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Activity log action (<c>GET /activity/log</c>): who may read it, and what it returns for each category.
/// </summary>
public sealed class ActivityLogEndpointTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-activity-log-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly SystemActivityRepository _repository;

    public ActivityLogEndpointTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _repository = new SystemActivityRepository(_database);
    }

    [Fact]
    public async Task TheActivityLogAction_IsRefusedToAnyoneWhoIsNotAnAdministratorOrAnApplicationWithTheActivityPermission()
    {
        await using var app = BuildApplication();

        var endpoint = EndpointOf(app);

        Assert.Equal("GetActivityLog", endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        var policy = Assert.Single(endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
        Assert.Single(policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>());
        Assert.Equal(
            ApplicationPermissionIds.SystemActivityRead,
            Assert.Single(policy.Requirements.OfType<AdministratorOrApplicationRequirement>()).Permission);
    }

    [Fact]
    public async Task TheAddedCategory_ReturnsOnlyAddedRows_WithTheirPlainWords()
    {
        await Log(SystemActionType.MediaAdded, "Dune", changes: """{"media_type":"Movies"}""");
        await Log(SystemActionType.PersonalMediaAdded, "3,000 photos", detail: "Added to Family photos", changes: """{"count":3000}""");
        await Log(SystemActionType.ReviewItemCreated, "Arrival");
        await Log(SystemActionType.MediaFailed, "broken.mkv");
        await Log(SystemActionType.FileHashed, "not shown");

        var root = await GetAsync("?category=added");

        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal("added", item.GetProperty("category").GetString()));
        Assert.Equal(["PersonalMediaAdded", "MediaAdded"], items.Select(item => item.GetProperty("kind").GetString()));
        Assert.Equal("Added to Movies", items[1].GetProperty("detail").GetString());
        Assert.Equal(3000, items[0].GetProperty("count").GetInt32());
        Assert.Equal(2, root.GetProperty("total_count").GetInt32());
        Assert.False(root.GetProperty("has_more").GetBoolean());
    }

    [Fact]
    public async Task WithNoCategory_ReturnsTheFourCategoriesTogether_ButNotTechnicalRows()
    {
        await Log(SystemActionType.MediaAdded, "Dune");
        await Log(SystemActionType.ReviewItemCreated, "Arrival");
        await Log(SystemActionType.MediaFailed, "broken.mkv");
        await Log(SystemActionType.ServerStarted, null);
        await Log(SystemActionType.FileHashed, "not shown");

        var root = await GetAsync(string.Empty);

        Assert.Equal(4, root.GetProperty("total_count").GetInt32());
        Assert.DoesNotContain(
            root.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == SystemActionType.FileHashed);
    }

    [Fact]
    public async Task SearchingAndPaging_NarrowTheRowsAndReportTheTotal()
    {
        for (var index = 0; index < 5; index++)
        {
            await Log(SystemActionType.MediaAdded, $"Dune {index}");
        }

        await Log(SystemActionType.MediaAdded, "Arrival");

        var root = await GetAsync("?category=added&search=dune&offset=0&limit=2");

        Assert.Equal(5, root.GetProperty("total_count").GetInt32());
        Assert.Equal(2, root.GetProperty("items").GetArrayLength());
        Assert.True(root.GetProperty("has_more").GetBoolean());
    }

    private async Task Log(string kind, string? name, string? detail = null, string? changes = null) =>
        await _repository.LogAsync(new SystemActivityEntry
        {
            ActionType = kind,
            CollectionName = name,
            Detail = detail,
            ChangesJson = changes,
        });

    private WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ISystemActivityRepository>(_repository);
        builder.Services.AddSingleton<IActivityLogReadService>(new ActivityLogReadService(_repository));
        // The other Activity actions are mapped in the same group; naming their services lets the group be built,
        // and these tests never call them.
        builder.Services.AddSingleton<IIngestionPresentationReadService>(_ => null!);
        builder.Services.AddSingleton<IPersonRepository>(_ => null!);
        builder.Services.AddSingleton<IQidLabelRepository>(_ => null!);
        builder.Services.AddSingleton<IConfigurationLoader>(_ => null!);
        builder.Services.AddSingleton<ActivityRetentionPruner>(_ => null!);
        var app = builder.Build();
        app.MapActivityEndpoints();
        return app;
    }

    private static RouteEndpoint EndpointOf(WebApplication app) =>
        Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), candidate =>
                candidate.RoutePattern.RawText == "/activity/log" &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);

    // Runs the endpoint's handler directly, the way the other endpoint tests do, so no host or sign-in is needed.
    private async Task<JsonElement> GetAsync(string query)
    {
        await using var app = BuildApplication();
        var responseBody = new MemoryStream();
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            Request = { Method = "GET", Path = "/activity/log", QueryString = new QueryString(query) },
            Response = { Body = responseBody },
        };

        await EndpointOf(app).RequestDelegate!(context);
        responseBody.Position = 0;
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        return JsonDocument.Parse(await new StreamReader(responseBody).ReadToEndAsync()).RootElement.Clone();
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }
}
