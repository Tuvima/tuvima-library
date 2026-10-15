namespace MediaEngine.Web.Models.ViewDTOs;

public sealed record MediaSectionNavigationItem(
    string Label,
    string Route,
    string Icon,
    string? Meta = null,
    bool Exact = false,
    MediaSectionNavigationDropTarget? DropTarget = null,
    IReadOnlyList<MediaSectionNavigationItem>? Children = null,
    MediaSectionNavigationItemManagement? Management = null,
    bool SectionBreakBefore = false,
    bool MetaIsNotification = false,
    string? MetaAriaLabel = null,
    bool AttentionDot = false);

public sealed record MediaSectionNavigationGroup(
    string Label,
    IReadOnlyList<MediaSectionNavigationItem> Items,
    MediaSectionNavigationCreateOptions? CreateOptions = null);

public sealed record MediaSectionNavigationItemManagement(
    Guid ContainerId,
    string ContainerKind,
    bool IsSmart = false,
    bool CanRename = true,
    bool CanDelete = true,
    bool CanReorder = true);

public sealed record MediaSectionNavigationCreateOptions(
    string ManualLabel,
    string SmartLabel,
    string InputPlaceholder,
    MediaSectionNavigationCreateTarget ManualTarget,
    MediaSectionNavigationCreateTarget SmartTarget,
    MediaSectionNavigationDropTarget? ManualDropTarget = null);

public abstract record MediaSectionNavigationCreateTarget;

public sealed record ManualPlaylistCreateTarget : MediaSectionNavigationCreateTarget;

public sealed record SmartPlaylistCreateTarget : MediaSectionNavigationCreateTarget;

public sealed record ManualGalleryCreateTarget : MediaSectionNavigationCreateTarget;

public sealed record SmartGalleryCreateTarget : MediaSectionNavigationCreateTarget;

/// <summary>
/// Identifies the destination represented by a navigation rail drop target.
/// The concrete record type keeps feature handlers from confusing playlists,
/// Galleries, and other containers that may happen to share a Guid identifier.
/// </summary>
public abstract record MediaSectionNavigationDropTarget;

public sealed record PlaylistNavigationDropTarget(Guid PlaylistId) : MediaSectionNavigationDropTarget;

public sealed record ManualGalleryNavigationDropTarget(Guid GalleryId) : MediaSectionNavigationDropTarget;

public sealed record SmartGalleryNavigationDropTarget(Guid GalleryId) : MediaSectionNavigationDropTarget;

public sealed record NewGalleryNavigationDropTarget : MediaSectionNavigationDropTarget;

public sealed record NewPlaylistNavigationDropTarget : MediaSectionNavigationDropTarget;

public sealed record ContainerNavigationDropTarget(string ContainerKind, Guid? ContainerId = null)
    : MediaSectionNavigationDropTarget;

public sealed record MediaSectionNavigationDropEvent(
    MediaSectionNavigationItem Item,
    MediaSectionNavigationDropTarget Target);

public sealed record MediaSectionNavigationCreateEvent(
    string GroupLabel,
    MediaSectionNavigationCreateTarget Target,
    string Name);

public sealed record MediaSectionNavigationCreateDropEvent(
    string GroupLabel,
    MediaSectionNavigationCreateTarget Target,
    MediaSectionNavigationDropTarget DropTarget);

public sealed record MediaSectionNavigationManageEvent(
    MediaSectionNavigationItem Item,
    MediaSectionNavigationItemManagement Management);

public sealed record MediaSectionNavigationRenameEvent(
    MediaSectionNavigationItem Item,
    MediaSectionNavigationItemManagement Management,
    string Name);

public sealed record MediaSectionNavigationReorderEvent(
    MediaSectionNavigationItem Item,
    MediaSectionNavigationItemManagement Management,
    Guid? BeforeContainerId = null,
    int Direction = 0);
