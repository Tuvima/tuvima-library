using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
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
/// <param name="CanSave">False when the Engine has no My List entry for this kind of group (a Series).</param>
/// <param name="CanEdit">False when the item has no editor of its own (a playlist, an audiobook Series).</param>
/// <param name="CanDelete">False for a Series, curated collection, Universe or playlist: its Delete is not on the cover.</param>
public sealed record MediaTileActionTarget(Guid Id, string Title, ProfileEntityKind SavedKind, DetailEntityType EntityType, string MediaType, string? GroupKind, bool CanSave = true, bool CanEdit = true, bool CanDelete = true)
{
    public bool IsGroup => GroupKind is not null;

    /// <summary>The target of a tile, or null when the tile is not a title that can be saved, edited or deleted.</summary>
    public static MediaTileActionTarget? For(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        // A show or an album is a title with its own menu (item 30b). A playlist and a group of titles (Series, curated collection,
        // Universe) have a smaller one: only what the Engine already does for them (One tile design D3).
        var isShowOrAlbumGroup = item.Presentation is MediaTilePresentation.TvSeries or MediaTilePresentation.Album;
        if (item.Person is not null)
        {
            return null;
        }

        if (item.IsPlaylist)
        {
            var playlistId = item.CollectionId ?? item.Id;
            return playlistId == Guid.Empty
                ? null
                : new(playlistId, item.Title, ProfileEntityKind.Playlist, DetailEntityType.Collection, "Music", null, CanEdit: false, CanDelete: false);
        }

        if (item.IsCollection && !isShowOrAlbumGroup)
        {
            return ForGroup(item);
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
            // Videos get the same menu as a movie or a show (item 30b): a folder of videos is saved and edited as a show, one video as a movie.
            "videos" => new(id, title, ProfileEntityKind.TvShow, DetailEntityType.TvShow, VideoFolderWording.FolderMediaType, LibraryGroupKinds.Show),
            "video" => new(id, title, ProfileEntityKind.Movie, DetailEntityType.Movie, VideoFolderWording.FolderMediaType, null),
            "music" => new(id, title, ProfileEntityKind.Album, DetailEntityType.MusicAlbum, "Music", LibraryGroupKinds.Album),
            "audiobook" or "audiobooks" => new(id, title, ProfileEntityKind.Audiobook, DetailEntityType.Audiobook, "Audiobooks", null),
            "comic" or "comics" => new(id, title, ProfileEntityKind.Comic, DetailEntityType.ComicIssue, "Comics", null),
            "book" or "books" => new(id, title, ProfileEntityKind.Book, DetailEntityType.Book, "Books", null),
            _ => null,
        };
    }

    // A Series, curated collection or Universe: My List for the curated kinds, Edit through the editor's own answer, never Delete (a Universe tile gets neither My List nor Edit).
    private static MediaTileActionTarget? ForGroup(MediaTileViewModel item)
    {
        var id = item.CollectionId ?? item.Id;
        if (id == Guid.Empty)
        {
            return null;
        }

        // A Universe is not a Collection: it has no My List entry, and its Edit is the Universe page's own (an administrator unlock and the
        // Universe's Wikidata id, not a collection id), which a tile cannot open. So its tile offers neither rather than a wrong one.
        if (item.GroupKind == MediaTileGroupKind.Universe)
        {
            return new(id, item.Title, ProfileEntityKind.Collection, DetailEntityType.Universe, "Books", null, CanSave: false, CanEdit: false, CanDelete: false);
        }

        var isSeries = item.GroupKind == MediaTileGroupKind.Series
            || item.Presentation is MediaTilePresentation.MovieSeries or MediaTilePresentation.BookSeries
                or MediaTilePresentation.ComicSeries or MediaTilePresentation.AudiobookSeries;
        var (kind, mediaType, canEdit) = item.Presentation switch
        {
            MediaTilePresentation.MovieSeries => (DetailEntityType.MovieSeries, "Movies", true),
            MediaTilePresentation.BookSeries => (DetailEntityType.BookSeries, "Books", true),
            MediaTilePresentation.ComicSeries => (DetailEntityType.ComicSeries, "Comics", true),
            // No editor kind exists for an audiobook Series yet: no Edit row rather than a guess.
            MediaTilePresentation.AudiobookSeries => (DetailEntityType.Collection, "Audiobooks", false),
            _ => (DetailEntityType.Collection, "Books", true),
        };
        return new(id, item.Title, ProfileEntityKind.Collection, kind, mediaType, null, CanSave: !isSeries, CanEdit: canEdit, CanDelete: false);
    }

    /// <summary>Restart only shows once the title has been started.</summary>
    public static bool IsStarted(MediaTileViewModel item) =>
        item.ContinuationState != DisplayContinuationState.Unstarted || item.ProgressPct is > 0;

    /// <summary>The page that plays or reads the title from the beginning.</summary>
    public static string RestartUrl(MediaTileViewModel item) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(item.PrimaryNavigationUrl ?? item.NavigationUrl, "restart", "true");

    /// <summary>
    /// What a Continue tile's "Go to ..." names: "show" for an episode, otherwise the kind of the title ("movie", "book", "audiobook",
    /// "comic", "album") or "series" for a group; null when the kind is not known (the row then reads "Go to title").
    /// </summary>
    public static string? GoToKind(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // A folder of Other videos is built like a show but is a folder (item 17).
        if (VideoFolderWording.IsVideoFolderMediaType(item.MediaKind))
        {
            return "folder";
        }

        if (item.EpisodeContext is not null || item.Subject == DisplaySubjectKind.TvEpisode || item.Subject == DisplaySubjectKind.TvShow)
        {
            return "show";
        }

        if (item.IsCollection && item.Presentation is not (MediaTilePresentation.Album or MediaTilePresentation.TvSeries))
        {
            return "series";
        }

        if (item.Presentation == MediaTilePresentation.Album)
        {
            return "album";
        }

        return (item.MediaKind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "movie" or "movies" => "movie",
            "tv" => "show",
            "music" => "album",
            "audiobook" or "audiobooks" => "audiobook",
            "comic" or "comics" => "comic",
            "book" or "books" => "book",
            _ => null,
        };
    }

    /// <summary>The title's own page, for "Go to ...": the show's page for an episode, otherwise the page the tile opens.</summary>
    public static string GoToUrl(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.EpisodeContext is { } episode && episode.ShowWorkId != Guid.Empty)
        {
            return $"/details/tvshow/{episode.ShowWorkId:D}?context=watch";
        }

        return item.DetailsNavigationUrl ?? item.NavigationUrl;
    }

    /// <summary>The pop-over editor's target for this tile.</summary>
    public ListEditTarget ToEditTarget() =>
        ListEditTarget.ForLibraryRow(Id, Title, MediaType) with { Kind = EntityType };
}
