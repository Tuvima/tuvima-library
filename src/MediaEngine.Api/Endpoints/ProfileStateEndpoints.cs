using System.Security.Claims;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Activity;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MediaEngine.Api.Endpoints;

public static class ProfileStateEndpoints
{
    public static IEndpointRouteBuilder MapProfileStateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/profile-state")
            .WithTags("Profile State")
            .RequireAuthorization(AuthPolicies.Authenticated);

        group.MapGet("/saved", async (
            ClaimsPrincipal user,
            IProfileStateRepository repository,
            ProfileStateAccessService access,
            CancellationToken ct) =>
        {
            var profileId = RequireProfileId(user);
            var scope = await access.ResolveAsync(profileId, ct);
            var items = await repository.GetSavedItemsAsync(profileId, ct);
            return Results.Ok(items.Where(item => scope.Allows(item.EntityKind, item.EntityId)).Select(ToDto).ToList());
        })
        .WithName("GetSavedProfileItems")
        .Produces<IReadOnlyList<ProfileSavedItemDto>>()
        .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapGet("/saved/finished", async (
            ClaimsPrincipal user,
            IProfileStateRepository repository,
            [FromServices] DisplayComposerService display,
            CancellationToken ct) =>
        {
            var profileId = RequireProfileId(user);
            // The composer reads the My List grid source, so the profile's own visibility rules decide what can be finished.
            var finished = await display.LoadFinishedSavedAsync(profileId, ct);
            var saved = (await repository.GetSavedItemsAsync(profileId, ct))
                .ToDictionary(item => (item.EntityKind, item.EntityId));
            return Results.Ok(finished
                .Where(entry => saved.ContainsKey((entry.Kind, entry.EntityId)))
                .Select(entry => ToDto(saved[(entry.Kind, entry.EntityId)]))
                .ToList());
        })
        .WithName("GetFinishedSavedProfileItems")
        .Produces<IReadOnlyList<ProfileSavedItemDto>>()
        .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapPost("/saved/remove", async (
            RemoveSavedItemsRequest request,
            ClaimsPrincipal user,
            IProfileStateRepository repository,
            CancellationToken ct) =>
        {
            if (request.Items is null || request.Items.Count > RemoveSavedItemsRequest.MaxItems)
            {
                return ApiErrors.BadRequest($"Send between 0 and {RemoveSavedItemsRequest.MaxItems} items to remove.");
            }
            var profileId = RequireProfileId(user);
            // My List holds no songs (those are Favorites), so a song is never removed through this action.
            var keys = request.Items
                .Where(item => Enum.IsDefined(item.EntityKind) && item.EntityKind != ProfileEntityKind.Song)
                .Select(item => new ProfileSavedKey(item.EntityKind, item.EntityId))
                .Distinct()
                .ToList();
            var removed = await repository.RemoveSavedItemsAsync(profileId, keys, ct);
            return Results.Ok(removed.Select(ToDto).ToList());
        })
        .WithName("RemoveSavedProfileItems")
        .Produces<IReadOnlyList<ProfileSavedItemDto>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireClientScope(ClientApiScopes.ProgressWrite);

        group.MapPost("/saved/restore", async (
            RestoreSavedItemsRequest request,
            ClaimsPrincipal user,
            IProfileStateRepository repository,
            ProfileStateAccessService access,
            CancellationToken ct) =>
        {
            if (request.Items is null || request.Items.Count > RestoreSavedItemsRequest.MaxItems)
            {
                return ApiErrors.BadRequest($"Send between 0 and {RestoreSavedItemsRequest.MaxItems} items to restore.");
            }
            var profileId = RequireProfileId(user);
            var scope = await access.ResolveAsync(profileId, ct, includeUnmatched: true);
            // Same check as saving one item; what the profile may not see is skipped, as are songs (not part of My List).
            var allowed = request.Items
                .Where(item => Enum.IsDefined(item.EntityKind) && item.EntityKind != ProfileEntityKind.Song)
                .Where(item => scope.Allows(item.EntityKind, item.EntityId))
                .DistinctBy(item => (item.EntityKind, item.EntityId))
                .Select(item => new ProfileSavedRestore(item.EntityKind, item.EntityId, item.SavedAt, item.Position))
                .ToList();
            var restored = await repository.RestoreSavedItemsAsync(profileId, allowed, ct);
            return Results.Ok(restored.Select(ToDto).ToList());
        })
        .WithName("RestoreSavedProfileItems")
        .Produces<IReadOnlyList<ProfileSavedItemDto>>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .RequireClientScope(ClientApiScopes.ProgressWrite);

