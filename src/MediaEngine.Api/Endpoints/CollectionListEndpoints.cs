using System.Text;
using MediaEngine.Api.Http;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.Collections.ListImport;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Import;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Imported lists on collections (Collections and lists spec D5, D6, 4b "From a list", 4d, 4h): reading a list file for a
/// preview, attaching it to a collection, changing how it is followed, refreshing it and fixing a title it could not match.
/// Files are read in memory and never leave this machine.
/// </summary>
public static class CollectionListEndpoints
{
    public static RouteGroupBuilder MapCollectionListEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/preview-list", async (
            ListImportPreviewRequest body,
            [FromServices] ListImportService service,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            if (!ListUpload.TryDecode(body.FileName, body.ContentBase64, body.Text, out var bytes, out var error))
            {
                return ApiErrors.BadRequest(error!);
            }

            try
            {
                var parsed = ListFileParser.Parse(body.FileName, bytes!);
                var allowed = await display.LoadVisibleWorkIdsAsync(ct);
                return Results.Ok(await service.PreviewAsync(body.FileName, parsed, body.Shelf, body.Overrides, allowed, ct));
            }
            catch (ListFileNotUnderstoodException ex)
            {
                return ApiErrors.Unprocessable(ex.Message);
            }
        })
        .WithName("PreviewCollectionList")
        .WithSummary("Reads a list file and says how much of it the Library owns. Nothing is saved.")
        .Produces<ListImportPreviewDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite);

        group.MapPost("/{id:guid}/list", async (
            Guid id,
            ListImportRequest body,
            ICollectionRepository collections,
            IProfileRepository profiles,
            HttpContext http,
            [FromServices] ListImportService service,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var collection = await collections.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            var profile = await ResolveActiveProfileAsync(body.ProfileId, profiles, http, ct);
            if (!CollectionAccessPolicy.CanEdit(collection, profile, hasCollectionsWrite: true))
            {
                return ApiErrors.Forbidden("The active profile cannot edit this collection.");
            }

            if (!ListUpload.TryDecode(body.FileName, body.ContentBase64, body.Text, out var bytes, out var error))
            {
                return ApiErrors.BadRequest(error!);
            }

            try
            {
                var parsed = ListFileParser.Parse(body.FileName, bytes!);
                var allowed = await display.LoadVisibleWorkIdsAsync(ct);
                var result = await service.ImportAsync(
                    id, body.FileName, parsed, body.Shelf, body.Order, body.Refresh, body.SyncMode, body.Overrides, allowed, ct);
                CollectionLanePolicy.MarkChanged();
                return Results.Ok(result);
            }
            catch (ListFileNotUnderstoodException ex)
            {
                return ApiErrors.Unprocessable(ex.Message);
            }
        })
        .WithName("ImportCollectionList")
        .WithSummary("Makes an imported list the source of a collection and fills the collection with the titles Library owns.")
        .Produces<ListImportResultDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        group.MapGet("/{id:guid}/list", async (
            Guid id,
            ICollectionRepository collections,
            IProfileRepository profiles,
            HttpContext http,
            [FromServices] ListImportService service,
            Guid? profileId,
            CancellationToken ct) =>
        {
            var collection = await collections.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            var profile = await ResolveActiveProfileAsync(profileId, profiles, http, ct);
            if (!CollectionAccessPolicy.CanEdit(collection, profile, hasCollectionsWrite: true))
            {
                return ApiErrors.Forbidden("The active profile cannot edit this collection.");
            }

            var source = await service.GetSourceAsync(id, ct);
            return source is null
                ? ApiErrors.NotFound("This collection was not made from a list.")
                : Results.Ok(source);
        })
        .WithName("GetCollectionListSource")
        .WithSummary("Returns where a collection's list came from, how it is followed and the titles that could not be matched.")
        .Produces<CollectionListSourceDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        group.MapPut("/{id:guid}/list", async (
            Guid id,
            ListSourceSettingsRequest body,
            ICollectionRepository collections,
            IProfileRepository profiles,
            HttpContext http,
            [FromServices] ListImportService service,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var collection = await collections.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            var profile = await ResolveActiveProfileAsync(body.ProfileId, profiles, http, ct);
            if (!CollectionAccessPolicy.CanEdit(collection, profile, hasCollectionsWrite: true))
            {
                return ApiErrors.Forbidden("The active profile cannot edit this collection.");
            }

            var allowed = await display.LoadVisibleWorkIdsAsync(ct);
            if (!await service.UpdateSettingsAsync(id, body.Order, body.Refresh, body.SyncMode, allowed, ct))
            {
                return ApiErrors.NotFound("This collection was not made from a list.");
            }

            CollectionLanePolicy.MarkChanged();
            return Results.Ok(await service.GetSourceAsync(id, ct));
        })
        .WithName("UpdateCollectionListSettings")
        .WithSummary("Changes the order, refresh and 'when a title leaves the list' choices of an imported list. Returns the list as it is now.")
        .Produces<CollectionListSourceDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        group.MapPost("/{id:guid}/list/refresh", async (
            Guid id,
            ListRefreshRequest body,
            ICollectionRepository collections,
            IProfileRepository profiles,
            HttpContext http,
            [FromServices] ListImportService service,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var collection = await collections.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            var profile = await ResolveActiveProfileAsync(body.ProfileId, profiles, http, ct);
            if (!CollectionAccessPolicy.CanEdit(collection, profile, hasCollectionsWrite: true))
            {
                return ApiErrors.Forbidden("The active profile cannot edit this collection.");
            }

            ParsedList? fresh = null;
            if (!string.IsNullOrEmpty(body.ContentBase64) || !string.IsNullOrEmpty(body.Text))
            {
                if (!ListUpload.TryDecode(body.FileName ?? string.Empty, body.ContentBase64, body.Text, out var bytes, out var error))
                {
                    return ApiErrors.BadRequest(error!);
                }

                try
                {
                    fresh = ListFileParser.Parse(body.FileName ?? string.Empty, bytes!);
                }
                catch (ListFileNotUnderstoodException ex)
                {
                    return ApiErrors.Unprocessable(ex.Message);
                }
            }

            var allowed = await display.LoadVisibleWorkIdsAsync(ct);
            var result = await service.RefreshAsync(id, fresh, allowed, ct);
            if (result is null)
            {
                return ApiErrors.NotFound("This collection was not made from a list.");
            }

            CollectionLanePolicy.MarkChanged();
            return Results.Ok(result);
        })
        .WithName("RefreshCollectionList")
        .WithSummary("Refreshes an imported list: with a newer file its entries are replaced, without one they are matched against the library again.")
        .Produces<ListRefreshResultDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        group.MapPut("/{id:guid}/list/entries/{entryId:guid}", async (
            Guid id,
            Guid entryId,
            ListEntryFixRequest body,
            ICollectionRepository collections,
            IProfileRepository profiles,
            HttpContext http,
            [FromServices] ListImportService service,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var collection = await collections.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            var profile = await ResolveActiveProfileAsync(body.ProfileId, profiles, http, ct);
            if (!CollectionAccessPolicy.CanEdit(collection, profile, hasCollectionsWrite: true))
            {
                return ApiErrors.Forbidden("The active profile cannot edit this collection.");
            }

            var action = (body.Action ?? string.Empty).Trim().ToLowerInvariant();
            if (action is not ("pick" or "ignore" or "restore"))
            {
                return ApiErrors.BadRequest("action must be pick, ignore or restore.");
            }

            if (action == "pick" && (body.WorkId is null || body.WorkId == Guid.Empty))
            {
                return ApiErrors.BadRequest("work_id is required to pick a title.");
            }

            var allowed = await display.LoadVisibleWorkIdsAsync(ct);
            if (!await service.FixEntryAsync(id, entryId, action, body.WorkId, allowed, ct))
            {
                return ApiErrors.NotFound("That list entry was not found, or the title is not one the Library owns.");
            }

            CollectionLanePolicy.MarkChanged();
            return Results.Ok(await service.GetSourceAsync(id, ct));
        })
        .WithName("FixCollectionListEntry")
        .WithSummary("Picks the right title for a list entry, ignores it, or puts it back to automatic matching. Returns the list as it is now.")
        .Produces<CollectionListSourceDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        return group;
    }

    private static async Task<Profile?> ResolveActiveProfileAsync(
        Guid? profileId, IProfileRepository profileRepo, HttpContext httpContext, CancellationToken ct)
    {
        var authority = await httpContext.RequestServices
            .GetRequiredService<IRequestAuthorityResolver>()
            .ResolveAsync(httpContext, ct);
        if (authority.ActiveProfileId is not { } activeProfileId
            || (profileId.HasValue && profileId.Value != activeProfileId))
        {
            return null;
        }

        return await profileRepo.GetByIdAsync(activeProfileId, ct);
    }
}

