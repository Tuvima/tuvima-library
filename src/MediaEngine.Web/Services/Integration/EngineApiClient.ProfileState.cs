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
