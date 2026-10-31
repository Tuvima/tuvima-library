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
    bool AttentionDot = false,
    MediaSectionAttentionLevel Attention = MediaSectionAttentionLevel.None,
    string? AttentionLabel = null,
    IReadOnlyList<string>? ActiveRoutes = null,
    IReadOnlyList<string>? InactiveRoutes = null)
{
    /// <summary>
    /// The row's own level or, for a parent, the most urgent level of any row beneath it, so a collapsed parent still
    /// shows that something inside needs a look.
    /// </summary>
    public MediaSectionAttentionLevel EffectiveAttention
    {
        get
        {
            var level = Attention;
            if (Children is not null)
            {
                foreach (var child in Children)
                {
                    var childLevel = child.EffectiveAttention;
                    if (childLevel > level)
                    {
                        level = childLevel;
                    }
                }
            }

            return level;
        }
    }
}

/// <summary>
/// How much a menu row needs the person's attention. Ordered by urgency: <see cref="Waiting"/> is something an
/// administrator can resolve (the amber "waiting" colour); <see cref="Blocked"/> is something that cannot work (red).
/// </summary>
public enum MediaSectionAttentionLevel
{
    None = 0,
    Waiting = 1,
    Blocked = 2,
}

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
    MediaSectionNavigationDropTarget? ManualDropTarget = null,
    bool OpensCollectionFlow = false);

public abstract record MediaSectionNavigationCreateTarget;

public sealed record ManualPlaylistCreateTarget : MediaSectionNavigationCreateTarget;

public sealed record SmartPlaylistCreateTarget : MediaSectionNavigationCreateTarget;

/// <summary>The rail "+" on Watch and Read: opens the New collection flow with that area's media type chips ticked.</summary>
public sealed record CollectionFlowCreateTarget(string Area) : MediaSectionNavigationCreateTarget;

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
