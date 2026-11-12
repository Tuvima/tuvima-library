using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    Task<IReadOnlyList<ProfileSavedItemDto>> GetSavedItemsAsync(CancellationToken ct = default);
    Task<ProfileSavedItemDto?> GetSavedItemAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);
    Task<ProfileStateMutationDto?> SaveItemAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);
    Task<bool> RemoveSavedItemAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);
    /// <summary>Moves one song to a new place (0 is the top) in the signed-in profile's Favorites; false when the Engine refused or could not be reached.</summary>
    Task<bool> MoveFavoriteSongAsync(Guid songId, int toIndex, CancellationToken ct = default);
    Task<IReadOnlyList<ProfileReactionDto>> GetProfileReactionsAsync(CancellationToken ct = default);
    Task<ProfileReactionDto?> GetProfileReactionAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);
    Task<ProfileStateMutationDto?> SetProfileReactionAsync(ProfileEntityKind entityKind, Guid entityId, ProfileReactionKind reaction, CancellationToken ct = default);
    Task<bool> RemoveProfileReactionAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);
}
