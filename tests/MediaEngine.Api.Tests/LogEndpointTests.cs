using System.Text;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.EngineLogs;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Logs page actions: technical log, retention settings, and the Scheduled tasks list. All are for the server
/// administrator only; the retention action refuses limits outside the allowed ranges and saves valid ones.
/// </summary>
public sealed class LogEndpointTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-log-endpoints-{Guid.NewGuid():N}.db");
    private readonly string _configDirectory = Path.Combine(Path.GetTempPath(), $"tuvima-log-endpoints-config-{Guid.NewGuid():N}");
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), $"tuvima-log-endpoints-logs-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly ScheduledTaskRegistry _registry = new([], TimeProvider.System);

    public LogEndpointTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        Directory.CreateDirectory(_logDirectory);
        _configuration = new ConfigurationDirectoryLoader(_configDirectory);
    }

    [Theory]
    [InlineData("GET", "/logs/technical", "GetTechnicalLog")]
    [InlineData("GET", "/logs/retention", "GetLogRetention")]
    [InlineData("PUT", "/logs/retention", "UpdateLogRetention")]
    [InlineData("POST", "/logs/export/preview", "PreviewLogExport")]
    [InlineData("POST", "/logs/export", "ExportLogs")]
    [InlineData("GET", "/system/scheduled-tasks", "GetScheduledTasks")]
    public async Task EveryLogAction_IsForTheServerAdministratorOnly(string method, string route, string name)
    {
        await using var app = BuildApplication();

        var endpoint = EndpointOf(app, method, route);

        Assert.Equal(name, endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        // The same policy the other server-administrator actions (such as pruning the activity log) use.
        var policies = endpoint.Metadata.OfType<IAuthorizeData>().Select(data => data.Policy).ToArray();
        Assert.Contains(AuthPolicies.Administrator, policies);
        Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
    }

    [Fact]
    public async Task ReadingRetention_ReturnsTheLimitsAndWhatIsStoredNow()
    {
        File.WriteAllBytes(Path.Combine(_logDirectory, "tuvima-20261019.log"), new byte[2048]);
        await using var app = BuildApplication();

        var (status, body) = await SendAsync(app, "GET", "/logs/retention");

        Assert.Equal(StatusCodes.Status200OK, status);
        var root = JsonDocument.Parse(body).RootElement;
        var settings = root.GetProperty("settings");
        Assert.Equal(90, settings.GetProperty("activity_retention_days").GetInt32());
        Assert.Equal(500_000, settings.GetProperty("activity_max_events").GetInt32());
        Assert.Equal(90, settings.GetProperty("log_retention_days").GetInt32());
        Assert.Equal(1024, settings.GetProperty("log_max_total_mb").GetInt32());
        Assert.Equal(0, root.GetProperty("activity_event_count").GetInt64());
        Assert.Equal(1, root.GetProperty("log_file_count").GetInt32());
        Assert.Equal(2048, root.GetProperty("log_total_bytes").GetInt64());
    }

    [Fact]
    public async Task UpdatingRetention_SavesTheFourLimits_SoTheNextPruneUsesThem()
    {
        await using var app = BuildApplication();

        var (status, body) = await SendAsync(app, "PUT", "/logs/retention",
            """{"activity_retention_days":30,"activity_max_events":250000,"log_retention_days":14,"log_max_total_mb":512}""");

        Assert.True(status == StatusCodes.Status200OK, body);
        var saved = _configuration.LoadMaintenance();
        Assert.Equal(30, saved.ActivityRetentionDays);
        Assert.Equal(250_000, saved.ActivityMaxEvents);
        Assert.Equal(14, saved.LogRetentionDays);
        Assert.Equal(512, saved.LogMaxTotalMegabytes);
        Assert.Equal(30, JsonDocument.Parse(body).RootElement.GetProperty("settings").GetProperty("activity_retention_days").GetInt32());
    }

    [Theory]
    [InlineData("""{"activity_retention_days":0,"activity_max_events":250000,"log_retention_days":14,"log_max_total_mb":512}""")]
    [InlineData("""{"activity_retention_days":30,"activity_max_events":5,"log_retention_days":14,"log_max_total_mb":512}""")]
    [InlineData("""{"activity_retention_days":30,"activity_max_events":250000,"log_retention_days":99999,"log_max_total_mb":512}""")]
    [InlineData("""{"activity_retention_days":30,"activity_max_events":250000,"log_retention_days":14,"log_max_total_mb":1}""")]
    public async Task UpdatingRetention_WithALimitOutOfRange_IsRefusedAndSavesNothing(string request)
    {
        await using var app = BuildApplication();

        var (status, _) = await SendAsync(app, "PUT", "/logs/retention", request);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        var unchanged = _configuration.LoadMaintenance();
        Assert.Equal(90, unchanged.ActivityRetentionDays);
        Assert.Equal(500_000, unchanged.ActivityMaxEvents);
    }

    [Theory]
    [InlineData("?level=loud")]
    [InlineData("?area=Nonsense")]
    [InlineData("?from=2026-10-19T10:00:00Z&to=2026-10-19T09:00:00Z")]
    public async Task TheTechnicalLog_RefusesAnUnknownLevelOrAreaOrAnUpsideDownRange(string query)
    {
        await using var app = BuildApplication();

        var (status, _) = await SendAsync(app, "GET", "/logs/technical", query: query);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    [Fact]
    public async Task TheTechnicalLog_ReturnsLinesNewestFirstWithAreaLevelAndACursorForMore()
    {
        File.WriteAllText(Path.Combine(_logDirectory, "tuvima-20261019.log"),
            "2026-10-19 09:00:00.000 +00:00 [INF] MediaEngine.Ingestion.IngestionEngine\n  first\n"
            + "2026-10-19 09:01:00.000 +00:00 [ERR] MediaEngine.Identity.SignInService\n  second\n"
            + "2026-10-19 09:02:00.000 +00:00 [WRN] MediaEngine.Providers.Workers.RetailMatchWorker\n  third\n");
        await using var app = BuildApplication();

        var (status, body) = await SendAsync(app, "GET", "/logs/technical", query: "?limit=2");

        Assert.Equal(StatusCodes.Status200OK, status);
        var root = JsonDocument.Parse(body).RootElement;
        var lines = root.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(["third", "second"], lines.Select(line => line.GetProperty("message").GetString()));
        Assert.Equal("warning", lines[0].GetProperty("level").GetString());
        Assert.Equal("Providers", lines[0].GetProperty("area").GetString());
        Assert.Equal("Security", lines[1].GetProperty("area").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("next_cursor").GetString()));
    }

    [Fact]
    public async Task ScheduledTasks_ListsEachTaskWithItsWordsLastRunAndNextRun()
    {
        var task = _registry.Register(new ScheduledTaskDefinition("tidy", "Tidy up", "Removes old files.", "Every day at 3:00"));
        task.Done();
        task.NextRunAt(new DateTimeOffset(2026, 10, 20, 3, 0, 0, TimeSpan.Zero));
        _registry.Register(new ScheduledTaskDefinition("backup", "Back up", "Copies the data store.", "Every day at 2:00"))
            .Failed(new IOException("Disk full"));
        await using var app = BuildApplication();

        var (status, body) = await SendAsync(app, "GET", "/system/scheduled-tasks");

        Assert.Equal(StatusCodes.Status200OK, status);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(_registry.StartedAt, root.GetProperty("engine_started_at").GetDateTimeOffset());
        var tasks = root.GetProperty("tasks").EnumerateArray().ToArray();
        Assert.Equal(["backup", "tidy"], tasks.Select(item => item.GetProperty("id").GetString()));

        var backup = tasks[0];
        Assert.Equal("failed", backup.GetProperty("last_run_result").GetString());
        Assert.Equal("Disk full", backup.GetProperty("last_run_reason").GetString());
        Assert.Equal(JsonValueKind.Null, backup.GetProperty("next_run_at").ValueKind);

        var tidy = tasks[1];
        Assert.Equal("done", tidy.GetProperty("last_run_result").GetString());
        Assert.Equal("Every day at 3:00", tidy.GetProperty("schedule").GetString());
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 3, 0, 0, TimeSpan.Zero), tidy.GetProperty("next_run_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task ScheduledTasks_ForATaskThatHasNotRun_HasNoLastRun()
    {
        _registry.Register(new ScheduledTaskDefinition("tidy", "Tidy up", "Removes old files.", "Every day at 3:00"));
        await using var app = BuildApplication();

        var (_, body) = await SendAsync(app, "GET", "/system/scheduled-tasks");

        var task = JsonDocument.Parse(body).RootElement.GetProperty("tasks")[0];
        Assert.Equal(JsonValueKind.Null, task.GetProperty("last_run_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, task.GetProperty("last_run_result").ValueKind);
    }

    [Fact]
    public async Task Export_ReturnsOneTextFileNamedFromTheRange_WithPrivateDetailsHidden()
    {
        File.WriteAllText(Path.Combine(_logDirectory, "tuvima-20261019.log"),
            "2026-10-19 09:00:00.000 +00:00 [ERR] MediaEngine.Ingestion.IngestionEngine\n  Could not read C:\\Media\\Dune.mkv with api_key=ABCDEF123456\n");
        File.SetLastWriteTimeUtc(Path.Combine(_logDirectory, "tuvima-20261019.log"), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await using var app = BuildApplication();
        IHeaderDictionary? headers = null;

        var (status, body) = await SendAsync(app, "POST", "/logs/export",
            """{"from":"2026-10-19T08:00:00Z","to":"2026-10-19T10:00:00Z","level":"errors"}""", onHeaders: found => headers = found);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.StartsWith("text/plain", headers!.ContentType.ToString(), StringComparison.Ordinal);
        Assert.Contains("tuvima-logs-20261019-0800-20261019-1000.txt", headers.ContentDisposition.ToString(), StringComparison.Ordinal);
        Assert.Equal("no-store", headers.CacheControl.ToString());
        Assert.Contains("Could not read [path]", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEF123456", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Dune", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportPreview_SaysHowManyLinesAndHowLarge_TheSameWayTheExportIsBuilt()
    {
        File.WriteAllText(Path.Combine(_logDirectory, "tuvima-20261019.log"),
            "2026-10-19 09:00:00.000 +00:00 [ERR] MediaEngine.Ingestion.IngestionEngine\n  one\n"
            + "2026-10-19 09:01:00.000 +00:00 [ERR] MediaEngine.Ingestion.IngestionEngine\n  two\n");
        File.SetLastWriteTimeUtc(Path.Combine(_logDirectory, "tuvima-20261019.log"), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await using var app = BuildApplication();
        const string request = """{"from":"2026-10-19T08:00:00Z","to":"2026-10-19T10:00:00Z","level":"errors"}""";

        var (previewStatus, previewBody) = await SendAsync(app, "POST", "/logs/export/preview", request);
        var (_, exported) = await SendAsync(app, "POST", "/logs/export", request);

        Assert.Equal(StatusCodes.Status200OK, previewStatus);
        var preview = JsonDocument.Parse(previewBody).RootElement;
        Assert.Equal(2, preview.GetProperty("line_count").GetInt32());
        Assert.Equal(Encoding.UTF8.GetByteCount(exported), preview.GetProperty("size_bytes").GetInt64());
        Assert.False(preview.GetProperty("truncated").GetBoolean());
        Assert.Equal("tuvima-logs-20261019-0800-20261019-1000.txt", preview.GetProperty("file_name").GetString());
    }

    [Theory]
    [InlineData("""{"to":"2026-10-19T10:00:00Z","level":"errors"}""")]
    [InlineData("""{"from":"2026-10-19T10:00:00Z","to":"2026-10-19T08:00:00Z"}""")]
    [InlineData("""{"from":"2026-10-19T08:00:00Z","to":"2026-10-19T10:00:00Z","level":"loud"}""")]
    [InlineData("""{"from":"2026-10-19T08:00:00Z","to":"2026-10-19T10:00:00Z","areas":["Nonsense"]}""")]
    public async Task Export_WithAnIncompleteOrUnknownChoice_IsRefusedWithAReason(string request)
    {
        await using var app = BuildApplication();

        var (previewStatus, _) = await SendAsync(app, "POST", "/logs/export/preview", request);
        var (exportStatus, _) = await SendAsync(app, "POST", "/logs/export", request);

        Assert.Equal(StatusCodes.Status400BadRequest, previewStatus);
        Assert.Equal(StatusCodes.Status400BadRequest, exportStatus);
    }

    private WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<ISystemActivityRepository>(new SystemActivityRepository(_database));
        builder.Services.AddSingleton<IConfigurationLoader>(_configuration);
        builder.Services.AddSingleton(new EngineLogLocation(_logDirectory));
        builder.Services.AddSingleton<ITechnicalLogService>(new TechnicalLogService(new EngineLogLocation(_logDirectory)));
        builder.Services.AddSingleton<IScheduledTaskRegistry>(_registry);
        builder.Services.AddSingleton<ISystemActivityRangeReader>(new SystemActivityRepository(_database));
        builder.Services.AddSingleton<ILogExportService>(sp => new LogExportService(
            sp.GetRequiredService<ITechnicalLogService>(), sp.GetRequiredService<ISystemActivityRangeReader>(), TimeProvider.System));
        var app = builder.Build();
        app.MapLogEndpoints();
        app.MapScheduledTaskEndpoints();
        return app;
    }

    private static RouteEndpoint EndpointOf(WebApplication app, string method, string route) =>
        Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), candidate =>
                candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);

    // Runs the endpoint's handler directly, the way the other endpoint tests do, so no host or sign-in is needed.
    private static async Task<(int Status, string Text)> SendAsync(
        WebApplication app, string method, string route, string? json = null, string query = "", Action<IHeaderDictionary>? onHeaders = null)
    {
        var responseBody = new MemoryStream();
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            Request = { Method = method, Path = route, QueryString = new QueryString(query) },
            Response = { Body = responseBody },
        };

        if (json is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
        }

        await EndpointOf(app, method, route).RequestDelegate!(context);
        responseBody.Position = 0;
        onHeaders?.Invoke(context.Response.Headers);
        return (context.Response.StatusCode, await new StreamReader(responseBody).ReadToEndAsync());
    }

    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    public void Dispose()
    {
        _configuration.Dispose();
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
        TestTemp.DeleteDirectory(_configDirectory);
        TestTemp.DeleteDirectory(_logDirectory);
    }
}
