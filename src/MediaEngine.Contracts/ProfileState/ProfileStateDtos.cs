using MediaEngine.Domain.Enums;

namespace MediaEngine.Contracts.ProfileState;

public sealed record ProfileSavedItemDto(
    ProfileEntityKind EntityKind,
    Guid EntityId,
    DateTimeOffset SavedAt,
    int? Position);

/// <summary>
/// Move one song to a new place in the profile's Favorites. Name the neighbour: <paramref name="BeforeEntityId"/> puts the song directly
/// before that favorite, and sending neither field puts it last, so a heart added or removed elsewhere meanwhile cannot shift the target.
/// <paramref name="ToIndex"/> (counts the songs the profile sees, from 0 at the top) still works but cannot be sent with a neighbour.
/// </summary>
public sealed record ProfileSavedItemMoveRequest(int? ToIndex = null, Guid? BeforeEntityId = null);

/// <summary>One saved item named by kind and id, for the My List remove-many action.</summary>
public sealed record ProfileSavedItemKeyDto(
    ProfileEntityKind EntityKind,
    Guid EntityId);

/// <summary>Body of <c>POST /saved/remove</c>: up to <see cref="MaxItems"/> items to take off the signed-in profile's My List.</summary>
public sealed record RemoveSavedItemsRequest(IReadOnlyList<ProfileSavedItemKeyDto> Items)
{
    public const int MaxItems = 500;
}

/// <summary>Body of <c>POST /saved/restore</c>: up to <see cref="MaxItems"/> items (as returned by remove) to save again with their original dates.</summary>
public sealed record RestoreSavedItemsRequest(IReadOnlyList<ProfileSavedItemDto> Items)
{
    public const int MaxItems = 500;
}

public sealed record ProfileReactionDto(
    ProfileEntityKind EntityKind,
    Guid EntityId,
    ProfileReactionKind Reaction,
    DateTimeOffset UpdatedAt);

public sealed record ProfileStateMutationDto(
    ProfileEntityKind EntityKind,
    Guid EntityId,
    bool IsSaved,
    ProfileReactionKind? Reaction,
    DateTimeOffset UpdatedAt);
