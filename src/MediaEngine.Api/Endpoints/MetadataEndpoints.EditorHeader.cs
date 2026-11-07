using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Extras;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    /// <summary>
    /// The editor's v5 header reads (universal editor v5, section 5 "Engine reads"): one more page of the item dropdown, the Match tab's
    /// current match found by its stored id, the supporting-file editor's read, "Suggest details" for a supporting file's document, the folder button's tree, and the rules of
    /// one kind of item. The first view of the header itself rides on the editor-context read. All six are reads: nothing is written until Save.
    /// </summary>
    private static void MapEditorHeaderEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{entityId:guid}/editor-nav/level", async (
            Guid entityId, Guid? level_id, string? q, int? offset, Guid? supporting_file_id, HttpContext http, EditorHeaderReadService header,
            CancellationToken ct) =>
        {
            var level = await header.GetLevelAsync(
                entityId, level_id, q, PagedRequest.From(offset, null).Offset, await CollectionEndpoints.ResolveViewerProfileIdAsync(http, ct), ct,
                supporting_file_id);
            return level is null ? ApiErrors.NotFound("That list was not found.") : Results.Ok(level);
        }).WithName("GetEditorNavLevel")
          .WithSummary("One searchable, paged list of the editor's item dropdown: a season, a section or the whole title.")
          .Produces<EditorHeaderLevelDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        // The rules of one kind of item, for the editors that do not open on a work (a person, a Universe, a supporting file, a View photo or
        // gallery, several items at once). The same numbers and per-kind rules the header carries; the Dashboard holds none of them.
        group.MapGet("/editor-config", (string? kind, EditorHeaderReadService header) =>
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                return ApiErrors.BadRequest("Say which kind of item the editor rules are for.");
            }

            var config = header.GetKindConfig(kind.Trim());
            return config is null ? ApiErrors.NotFound("That kind of item is not known.") : Results.Ok(config);
        }).WithName("GetEditorKindConfig")
          .WithSummary("The editor's numbers and the tabs, facts and search fields of one kind of item (person, Universe, supporting file, View photo or gallery, several items).")
          .Produces<EditorHeaderConfigDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status400BadRequest)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead);

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

        group.MapGet("/{entityId:guid}/match-setup", async (
            Guid entityId, EditorMatchSearchService match, CancellationToken ct) =>
        {
            var setup = await match.SetupAsync(entityId, ct);
            return setup is null ? ApiErrors.NotFound("Match setup was not found.") : Results.Ok(setup);
        }).WithName("GetEditorMatchSetup")
          .WithSummary("What the Match tab's search opens on: the providers for the item's kind with their states, the boxes pre-filled from the file, and the local evidence. Makes no search.")
          .Produces<EditorMatchSetupDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapPost("/{entityId:guid}/match-placement", async (
            Guid entityId,
            EditorMatchPlacementRequestDto request,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            IProfileRepository profiles,
            [Microsoft.AspNetCore.Mvc.FromServices] EditorMatchPlacementService placement,
            CatalogueResourceAuthorizationService resources,
            CancellationToken ct) =>
        {
            var actor = await LibraryItemEndpoints.ResolveMoveActorAsync(http, resolver, profiles, ct);
            if (actor is null)
            {
                return ApiErrors.NotFound("The editing session is unavailable.");
            }

            var plan = await placement.PlanAsync(entityId, request.Candidate, actor, LibraryItemEndpoints.CanEdit(http, resources), ct);
            return plan is null ? ApiErrors.NotFound("Match placement was not found.") : Results.Ok(plan);
        }).WithName("GetEditorMatchPlacement")
          .WithSummary("Where applying a chosen match would put the item and its file: the album, season or series it names, and whether the file moves (Managed folder) or stays (Read-only). Reads only.")
          .Produces<EditorMatchPlacementDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);

        group.MapPost("/{entityId:guid}/match-search", async (
            Guid entityId, EditorMatchSearchRequestDto request, EditorMatchSearchService match, CancellationToken ct) =>
        {
            var found = await match.SearchAsync(entityId, request, ct);
            return found is null ? ApiErrors.NotFound("Match search was not found.") : Results.Ok(found);
        }).WithName("SearchEditorMatch")
          .WithSummary("Searches one provider for the item and judges each result against the file (Strong, Possible or Check). Writes nothing.")
          .Produces<EditorMatchSearchResultDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);

        // Everything the supporting-file editor needs to open one file: the file as set and as read from the folders, the lists the form
        // chooses from, and the owner's header with this file marked current. A read; the form's changes are staged and saved as a change.
        group.MapGet("/{entityId:guid}/supporting-files/{supportingFileId:guid}/editor", async (
            Guid entityId, Guid supportingFileId, HttpContext http, SupportingFileEditorReadService editor, CancellationToken ct) =>
        {
            var read = await editor.GetAsync(entityId, supportingFileId, await CollectionEndpoints.ResolveViewerProfileIdAsync(http, ct), ct);
            return read is null ? ApiErrors.NotFound("That supporting file was not found.") : Results.Ok(read);
        }).WithName("GetSupportingFileEditor")
          .WithSummary("One supporting file for the editor: its values, the roles, groups and places the form offers, and the owner's header.")
          .Produces<EditorSupportingFileDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapGet("/{entityId:guid}/supporting-files/{supportingFileId:guid}/suggest-details", async (
            Guid entityId, Guid supportingFileId, EditorHeaderReadService header, EditorSuggestDetailsService suggest, CancellationToken ct) =>
        {
            // Only a supporting file of this item's own folder: the route never reads another title's file (a book's neighbour in its Series included).
            if (!await header.OwnsSupportingFileAsync(entityId, supportingFileId, ct))
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
