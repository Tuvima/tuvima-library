using System.Text.Json;
using MediaEngine.AI.Configuration;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// <c>GET /system/scheduled-tasks</c> lists every recurring task with the schedule in force, the next run computed from
/// it and the last run from the durable run records.
/// </summary>
public sealed class ScheduledTaskListEndpointTests : IDisposable
{
    private const string Route = "/system/scheduled-tasks";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-scheduled-tasks-{Guid.NewGuid():N}.db");
    private readonly string _configDirectory = Path.Combine(Path.GetTempPath(), $"tuvima-scheduled-tasks-config-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ScheduledTaskRunRepository _runs;

    public ScheduledTaskListEndpointTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _runs = new ScheduledTaskRunRepository(_database);
    }

    [Fact]
    public async Task TheRoute_ListsRegisteredTasksByName_WithNextRunFromTheCurrentCronAndTheLastRunFromRecords()
    {
        var registry = new ScheduledTaskRegistry();
        var schedule = "0 2 * * *";
        registry.Register(new ScheduledTaskDescriptor(
            "vibe_tags", "Vibe tags", ScheduledTaskOwners.LocalAi, null, () => schedule, "Local AI > Automation"));
        registry.Register(new ScheduledTaskDescriptor(
            "activity_pruning", "Activity log cleanup", ScheduledTaskOwners.Library, null, () => "0 3 * * *", "Maintenance"));
        var started = new DateTimeOffset(2026, 10, 19, 2, 0, 0, TimeSpan.Zero);
        var runId = await _runs.StartAsync("vibe_tags", null, started);
        await _runs.FinishAsync(runId, ScheduledTaskOutcomes.Skipped, started.AddSeconds(1), null, "Vibe tags is off");

        await using var app = BuildApplication(registry);
        var before = DateTimeOffset.Now;
        var body = await GetAsync(app);

        var tasks = body.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(["Activity log cleanup", "Vibe tags"], tasks.Select(task => task.GetProperty("name").GetString()));

        var vibe = tasks[1];
        Assert.Equal("vibe_tags", vibe.GetProperty("key").GetString());
        Assert.Equal("Local AI", vibe.GetProperty("owner").GetString());
        Assert.Equal(JsonValueKind.Null, vibe.GetProperty("plugin_id").ValueKind);
        Assert.Equal("0 2 * * *", vibe.GetProperty("schedule").GetString());
        Assert.False(vibe.GetProperty("is_continuous").GetBoolean());
        Assert.Equal("Local AI > Automation", vibe.GetProperty("settings_place").GetString());
        var next = vibe.GetProperty("next_run_at").GetDateTimeOffset();
        Assert.True(next > before);
        Assert.True(next <= before.AddDays(1).AddMinutes(1));
        Assert.Equal(next, CronScheduler.NextOccurrence("0 2 * * *", next.AddMinutes(-1)));
        var last = vibe.GetProperty("last_run");
        Assert.Equal(started, last.GetProperty("started_at").GetDateTimeOffset());
        Assert.Equal(started.AddSeconds(1), last.GetProperty("finished_at").GetDateTimeOffset());
        Assert.Equal("skipped", last.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("items_processed").ValueKind);
        Assert.Equal("Vibe tags is off", last.GetProperty("message").GetString());

        Assert.Equal(JsonValueKind.Null, tasks[0].GetProperty("last_run").ValueKind);
    }

    [Fact]
    public async Task Build_ReadsTheScheduleAtQueryTime_SoAnEditShowsStraightAway()
    {
        var registry = new ScheduledTaskRegistry();
        var schedule = "0 2 * * *";
        registry.Register(new ScheduledTaskDescriptor(
            "series_order", "Series order", ScheduledTaskOwners.LocalAi, null, () => schedule, "Local AI > Automation"));
        var now = new DateTimeOffset(2026, 10, 19, 1, 0, 0, TimeSpan.Zero);

        var first = await ScheduledTaskListBuilder.BuildAsync(registry, _runs, now, default);
        schedule = "0 6 * * *";
        var second = await ScheduledTaskListBuilder.BuildAsync(registry, _runs, now, default);

        Assert.Equal("0 2 * * *", Assert.Single(first.Tasks).Schedule);
        var edited = Assert.Single(second.Tasks);
        Assert.Equal("0 6 * * *", edited.Schedule);
        Assert.Equal(CronScheduler.NextOccurrence("0 6 * * *", now)?.ToUniversalTime(), edited.NextRunAt);
    }

