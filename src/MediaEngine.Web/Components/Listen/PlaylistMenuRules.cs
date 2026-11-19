using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Components.Listen;

/// <summary>What the playlist "..." menu can do (spec listen-playlists D7).</summary>
public enum PlaylistMenuAction
{
    EditRules,
    Rename,
    EditDetails,
    Duplicate,
    TogglePin,
    Delete,
}

/// <summary>Which of the playlist page's two small windows is open: the name, or the description and cover.</summary>
public enum PlaylistEditMode
{
    Rename,
    Details,
}

/// <summary>
/// Which actions the one playlist menu offers for a playlist, so the sidebar, a tile and the playlist's own page all show the same
/// menu. A hand-made playlist gets Rename, Edit description and cover, Duplicate, Pin to top and Delete; a Smart list adds Edit rules
/// first and offers Duplicate only when the Engine can copy it (a rule-based list holds no songs of its own to copy). Favorites and the
/// other built-in lists have no menu. There is no M3U item until that spec is built.
/// </summary>
public static class PlaylistMenuRules
{
    /// <summary>The actions in the order they are drawn; empty for a list that has no menu.</summary>
    public static IReadOnlyList<PlaylistMenuAction> ActionsFor(ManagedCollectionViewModel playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        var smart = string.Equals(playlist.CollectionType, "Smart", StringComparison.OrdinalIgnoreCase);
        var handMade = string.Equals(playlist.CollectionType, "Playlist", StringComparison.OrdinalIgnoreCase);
        if (!smart && !handMade)
        {
            return [];
        }

        var actions = new List<PlaylistMenuAction>();
        if (smart)
        {
            actions.Add(PlaylistMenuAction.EditRules);
        }

        actions.Add(PlaylistMenuAction.Rename);
        actions.Add(PlaylistMenuAction.EditDetails);
        if (!smart || string.Equals(playlist.Resolution, "materialized", StringComparison.OrdinalIgnoreCase))
        {
            actions.Add(PlaylistMenuAction.Duplicate);
        }

        actions.Add(PlaylistMenuAction.TogglePin);
        actions.Add(PlaylistMenuAction.Delete);
        return actions;
    }

    /// <summary>The playlist as the collection editor wants it, for Edit rules on a Smart list (the page's own menu and the sidebar and tiles all open the same editor).</summary>
    public static CollectionListItemViewModel EditorItemFor(ManagedCollectionViewModel playlist) => new()
    {
        Id = playlist.Id,
        Name = playlist.Name,
        Description = playlist.Description,
        IconName = playlist.IconName,
        CollectionType = playlist.CollectionType,
        Resolution = playlist.Resolution,
        Scope = playlist.Scope,
        ProfileId = playlist.ProfileId,
        Visibility = playlist.Visibility,
        MembershipMode = playlist.MembershipMode,
        PrimaryArea = playlist.PrimaryArea,
        OwnerKind = playlist.OwnerKind,
        Audience = playlist.Audience,
        SelectedProfileIds = playlist.SelectedProfileIds,
        IsEnabled = playlist.IsEnabled,
        IsFeatured = playlist.IsFeatured,
        MinItems = playlist.MinItems,
        MaxItems = playlist.MaxItems,
        SeasonFrom = playlist.SeasonFrom,
        SeasonTo = playlist.SeasonTo,
        HiddenUntil = playlist.HiddenUntil,
        ItemCount = playlist.ItemCount,
        RuleJson = playlist.RuleJson,
        RuleHash = playlist.RuleHash,
        MatchMode = playlist.MatchMode,
        SortField = playlist.SortField,
        SortDirection = playlist.SortDirection,
        SecondarySortField = playlist.SecondarySortField,
        SecondarySortDirection = playlist.SecondarySortDirection,
        CoverUrl = playlist.CoverArtworkUrl,
        BackgroundUrl = playlist.BackgroundArtworkUrl,
        BannerUrl = playlist.BannerArtworkUrl,
        LogoUrl = playlist.LogoArtworkUrl,
        CreatedAt = playlist.CreatedAt,
        CanEdit = playlist.CanEdit,
        CanShare = playlist.CanShare,
    };
}
