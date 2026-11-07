using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    /// <summary>
    /// The Match tab's reads for the TV and music flows (universal editor v5, 3c): judge a record picked outside the result list, the
    /// releases and tracks of an album, and the lining-up of several ticked episodes or songs against one show or release (D36).
    /// All are reads: nothing is written until Save changes.
    /// </summary>
    private static void MapEditorMusicMatchEndpoints(RouteGroupBuilder group)
    {
        group.MapPost("/{entityId:guid}/match-verdict", async (
            Guid entityId, EditorMatchVerdictRequestDto request, EditorMatchSearchService match, CancellationToken ct) =>
        {
            var judged = await match.JudgeCandidateAsync(entityId, request.Candidate, ct);
            return judged is null ? ApiErrors.NotFound("Match verdict was not found.") : Results.Ok(judged);
        }).WithName("GetEditorMatchVerdict")
          .WithSummary("Judges a record the person picked in the TV or music flow against the item's file (Strong, Possible or Check) with its reasons. Writes nothing.")
          .Produces<EditorMatchResultDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);

        group.MapGet("/{entityId:guid}/match-music/releases", async (
            Guid entityId, string? artist, string? album, string? song, EditorMusicMatchService music, CancellationToken ct) =>
        {
            var found = await music.ReleasesAsync(entityId, artist, album, song, ct);
            return found is null ? ApiErrors.NotFound("This is not a song or an album.") : Results.Ok(found);
        }).WithName("GetEditorMusicReleases")
          .WithSummary("The releases of an album that match the artist, album or song words, with the one that fits the file suggested and the current one marked.")
          .Produces<EditorMusicReleasesDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);

        group.MapGet("/{entityId:guid}/match-music/releases/{releaseId}", async (
            Guid entityId, string releaseId, EditorMusicMatchService music, CancellationToken ct) =>
        {
            if (!Guid.TryParse(releaseId, out _))
            {
                return ApiErrors.BadRequest("Choose a valid release.");
            }

            var tracks = await music.TracksAsync(entityId, releaseId, ct);
            return tracks is null ? ApiErrors.NotFound("That release could not be read.") : Results.Ok(tracks);
        }).WithName("GetEditorMusicReleaseTracks")
          .WithSummary("One release's discs and tracks with lengths and each recording's disambiguation, the track that fits the file marked.")
          .Produces<EditorMusicReleaseTracksDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status400BadRequest)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);

        group.MapPost("/{entityId:guid}/match-group", async (
            Guid entityId,
            EditorGroupMatchRequestDto request,
            HttpContext http,
            IRequestAuthorityResolver resolver,
            IProfileRepository profiles,
            [Microsoft.AspNetCore.Mvc.FromServices] EditorGroupMatchService groups,
            CatalogueResourceAuthorizationService resources,
            CancellationToken ct) =>
        {
            if (EditorGroupMatchService.Refusal(request) is { } refusal)
            {
                return ApiErrors.BadRequest(refusal);
            }

            var actor = await LibraryItemEndpoints.ResolveMoveActorAsync(http, resolver, profiles, ct);
            if (actor is null)
            {
                return ApiErrors.NotFound("The editing session is unavailable.");
            }

            return Results.Ok(await groups.LineUpAsync(request, actor, LibraryItemEndpoints.CanEdit(http, resources), ct));
        }).WithName("LineUpEditorGroupMatch")
          .WithSummary("Lines several ticked episodes or songs up with one show and season, or one release, by their own numbers. Reads only.")
          .Produces<EditorGroupMatchDto>(StatusCodes.Status200OK)
          .ProducesProblem(StatusCodes.Status400BadRequest)
          .ProducesProblem(StatusCodes.Status404NotFound)
          .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
          .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataMatch);
    }
}
