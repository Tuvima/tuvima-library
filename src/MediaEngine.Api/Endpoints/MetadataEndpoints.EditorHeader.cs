using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    /// <summary>
    /// The editor's v5 header reads (universal editor v5, section 5 "Engine reads"): one more page of the item dropdown, the Match tab's
    /// current match found by its stored id, "Suggest details" for a supporting file's document, and the folder button's tree. The first
    /// view of the header itself rides on the editor-context read. All four are reads: nothing is written until Save.
    /// </summary>
    private static void MapEditorHeaderEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{entityId:guid}/editor-nav/level", async (
            Guid entityId, Guid? level_id, string? q, int? offset, HttpContext http, EditorHeaderReadService header, CancellationToken ct) =>
        {
            var level = await header.GetLevelAsync(
                entityId, level_id, q, PagedRequest.From(offset, null).Offset, await CollectionEndpoints.ResolveViewerProfileIdAsync(http, ct), ct);
            return level is null ? ApiErrors.NotFound("That list was not found.") : Results.Ok(level);
        }).WithName("GetEditorNavLevel")
          .WithSummary("One searchable, paged list of the editor's item dropdown: a season, a section or the whole title.")
          .Produces<EditorHeaderLevelDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapGet("/{entityId:guid}/current-match", async (Guid entityId, EditorCurrentMatchService match, CancellationToken ct) =>
        {
            var current = await match.ReadAsync(entityId, ct);
            return current is null ? ApiErrors.NotFound("Current match was not found.") : Results.Ok(current);
        }).WithName("GetEditorCurrentMatch")
          .WithSummary("The record an item is matched to, found by its stored provider id (never a text search), with the reasons it fits.")
          .Produces<EditorCurrentMatchDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapGet("/{entityId:guid}/supporting-files/{supportingFileId:guid}/suggest-details", async (
            Guid entityId, Guid supportingFileId, IItemExtraRepository extras, EditorHeaderReadService header,
            EditorSuggestDetailsService suggest, CancellationToken ct) =>
        {
            // Only a supporting file of this item's own folder: the route never reads another title's file.
            var extra = await extras.GetAsync(supportingFileId, ct);
            var itemFolder = await header.GetItemFolderAsync(entityId, ct);
            if (extra is null || itemFolder is null
                || !string.Equals(Path.GetFullPath(extra.ItemFolder), Path.GetFullPath(itemFolder), StringComparison.OrdinalIgnoreCase))
            {
                return ApiErrors.NotFound("That supporting file was not found.");
            }

            var suggestions = await suggest.ReadAsync(supportingFileId, ct);
            return suggestions is null ? ApiErrors.NotFound("That supporting file was not found.") : Results.Ok(suggestions);
        }).WithName("GetSupportingFileSuggestDetails")
          .WithSummary("What a supporting file's document says about itself (title, subject, author, first lines). Local only: no provider, no AI.")
          .Produces<EditorSuggestDetailsDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapGet("/{entityId:guid}/folder-tree", async (
            Guid entityId, string? folder, int? offset, EditorFolderTreeService tree, CancellationToken ct) =>
        {
            var page = await tree.ReadAsync(entityId, folder, PagedRequest.From(offset, null).Offset, ct);
            return page is null ? ApiErrors.NotFound("That folder was not found.") : Results.Ok(page);
        }).WithName("GetEditorFolderTree")
          .WithSummary("One page of one folder of the title's folder tree, each file marked main, supporting, hidden or unsorted.")
          .Produces<EditorFolderTreeDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);
    }
}
