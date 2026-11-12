using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Contracts;

public interface IProfileStateRepository
{
    Task<IReadOnlyList<ProfileSavedItem>> GetSavedItemsAsync(Guid profileId, CancellationToken ct = default);
    Task<ProfileSavedItem?> GetSavedItemAsync(Guid profileId, ProfileEntityKind entityKind, Guid entityId, CancellationToken ct = default);
    Task<ProfileSavedItem> SaveItemAsync(Guid profileId, ProfileEntityKind entityKind, Guid entityId, int? position = null, CancellationToken ct = default);
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