    [Fact]
    public async Task Build_ForAnUnreadableSchedule_ReportsNoNextRunInsteadOfFailing()
    {
        var registry = new ScheduledTaskRegistry();
        registry.Register(new ScheduledTaskDescriptor(
            "retag_sweep", "File tag refresh", ScheduledTaskOwners.Library, null, () => "not a cron", "Maintenance"));

        var list = await ScheduledTaskListBuilder.BuildAsync(registry, _runs, DateTimeOffset.Now, default);

        var task = Assert.Single(list.Tasks);
        Assert.Null(task.NextRunAt);
        Assert.Equal("not a cron", task.Schedule);
    }

    [Fact]
    public async Task Catalog_ListsEveryRecurringTask_WithWebhookDeliveryAsContinuousAndItsLastRunFromTheNewestAttempt()
    {
        var webhooks = new ApplicationWebhookRepository(_database);
        var attemptedAt = new DateTimeOffset(2026, 10, 19, 8, 30, 0, TimeSpan.Zero);
        await SeedWebhookAttemptAsync(webhooks, attemptedAt, "delivered");
        var registry = new ScheduledTaskRegistry();
        Directory.CreateDirectory(_configDirectory);
        using var loader = new ConfigurationDirectoryLoader(_configDirectory);
        loader.SaveMaintenance(new MaintenanceSettings());
        ScheduledTaskCatalog.RegisterAll(registry, loader, webhooks);

        var list = await ScheduledTaskListBuilder.BuildAsync(registry, _runs, DateTimeOffset.Now, default);

        Assert.Equal(
            [
                "activity_pruning", "adaptive_streaming_cleanup", "enrichment_refresh_schedule",
                "folder_health_check", "library_reconciliation", "log_file_retention", "missing_universe_sweep",
                "pass2_nightly_sweep", "playback_session_cleanup", "plugin_segment_detection", "provider_health_checks",
                "rejected_file_cleanup", "retag_sweep", "storage_maintenance", "universe_enrichment", "unmatched_retry",
                "webhook_delivery",
            ],
            list.Tasks.Select(task => task.Key).Order(StringComparer.Ordinal));
        Assert.Equal(list.Tasks.OrderBy(task => task.Name, StringComparer.OrdinalIgnoreCase).Select(task => task.Key), list.Tasks.Select(task => task.Key));

        var webhook = Assert.Single(list.Tasks, task => task.Key == "webhook_delivery");
        Assert.True(webhook.IsContinuous);
        Assert.Equal("every 5 seconds", webhook.Schedule);
        Assert.Null(webhook.NextRunAt);
        Assert.NotNull(webhook.LastRun);
        Assert.Equal(attemptedAt, webhook.LastRun.StartedAt);
        Assert.Equal("succeeded", webhook.LastRun.Outcome);

        var segments = Assert.Single(list.Tasks, task => task.Key == "plugin_segment_detection");
        Assert.Equal("Plugin", segments.Owner);
        Assert.Equal("0 2 * * *", segments.Schedule);
        Assert.NotNull(segments.NextRunAt);
    }

