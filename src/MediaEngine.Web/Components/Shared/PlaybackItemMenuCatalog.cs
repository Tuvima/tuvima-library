using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Components.Shared;

/// <summary>The rows of the item actions menu (unified player v2 D23). The row keys are stable; the labels follow the media.</summary>
public enum PlaybackItemActionKey
{
    AddToPlaylist,
    MarkDone,
    MarkNotDone,
    History,
    GoToAlbum,
    GoToArtist,
    GoToBook,
    GoToAuthor,
    GoToShow,
    GoToSeason,
    GoToEpisode,
    GoToMovie,
    PlaybackSettings,
}

/// <summary>One row of the menu: what it is, how it reads, the 24 px glyph in front of it and whether a separator is drawn above it.</summary>
/// <param name="Route">The page a "Go to" row opens; null for the other rows.</param>
public sealed record PlaybackItemAction(
    PlaybackItemActionKey Key,
    string Label,
    PlaybackUtilityGlyphKind Glyph,
    bool DividerBefore = false,
    string? Route = null);

/// <summary>What the menu is built from.</summary>
/// <param name="Media">Which of the four media the item belongs to.</param>
/// <param name="Item">The item that is playing.</param>
/// <param name="IsDone">True when the item is already marked listened, finished or watched, so the row offers the opposite. Null while that is not known yet.</param>
/// <param name="CanMark">False when the Engine has nothing to mark for this item, which leaves the Mark row out.</param>
/// <param name="ShowWorkId">A TV episode's show, once the presentation context has loaded; the show, season and episode rows need it.</param>
/// <param name="SeasonId">The episode's season, when the presentation context names one.</param>
/// <param name="CanOpenPlaybackSettings">False leaves the Playback settings row out (a host with nowhere to open it).</param>
public sealed record PlaybackItemMenuContext(
    PlaybackBarMedia Media,
    ListenQueueItem Item,
    bool? IsDone = null,
    bool CanMark = true,
    Guid? ShowWorkId = null,
    Guid? SeasonId = null,
    bool CanOpenPlaybackSettings = true);

