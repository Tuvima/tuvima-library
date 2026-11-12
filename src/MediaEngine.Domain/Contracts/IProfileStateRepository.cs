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
    /// <summary>
    /// Moves one saved item so it sits directly before another saved item of the same kind (or last when <paramref name="beforeEntityId"/> is
    /// null). The neighbour is found inside the same write transaction as the move, so a save or removal landing in between cannot shift it.
    /// <see cref="SavedItemMoveResult.NotSaved"/> when either item is not saved by the profile; nothing changes unless the result is
    /// <see cref="SavedItemMoveResult.Moved"/>.
    /// </summary>
    Task<SavedItemMoveResult> MoveSavedItemBeforeAsync(Guid profileId, ProfileEntityKind entityKind, Guid entityId, Guid? beforeEntityId, CancellationToken ct = default);
    Task<bool> RemoveSavedItemAsync(Guid profileId, ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);

    /// <summary>
    /// Removes many of one profile's saved items in one transaction and returns what was actually removed, with each
    /// item's original saved time and position. Items that are not saved are skipped.
    /// </summary>
    Task<IReadOnlyList<ProfileSavedItem>> RemoveSavedItemsAsync(Guid profileId, IReadOnlyList<ProfileSavedKey> items, CancellationToken ct = default);

    /// <summary>
    /// Saves many items for one profile in one transaction with the saved time and position given (undo of a removal).
    /// Items whose target no longer exists are skipped; the rest are returned as stored.
    /// </summary>
    Task<IReadOnlyList<ProfileSavedItem>> RestoreSavedItemsAsync(Guid profileId, IReadOnlyList<ProfileSavedRestore> items, CancellationToken ct = default);

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

public sealed record ProfileSavedKey(ProfileEntityKind EntityKind, Guid EntityId);

public sealed record ProfileSavedRestore(
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
