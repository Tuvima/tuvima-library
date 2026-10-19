using System.Text.Json;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Endpoints;

public static class ActivityEndpoints
{
    public static IEndpointRouteBuilder MapActivityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/activity")
            .WithTags("Activity");

        group.MapGet("/summary", async (
            IIngestionPresentationReadService readService,
            CancellationToken ct) => Results.Ok(await readService.GetActivitySummaryAsync(ct)))
        .WithName("GetActivityHistorySummary")
        .WithSummary("Returns the compact Activity and Audit history summary.")
        .Produces<ActivityHistorySummaryDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        group.MapGet("/batches", async (
            IActivityBatchReadService readService,
            string? search,
            string? mediaType,
            string? status,
            string? source,
            string? eventType,
            DateTimeOffset? start,
            DateTimeOffset? end,
            int? offset,
            int? limit,
            string? sort,
            string? sortDirection,
            bool? historicalOnly,
            CancellationToken ct) =>
        {
            var page = PagedRequest.From(offset, limit, defaultLimit: 25);
            var query = new ActivityBatchQuery(
                search,
                mediaType,
                status,
                source,
                eventType,
                start,
                end,
                page.Offset,
                page.Limit,
                sort,
                sortDirection, historicalOnly == true);

            return Results.Ok(await readService.GetBatchesAsync(query, ct));
        })
        .WithName("GetActivityBatches")
        .WithSummary("Returns paged ingestion batch summaries for the Activity audit page.")
        .Produces<PagedResponse<ActivityBatchSummaryDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        group.MapGet("/batches/{batchId:guid}", async (
            Guid batchId,
            IActivityBatchReadService readService,
            CancellationToken ct) =>
        {
            var batch = await readService.GetBatchAsync(batchId, ct);
            return batch is null
                ? ApiErrors.NotFound($"No activity operation found for batch '{batchId}'.")
                : Results.Ok(batch);
        })
        .WithName("GetActivityBatch")
        .WithSummary("Returns one exact activity operation regardless of the current history date range.")
        .Produces<ActivityBatchSummaryDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        group.MapGet("/batches/{batchId:guid}/presentation", async (
            Guid batchId,
            IIngestionPresentationReadService readService,
            CancellationToken ct) =>
        {
            var presentation = await readService.GetBatchPresentationAsync(batchId, ct);
            return presentation is null
                ? ApiErrors.NotFound($"No activity operation found for batch '{batchId}'.")
                : Results.Ok(presentation);
        })
        .WithName("GetActivityBatchPresentation")
        .WithSummary("Returns the human-readable expanded presentation for one durable batch.")
        .Produces<ActivityBatchPresentationDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        group.MapGet("/batches/{batchId:guid}/media-groups", async (
            Guid batchId,
            IIngestionPresentationReadService readService,
            string? search,
            string? lane,
            string? sort,
            int? offset,
            int? limit,
            CancellationToken ct) =>
        {
            var page = PagedRequest.From(offset, limit, 50, 100);
            return Results.Ok(await readService.GetBatchMediaAsync(batchId, page.Offset, page.Limit, search, lane, sort, ct));
        })
        .WithName("GetActivityBatchMediaGroups")
        .WithSummary("Returns paged, event-scoped media groups produced by one batch.")
        .Produces<PagedResponse<IngestionMediaGroupDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        group.MapGet("/batches/{batchId:guid}/groups", async (
            Guid batchId,
            IActivityBatchReadService readService,
            CancellationToken ct) =>
        {
            var groups = await readService.GetGroupsAsync(batchId, ct);
            return Results.Ok(groups);
        })
        .WithName("GetActivityBatchGroups")
        .WithSummary("Returns media-type rollups for one ingestion batch.")
        .Produces<List<ActivityMediaTypeGroupDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        group.MapGet("/batches/{batchId:guid}/insights", async (
            Guid batchId,
            IActivityBatchReadService readService,
            CancellationToken ct) =>
        {
            return Results.Ok(await readService.GetInsightsAsync(batchId, ct));
        })
        .WithName("GetActivityBatchInsights")
        .WithSummary("Returns compact enrichment and provider aggregates for one durable batch.")
        .Produces<ActivityBatchInsightsDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        group.MapGet("/batches/{batchId:guid}/items", async (
            Guid batchId,
            IActivityBatchReadService readService,
            string? mediaType,
            string? search,
            string? status,
            string? source,
            int? offset,
            int? limit,
            string? sort,
            string? sortDirection,
            CancellationToken ct) =>
        {
            var page = PagedRequest.From(offset, limit, defaultLimit: 25, maxLimit: 100);
            return Results.Ok(await readService.GetItemsAsync(batchId, mediaType, search, status, source, page.Offset, page.Limit, sort, sortDirection, ct));
        })
        .WithName("GetActivityBatchItems")
        .WithSummary("Returns paged title/item rows for one ingestion batch.")
        .Produces<PagedResponse<ActivityBatchItemDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        group.MapGet("/batches/{batchId:guid}/events", async (
            Guid batchId,
            IActivityBatchReadService readService,
            string? search,
            string? eventType,
            string? status,
            int? offset,
            int? limit,
            CancellationToken ct) =>
        {
            var page = PagedRequest.From(offset, limit, defaultLimit: 25, maxLimit: 100);
            return Results.Ok(await readService.GetEventsAsync(batchId, search, eventType, status, page.Offset, page.Limit, ct));
        })
        .WithName("GetActivityBatchEvents")
        .WithSummary("Returns a filtered, paged technical event log for one durable batch.")
        .Produces<PagedResponse<ActivityTechnicalEventDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        group.MapGet("/batches/{batchId:guid}/items/{assetId:guid}", async (
            Guid batchId,
            Guid assetId,
            IActivityBatchReadService readService,
            CancellationToken ct) =>
        {
            var detail = await readService.GetItemDetailAsync(batchId, assetId, ct);
            return detail is null
                ? ApiErrors.NotFound($"No item found for batch '{batchId}' and asset '{assetId}'.")
                : Results.Ok(detail);
        })
        .WithName("GetActivityBatchItemDetail")
        .WithSummary("Returns timeline, file details, people, and provenance for one batch item.")
        .Produces<ActivityBatchItemDetailDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        group.MapGet("/people", async (
            IActivityBatchReadService readService,
            string? search,
            string? mediaType,
            string? status,
            string? source,
            string? eventType,
            DateTimeOffset? start,
            DateTimeOffset? end,
            int? offset,
            int? limit,
            string? sort,
            string? sortDirection,
            CancellationToken ct) =>
        {
            var page = PagedRequest.From(offset, limit, defaultLimit: 25);
            var query = new ActivityBatchQuery(
                search,
                mediaType,
                status,
                source,
                eventType,
                start,
                end,
                page.Offset,
                page.Limit,
                sort,
                sortDirection);

            return Results.Ok(await readService.GetPeopleAsync(query, ct));
        })
        .WithName("GetActivityPeopleAudit")
        .WithSummary("Returns people hydrated or linked by ingestion batches with source provenance.")
        .Produces<PagedResponse<ActivityPersonAuditDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.SystemActivityRead);

        // GET /activity/recent?limit=50 — returns the most recent activity entries.
        group.MapGet("/recent", async (
            ISystemActivityRepository repo,
            IPersonRepository personRepo,
            IQidLabelRepository qidLabelRepo,
            int? limit,
            CancellationToken ct) =>
        {
            var entries = await repo.GetRecentAsync(PagedRequest.From(null, limit, defaultLimit: 50).Limit);

            var response = await MapEntriesAsync(entries, personRepo, qidLabelRepo, ct);

            return Results.Ok(response);
        })
        .WithName("GetRecentActivity")
        .WithSummary("Returns the most recent activity log entries, newest first.")
        .Produces<List<ActivityEntryResponse>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.SystemActivityRead);

        // GET /activity/log?category=&search=&offset=&limit= — the Events part of System > Logs, already worded.
        group.MapGet("/log", async (
            IActivityLogReadService readService,
            string? category,
            string? search,
            int? offset,
            int? limit,
            CancellationToken ct) =>
        {
            var page = PagedRequest.From(offset, limit, defaultLimit: 50);
            return Results.Ok(await readService.GetLogAsync(category, search, page.Offset, page.Limit, ct));
        })
        .WithName("GetActivityLog")
        .WithSummary("Returns one page of the Activity log (added media, Needs Review, failures and system events), newest first, in plain words.")
        .Produces<PagedResponse<ActivityLogEntryDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.SystemActivityRead);

        // POST /activity/prune — manual prune trigger.
        group.MapPost("/prune", async (
            ActivityRetentionPruner pruner,
            IConfigurationLoader configLoader,
            CancellationToken ct) =>
        {
            var result = await pruner.PruneAsync(configLoader.LoadMaintenance(), ct);

            return Results.Ok(new PruneResponse
            {
                Deleted = result.Total,
                RetentionDays = result.RetentionDays,
            });
        })
        .WithName("PruneActivity")
        .WithSummary("Deletes activity entries older than the retention period, then the oldest ones past the event limit.")
        .Produces<PruneResponse>(StatusCodes.Status200OK)
        .RequireEffectiveAdministrator();

        // GET /activity/stats — entry count and retention setting.
        group.MapGet("/stats", async (
            ISystemActivityRepository repo,
            IConfigurationLoader configLoader) =>
        {
            var maintenance = configLoader.LoadMaintenance();
            var count = await repo.CountAsync();

            return Results.Ok(new ActivityStatsResponse
            {
                TotalEntries = count,
                RetentionDays = maintenance.ActivityRetentionDays,
            });
        })
        .WithName("GetActivityStats")
        .WithSummary("Returns the total entry count and the current retention setting.")
        .Produces<ActivityStatsResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.SystemActivityRead);

        // GET /activity/by-types?types=BatchCreated,BatchCompleted&limit=50
        // Returns recent entries filtered by action types for activity summaries.
        group.MapGet("/by-types", async (
            ISystemActivityRepository repo,
            IPersonRepository personRepo,
            IQidLabelRepository qidLabelRepo,
            string? types,
            int? limit,
            CancellationToken ct) =>
        {
            var typeList = string.IsNullOrWhiteSpace(types)
                ? Array.Empty<string>()
                : types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var entries = await repo.GetRecentByTypesAsync(typeList, PagedRequest.From(null, limit, defaultLimit: 50).Limit);
            var response = await MapEntriesAsync(entries, personRepo, qidLabelRepo, ct);
            return Results.Ok(response);
        })
        .WithName("GetActivityByTypes")
        .WithSummary("Returns recent activity entries filtered by one or more action types (comma-separated).")
        .Produces<List<ActivityEntryResponse>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.SystemActivityRead);

        // GET /activity/run/{runId} — returns all entries for a specific ingestion run.
        group.MapGet("/run/{runId:guid}", async (
            ISystemActivityRepository repo,
            IPersonRepository personRepo,
            IQidLabelRepository qidLabelRepo,
            Guid runId,
            CancellationToken ct) =>
        {
            var entries = await repo.GetByRunIdAsync(runId);
            var response = await MapEntriesAsync(entries, personRepo, qidLabelRepo, ct);
            return Results.Ok(response);
        })
        .WithName("GetActivityByRunId")
        .WithSummary("Returns all activity entries for a given ingestion run, ordered by timestamp.")
        .Produces<List<ActivityEntryResponse>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        return app;
    }

    private static async Task<List<ActivityEntryResponse>> MapEntriesAsync(
        IReadOnlyList<SystemActivityEntry> entries,
        IPersonRepository personRepo,
        IQidLabelRepository qidLabelRepo,
        CancellationToken ct)
    {
        var response = new List<ActivityEntryResponse>(entries.Count);
        foreach (var entry in entries)
        {
            response.Add(await MapEntryAsync(entry, personRepo, qidLabelRepo, ct));
        }

        return response;
    }

    private static async Task<ActivityEntryResponse> MapEntryAsync(
        SystemActivityEntry e,
        IPersonRepository personRepo,
        IQidLabelRepository qidLabelRepo,
        CancellationToken ct)
    {
        var collectionName = e.CollectionName;
        var detail = e.Detail;

        if (string.Equals(e.ActionType, SystemActionType.PersonHydrated, StringComparison.OrdinalIgnoreCase)
            && NeedsPersonNameResolution(collectionName, detail))
        {
            var qid = ExtractPersonQid(e.ChangesJson) ?? ExtractFirstQid(detail);
            var resolvedName = await ResolveActivityPersonNameAsync(e.EntityId, qid, personRepo, qidLabelRepo, ct);
            if (!string.IsNullOrWhiteSpace(resolvedName))
            {
                collectionName = resolvedName;
                detail = $"Person \"{resolvedName}\" enriched from Wikidata";
            }
            else if (!string.IsNullOrWhiteSpace(qid))
            {
                collectionName = $"Name pending ({qid})";
                detail = $"Person \"Name pending ({qid})\" enriched from Wikidata";
            }
        }

        return new ActivityEntryResponse
        {
            Id = e.Id,
            OccurredAt = e.OccurredAt.ToString("O"),
            ActionType = e.ActionType,
            CollectionName = collectionName,
            EntityId = e.EntityId?.ToString(),
            EntityType = e.EntityType,
            ProfileId = e.ProfileId?.ToString(),
            ChangesJson = e.ChangesJson,
            Detail = detail,
            IngestionRunId = e.IngestionRunId?.ToString(),
        };
    }

    private static async Task<string?> ResolveActivityPersonNameAsync(
        Guid? personId,
        string? qid,
        IPersonRepository personRepo,
        IQidLabelRepository qidLabelRepo,
        CancellationToken ct)
    {
        if (personId.HasValue)
        {
            var person = await personRepo.FindByIdAsync(personId.Value, ct);
            var personName = ResolveBestPersonName(person?.Name);
            if (!string.IsNullOrWhiteSpace(personName))
            {
                return personName;
            }
        }

        if (!string.IsNullOrWhiteSpace(qid))
        {
            var label = await qidLabelRepo.GetLabelAsync(qid, ct);
            var labelName = ResolveBestPersonName(label);
            if (!string.IsNullOrWhiteSpace(labelName))
            {
                return labelName;
            }
        }

        return null;
    }

    private static bool NeedsPersonNameResolution(string? collectionName, string? detail) =>
        IsPlaceholderPersonName(collectionName)
        || (!string.IsNullOrWhiteSpace(detail)
            && detail.Contains("Unknown Person (", StringComparison.OrdinalIgnoreCase));

    private static string? ResolveBestPersonName(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var trimmed = candidate.Trim();
            if (!IsPlaceholderPersonName(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static bool IsPlaceholderPersonName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("Unknown Person (", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return trimmed.Length > 1
            && trimmed[0] is 'Q'
            && trimmed.Skip(1).All(char.IsDigit);
    }

    private static string? ExtractPersonQid(string? changesJson)
    {
        if (string.IsNullOrWhiteSpace(changesJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(changesJson);
            return doc.RootElement.TryGetProperty("qid", out var qidElement)
                ? NormalizeQid(qidElement.GetString())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractFirstQid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var index = value.IndexOf('Q');
        while (index >= 0 && index < value.Length - 1)
        {
            var end = index + 1;
            while (end < value.Length && char.IsDigit(value[end]))
            {
                end++;
            }

            if (end > index + 1)
            {
                return value[index..end];
            }

            index = value.IndexOf('Q', index + 1);
        }

        return null;
    }

    private static string? NormalizeQid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var qid = value.Trim();
        var slash = qid.LastIndexOf('/');
        if (slash >= 0)
        {
            qid = qid[(slash + 1)..];
        }

        return qid.Length > 1 && qid[0] is 'Q' && qid.Skip(1).All(char.IsDigit)
            ? qid
            : null;
    }
}
