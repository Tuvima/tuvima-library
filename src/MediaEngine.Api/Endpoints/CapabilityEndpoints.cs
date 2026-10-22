using MediaEngine.Api.Security;
using MediaEngine.Contracts.Operations;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Endpoints;

public static class CapabilityEndpoints
{
    public static IEndpointRouteBuilder MapCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/assets/{id:guid}/capabilities", async (
            Guid id,
            IEntityCapabilityStateRepository repository,
            [Microsoft.AspNetCore.Mvc.FromServices] IVaultLibraryRepository vaults,
            CancellationToken ct) =>
        {
            // A Vault asset (or a work with a file in one) reports no capability state, exactly like an id that does not exist.
            if (await vaults.IsInVaultAsync(id, ct))
            {
                return Results.Ok(new List<CapabilityStateDto>());
            }

            var states = await repository.GetByEntityAsync(id, ct);
            return Results.Ok(states.Select(MapCapabilityState).ToList());
        })
        .WithTags("Capabilities")
        .WithName("GetAssetCapabilities")
        .WithSummary("List explicit capability readiness states for a media asset.")
        .Produces<IReadOnlyList<CapabilityStateDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRead);

        app.MapGet("/capabilities/summary", async (
            IEntityCapabilityStateRepository repository,
            CancellationToken ct) =>
        {
            var summary = await repository.GetSummaryAsync(ct);
            return Results.Ok(summary);
        })
        .WithTags("Capabilities")
        .WithName("GetCapabilitySummary")
        .WithSummary("Return counts by capability/status.")
        .Produces<IReadOnlyDictionary<string, int>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRead);

        return app;
    }

    internal static CapabilityStateDto MapCapabilityState(EntityCapabilityState state) => new()
    {
        Id = state.Id,
        EntityId = state.EntityId,
        EntityKind = state.EntityKind,
        MediaType = state.MediaType,
        CapabilityId = state.CapabilityId,
        CapabilityKind = state.CapabilityKind,
        CapabilityVersion = state.CapabilityVersion,
        SubKey = state.SubKey,
        Status = state.Status,
        Requiredness = state.Requiredness,
        Source = state.Source,
        Confidence = state.Confidence,
        ArtifactCount = state.ArtifactCount,
        ArtifactSummary = state.ArtifactSummary,
        ResultSummary = state.ResultSummary,
        LastOperationId = state.LastOperationId,
        FirstAttemptedAt = state.FirstAttemptedAt,
        LastAttemptedAt = state.LastAttemptedAt,
        SucceededAt = state.SucceededAt,
        NextRetryAt = state.NextRetryAt,
        Stale = state.Stale,
        NeedsRerun = state.NeedsRerun,
        MissingReason = state.MissingReason,
        LastError = state.LastError,
        CreatedAt = state.CreatedAt,
        UpdatedAt = state.UpdatedAt
    };
}
