using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Contracts.Operations;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Endpoints;

public static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/operations")
            .WithTags("Operations");

        group.MapGet("/", async (
            string? queueName,
            int? limit,
            IMediaOperationRepository repository,
            VaultOperationGate vault,
            CancellationToken ct) =>
        {
            var paged = PagedRequest.From(null, limit, defaultLimit: 200);
            var operations = await vault.VisibleAsync(await repository.GetQueueAsync(queueName, paged.Limit, ct), ct);
            return Results.Ok(operations.Select((op, index) => MapOperation(op, index + 1)).ToList());
        })
        .WithName("ListMediaOperations")
        .WithSummary("List durable media operations by queue order.")
        .Produces<IReadOnlyList<OperationDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionStatusRead);

        group.MapGet("/{id:guid}", async (
            Guid id,
            IMediaOperationRepository repository,
            IMediaOperationEventRepository events,
            VaultOperationGate vault,
            CancellationToken ct) =>
        {
            var operation = await repository.GetByIdAsync(id, ct);
            if (operation is null || await vault.HidesAsync(operation, ct))
            {
                return ApiErrors.NotFound($"Media operation '{id}' not found.");
            }

            var timeline = await events.GetByOperationAsync(id, ct);
            return Results.Ok(new OperationDetailDto
            {
                Operation = MapOperation(operation, null),
                Events = timeline.Select(MapOperationEvent).ToList(),
            });
        })
        .WithName("GetMediaOperation")
        .WithSummary("Get one durable media operation and its event timeline.")
        .Produces<OperationDetailDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionHistoryRead);

        group.MapGet("/summary", async (
            IMediaOperationRepository repository,
            CancellationToken ct) =>
        {
            var summary = await repository.GetSummaryAsync(ct);
            return Results.Ok(summary);
        })
        .WithName("GetMediaOperationsSummary")
        .WithSummary("Get media operation counts by status.")
        .Produces<IReadOnlyDictionary<string, int>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionStatusRead);

        group.MapPost("/{id:guid}/retry", async (
            Guid id,
            IMediaOperationRepository repository,
            VaultOperationGate vault,
            CancellationToken ct) =>
        {
            if (await repository.GetByIdAsync(id, ct) is not { } operation || await vault.HidesAsync(operation, ct))
            {
                return ApiErrors.NotFound($"Media operation '{id}' not found.");
            }

            await repository.RequeueAsync(id, ct);
            return Results.Accepted($"/operations/{id}");
        })
        .WithName("RetryMediaOperation")
        .WithSummary("Requeue a durable media operation for another attempt.")
        .Produces(StatusCodes.Status202Accepted)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionRetry);

        group.MapPost("/{id:guid}/cancel", async (
            Guid id,
            IMediaOperationRepository repository,
            VaultOperationGate vault,
            CancellationToken ct) =>
        {
            if (await repository.GetByIdAsync(id, ct) is not { } operation || await vault.HidesAsync(operation, ct))
            {
                return ApiErrors.NotFound($"Media operation '{id}' not found.");
            }

            await repository.MarkCancelledAsync(id, "Cancelled by user.", ct);
            return Results.Accepted($"/operations/{id}");
        })
        .WithName("CancelMediaOperation")
        .WithSummary("Cancel a durable media operation.")
        .Produces(StatusCodes.Status202Accepted)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.IngestionCancel);

        return app;
    }

    internal static OperationDto MapOperation(MediaOperation operation, int? queuePosition) => new()
    {
        Id = operation.Id,
        OperationType = operation.OperationType,
        OperationKind = operation.OperationKind,
        EntityId = operation.EntityId,
        EntityKind = operation.EntityKind,
        SourcePath = operation.SourcePath,
        CapabilityId = operation.CapabilityId,
        CapabilityVersion = operation.CapabilityVersion,
        SubKey = operation.SubKey,
        PluginId = operation.PluginId,
        PluginVersion = operation.PluginVersion,
        ProviderId = operation.ProviderId,
        ModelId = operation.ModelId,
        Status = operation.Status,
        Stage = operation.Stage,
        Priority = operation.Priority,
        QueueName = operation.QueueName,
        QueuePosition = queuePosition,
        AttemptCount = operation.AttemptCount,
        LeaseOwner = operation.LeaseOwner,
        LeaseExpiresAt = operation.LeaseExpiresAt,
        HeartbeatAt = operation.HeartbeatAt,
        NextRetryAt = operation.NextRetryAt,
        ProgressPercent = operation.ProgressPercent,
        ItemsTotal = operation.ItemsTotal,
        ItemsCompleted = operation.ItemsCompleted,
        ItemsFailed = operation.ItemsFailed,
        ResultSummary = operation.ResultSummary,
        LastError = operation.LastError,
        MissingReason = operation.MissingReason,
        CreatedAt = operation.CreatedAt,
        UpdatedAt = operation.UpdatedAt,
        CompletedAt = operation.CompletedAt
    };
    internal static OperationEventDto MapOperationEvent(MediaOperationEvent evt) => new()
    {
        Id = evt.Id,
        OperationId = evt.OperationId,
        EntityId = evt.EntityId,
        EventType = evt.EventType,
        OldStatus = evt.OldStatus,
        NewStatus = evt.NewStatus,
        OldStage = evt.OldStage,
        NewStage = evt.NewStage,
        Message = evt.Message,
        DetailJson = evt.DetailJson,
        OccurredAt = evt.OccurredAt
    };
}
