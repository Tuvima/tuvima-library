namespace MediaEngine.Contracts.Display;

/// <summary>One collection shown as a lane, as the Lanes settings page lists it.</summary>
/// <param name="CollectionId">The collection.</param>
/// <param name="Name">The collection's name.</param>
/// <param name="CollectionType">The collection's type, for its icon.</param>
/// <param name="IconName">The collection's icon hint, if it has one.</param>
/// <param name="Visible">False when an administrator switched the lane off for everyone (shared lanes only).</param>
public sealed record DisplayLaneDto(Guid CollectionId, string Name, string CollectionType, string? IconName, bool Visible);

/// <summary>What the Lanes settings page shows the active profile for one place (Home, Watch, Read or Listen).</summary>
/// <param name="Location">The place: home, watch, read or listen.</param>
/// <param name="CanManageShared">True for an administrator, who orders the shared lanes and switches them off for everyone.</param>
/// <param name="Shared">The lanes everyone sees, in the administrator's order. Empty for someone who cannot manage them.</param>
/// <param name="Mine">The profile's own collections shown here, in the order the profile set.</param>
/// <param name="HiddenByMe">Shared lanes this profile hid with "Hide this lane", so it can show them again.</param>
public sealed record DisplayLaneSettingsDto(
    string Location,
    bool CanManageShared,
    IReadOnlyList<DisplayLaneDto> Shared,
    IReadOnlyList<DisplayLaneDto> Mine,
    IReadOnlyList<DisplayLaneDto> HiddenByMe);

/// <summary>One shared lane in an order request: the collection and whether everyone sees it.</summary>
public sealed record DisplayLaneOrderEntry(Guid CollectionId, bool Visible);

/// <summary>Body of the action that orders the shared lanes at one place and switches them on or off for everyone.</summary>
public sealed record DisplaySharedLaneOrderRequest(IReadOnlyList<DisplayLaneOrderEntry> Lanes);

/// <summary>Body of the action that orders the active profile's own lanes at one place.</summary>
public sealed record DisplayOwnLaneOrderRequest(IReadOnlyList<Guid> CollectionIds);

/// <summary>Body of the action that hides one lane from the active profile, or shows it again.</summary>
public sealed record DisplayLaneHideRequest(bool Hidden);
