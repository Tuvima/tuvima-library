using MediaEngine.Domain.Enums;

namespace MediaEngine.Contracts.ProfileState;

public sealed record ProfileSavedItemDto(
    ProfileEntityKind EntityKind,
    Guid EntityId,
    DateTimeOffset SavedAt,
    int? Position);

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
