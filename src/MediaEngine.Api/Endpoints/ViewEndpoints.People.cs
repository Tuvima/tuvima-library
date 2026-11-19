using MediaEngine.Api.Http;
using MediaEngine.Api.Services.Faces;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// People in View (spec view-people-search WP8): who is in a person's own photos, naming, merging and the fixes. Everything here is the
/// caller's own face data under the same preference authority as their other View settings; nobody else's faces are reachable.
/// </summary>
public static partial class ViewEndpoints
{
    private static void MapPeople(RouteGroupBuilder group)
    {
        var faces = group.MapGroup("/faces");

        faces.MapGet("/groups", async (bool? named, bool? includeHidden, int? take, IViewRequestProfileContext identity, [FromServices] IViewFaceRepository repository,
            IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: false, ct, async profileId =>
                Results.Ok(new ViewFaceGroupsDto((await repository.GetGroupsAsync(profileId, named, includeHidden ?? false, take ?? 100, ct)).Select(ToDto).ToList()))))
            .WithName("GetViewFaceGroups").Produces<ViewFaceGroupsDto>();

        faces.MapGet("/groups/{personId:guid}", async (Guid personId, IViewRequestProfileContext identity, [FromServices] IViewFaceRepository repository,
            IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: false, ct, async profileId =>
                await repository.GetGroupAsync(profileId, personId, ct) is { } found ? Results.Ok(ToDto(found)) : Missing()))
            .WithName("GetViewFaceGroup").Produces<ViewFaceGroupDto>();

        faces.MapGet("/groups/{personId:guid}/faces", async (Guid personId, int? take, IViewRequestProfileContext identity, [FromServices] IViewFaceRepository repository,
            IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: false, ct, async profileId =>
                Results.Ok(new ViewFaceBoxesDto((await repository.GetFacesOfPersonAsync(profileId, personId, take ?? 60, ct)).Select(ToDto).ToList()))))
            .WithName("GetViewFaceGroupFaces").Produces<ViewFaceBoxesDto>();

        faces.MapGet("/groups/{personId:guid}/suggestions", async (Guid personId, IViewRequestProfileContext identity, [FromServices] ViewPeopleService people,
            IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: false, ct, async profileId =>
            {
                var suggestions = await people.SuggestAsync(profileId, personId, ct);
                return Results.Ok(new ViewFaceSuggestionsDto(suggestions.Select(item => new ViewFaceSuggestionDto(ToDto(item.Group), item.Faces.Select(ToDto).ToList())).ToList()));
            }))
            .WithName("GetViewFaceSuggestions").Produces<ViewFaceSuggestionsDto>();

        faces.MapGet("/items/{itemId:guid}", async (Guid itemId, IViewRequestProfileContext identity, [FromServices] IViewFaceRepository repository,
            IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: false, ct, async profileId =>
                Results.Ok(new ViewFaceBoxesDto((await repository.GetFacesOfItemAsync(profileId, itemId, ct)).Select(ToDto).ToList()))))
            .WithName("GetViewItemFaces").Produces<ViewFaceBoxesDto>();

        faces.MapGet("/{faceId:guid}/crop", async (Guid faceId, int? size, IViewRequestProfileContext identity, [FromServices] IViewFaceRepository repository,
            [FromServices] ILocalAssetRepository assets, IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: false, ct, async profileId =>
            {
                var face = await repository.GetFaceAsync(profileId, faceId, ct);
                var location = face is null ? null : assets.ResolveContent(face.ItemId);
                if (face is null || location is null || !File.Exists(location.FilePath))
                {
                    return Missing();
                }

                using var picture = OrientedImage.Load(location.FilePath);
                var jpeg = picture is null ? null : OrientedImage.CropFace(picture, face.X, face.Y, face.Width, face.Height, Math.Clamp(size ?? 128, 32, 512));
                if (jpeg is null)
                {
                    return Missing();
                }

                return Results.File(jpeg, "image/jpeg");
            }))
            .WithName("GetViewFaceCrop").Produces(StatusCodes.Status200OK, contentType: "image/jpeg");

        faces.MapPost("/groups/{personId:guid}/name", async (Guid personId, [FromBody] ViewNamePersonRequest request, IViewRequestProfileContext identity,
            [FromServices] IViewFaceRepository repository, IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: true, ct, async profileId =>
            {
                if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
                {
                    return ApiErrors.BadRequest("A name is 1 to 120 characters.");
                }

                return await repository.NamePersonAsync(profileId, personId, request.Name, ct) is { } named
                    ? Results.Ok(new ViewNamePersonResultDto(named.PersonId, named.MergedIntoExisting))
                    : Missing();
            }))
            .WithName("NameViewFacePerson").Produces<ViewNamePersonResultDto>();

        faces.MapPost("/groups/{personId:guid}/merge", async (Guid personId, [FromBody] ViewMergePeopleRequest request, IViewRequestProfileContext identity,
            [FromServices] IViewFaceRepository repository, IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: true, ct, async profileId =>
                await repository.MergeAsync(profileId, personId, request.IntoPersonId, ct) ? Results.NoContent() : Missing()))
            .WithName("MergeViewFacePeople").Produces(StatusCodes.Status204NoContent);

        faces.MapPost("/groups/{personId:guid}/confirm", async (Guid personId, [FromBody] ViewConfirmFacesRequest request, IViewRequestProfileContext identity,
            [FromServices] IViewFaceRepository repository, IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: true, ct, async profileId =>
                await repository.ConfirmFacesAsync(profileId, personId, request.AcceptedFaceIds, request.RejectedFaceIds, ct) ? Results.NoContent() : Missing()))
            .WithName("ConfirmViewFaces").Produces(StatusCodes.Status204NoContent);

        faces.MapPost("/groups/{personId:guid}/reject", async (Guid personId, [FromBody] ViewRejectFacesRequest request, IViewRequestProfileContext identity,
            [FromServices] IViewFaceRepository repository, IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: true, ct, async profileId =>
                await repository.RejectFacesAsync(profileId, personId, request.FaceIds, ct) ? Results.NoContent() : Missing()))
            .WithName("RejectViewFaces").Produces(StatusCodes.Status204NoContent);

        faces.MapPut("/groups/{personId:guid}/hidden", async (Guid personId, [FromBody] ViewHidePersonRequest request, IViewRequestProfileContext identity,
            [FromServices] IViewFaceRepository repository, IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: true, ct, async profileId =>
                await repository.SetHiddenAsync(profileId, personId, request.Hidden, ct) ? Results.NoContent() : Missing()))
            .WithName("HideViewFacePerson").Produces(StatusCodes.Status204NoContent);

        faces.MapPut("/groups/{personId:guid}/cover", async (Guid personId, [FromBody] ViewPersonCoverRequest request, IViewRequestProfileContext identity,
            [FromServices] IViewFaceRepository repository, IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: true, ct, async profileId =>
                await repository.SetCoverAsync(profileId, personId, request.FaceId, ct) ? Results.NoContent() : Missing()))
            .WithName("SetViewFacePersonCover").Produces(StatusCodes.Status204NoContent);

        faces.MapDelete("/groups/{personId:guid}", async (Guid personId, IViewRequestProfileContext identity, [FromServices] IViewFaceRepository repository,
            IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: true, ct, async profileId =>
                await repository.DeletePersonAsync(profileId, personId, ct) ? Results.NoContent() : Missing()))
            .WithName("DeleteViewFacePerson").Produces(StatusCodes.Status204NoContent);

        faces.MapDelete("/{faceId:guid}", async (Guid faceId, IViewRequestProfileContext identity, [FromServices] IViewFaceRepository repository,
            IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: true, ct, async profileId =>
                await repository.RemoveFaceAsync(profileId, faceId, ct) ? Results.NoContent() : Missing()))
            .WithName("RemoveViewFace").Produces(StatusCodes.Status204NoContent);

        faces.MapDelete("/", async (IViewRequestProfileContext identity, [FromServices] IViewFaceRepository repository,
            IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: true, ct, async profileId =>
            {
                await repository.DeleteAllAsync(profileId, ct);
                return Results.NoContent();
            }))
            .WithName("DeleteAllViewFaceData").Produces(StatusCodes.Status204NoContent);

        faces.MapGet("/progress", async (IViewRequestProfileContext identity, [FromServices] IViewFaceRepository repository, [FromServices] IViewProfileRepository profiles,
            [FromServices] IViewPersonalSpaceRepository spaces, [FromServices] IFaceEngine engine, IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: false, ct, async profileId =>
            {
                var recognize = (await profiles.GetPreferencesAsync(profileId, ct)).RecognizePeople;
                var space = await spaces.GetByOwnerAsync(profileId, ct);
                var progress = space is null
                    ? new ViewFaceScanProgress(0, 0, 0, 0, 0)
                    : await repository.GetProgressAsync(profileId, space.LibraryId, ct);
                var state = !recognize ? ViewFaceStates.Off
                    : !engine.IsAvailable ? ViewFaceStates.GettingTools
                    : progress.PhotosLooked < progress.PhotosTotal ? ViewFaceStates.Looking
                    : ViewFaceStates.Done;
                return Results.Ok(new ViewFaceProgressDto(state, progress.PhotosLooked, progress.PhotosTotal, progress.Faces, progress.People, progress.UnnamedGroups));
            }))
            .WithName("GetViewFaceProgress").Produces<ViewFaceProgressDto>();

        group.MapGet("/preferences/recognize-people", async (IViewRequestProfileContext identity, [FromServices] IViewProfileRepository repository,
            IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: false, ct, async profileId =>
                Results.Ok(new ViewRecognizePeopleDto((await repository.GetPreferencesAsync(profileId, ct)).RecognizePeople))))
            .WithName("GetViewRecognizePeople").Produces<ViewRecognizePeopleDto>();

        group.MapPut("/preferences/recognize-people", async ([FromBody] ViewRecognizePeopleDto request, IViewRequestProfileContext identity,
            [FromServices] IViewProfileRepository repository, IViewResourceAuthorizationService authorization, CancellationToken ct) =>
            await OwnFacesAsync(identity, authorization, manage: true, ct, async profileId =>
                await repository.SaveRecognizePeopleAsync(profileId, request.Recognize, ct) ? Results.Ok(request) : Missing()))
            .WithName("UpdateViewRecognizePeople").Produces<ViewRecognizePeopleDto>();
    }

    private static async Task<IResult> OwnFacesAsync(
        IViewRequestProfileContext identity,
        IViewResourceAuthorizationService authorization,
        bool manage,
        CancellationToken ct,
        Func<Guid, Task<IResult>> run)
    {
        var authority = await identity.ResolveAuthorityAsync(ct);
        if (authority.ActiveProfileId is not { } profileId)
        {
            return Unauthenticated();
        }

        var access = await authorization.AuthorizeAsync(authority,
            manage
                ? new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Preference, null, ViewResourceAction.Manage)
                : new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Preference, null), ct);
        return access.IsAllowed ? await run(profileId) : Access(access.Outcome);
    }

    private static ViewFaceGroupDto ToDto(ViewFaceGroup group) =>
        new(group.PersonId, group.Name, group.Hidden, group.FaceCount, group.PhotoCount, group.CoverFaceId, group.SampleFaceIds);

    private static ViewFaceBoxDto ToDto(ViewFaceBox box) =>
        new(box.FaceId, box.ItemId, box.PersonId, box.PersonName, box.X, box.Y, box.Width, box.Height);
}
