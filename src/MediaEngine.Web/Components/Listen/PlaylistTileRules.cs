using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Components.Listen;

/// <summary>How the Playlists page orders its tiles (spec listen-playlists WP3, mockup A "Sort: Manual").</summary>
public enum PlaylistTileSort
{
    /// <summary>The order this profile keeps (it follows the person across devices).</summary>
    Manual,

    /// <summary>By name, A to Z.</summary>
    Name,

    /// <summary>Newest first.</summary>
    RecentlyAdded,
}

/// <summary>What the New playlist window answered: the name typed and whether the person chose Smart (the rule editor) or Hand-picked.</summary>
public sealed record NewPlaylistChoice(string Name, bool IsSmart);

/// <summary>The rules the Playlists page and its tiles share, kept apart from the markup so they can be tested alone.</summary>
public static class PlaylistTileRules
{
    /// <summary>The sort choices in the order the Sort menu lists them.</summary>
    public static IReadOnlyList<PlaylistTileSort> SortChoices { get; } = [PlaylistTileSort.Manual, PlaylistTileSort.Name, PlaylistTileSort.RecentlyAdded];

    /// <summary>
    /// The playlists in the order a sort asks for. Whatever the sort, the playlists this profile pinned stay first (Pin to top means top);
    /// Manual keeps the order the caller already gave, which is the profile's own order.
    /// </summary>
    public static List<ManagedCollectionViewModel> Sort(IEnumerable<ManagedCollectionViewModel> playlists, PlaylistTileSort sort)
    {
        ArgumentNullException.ThrowIfNull(playlists);
        var pinnedFirst = playlists.OrderByDescending(playlist => playlist.IsPinned == true);
        return sort switch
        {
            PlaylistTileSort.Name => [.. pinnedFirst.ThenBy(playlist => playlist.Name, StringComparer.CurrentCultureIgnoreCase)],
            PlaylistTileSort.RecentlyAdded => [.. pinnedFirst.ThenByDescending(playlist => playlist.CreatedAt)],
            _ => [.. pinnedFirst],
        };
    }

    /// <summary>A rule-based playlist: its songs come from its rules, so it cannot take dropped songs.</summary>
    public static bool IsSmart(ManagedCollectionViewModel playlist)
        => string.Equals(playlist.CollectionType, "Smart", StringComparison.OrdinalIgnoreCase)
           || string.Equals(playlist.MembershipMode, "Smart", StringComparison.OrdinalIgnoreCase);

    /// <summary>A tile takes dropped songs exactly when the Add to playlist list would offer that playlist (a Smart or system list never).</summary>
    public static bool AcceptsSongDrops(ManagedCollectionViewModel playlist)
        => CollectionAuthoringRule.IsAddTarget(playlist, playlist: true);
}
