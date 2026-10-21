using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Endpoints;

internal static class UniverseLoreEndpoints
{
    private const string UniverseNotInLibrary = "That Universe is not in this Library.";

    internal static RouteGroupBuilder MapUniverseLoreEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/universe/{qid}")
            .WithTags("Universe Lore");

        group.MapGet("/lore-sources", async (
            string qid,
            PluginUniverseLoreService lore,
            CancellationToken ct) =>
        {
            var sources = await lore.GetSourcesAsync(qid, ct).ConfigureAwait(false);
            return Results.Ok(sources.Select(ToDto).ToList());
        })
        .WithName("GetUniverseLoreSources")
        .Produces<List<UniverseLoreSourceResponse>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsRead);

        group.MapPost("/lore-sources/discover", async (
            string qid,
            PluginUniverseLoreService lore,
            CancellationToken ct) =>
        {
            var sources = await lore.DiscoverSourcesAsync(qid, ct).ConfigureAwait(false);
            return sources is null
                ? ApiErrors.NotFound(UniverseNotInLibrary)
                : Results.Ok(sources.Select(ToDto).ToList());
        })
        .WithName("DiscoverUniverseLoreSources")
        .Produces<List<UniverseLoreSourceResponse>>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .RequireEffectiveAdministrator();

        group.MapPost("/lore-sources/manual", async (
            string qid,
            UniverseLoreManualSourceRequest request,
            PluginUniverseLoreService lore,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.BaseUrl))
            {
                return ApiErrors.BadRequest("A base URL is required.");
            }

            try
            {
                var source = await lore.AddManualSourceAsync(
                    qid,
                    request.SourceName ?? string.Empty,
                    request.BaseUrl,
                    request.ApiUrl,
                    ct).ConfigureAwait(false);
                return source is null
                    ? ApiErrors.NotFound(UniverseNotInLibrary)
                    : Results.Ok(ToDto(source));
            }
            catch (UriFormatException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("AddManualUniverseLoreSource")
        .Produces<UniverseLoreSourceResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsManage);

        group.MapPost("/lore-sources/{sourceId:guid}/approve", async (
            string qid,
            Guid sourceId,
            PluginUniverseLoreService lore,
            CancellationToken ct) =>
        {
            try
            {
                var sources = await lore.SetSourceStatusAsync(
                    qid,
                    sourceId,
                    PluginLoreSourceStatus.Approved,
                    actor: "admin",
                    ct).ConfigureAwait(false);
                return Results.Ok(sources.Select(ToDto).ToList());
            }
            catch (InvalidOperationException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("ApproveUniverseLoreSource")
        .Produces<List<UniverseLoreSourceResponse>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsManage);

        group.MapPost("/lore-sources/{sourceId:guid}/reject", async (
            string qid,
            Guid sourceId,
            PluginUniverseLoreService lore,
            CancellationToken ct) =>
        {
            try
            {
                var sources = await lore.SetSourceStatusAsync(
                    qid,
                    sourceId,
                    PluginLoreSourceStatus.Rejected,
                    actor: "admin",
                    ct).ConfigureAwait(false);
                return Results.Ok(sources.Select(ToDto).ToList());
            }
            catch (InvalidOperationException ex)
            {
                return ApiErrors.BadRequest(ex.Message);
            }
        })
        .WithName("RejectUniverseLoreSource")
        .Produces<List<UniverseLoreSourceResponse>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsManage);

        group.MapPost("/lore/enrich", async (
            string qid,
            PluginUniverseLoreService lore,
            CancellationToken ct) =>
        {
            var summary = await lore.EnrichUniverseAsync(qid, ct).ConfigureAwait(false);
            if (summary is null)
            {
                return ApiErrors.NotFound(UniverseNotInLibrary);
            }

            return Results.Ok(new UniverseLoreEnrichResponse(
                universe_qid: summary.UniverseQid,
                sources_enriched: summary.SourcesEnriched,
                entities_written: summary.EntitiesWritten,
                relationships_written: summary.RelationshipsWritten));
        })
        .WithName("EnrichUniverseLoreSources")
        .Produces<UniverseLoreEnrichResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.PluginsJobsRun);

        return group;
    }

    private static UniverseLoreSourceResponse ToDto(PluginLoreSourceRecord source) => new(
        id: source.Id,
        universe_qid: source.UniverseQid,
        plugin_id: source.PluginId,
        source_key: source.SourceKey,
        source_name: source.SourceName,
        base_url: source.BaseUrl,
        api_url: source.ApiUrl,
        status: source.Status,
        confidence: source.Confidence,
        license: source.License,
        approved_at: source.ApprovedAt,
        approved_by: source.ApprovedBy,
        rejected_at: source.RejectedAt,
        last_discovered_at: source.LastDiscoveredAt,
        last_enriched_at: source.LastEnrichedAt,
        created_at: source.CreatedAt,
        updated_at: source.UpdatedAt);
}
