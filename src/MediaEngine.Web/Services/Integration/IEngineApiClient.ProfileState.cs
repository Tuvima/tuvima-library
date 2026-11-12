using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    Task<IReadOnlyList<ProfileSavedItemDto>> GetSavedItemsAsync(CancellationToken ct = default);
    Task<ProfileSavedItemDto?> GetSavedItemAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);
    Task<ProfileStateMutationDto?> SaveItemAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);
    Task<bool> RemoveSavedItemAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);
    Task<IReadOnlyList<ProfileSavedItemDto>> GetFinishedSavedItemsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ProfileSavedItemDto>?> RemoveSavedItemsAsync(IReadOnlyList<ProfileSavedItemKeyDto> items, CancellationToken ct = default);
    Task<IReadOnlyList<ProfileSavedItemDto>?> RestoreSavedItemsAsync(IReadOnlyList<ProfileSavedItemDto> items, CancellationToken ct = default);
    Task<IReadOnlyList<ProfileReactionDto>> GetProfileReactionsAsync(CancellationToken ct = default);
    Task<ProfileReactionDto?> GetProfileReactionAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);
    Task<ProfileStateMutationDto?> SetProfileReactionAsync(ProfileEntityKind entityKind, Guid entityId, ProfileReactionKind reaction, CancellationToken ct = default);
    Task<bool> RemoveProfileReactionAsync(ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);
}
