using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.Collections.Packs;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Packs (Collections and lists spec D14 and 4e): the starter packs with what each would create from this library, the
/// create call, the pack file preview and export, and removing a set. A set holds no titles of its own, so the
/// catalogue entity check (which clears a request by the files behind an entity) has nothing to look at; the service
/// checks the caller may edit the set instead. Mapped onto the <c>/collections</c> group by
/// <see cref="CollectionEndpoints.MapCollectionEndpoints"/>.
/// </summary>
internal static class CollectionPackEndpoints
{
    public static RouteGroupBuilder MapCollectionPackEndpoints(this RouteGroupBuilder group)
    {
        // GET /collections/packs — the starter packs, each with the collections the library supports and their counts.
        group.MapGet("/packs", async (
            string? media_types,
            CollectionPackService packs,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var viewer = await CollectionEndpoints.ResolveViewerProfileIdAsync(httpContext, ct);
            var types = (media_types ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Results.Ok(await packs.ListAsync(types, viewer, ct));
        })
        .WithName("ListCollectionPacks")
        .WithSummary("List the starter packs and what each would create from the library.")
        .Produces<CollectionPackListResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrAdultProfile(ApplicationPermissionIds.CollectionsWrite);

        // POST /collections/packs/preview-file — what a pack file would create here.
        group.MapPost("/packs/preview-file", async (
            CollectionPackFilePreviewRequest body,
            CollectionPackService packs,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            try
            {
                var viewer = await CollectionEndpoints.ResolveViewerProfileIdAsync(httpContext, ct);
                return Results.Ok(await packs.PreviewAsync(body.Content, viewer, ct));
            }
            catch (CollectionPackException ex)
            {
                return ToResult(ex);
            }
        })
        .WithName("PreviewCollectionPackFile")
        .WithSummary("Read a pack file and show the collections it would create.")
        .Produces<CollectionPackSummaryDto>(StatusCodes.Status200OK)
        .RequireAdministratorOrAdultProfile(ApplicationPermissionIds.CollectionsWrite);

        // POST /collections/packs — create the ticked packs (and the pack file, if one is sent).
        group.MapPost("/packs", async (
            CollectionPackCreateRequest body,
            CollectionPackService packs,
            IProfileRepository profileRepo,
            Guid? profileId,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            try
            {
                var caller = await CollectionCaller.ResolveAsync(httpContext, profileId, profileRepo, ct);
                var viewer = await CollectionEndpoints.ResolveViewerProfileIdAsync(httpContext, ct);
                return Results.Ok(await packs.CreateAsync(body, caller.Profile, viewer, ct, caller.IsAdministrator));
            }
            catch (CollectionPackException ex)
            {
                return ToResult(ex);
            }
        })
        .WithName("CreateCollectionPacks")
        .WithSummary("Create the collections of the chosen packs, under a set where a pack makes several.")
        .Produces<CollectionPackCreateResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrAdultProfile(ApplicationPermissionIds.CollectionsWrite);

        // GET /collections/{id}/pack-file — a set written out as a pack file.
        group.MapGet("/{id:guid}/pack-file", async (
            Guid id,
            CollectionPackService packs,
            IProfileRepository profileRepo,
            Guid? profileId,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            try
            {
                var caller = await CollectionCaller.ResolveAsync(httpContext, profileId, profileRepo, ct);
                return Results.Ok(await packs.ExportSetAsync(id, caller.Profile, ct, caller.IsAdministrator));
            }
            catch (CollectionPackException ex)
            {
                return ToResult(ex);
            }
        })
        .WithName("ExportCollectionSetAsPackFile")
        .WithSummary("Write a set out as a pack file that can be imported on another Library.")
        .Produces<CollectionPackExportResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrAdultProfile(ApplicationPermissionIds.CollectionsWrite);

        // DELETE /collections/{id}/set — remove a set with its collections and where they were shown.
        group.MapDelete("/{id:guid}/set", async (
            Guid id,
            CollectionPackService packs,
            IProfileRepository profileRepo,
            Guid? profileId,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            try
            {
                var caller = await CollectionCaller.ResolveAsync(httpContext, profileId, profileRepo, ct);
                return Results.Ok(await packs.RemoveSetAsync(id, caller.Profile, ct, caller.IsAdministrator));
            }
            catch (CollectionPackException ex)
            {
                return ToResult(ex);
            }
        })
        .WithName("RemoveCollectionSet")
        .WithSummary("Remove a set: its collections and their lane placements go with it.")
        .Produces<CollectionSetRemovedResponse>(StatusCodes.Status200OK)
        .RequireAdministratorOrAdultProfile(ApplicationPermissionIds.CollectionsWrite);

        return group;
    }

    private static IResult ToResult(CollectionPackException ex) => ex.Failure switch
    {
        CollectionPackFailure.NotFound => ApiErrors.NotFound(ex.Message),
        CollectionPackFailure.Forbidden => ApiErrors.Forbidden(ex.Message),
        _ => ApiErrors.BadRequest(ex.Message),
    };
}
