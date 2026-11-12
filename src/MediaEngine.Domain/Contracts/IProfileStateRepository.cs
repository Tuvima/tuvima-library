using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Contracts;

public interface IProfileStateRepository
{
    Task<IReadOnlyList<ProfileSavedItem>> GetSavedItemsAsync(Guid profileId, CancellationToken ct = default);
    Task<ProfileSavedItem?> GetSavedItemAsync(Guid profileId, ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);
    /// <summary>One kind of the profile's saved items in their saved order (Favorites are the Song kind): by position, then newest saved first where a position is missing.</summary>
    Task<IReadOnlyList<ProfileSavedItem>> GetSavedItemsAsync(Guid profileId, ProfileEntityKind entityKind, CancellationToken ct = default);
    /// <summary>Saves an item. A song saved to Favorites with no position goes to the top of the profile's Favorites.</summary>
    Task<ProfileSavedItem> SaveItemAsync(Guid profileId, ProfileEntityKind entityKind, Guid entityId, int? position = null, CancellationToken ct = default);
    /// <summary>
    /// Moves one saved item to a new index within the profile's saved items of the same kind and writes the whole order back (0, 1, 2 …) in
    /// one transaction. Nothing changes unless the result is <see cref="SavedItemMoveResult.Moved"/>.
    /// </summary>
    Task<SavedItemMoveResult> MoveSavedItemAsync(Guid profileId, ProfileEntityKind entityKind, Guid entityId, int toIndex, CancellationToken ct = default);
    Task<bool> RemoveSavedItemAsync(Guid profileId, ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);

    Task<IReadOnlyList<ProfileReactionState>> GetReactionsAsync(Guid profileId, CancellationToken ct = default);
    Task<ProfileReactionState?> GetReactionAsync(Guid profileId, ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);
    Task<ProfileReactionState> SetReactionAsync(Guid profileId, ProfileEntityKind entityKind, Guid entityId, ProfileReactionKind reaction, CancellationToken ct = default);
    Task<bool> RemoveReactionAsync(Guid profileId, ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);
}

public enum SavedItemMoveResult
{
    Moved,
    NotSaved,
    IndexOutOfRange,
}

public sealed record ProfileSavedItem(
    Guid ProfileId,
    ProfileEntityKind EntityKind,
    Guid EntityId,
    DateTimeOffset SavedAt,
    int? Position);

public sealed record ProfileReactionState(
    Guid ProfileId,
    ProfileEntityKind EntityKind,
    Guid EntityId,
    ProfileReactionKind Reaction,
    DateTimeOffset UpdatedAt);
