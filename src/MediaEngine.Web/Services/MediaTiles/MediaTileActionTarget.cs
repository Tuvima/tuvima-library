using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>
/// What a cover's overlay buttons ("+" My List and the "..." menu) act on (Home, detail and tiles spec D6, D8). A tile that is not a
/// movie, show, book, audiobook, comic or album (a person, a Series group, a shelf) has no target and draws no buttons.
/// </summary>
/// <param name="Id">The title's own id: the show for an episode tile.</param>
/// <param name="Title">The name the menu and its dialogs use.</param>
/// <param name="SavedKind">What My List stores it as.</param>
/// <param name="EntityType">What the item's page is, which the Engine's edit check reads.</param>
/// <param name="MediaType">The editor media type.</param>
/// <param name="GroupKind">The Delete group kind for a show or an album; null for a single title.</param>
public sealed record MediaTileActionTarget(Guid Id, string Title, ProfileEntityKind SavedKind, DetailEntityType EntityType, string MediaType, string? GroupKind)
{
    public bool IsGroup => GroupKind is not null;

    /// <summary>The target of a tile, or null when the tile is not a title that can be saved, edited or deleted.</summary>
    public static MediaTileActionTarget? For(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsPlaylist || item.IsCollection || item.Person is not null)
        {
            return null;
        }

        var id = item.EpisodeContext?.ShowWorkId ?? item.WorkId ?? item.Id;
        if (id == Guid.Empty)
        {
            return null;
        }

        var title = item.EpisodeContext?.ShowTitle ?? item.Title;
        return (item.MediaKind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "movie" or "movies" => new(id, title, ProfileEntityKind.Movie, DetailEntityType.Movie, "Movies", null),
            "tv" => new(id, title, ProfileEntityKind.TvShow, DetailEntityType.TvShow, "TV", LibraryGroupKinds.Show),
            "music" => new(id, title, ProfileEntityKind.Album, DetailEntityType.MusicAlbum, "Music", LibraryGroupKinds.Album),
            "audiobook" or "audiobooks" => new(id, title, ProfileEntityKind.Audiobook, DetailEntityType.Audiobook, "Audiobooks", null),
            "comic" or "comics" => new(id, title, ProfileEntityKind.Comic, DetailEntityType.ComicIssue, "Comics", null),
            "book" or "books" => new(id, title, ProfileEntityKind.Book, DetailEntityType.Book, "Books", null),
            _ => null,
        };
    }

    /// <summary>Restart only shows once the title has been started.</summary>
    public static bool IsStarted(MediaTileViewModel item) =>
        item.ContinuationState != DisplayContinuationState.Unstarted || item.ProgressPct is > 0;

    /// <summary>The page that plays or reads the title from the beginning.</summary>
    public static string RestartUrl(MediaTileViewModel item) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(item.PrimaryNavigationUrl ?? item.NavigationUrl, "restart", "true");

    /// <summary>The pop-over editor's target for this tile.</summary>
    public ListEditTarget ToEditTarget() =>
        ListEditTarget.ForLibraryRow(Id, Title, MediaType) with { Kind = EntityType };
}
