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
        group.MapPost("/items/edit", async ([FromBody] ViewItemsEditSetRequest set,
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

                var requests = set.Edits ?? [];
                if (requests.Any(request => request is null))
                {
                    throw new ArgumentException("Every edit needs its items.");
                }

                if (requests.Count > ViewItemsEditLimits.MaximumItems
                    || requests.Sum(request => request?.ItemIds?.Count ?? 0) > ViewItemsEditLimits.MaximumItems)
                {
                    throw new ArgumentException($"One Save can change at most {ViewItemsEditLimits.MaximumItems} items.");
                }

                // Shared Library editors may change a shared document's own fields and tags, nothing else: every other change
                // (description, people, date, place, galleries) belongs to the item's owner.
                var ownerOnlyIds = new HashSet<Guid>();
                var ids = new List<Guid>();
                foreach (var request in requests)
                {
                    var ownerOnly = request.SetDescription || request.PeopleAdd is { Count: > 0 } || request.PeopleRemove is { Count: > 0 }
                        || request.CapturedAt is not null || request.CapturedShiftSeconds is not null || request.CapturedReset
                        || request.Location is not null || request.GalleryAddIds is { Count: > 0 } || request.GalleryRemoveIds is { Count: > 0 }
                        || !string.IsNullOrWhiteSpace(request.CreateGalleryName);
                    foreach (var id in request.ItemIds ?? [])
                    {
                        ids.Add(id);
                        if (ownerOnly)
                        {
                            ownerOnlyIds.Add(id);
                        }
                    }
                }

                ids = ids.Distinct().ToList();
                var refusals = new List<ViewItemsEditRefusalDto>();
                foreach (var id in ids)
                {
                    var isDocument = string.Equals(assets.GetContentKind(id), ViewContentKinds.Documents, StringComparison.Ordinal);
                    var decision = isDocument && !ownerOnlyIds.Contains(id)
                        ? await AuthorizeDocumentEditAsync(id, identity, preferences, authorization, assets, ct)
                        : await AuthorizeOwnedItemAsync(id, identity, authorization, ct);
                    if (!decision.IsAllowed
                        || await DocumentsBlockedAsync(id, assets, identity, preferences, ct)
                        || await OtherKindBlockedAsync(id, assets, identity, preferences, ct))
                    {
                        refusals.Add(new ViewItemsEditRefusalDto(id, "You can't change this item."));
                    }
                }

                // Anything refused (or not theirs) answers the same plain way before any item is read, so nothing about another
                // person's items can be learned from the answer.
                if (refusals.Count > 0)
                {
                    return Results.Ok(new ViewItemsEditResultDto(false, false, 0, refusals,
                        "Nothing was saved: some items can't be changed this way."));
                }

                var (access, refusal) = await ResolveCatalogueChangeAccessAsync(false, identity, preferences, resolver, authorization, ct);
                if (access is null)
                {
                    return refusal!;
                }

                var blocked = await BlockedKindsAsync(authority, preferences, ct);
                var space = requests.Any(request => request.CreateGalleryName is { Length: > 0 }) ? await spaces.GetByOwnerAsync(profileId, ct) : null;
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
                return Results.Ok(await service.RunAsync(context, set, refusals, ct));
            }
            catch (Exception exception) when (exception is ViewCatalogueConflictException or ViewCatalogueAccessException or ArgumentException)
            {
                return exception is ArgumentException ? ApiErrors.BadRequest(exception.Message) : CatalogueFailure(exception);
            }
        }).WithName("EditViewItems").Produces<ViewItemsEditResultDto>();
    }
}