/// <summary>Turns the file or pasted text of a request into bytes, within the Import size limits.</summary>
internal static class ListUpload
{
    public static bool TryDecode(string fileName, string? contentBase64, string? text, out byte[]? bytes, out string? error)
    {
        bytes = null;
        error = null;
        if (!string.IsNullOrEmpty(contentBase64))
        {
            // Base64 is about a third larger than the bytes it holds.
            if (contentBase64.Length > (ImportLimits.MaximumFileBytes / 3 + 1) * 4)
            {
                error = $"The file is larger than {ImportLimits.MaximumFileBytes / (1024 * 1024)} MB.";
                return false;
            }

            try
            {
                bytes = Convert.FromBase64String(contentBase64);
            }
            catch (FormatException)
            {
                error = "The file content is not valid base64.";
                return false;
            }

            if (bytes.Length > ImportLimits.MaximumFileBytes)
            {
                bytes = null;
                error = $"The file is larger than {ImportLimits.MaximumFileBytes / (1024 * 1024)} MB.";
                return false;
            }

            return true;
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            if (text.Length > ImportLimits.MaximumPastedCharacters)
            {
                error = "The pasted text is too long.";
                return false;
            }

            bytes = Encoding.UTF8.GetBytes(text);
            return true;
        }

        error = "Provide a file or some pasted text.";
        return false;
    }
}