    [Fact]
    public async Task Catalog_NeverShowsAFileNameOrPath_InAnythingAUserReads()
    {
        var registry = new ScheduledTaskRegistry();
        Directory.CreateDirectory(_configDirectory);
        using var loader = new ConfigurationDirectoryLoader(_configDirectory);
        loader.SaveMaintenance(new MaintenanceSettings());
        ScheduledTaskCatalog.RegisterAll(registry, loader, new ApplicationWebhookRepository(_database));

        var list = await ScheduledTaskListBuilder.BuildAsync(registry, _runs, DateTimeOffset.Now, default);

        Assert.NotEmpty(list.Tasks);
        Assert.All(list.Tasks, task =>
        {
            foreach (var text in new[] { task.Name, task.SettingsPlace })
            {
                Assert.DoesNotContain(".json", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("config/", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("config\\", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("/", text, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public void MaintenanceDefaults_ListEveryScheduleKeyAServiceReads_AndNoneThatNothingReads()
    {
        var keys = new MaintenanceSettings().Schedules.Keys;

        Assert.Equal(
            [
                "activity_pruning", "library_reconciliation", "log_file_retention", "missing_universe_sweep", "pass2_nightly_sweep",
                "plugin_segment_detection", "rejected_file_cleanup", "retag_sweep", "storage_maintenance", "universe_enrichment",
            ],
            keys.Order(StringComparer.Ordinal));
    }

    private WebApplication BuildApplication(IScheduledTaskRegistry registry)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton<IScheduledTaskRunRepository>(_runs);
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<DataStoreVersionRepository>();
        // The other system routes are mapped too; their services only need to be known so they bind as services.
        builder.Services.AddSingleton<IConfigurationLoader>(_ => null!);
        builder.Services.AddSingleton<StartupReadinessService>(_ => null!);
        builder.Services.AddSingleton<IMediaOperationRepository>(_ => null!);
        builder.Services.AddSingleton<IFileWatcher>(_ => null!);
        builder.Services.AddSingleton<AssetStoreCleanupService>(_ => null!);
        builder.Services.AddSingleton<DatabaseBackupService>(_ => null!);
        builder.Services.AddSingleton<SystemResourcesService>(_ => null!);
        var app = builder.Build();
        app.MapSystemEndpoints();
        return app;
    }

    private static async Task<JsonElement> GetAsync(WebApplication app)
    {
        var endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            candidate => candidate.RoutePattern.RawText == Route
                && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);
        var responseBody = new MemoryStream();
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            Request = { Method = "GET", Path = Route },
            Response = { Body = responseBody },
        };

        await endpoint.RequestDelegate!(context);
        responseBody.Position = 0;
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        return JsonDocument.Parse(await new StreamReader(responseBody).ReadToEndAsync()).RootElement.Clone();
    }

    private async Task SeedWebhookAttemptAsync(ApplicationWebhookRepository webhooks, DateTimeOffset attemptedAt, string status)
    {
        var endpoint = new ApplicationWebhook
        {
            Id = Guid.NewGuid(),
            ApplicationId = MediaEngine.Domain.Entities.BuiltInApplicationIds.NativeClient,
            Url = "https://receiver.example.invalid/events",
            EventTypesJson = "[\"ingestion.completed\"]",
            SecretCiphertext = "encrypted-fixture",
            IsEnabled = true,
            CreatedAt = attemptedAt,
            UpdatedAt = attemptedAt,
        };
        await webhooks.SaveAsync(endpoint, true);
        await webhooks.QueueAsync(endpoint, 1,
        [
            new ApplicationWebhookDelivery
            {
                Id = Guid.NewGuid(),
                WebhookId = endpoint.Id,
                WebhookVersion = endpoint.Version,
                EventId = Guid.NewGuid(),
                EventSequence = 1,
                EventJson = "{}",
                CreatedAt = attemptedAt,
                UpdatedAt = attemptedAt,
                NextAttemptAt = attemptedAt.AddSeconds(-1),
            },
        ]);
        var due = Assert.Single(await webhooks.DueAsync(attemptedAt));
        due.AttemptCount = 1;
        due.Status = status;
        due.UpdatedAt = attemptedAt;
        await webhooks.FinishAttemptAsync(due, "HTTP 204", true, attemptedAt);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
        if (Directory.Exists(_configDirectory))
        {
            Directory.Delete(_configDirectory, recursive: true);
        }
    }
}
