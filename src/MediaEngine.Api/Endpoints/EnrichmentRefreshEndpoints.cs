using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Contracts.Operations;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Endpoints;

public static class EnrichmentRefreshEndpoints
{
    public static IEndpointRouteBuilder MapEnrichmentRefreshEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/ingestion/refresh-schedule")
            .WithTags("Ingestion");

        group.MapGet("/", async (
            string? entityType,
            string? status,
            int? limit,
            EnrichmentRefreshScheduleService schedule,
            CancellationToken ct) => Results.Ok(await schedule.GetAsync(
                entityType,
                status,
                PagedRequest.From(null, limit, defaultLimit: 250, maxLimit: 1000).Limit,
                ct)))
            .WithName("GetEnrichmentRefreshSchedule")
            .WithSummary("Upcoming and active recurring enrichment refreshes.")
            .Produces<EnrichmentRefreshScheduleResponse>()
            .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRead);

        group.MapGet("/{entityType}/{entityId:guid}", async (
            string entityType,
            Guid entityId,
            EnrichmentRefreshScheduleService schedule,
            CancellationToken ct) =>
        {
            var item = await schedule.GetForEntityAsync(entityType, entityId, ct);
            return item is null
                ? ApiErrors.NotFound($"Refresh target '{entityType}/{entityId}' was not found or does not support scheduled enrichment.")
                : Results.Ok(item);
        })
        .WithName("GetEntityEnrichmentRefreshSchedule")
        .WithSummary("Returns one enrichment schedule without synchronizing the whole library.")
        .Produces<EnrichmentRefreshScheduleDto>()
        .Produces(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRead);

        group.MapPost("/{entityType}/{entityId:guid}/run-now", async (
            string entityType,
            Guid entityId,
            EnrichmentRefreshScheduleService schedule,
            IExternalLookupGuard lookupGuard,
            CancellationToken ct) =>
        {
            if (!entityType.Equals("Person", StringComparison.OrdinalIgnoreCase) && await lookupGuard.BlocksEntityAsync(entityId, ct))
            {
                return ApiErrors.Conflict("This item's library never looks anything up, so it is not refreshed from outside.");
            }

            var queued = await schedule.QueueNowAsync(entityType, entityId, "Manual", ct);
            return queued is null
                ? ApiErrors.NotFound($"Refresh target '{entityType}/{entityId}' was not found.")
                : Results.Accepted(value: queued);
        })
        .WithName("RunEnrichmentRefreshNow")
        .WithSummary("Queue an entity for the full enrichment cycle now.")
        .Produces<EnrichmentRefreshQueuedResponse>(StatusCodes.Status202Accepted)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRun);

        return app;
    }
}