        group.MapGet("/saved/{entityKind}/{entityId:guid}", async (
            string entityKind,
            Guid entityId,
            ClaimsPrincipal user,
            IProfileStateRepository repository,
            ProfileStateAccessService access,
            CancellationToken ct) =>
        {
            if (!TryKind(entityKind, out var kind))
            {
                return ApiErrors.BadRequest("Unknown profile entity kind.");
            }
            var profileId = RequireProfileId(user);
            if (!(await access.ResolveAsync(profileId, ct, includeUnmatched: true)).Allows(kind, entityId))
            {
                return Results.NoContent();
            }
            var item = await repository.GetSavedItemAsync(profileId, kind, entityId, ct);
            return item is null ? Results.NoContent() : Results.Ok(ToDto(item));
        })
        .WithName("GetSavedProfileItem")
        .Produces<ProfileSavedItemDto>()
        .Produces(StatusCodes.Status204NoContent)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapPut("/saved/{entityKind}/{entityId:guid}", async (
            string entityKind,
            Guid entityId,
            ClaimsPrincipal user,
            IProfileStateRepository repository,
            ProfileStateAccessService access,
            IDisplayProjectionReadService display,
            PersonalActivityRecorder activity,
            CancellationToken ct) =>
        {
            if (!TryKind(entityKind, out var kind))
            {
                return ApiErrors.BadRequest("Unknown profile entity kind.");
            }
            try
            {
                var profileId = RequireProfileId(user);
                if (!(await access.ResolveAsync(profileId, ct, includeUnmatched: true)).Allows(kind, entityId))
                {
                    return ApiErrors.NotFound($"{kind} '{entityId:D}' is unavailable to the active profile.");
                }
                var alreadySaved = await repository.GetSavedItemAsync(profileId, kind, entityId, ct) is not null;
                var item = await repository.SaveItemAsync(profileId, kind, entityId, ct: ct);
                if (!alreadySaved)
                {
                    // Adding to My list shows in the person's own activity; the recorder never fails the request.
                    await activity.RecordMyListAddedAsync(profileId, kind, entityId, display.LoadWorksIncludingUnmatchedAsync, ct);
                }
                return Results.Ok(new ProfileStateMutationDto(kind, entityId, true, null, item.SavedAt));
            }
            catch (KeyNotFoundException ex) { return ApiErrors.NotFound(ex.Message); }
            catch (InvalidOperationException ex) { return ApiErrors.BadRequest(ex.Message); }
        })
        .WithName("SaveProfileItem")
        .Produces<ProfileStateMutationDto>()
        .RequireClientScope(ClientApiScopes.ProgressWrite);

        group.MapDelete("/saved/{entityKind}/{entityId:guid}", async (
            string entityKind,
            Guid entityId,
            ClaimsPrincipal user,
            IProfileStateRepository repository,
            CancellationToken ct) =>
        {
            if (!TryKind(entityKind, out var kind))
            {
                return ApiErrors.BadRequest("Unknown profile entity kind.");
            }
            var profileId = RequireProfileId(user);
            await repository.RemoveSavedItemAsync(profileId, kind, entityId, ct);
            return Results.NoContent();
        })
        .WithName("RemoveSavedProfileItem")
        .Produces(StatusCodes.Status204NoContent)
        .RequireClientScope(ClientApiScopes.ProgressWrite);

