using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    /// <summary>
    /// The Match tab's Wikidata side (universal editor v5, 3c "Wikidata"): what the item is linked to now, a search or pasted link with
    /// text-only results and alignment evidence, and the check that the provider match and the Wikidata item agree. All three are reads:
    /// nothing is written until Save changes, and the link itself is saved by the edit session (<c>wikidata_link</c> and <c>wikidata_removal</c>).
    /// </summary>
    private static void MapEditorWikidataEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{entityId:guid}/current-wikidata", async (Guid entityId, EditorWikidataMatchService wikidata, CancellationToken ct) =>
        {
            var identity = await wikidata.ReadIdentityAsync(entityId, ct);
            return identity is null ? ApiErrors.NotFound("Wikidata identity was not found.") : Results.Ok(identity);
        }).WithName("GetEditorCurrentWikidata")
          .WithSummary("The Wikidata item an item is linked to now, found by its stored QID (never a text search). Reads only.")
          .Produces<EditorWikidataIdentityDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapPost("/{entityId:guid}/wikidata-search", async (
            Guid entityId, EditorWikidataSearchRequestDto request, EditorWikidataMatchService wikidata, CancellationToken ct) =>
        {
            var found = await wikidata.SearchAsync(entityId, request, ct);
            return found is null ? ApiErrors.NotFound("Wikidata search was not found.") : Results.Ok(found);
        }).WithName("SearchEditorWikidata")
          .WithSummary("Searches Wikidata for the item, or resolves a pasted link or QID. Text-only results with the item's type, its relation to the item (itself or parent) and alignment evidence. Writes nothing.")
          .Produces<EditorWikidataSearchResultDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);

        group.MapPost("/{entityId:guid}/wikidata-conflict", async (
            Guid entityId, EditorWikidataConflictRequestDto request, EditorWikidataMatchService wikidata, CancellationToken ct) =>
        {
            var compared = await wikidata.CompareAsync(entityId, request, ct);
            return compared is null ? ApiErrors.NotFound("Wikidata conflict check was not found.") : Results.Ok(compared);
        }).WithName("CompareEditorWikidataWithProviderMatch")
          .WithSummary("Whether the provider match and the Wikidata item disagree (shared identifiers, year, title). Reads only; neither side is changed.")
          .Produces<EditorWikidataConflictDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);
    }
}
