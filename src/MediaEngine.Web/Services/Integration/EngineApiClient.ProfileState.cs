using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<IReadOnlyList<ProfileSavedItemDto>> GetSavedItemsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ProfileSavedItemDto>>(
            "Saved profile items",
            "/api/v1/profile-state/saved",
            () => [],
            ct: ct);

    public Task<ProfileSavedItemDto?> GetSavedItemAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default) =>
        GetAsync<ProfileSavedItemDto>(
            "Saved profile item",
            $"/api/v1/profile-state/saved/{entityKind}/{entityId:D}",
            logAsWarning: false,
            ct: ct);

    public Task<ProfileStateMutationDto?> SaveItemAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default) =>
        PutAsync<object, ProfileStateMutationDto>(
            "Save profile item",
            $"/api/v1/profile-state/saved/{entityKind}/{entityId:D}",
            new { },
            ct: ct);

    public Task<bool> RemoveSavedItemAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default) =>
        DeleteAsync("Remove saved profile item", $"/api/v1/profile-state/saved/{entityKind}/{entityId:D}", ct: ct);

    public Task<bool> MoveFavoriteSongAsync(Guid songId, int toIndex, CancellationToken ct = default) =>
        PostAsync("Move favorite song", $"/api/v1/profile-state/favorites/songs/{songId:D}/move", new ProfileSavedItemMoveRequest(toIndex), ct: ct);

    public Task<IReadOnlyList<ProfileSavedItemDto>> GetFinishedSavedItemsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ProfileSavedItemDto>>(
            "Finished saved profile items",
            "/api/v1/profile-state/saved/finished",
            () => [],
            ct: ct);

    // Null means the call failed (so the page can say My List could not be changed); an empty list means nothing was affected.
    public Task<IReadOnlyList<ProfileSavedItemDto>?> RemoveSavedItemsAsync(IReadOnlyList<ProfileSavedItemKeyDto> items, CancellationToken ct = default) =>
        PostAsync<RemoveSavedItemsRequest, IReadOnlyList<ProfileSavedItemDto>>(
            "Remove saved profile items",
            "/api/v1/profile-state/saved/remove",
            new RemoveSavedItemsRequest(items),
            ct: ct);

    public Task<IReadOnlyList<ProfileSavedItemDto>?> RestoreSavedItemsAsync(IReadOnlyList<ProfileSavedItemDto> items, CancellationToken ct = default) =>
        PostAsync<RestoreSavedItemsRequest, IReadOnlyList<ProfileSavedItemDto>>(
            "Restore saved profile items",
            "/api/v1/profile-state/saved/restore",
            new RestoreSavedItemsRequest(items),
            ct: ct);

    public Task<IReadOnlyList<ProfileReactionDto>> GetProfileReactionsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ProfileReactionDto>>(
            "Profile reactions",
            "/api/v1/profile-state/reactions",
            () => [],
            ct: ct);

    public Task<ProfileReactionDto?> GetProfileReactionAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default) =>
        GetAsync<ProfileReactionDto>(
            "Profile reaction",
            $"/api/v1/profile-state/reactions/{entityKind}/{entityId:D}",
            logAsWarning: false,
            ct: ct);

    public Task<ProfileStateMutationDto?> SetProfileReactionAsync(ProfileEntityKind entityKind, Guid entityId, ProfileReactionKind reaction, CancellationToken ct = default) =>
        PutAsync<object, ProfileStateMutationDto>(
            "Set profile reaction",
            $"/api/v1/profile-state/reactions/{entityKind}/{entityId:D}/{reaction}",
            new { },
            ct: ct);

    public Task<bool> RemoveProfileReactionAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default) =>
        DeleteAsync("Remove profile reaction", $"/api/v1/profile-state/reactions/{entityKind}/{entityId:D}", ct: ct);
}