        group.MapGet("/reactions", async (
            ClaimsPrincipal user,
            IProfileStateRepository repository,
            ProfileStateAccessService access,
            CancellationToken ct) =>
        {
            var profileId = RequireProfileId(user);
            var scope = await access.ResolveAsync(profileId, ct);
            var items = await repository.GetReactionsAsync(profileId, ct);
            return Results.Ok(items.Where(item => scope.Allows(item.EntityKind, item.EntityId)).Select(ToDto).ToList());
        })
        .WithName("GetProfileReactions")
        .Produces<IReadOnlyList<ProfileReactionDto>>()
        .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapGet("/reactions/{entityKind}/{entityId:guid}", async (
            string entityKind,
            Guid entityId,
            ClaimsPrincipal user,
            IProfileStateRepository repository,
            ProfileStateAccessService access,
            CancellationToken ct) =>
        {
            if (!TryKind(entityKind, out var kind))
            {
                return ApiErrors.BadRequest("Unknown profile entity kind.");
            }
            var profileId = RequireProfileId(user);
            if (!(await access.ResolveAsync(profileId, ct, includeUnmatched: true)).Allows(kind, entityId))
            {
                return Results.NoContent();
            }
            var item = await repository.GetReactionAsync(profileId, kind, entityId, ct);
            return item is null ? Results.NoContent() : Results.Ok(ToDto(item));
        })
        .WithName("GetProfileReaction")
        .Produces<ProfileReactionDto>()
        .Produces(StatusCodes.Status204NoContent)
        .RequireClientScope(ClientApiScopes.LibraryRead);

        group.MapPut("/reactions/{entityKind}/{entityId:guid}/{reaction}", async (
            string entityKind,
            Guid entityId,
            string reaction,
            ClaimsPrincipal user,
            IProfileStateRepository repository,
            ProfileStateAccessService access,
            CancellationToken ct) =>
        {
            if (!TryKind(entityKind, out var kind))
            {
                return ApiErrors.BadRequest("Unknown profile entity kind.");
            }
            if (!Enum.TryParse<ProfileReactionKind>(reaction, true, out var reactionKind))
            {
                return ApiErrors.BadRequest("Unknown reaction kind.");
            }
            try
            {
                var profileId = RequireProfileId(user);
                if (!(await access.ResolveAsync(profileId, ct, includeUnmatched: true)).Allows(kind, entityId))
                {
                    return ApiErrors.NotFound($"{kind} '{entityId:D}' is unavailable to the active profile.");
                }
                var item = await repository.SetReactionAsync(profileId, kind, entityId, reactionKind, ct);
                return Results.Ok(new ProfileStateMutationDto(kind, entityId, false, item.Reaction, item.UpdatedAt));
            }
            catch (KeyNotFoundException ex) { return ApiErrors.NotFound(ex.Message); }
        })
        .WithName("SetProfileReaction")
        .Produces<ProfileStateMutationDto>()
        .RequireClientScope(ClientApiScopes.ProgressWrite);

        group.MapDelete("/reactions/{entityKind}/{entityId:guid}", async (
            string entityKind,
            Guid entityId,
            ClaimsPrincipal user,
            IProfileStateRepository repository,
            CancellationToken ct) =>
        {
            if (!TryKind(entityKind, out var kind))
            {
                return ApiErrors.BadRequest("Unknown profile entity kind.");
            }
            var profileId = RequireProfileId(user);
            await repository.RemoveReactionAsync(profileId, kind, entityId, ct);
            return Results.NoContent();
        })
        .WithName("RemoveProfileReaction")
        .Produces(StatusCodes.Status204NoContent)
        .RequireClientScope(ClientApiScopes.ProgressWrite);

        return app;
    }

    private static bool TryKind(string value, out ProfileEntityKind kind) =>
        Enum.TryParse(value, true, out kind) && Enum.IsDefined(kind);

    private static Guid RequireProfileId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(TuvimaClaimTypes.ActiveProfileId), out var profileId)
            ? profileId
            : throw new UnauthorizedAccessException("The authenticated profile identity is missing.");

    private static ProfileSavedItemDto ToDto(ProfileSavedItem item) =>
        new(item.EntityKind, item.EntityId, item.SavedAt, item.Position);

    private static ProfileReactionDto ToDto(ProfileReactionState item) =>
        new(item.EntityKind, item.EntityId, item.Reaction, item.UpdatedAt);
}
