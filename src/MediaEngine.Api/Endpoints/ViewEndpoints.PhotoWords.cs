using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The "Read words in my photos" switch (spec view-people-search WP5). It is one person's own setting, read and changed only by that person
/// through the same preference authority as the other View settings; nobody else's setting is ever reachable.
/// </summary>
public static partial class ViewEndpoints
{
    private static void MapPhotoWordsPreference(RouteGroupBuilder group)
    {
        group.MapGet("/preferences/photo-words", async (IViewRequestProfileContext identity, IViewProfileRepository repository,
            IViewResourceAuthorizationService authorization, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            var access = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Preference, null), ct);
            return access.IsAllowed
                ? Results.Ok(new ViewPhotoWordsPreferenceDto((await repository.GetPreferencesAsync(profileId, ct)).ReadWordsInPhotos))
                : Access(access.Outcome);
        }).WithName("GetViewPhotoWordsPreference").Produces<ViewPhotoWordsPreferenceDto>();

        group.MapPut("/preferences/photo-words", async ([FromBody] ViewPhotoWordsPreferenceDto request,
            IViewRequestProfileContext identity, IViewProfileRepository repository,
            IViewResourceAuthorizationService authorization, IViewPersonalSpaceRepository spaces,
            [FromServices] DocumentOcrQueue ocrQueue, CancellationToken ct) =>
        {
            var authority = await identity.ResolveAuthorityAsync(ct);
            if (authority.ActiveProfileId is not { } profileId)
            {
                return Unauthenticated();
            }

            var access = await authorization.AuthorizeAsync(authority,
                new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Preference, null, ViewResourceAction.Manage), ct);
            if (!access.IsAllowed)
            {
                return Access(access.Outcome);
            }

            if (!await repository.SaveReadWordsInPhotosAsync(profileId, request.ReadWords, ct))
            {
                return Missing();
            }

            if (request.ReadWords && await spaces.GetByOwnerAsync(profileId, ct) is { } space)
            {
                // Only this person's own library is read; turning the switch on processes the photos already there.
                await ocrQueue.StartPhotoWordsScanAsync(space.LibraryId, ct);
            }

            return Results.Ok(request);
        }).WithName("UpdateViewPhotoWordsPreference").Produces<ViewPhotoWordsPreferenceDto>();
    }
}
