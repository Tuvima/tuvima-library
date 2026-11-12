using MediaEngine.Domain.Enums;

namespace MediaEngine.Contracts.ProfileState;

public sealed record ProfileSavedItemDto(
    ProfileEntityKind EntityKind,
    Guid EntityId,
    DateTimeOffset SavedAt,
    int? Position);

/// <summary>Move one song to a new place in the profile's Favorites. <paramref name="ToIndex"/> counts the songs the profile sees, from 0 at the top.</summary>
public sealed record ProfileSavedItemMoveRequest(int ToIndex);

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
