using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.EngineLogs;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.System;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The Logs page's Engine actions (technical log and retention) and the Scheduled tasks list. Logs can mention other
/// people and file names, so every action here is for the server administrator only.
/// </summary>
public static class LogEndpoints
{
    public static IEndpointRouteBuilder MapLogEndpoints(this IEndpointRouteBuilder app)
    {
        var logs = app.MapGroup("/logs").WithTags("Logs");

        // GET /logs/technical?from&to&level&area&q&cursor&limit — the Engine's log files, newest first.
        logs.MapGet("/technical", async (
            ITechnicalLogService reader,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? level,
            string? area,
            string? q,
            string? cursor,
            int? limit,
            CancellationToken ct) =>
        {
            if (!TechnicalLogService.TryParseMinimumLevel(level, out _))
            {
                return ApiErrors.BadRequest("Level must be one of: error, warning, information, all.");
            }

            if (!string.IsNullOrWhiteSpace(area) && LogAreas.Normalize(area) is null)
            {
                return ApiErrors.BadRequest($"Area must be one of: {string.Join(", ", LogAreas.All)}.");
            }

            if (from is { } start && to is { } end && start > end)
            {
                return ApiErrors.BadRequest("The start of the time range must be before its end.");
            }

            var size = PagedRequest.From(
                null, limit, defaultLimit: TechnicalLogService.DefaultLimit, maxLimit: TechnicalLogService.MaxLimit).Limit;
            var page = await reader.ReadAsync(new TechnicalLogQuery(from, to, level, area, q, cursor, size), ct);

            return Results.Ok(new TechnicalLogPageDto
            {
                Lines = page.Lines.Select(line => new TechnicalLogLineDto
                {
                    Time = line.Time,
                    Level = line.Level,
                    Area = line.Area,
                    Message = line.Message,
                }).ToList(),
                NextCursor = page.NextCursor,
            });
        })
        .WithName("GetTechnicalLog")
        .WithSummary("Returns the Engine's technical log lines newest first, filtered by time, level, area and text.")
        .Produces<TechnicalLogPageDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireEffectiveAdministrator();

        // POST /logs/export/preview — how many lines and how large the export would be. A body, not a query string, so
        // an id or search text is not left in browser history.
        logs.MapPost("/export/preview", async (
            LogExportRequest request,
            ILogExportService export,
            CancellationToken ct) =>
        {
            var problem = LogExportService.Validate(request);
            if (problem is not null)
            {
                return ApiErrors.BadRequest(problem);
            }

            var result = await export.BuildAsync(request, ct);
            return Results.Ok(new LogExportPreviewDto
            {
                LineCount = result.LineCount,
                SizeBytes = result.Content.LongLength,
                Truncated = result.Truncated,
                FileName = result.FileName,
            });
        })
        .WithName("PreviewLogExport")
        .WithSummary("Returns how many lines and how large a log export for these filters would be.")
        .Produces<LogExportPreviewDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireEffectiveAdministrator();

        // POST /logs/export — one plain-text file with private details hidden.
        logs.MapPost("/export", async (
            LogExportRequest request,
            ILogExportService export,
            HttpContext http,
            CancellationToken ct) =>
        {
            var problem = LogExportService.Validate(request);
            if (problem is not null)
            {
                return ApiErrors.BadRequest(problem);
            }

            var result = await export.BuildAsync(request, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(result.Content, "text/plain; charset=utf-8", result.FileName);
        })
        .WithName("ExportLogs")
        .WithSummary("Downloads one plain-text log file for the chosen range, level and areas, with private details hidden.")
        .Produces(StatusCodes.Status200OK, contentType: "text/plain")
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireEffectiveAdministrator();

        // GET /logs/retention — the four limits plus how much is stored now.
        logs.MapGet("/retention", async (
            ISystemActivityRepository activity,
            IConfigurationLoader configLoader,
            EngineLogLocation location,
            CancellationToken ct) =>
            Results.Ok(await BuildStatusAsync(activity, configLoader, location, ct)))
        .WithName("GetLogRetention")
        .WithSummary("Returns how long and how much of the Events log and the Engine's log files are kept.")
        .Produces<LogRetentionStatusDto>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // PUT /logs/retention — change the four limits. The next nightly clean-up uses them.
        logs.MapPut("/retention", async (
            LogRetentionSettingsDto request,
            ISystemActivityRepository activity,
            IConfigurationLoader configLoader,
            EngineLogLocation location,
            CancellationToken ct) =>
        {
            var problem = LogRetentionLimits.Validate(
                request.ActivityRetentionDays, request.ActivityMaxEvents,
                request.LogRetentionDays, request.LogMaxTotalMegabytes);
            if (problem is not null)
            {
                return ApiErrors.BadRequest(problem);
            }

            var maintenance = configLoader.LoadMaintenance();
            maintenance.ActivityRetentionDays = request.ActivityRetentionDays;
            maintenance.ActivityMaxEvents = request.ActivityMaxEvents;
            maintenance.LogRetentionDays = request.LogRetentionDays;
            maintenance.LogMaxTotalMegabytes = request.LogMaxTotalMegabytes;
            configLoader.SaveMaintenance(maintenance);

            return Results.Ok(await BuildStatusAsync(activity, configLoader, location, ct));
        })
        .WithName("UpdateLogRetention")
        .WithSummary("Changes how long and how much of the Events log and the Engine's log files are kept.")
        .Produces<LogRetentionStatusDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireEffectiveAdministrator();

        return app;
    }

    public static IEndpointRouteBuilder MapScheduledTaskEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /system/scheduled-tasks — every recurring Engine task with its last and next run.
        app.MapGet("/system/scheduled-tasks", (IScheduledTaskRegistry registry) =>
            Results.Ok(new ScheduledTasksResponse
            {
                EngineStartedAt = registry.StartedAt,
                Tasks = registry.GetSnapshot().Select(snapshot => new ScheduledTaskDto
                {
                    Id = snapshot.Definition.Id,
                    Name = snapshot.Definition.Name,
                    Description = snapshot.Definition.Description,
                    Schedule = snapshot.Definition.Schedule,
                    LastRunAt = snapshot.LastRun?.At,
                    LastRunResult = snapshot.LastRun is null ? null : snapshot.LastRun.Outcome.ToString().ToLowerInvariant(),
                    LastRunReason = snapshot.LastRun?.Reason,
                    NextRunAt = snapshot.NextRun,
                }).ToList(),
            }))
        .WithTags("System")
        .WithName("GetScheduledTasks")
        .WithSummary("Lists every recurring Engine task with when it last ran (since the Engine started) and when it runs next.")
        .Produces<ScheduledTasksResponse>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        return app;
    }

    private static async Task<LogRetentionStatusDto> BuildStatusAsync(
        ISystemActivityRepository activity,
        IConfigurationLoader configLoader,
        EngineLogLocation location,
        CancellationToken ct)
    {
        var maintenance = configLoader.LoadMaintenance();
        var (fileCount, totalBytes) = location.Measure();
        return new LogRetentionStatusDto
        {
            Settings = new LogRetentionSettingsDto
            {
                ActivityRetentionDays = maintenance.ActivityRetentionDays,
                ActivityMaxEvents = maintenance.ActivityMaxEvents,
                LogRetentionDays = maintenance.LogRetentionDays,
                LogMaxTotalMegabytes = maintenance.LogMaxTotalMegabytes,
            },
            ActivityEventCount = await activity.CountAsync(ct),
            LogFileCount = fileCount,
            LogTotalBytes = totalBytes,
        };
    }
}
