using MediaEngine.Api.Http;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// The one Save of the photos, videos and documents editor (spec photos-documents-editor, WP1). Every item is authorized on its own first,
/// with the same rules as the single-field routes; one refused item stops the whole Save and nothing is written. The work is done by
/// <see cref="ViewItemsEditService"/>.
/// </summary>
public static partial class ViewEndpoints
{
    private static void MapItemsEdit(RouteGroupBuilder group)
    {
        group.MapPost("/items/edit", async ([FromBody] ViewItemsEditRequest request,
            IViewRequestProfileContext identity, IViewProfileRepository preferences, IViewScopeResolver resolver,
            IViewResourceAuthorizationService authorization, ILocalAssetRepository assets,
            IViewPersonalSpaceRepository spaces, ViewDiscoveryService discovery, ViewItemsEditService service,
            CancellationToken ct) =>
        {
            try
            {
                var authority = await identity.ResolveAuthorityAsync(ct);
                if (!authority.IsAuthenticated || authority.ActiveProfileId is not { } profileId)
                {
                    return Unauthenticated();
                }

                var ids = (request.ItemIds ?? []).Distinct().ToList();
                var refusals = new List<ViewItemsEditRefusalDto>();
                var hasDocuments = false;
                foreach (var id in ids)
                {
                    var isDocument = string.Equals(assets.GetContentKind(id), ViewContentKinds.Documents, StringComparison.Ordinal);
                    hasDocuments |= isDocument;
                    var decision = isDocument
                        ? await AuthorizeDocumentEditAsync(id, identity, preferences, authorization, assets, ct)
                        : await AuthorizeOwnedItemAsync(id, identity, authorization, ct);
                    if (!decision.IsAllowed
                        || await DocumentsBlockedAsync(id, assets, identity, preferences, ct)
                        || await OtherKindBlockedAsync(id, assets, identity, preferences, ct))
                    {
                        refusals.Add(new ViewItemsEditRefusalDto(id, "You can't change this item."));
                    }
                }

                var (access, refusal) = await ResolveCatalogueChangeAccessAsync(hasDocuments, identity, preferences, resolver, authorization, ct);
                if (access is null)
                {
                    return refusal!;
                }

                var blocked = await BlockedKindsAsync(authority, preferences, ct);
                var space = request.CreateGalleryName is { Length: > 0 } ? await spaces.GetByOwnerAsync(profileId, ct) : null;
                var context = new ViewItemsEditService.Context(
                    access.Viewer, profileId, space?.Id, blocked,
                    async names =>
                    {
                        foreach (var person in names.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            var found = await discovery.GetPeopleAsync(new ViewDiscoveryRequest(ViewScopeRequest.Mine, 100, person), ct);
                            if (found.Page?.Items.Any(candidate => string.Equals(candidate.DisplayName, person, StringComparison.OrdinalIgnoreCase)) != true)
                            {
                                return person;
                            }
                        }

                        return null;
                    },
                    async galleryId => (await GalleryAccessAsync(galleryId, ViewResourceAction.Contribute, identity, authorization, ct)).IsAllowed);
                return Results.Ok(await service.RunAsync(context, request, refusals, ct));
            }
            catch (Exception exception) when (exception is ViewCatalogueConflictException or ViewCatalogueAccessException or ArgumentException)
            {
                return exception is ArgumentException ? ApiErrors.BadRequest(exception.Message) : CatalogueFailure(exception);
            }
        }).WithName("EditViewItems").Produces<ViewItemsEditResultDto>();
    }
}