/// <summary>
/// The item actions menu of unified player v2 D23: Music: Add to playlist, Mark as listened or unlistened, Listening history, Go to album, Go to
/// artist, Playback settings. Audiobook: the same with finished and Go to book and Go to author. TV: Mark as watched or unwatched, Watch history, Go
/// to show, season and episode. Movie: Mark as watched or unwatched, Watch history, Go to movie. Favorite and ratings are not repeated here. A
/// "Go to" row whose page cannot be named (no album, no author, no show yet) is left out rather than drawn as a dead row.
/// </summary>
public static class PlaybackItemMenuCatalog
{
    public static IReadOnlyList<PlaybackItemAction> Build(PlaybackItemMenuContext context)
    {
        var rows = new List<PlaybackItemAction>();
        var item = context.Item;
        var done = context.IsDone == true;

        void Add(PlaybackItemActionKey key, string label, PlaybackUtilityGlyphKind glyph, bool divider = false, string? route = null) =>
            rows.Add(new(key, label, glyph, divider && rows.Count > 0, route));

        void AddMarkAndHistory(bool divider, string markLabel, string unmarkLabel, string historyLabel)
        {
            var first = true;
            if (context.CanMark)
            {
                Add(done ? PlaybackItemActionKey.MarkNotDone : PlaybackItemActionKey.MarkDone,
                    done ? unmarkLabel : markLabel,
                    done ? PlaybackUtilityGlyphKind.Reset : PlaybackUtilityGlyphKind.Check, divider);
                first = false;
            }
            Add(PlaybackItemActionKey.History, historyLabel, PlaybackUtilityGlyphKind.Clock, divider && first);
        }

        switch (context.Media)
        {
            case PlaybackBarMedia.Music:
                Add(PlaybackItemActionKey.AddToPlaylist, "Add to playlist…", PlaybackUtilityGlyphKind.Playlist);
                AddMarkAndHistory(true, "Mark as listened", "Mark as unlistened", "Listening history");
                var album = ListenPlaybackIdentityRoutes.Album(item);
                var artist = ListenPlaybackIdentityRoutes.Artist(item);
                if (album is not null)
                {
                    Add(PlaybackItemActionKey.GoToAlbum, "Go to album", PlaybackUtilityGlyphKind.Disc, true, album);
                }
                if (artist is not null)
                {
                    Add(PlaybackItemActionKey.GoToArtist, "Go to artist", PlaybackUtilityGlyphKind.Person, album is null, artist);
                }
                break;

            case PlaybackBarMedia.Audiobook:
                Add(PlaybackItemActionKey.AddToPlaylist, "Add to playlist…", PlaybackUtilityGlyphKind.Playlist);
                AddMarkAndHistory(true, "Mark as finished", "Mark as unfinished", "Listening history");
                var book = ListenPlaybackIdentityRoutes.Audiobook(item);
                var author = PlaybackItemRoutes.Author(item);
                if (book is not null)
                {
                    Add(PlaybackItemActionKey.GoToBook, "Go to book", PlaybackUtilityGlyphKind.Book, true, book);
                }
                if (author is not null)
                {
                    Add(PlaybackItemActionKey.GoToAuthor, "Go to author", PlaybackUtilityGlyphKind.Person, book is null, author);
                }
                break;

            case PlaybackBarMedia.Tv:
                AddMarkAndHistory(false, "Mark as watched", "Mark as unwatched", "Watch history");
                if (context.ShowWorkId is { } show && show != Guid.Empty)
                {
                    Add(PlaybackItemActionKey.GoToShow, "Go to show", PlaybackUtilityGlyphKind.Episodes, true, PlaybackItemRoutes.Show(show));
                    if (context.SeasonId is { } season && season != Guid.Empty)
                    {
                        Add(PlaybackItemActionKey.GoToSeason, "Go to season", PlaybackUtilityGlyphKind.Queue, false, PlaybackItemRoutes.Season(show, season));
                    }
                }
                Add(PlaybackItemActionKey.GoToEpisode, "Go to episode", PlaybackUtilityGlyphKind.Play, context.ShowWorkId is null,
                    PlaybackItemRoutes.Episode(context.ShowWorkId, item.WorkId));
                break;

            default:
                AddMarkAndHistory(false, "Mark as watched", "Mark as unwatched", "Watch history");
                Add(PlaybackItemActionKey.GoToMovie, "Go to movie", PlaybackUtilityGlyphKind.Film, true, PlaybackItemRoutes.Movie(item.WorkId));
                break;
        }

        // Playback settings closes the music and audiobook menus; video has its own Settings tool in the bar.
        if (context.CanOpenPlaybackSettings && context.Media is PlaybackBarMedia.Music or PlaybackBarMedia.Audiobook)
        {
            Add(PlaybackItemActionKey.PlaybackSettings, "Playback settings", PlaybackUtilityGlyphKind.Settings, true);
        }

        return rows;
    }
}

/// <summary>The library pages the bar's links and the item actions menu open for the item that is playing.</summary>
public static class PlaybackItemRoutes
{
    public const string PlaybackSettings = "/settings/playback";

    public static string Show(Guid showWorkId) => $"/details/tvshow/{showWorkId:D}?context=watch";

    public static string Season(Guid showWorkId, Guid seasonId) => $"/details/tvshow/{showWorkId:D}?context=watch&season={seasonId:D}";

    /// <summary>The episode's page under its show; without the show (its context has not loaded) the episode's own page.</summary>
    public static string Episode(Guid? showWorkId, Guid episodeWorkId) => showWorkId is { } show && show != Guid.Empty && episodeWorkId != Guid.Empty
        ? MediaEngine.Contracts.Details.TvEpisodeDetailRoute.Build(show, episodeWorkId, "watch")
        : $"/details/work/{episodeWorkId:D}?context=watch";

    public static string Movie(Guid workId) => $"/details/work/{workId:D}?context=watch";

    /// <summary>The first author with a page of their own.</summary>
    public static string? Author(ListenQueueItem? item) => item?.Authors
        .Select(ListenPlaybackIdentityRoutes.Contributor)
        .FirstOrDefault(route => route is not null);

    /// <summary>
    /// The page a video bar link names, or null for plain text: "show" is the show, "episode" this episode, "movie" this film.
    /// </summary>
    public static string? ForVideoLink(string kind, Guid id, Guid? showWorkId, ListenQueueItem? item) => kind switch
    {
        "show" when showWorkId == id && id != Guid.Empty => Show(id),
        "episode" when item?.WorkId == id && id != Guid.Empty => Episode(showWorkId, id),
        "movie" when item?.WorkId == id && id != Guid.Empty => Movie(id),
        _ => null,
    };
}
