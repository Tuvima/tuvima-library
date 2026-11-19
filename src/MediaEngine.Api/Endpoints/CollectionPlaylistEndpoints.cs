using MediaEngine.Api.Http;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Playlist actions of the Listen playlists spec (WP1) that work at any playlist size: move one item to a new place, pin a
/// playlist for the active profile, and duplicate a playlist. Adding works lives with the other item routes in
/// <see cref="CollectionEndpoints"/>; the work happens in SQL in <see cref="IPlaylistRepository"/>.
/// </summary>
public static class CollectionPlaylistEndpoints
{
    public static RouteGroupBuilder MapCollectionPlaylistEndpoints(this RouteGroupBuilder group)
    {
        group.MapPut("/{id:guid}/items/{itemId:guid}/move", async (
            Guid id,
            Guid itemId,
            CollectionItemMoveRequest body,
            ICollectionRepository collectionRepo,
            IPlaylistRepository playlistRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            Guid? profileId,
            CancellationToken ct) =>
        {
            var caller = await CollectionCaller.ResolveAsync(httpContext, profileId, profileRepo, ct);
            var collection = await collectionRepo.GetByIdAsync(id, ct);
            if (collection is null)
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!CollectionAccessPolicy.CanEdit(collection, caller.Profile, caller.IsAdministrator))
            {
                return Results.Forbid();
            }

            if (!CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType)
                || collection.Resolution != CollectionResolution.Materialized)
            {
                return ApiErrors.BadRequest("Only saved/manual collections support direct item ordering.");
            }

            var choices = (body.Position.HasValue ? 1 : 0) + (body.ToTop ? 1 : 0) + (body.ToBottom ? 1 : 0);
            if (choices != 1)
            {
                return ApiErrors.BadRequest("Send exactly one of position, to_top or to_bottom.");
            }

            if (body.Position is < 1)
            {
                return ApiErrors.BadRequest("position must be 1 or more (1 is the top).");
            }

            // To the bottom is "past the end": the repository clamps to the last place, so the caller never has to know the size.
            var position = body.ToTop ? 1 : body.ToBottom ? int.MaxValue : body.Position!.Value;
            return await playlistRepo.MoveItemAsync(id, itemId, position, ct)
                ? Results.Ok()
                : ApiErrors.NotFound($"Item '{itemId}' not found in collection '{id}'.");
        })
        .WithName("MoveCollectionItem")
        .WithSummary("Moves one item of a saved/manual collection to a new position, at any collection size.")
        .Produces(StatusCodes.Status200OK)
        .RequireAdministratorOrAdultProfile(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.CollectionsWrite, "Collection", "id");

        // A pin is the person's own choice about their own list, so it needs the right to see the playlist, not to edit it.
        group.MapPut("/{id:guid}/pinned", async (
            Guid id,
            CollectionPinRequest body,
            ICollectionRepository collectionRepo,
            IPlaylistRepository playlistRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            Guid? profileId,
            CancellationToken ct) =>
        {
            var caller = await CollectionCaller.ResolveAsync(httpContext, profileId, profileRepo, ct);
            if (caller.Profile is null)
            {
                return ApiErrors.BadRequest("An active profile is required to pin a playlist.");
            }

            var collection = await collectionRepo.GetByIdAsync(id, ct);
            if (collection is null || !CollectionAccessPolicy.CanAccess(collection, caller.Profile))
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType))
            {
                return ApiErrors.BadRequest("Only playlists and saved collections can be pinned.");
            }

            await playlistRepo.SetPinnedAsync(caller.Profile.Id, id, body.Pinned, ct);
            return Results.Ok();
        })
        .WithName("SetCollectionPinned")
        .WithSummary("Pins or unpins a playlist for the active profile only.")
        .Produces(StatusCodes.Status200OK)
        .RequireAuthorization(AuthPolicies.Authenticated)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Collection", "id");

        group.MapPost("/{id:guid}/duplicate", async (
            Guid id,
            CollectionDuplicateRequest? body,
            ICollectionRepository collectionRepo,
            IProfileRepository profileRepo,
            HttpContext httpContext,
            Guid? profileId,
            IDisplayProjectionReadService display,
            IVaultLibraryRepository vaultLibraries,
            CancellationToken ct) =>
        {
            var caller = await CollectionCaller.ResolveAsync(httpContext, profileId, profileRepo, ct);
            var activeProfile = caller.Profile;
            if (activeProfile is null)
            {
                return ApiErrors.BadRequest("An active profile is required to duplicate a playlist.");
            }

            var source = await collectionRepo.GetByIdAsync(id, ct);
            if (source is null || !CollectionAccessPolicy.CanAccess(source, activeProfile))
            {
                return ApiErrors.NotFound($"Collection '{id}' not found.");
            }

            if (!CollectionAccessPolicy.IsManagedCollectionType(source.CollectionType)
                || source.Resolution != CollectionResolution.Materialized)
            {
                return ApiErrors.BadRequest("Only saved/manual collections can be duplicated.");
            }

            // The copy holds the songs this profile can open, in the same order, with no size limit.
            var allowedWorkIds = await display.LoadVisibleWorkIdsAsync(ct);
            var sourceItems = await collectionRepo.GetCollectionItemsAsync(id, int.MaxValue, ct);
            var workIds = sourceItems
                .OrderBy(item => item.SortOrder)
                .Select(item => item.WorkId)
                .Where(allowedWorkIds.Contains)
                .ToList();
            if (await CollectionVaultGuard.RefusesAsync(
                    ContainerOwnerKind.Profile, ContainerAudience.Private, workIds, vaultLibraries, ct))
            {
                return ApiErrors.Forbidden(CollectionVaultGuard.Message);
            }

            if (workIds.Count == 0 && CollectionAccessPolicy.IsCuratedCollection(source))
            {
                return ApiErrors.BadRequest("A collection needs at least one item. Choose something to add before saving.");
            }

            var name = string.IsNullOrWhiteSpace(body?.Name)
                ? $"{source.DisplayName} copy"
                : body.Name.Trim();
            var copy = new Collection
            {
                Id = Guid.NewGuid(),
                DisplayName = name,
                Description = source.Description,
                IconName = source.IconName,
                IsEnabled = true,
                SortField = source.SortField,
                SecondarySortField = source.SecondarySortField,
                SecondarySortDirection = source.SecondarySortDirection,
                CreatedAt = DateTimeOffset.UtcNow,
                MembershipMode = ContainerMembershipMode.Manual,
                PrimaryArea = source.PrimaryArea,
                // A copy belongs to the person who made it, whoever owned the original.
                OwnerKind = ContainerOwnerKind.Profile,
                Audience = ContainerAudience.Private,
            };
            copy.RestoreDefinition(
                source.CollectionType,
                CollectionScope.User,
                CollectionResolution.Materialized,
                source.MatchMode,
                source.SortDirection,
                CollectionUniverseStatus.Unknown);
            CollectionAccessPolicy.ApplyOwnership(copy, activeProfile.Id);

            var addedAt = DateTimeOffset.UtcNow;
            var items = workIds
                .Select((workId, index) => new CollectionItem
                {
                    Id = Guid.NewGuid(),
                    CollectionId = copy.Id,
                    WorkId = workId,
                    SortOrder = index + 1,
                    AddedAt = addedAt,
                })
                .ToList();
            await collectionRepo.CreateManagedCollectionAsync(copy, items, ct);

            var dto = ManagedCollectionMapper.FromDomain(copy, items.Count, activeProfile, caller.IsAdministrator);
            return Results.Created($"/collections/{copy.Id}", dto);
        })
        .WithName("DuplicateCollection")
        .WithSummary("Copies a saved/manual collection (a playlist) with its items in order into a new collection owned by the active profile.")
        .Produces<ManagedCollectionDto>(StatusCodes.Status201Created)
        .RequireAdministratorOrAdultProfile(ApplicationPermissionIds.CollectionsWrite)
        .RequireCatalogueEntityAccess(ApplicationPermissionIds.LibraryRead, "Collection", "id");

        return group;
    }
}
