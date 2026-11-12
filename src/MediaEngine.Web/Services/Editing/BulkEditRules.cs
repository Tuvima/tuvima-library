using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Who may be ticked, which bulk actions the selection bar offers for the ticked rows, and the plain words around them (universal
/// editor spec D14). The bar hides what does not make sense; the panel and the Engine check again.
/// </summary>
public static class BulkEditRules
{
    /// <summary>Shown on a field whose ticked items disagree; nothing is staged until the field is changed.</summary>
    public const string MixedLeaveUnchanged = "Mixed, leave unchanged";

    /// <summary>The most titles the Engine deletes in one request (its own cap, <c>LibraryItemDeletionService.MaxWorks</c>); more than this asks to delete fewer.</summary>
    public const int MaxDeleteItems = 200;

    /// <summary>Why Delete is off above the cap.</summary>
    public const string DeleteLimitReason = "Delete up to 200 at a time";

    /// <summary>A row can be ticked when it is one title (a track, episode, audiobook part, book, issue or movie), never a season, album or series.</summary>
    public static bool IsSelectableKind(DetailEntityType kind) => LibraryItemDeletionFlow.IsSingleTitle(kind);

    /// <summary>
    /// Move is offered when every ticked row is the same kind of single title: the destination picker only offers places that accept
    /// that kind (D15), so a mixed selection has no one right answer.
    /// </summary>
    public static bool CanMove(IReadOnlyList<BulkEditRow> rows) =>
        rows.Count > 0
        && rows.All(row => IsSelectableKind(row.Kind))
        && rows.Select(row => row.Kind).Distinct().Count() == 1;

    /// <summary>
    /// Match is offered for songs of one album and episodes of one season only (spec D14): the Engine pairs each ticked row with the
    /// entry of that album or season it fits. Anything else has no list the ticked rows share.
    /// </summary>
    public static bool CanMatch(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null)
    {
        if (rows.Count == 0 || rows[0].ParentId is not { } parent || parent == Guid.Empty || rows.Any(row => row.ParentId != parent))
        {
            return false;
        }

        return AllEpisodes(rows) || AllSongs(rows, pageMediaType);
    }

    /// <summary>"Put in a series" is offered when every ticked row is a video: the same series chooser as one video, applied to each.</summary>
    public static bool CanPutInSeries(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null) => AllVideos(rows, pageMediaType);

    public const string PutInSeriesLabel = "Put in a series";

    /// <summary>Renumber sequentially is for music: songs of one album, numbered in the order the list shows them.</summary>
    public static bool CanRenumber(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null) =>
        rows.Count > 0 && rows[0].ParentId is { } parent && parent != Guid.Empty && rows.All(row => row.ParentId == parent) && AllSongs(rows, pageMediaType);

    /// <summary>Change album is for music rows (any album); the destination picker only offers albums.</summary>
    public static bool CanChangeAlbum(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null) =>
        rows.Count > 0 && AllSongs(rows, pageMediaType);

    /// <summary>
    /// Every ticked row must be one title (a whole show or album is never deleted in one go). Who may delete is the single Delete's own gate
    /// (<see cref="LibraryItemDeletionFlow.CanDelete"/>), which the caller checks as well.
    /// </summary>
    public static bool AreDeletable(IReadOnlyList<BulkEditRow> rows) =>
        rows.Count > 0 && rows.All(row => IsSelectableKind(row.Kind));

    /// <summary>
    /// "Add to collection" is offered when every ticked row is a single title (the rows a collection can hold), or every ticked row is an
    /// album (Listen music spec D15). Songs go into playlists instead and are never offered it (spec D9), whatever else is ticked with them.
    /// </summary>
    public static bool CanAddToCollection(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null) =>
        !rows.Any(row => IsSong(row, pageMediaType)) && (IsAlbumSelection(rows) || AreDeletable(rows));

    /// <summary>
    /// "Add to playlist" is the Listen kind rule (spec D9, D15), decided here and nowhere else: ticked songs and ticked albums are offered it
    /// (an album adds all its songs); audiobooks, books, videos and everything else are not.
    /// </summary>
    public static bool CanAddToPlaylist(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null) =>
        AllSongs(rows, pageMediaType) || IsAlbumSelection(rows);

    /// <summary>
    /// True when every ticked row is a song. Ticked songs are played, queued and put in playlists on any desktop or tablet by anyone who may
    /// play, without the right to edit (spec D9); only Edit, Match and Move need that.
    /// </summary>
    public static bool IsSongSelection(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null) => AllSongs(rows, pageMediaType);

    /// <summary>True when every ticked row is an album (its songs are added to a playlist, in album order).</summary>
    public static bool IsAlbumSelection(IReadOnlyList<BulkEditRow> rows) =>
        rows.Count > 0 && rows.All(row => row.Kind == DetailEntityType.MusicAlbum);

    private static bool IsSong(BulkEditRow row, string? pageMediaType) =>
        row.Kind == DetailEntityType.Work && string.Equals(row.MediaType ?? pageMediaType, "Music", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Add 3 to collection": the ticked-rows bar's button (Collections and lists spec 4a).</summary>
    public static string AddToCollectionLabel(int count) => $"Add {count} to collection";

    /// <summary>The bar's "..." menu: Delete from library, last and red, only for the person who may delete, and off with its reason above the Engine's cap.</summary>
    public static IReadOnlyList<DetailAction> BarMenuActions(IReadOnlyList<BulkEditRow> rows, bool canDelete, bool busy)
    {
        if (!canDelete || !AreDeletable(rows))
        {
            return [];
        }

        var overLimit = rows.Count > MaxDeleteItems;
        return
        [
            new DetailAction
            {
                Key = DetailActionKeys.DeleteFromLibrary,
                Label = DetailMenuGrouping.DeleteFromLibraryAction.Label,
                Icon = DetailMenuGrouping.DeleteFromLibraryAction.Icon,
                IsDestructive = true,
                IsDisabled = busy || overLimit,
                Tooltip = overLimit ? DeleteLimitReason : null,
            },
        ];
    }

    /// <summary>"song", "episode" or "item": the plain noun for what is ticked.</summary>
    public static string Noun(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null)
    {
        var plural = rows.Count != 1;
        if (AllEpisodes(rows))
        {
            return plural ? "episodes" : "episode";
        }

        if (AllSongs(rows, pageMediaType))
        {
            return plural ? "songs" : "song";
        }

        if (AllVideos(rows, pageMediaType))
        {
            return plural ? "videos" : "video";
        }

        return plural ? "items" : "item";
    }

    /// <summary>The pop-over's title while several ticked items are edited together: "Editing 4 songs".</summary>
    public static string EditingTitle(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null) =>
        $"Editing {rows.Count} {Noun(rows, pageMediaType)}";

    /// <summary>The Move entry's label for these rows: "Move to album" or "Move to season" for music and TV, else the single Move's own wording.</summary>
    public static string MoveLabel(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null)
    {
        if (AllEpisodes(rows))
        {
            return "Move to season";
        }

        return AllSongs(rows, pageMediaType) ? "Move to album" : LibraryMoveText.MoveLabel(MediaTypeOf(rows, pageMediaType));
    }

    // -- Several items by rules (spec 3c, WP9) ---------------------------------------------------------------------------------------

    /// <summary>Fields that identify one item. They are never offered for several, whatever the kind.</summary>
    private static readonly HashSet<string> IdentifyingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "original_title", "sort_title", "subtitle", "track_number", "episode_number", "issue_number", "isbn", "file_name", "filename",
    };

    /// <summary>Fields whose value must differ from item to item: giving them one value would make the items collide.</summary>
    private static readonly Dictionary<string, string> UniquePerItem = new(StringComparer.OrdinalIgnoreCase)
    {
        ["track_number"] = "track",
        ["episode_number"] = "episode",
        ["issue_number"] = "issue",
    };

    /// <summary>Fields that describe a group of one kind only; offered only when every ticked item is that kind.</summary>
    private static readonly HashSet<string> KindSpecificKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "album", "album_artist", "disc_number", "series", "author", "explicit", "season", "show",
    };

    /// <summary>What the ticked rows are, when they are all one kind. <see cref="BulkItemKind.Mixed"/> offers only what every kind shares (tags).</summary>
    public static BulkItemKind KindOf(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null)
    {
        if (rows.Count == 0)
        {
            return BulkItemKind.None;
        }

        if (AllEpisodes(rows))
        {
            return BulkItemKind.Episode;
        }

        if (AllSongs(rows, pageMediaType))
        {
            return BulkItemKind.Song;
        }

        if (AllVideos(rows, pageMediaType))
        {
            return BulkItemKind.Video;
        }

        var allBooks = rows.All(row =>
            row.Kind == DetailEntityType.Work
            && (row.MediaType ?? pageMediaType) is { } type
            && (string.Equals(type, "Books", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "Audiobooks", StringComparison.OrdinalIgnoreCase)));
        return allBooks ? BulkItemKind.Book : BulkItemKind.Mixed;
    }

    /// <summary>The group fields a kind offers (spec 3c), in the order they are shown. Tags are offered for everything.</summary>
    public static IReadOnlyList<string> GroupFieldKeys(BulkItemKind kind) => kind switch
    {
        BulkItemKind.Song => ["album", "album_artist", "genre", "year", "disc_number", "custom_tags", "explicit"],
        BulkItemKind.Video => ["custom_tags", "explicit"],
        BulkItemKind.Book => ["series", "author", "custom_tags"],
        BulkItemKind.Episode => ["custom_tags"],
        BulkItemKind.None => [],
        _ => ["custom_tags"],
    };

    /// <summary>True when a field with this key identifies one item (title, track or episode number, ISBN, file name).</summary>
    public static bool IsIdentifying(string key) => IdentifyingKeys.Contains(key);

    /// <summary>
    /// Whether a field is offered for these items: never an identifying one; a kind-specific group field only for its kind; everything else
    /// the Engine lists as changeable stays as it was.
    /// </summary>
    public static bool IsOffered(string key, BulkItemKind kind) =>
        !IsIdentifying(key) && (!KindSpecificKeys.Contains(key) || GroupFieldKeys(kind).Contains(key, StringComparer.OrdinalIgnoreCase));

    /// <summary>The fields the Engine reported, without the ones these items must not share.</summary>
    public static IReadOnlyList<BulkFieldView> OfferedFields(IReadOnlyList<BulkFieldView> fields, BulkItemKind kind) =>
        [.. fields.Where(field => IsOffered(field.Key, kind))];

    /// <summary>
    /// Why one value for every item would make them collide ("These 3 songs would all be track 4"), or null when it would not. Only
    /// per-item numbers collide; <paramref name="count"/> is how many items would receive the value.
    /// </summary>
    public static string? CollisionReason(string key, int count, string noun, string? value)
    {
        if (count < 2 || !UniquePerItem.TryGetValue(key, out var label))
        {
            return null;
        }

        var shown = string.IsNullOrWhiteSpace(value) ? string.Empty : $" {value.Trim()}";
        return $"These {count} {noun} would all be {label}{shown}.";
    }

    /// <summary>
    /// Said above the artwork choice: which picture one choice sets for all, in words. Null when the items share no picture role. Only songs
    /// of one album share one (the album's cover); books, videos and episodes each have a picture of their own, so one choice cannot be set
    /// for several of them.
    /// </summary>
    public static string? ArtworkSentence(BulkItemKind kind, int count) => kind switch
    {
        BulkItemKind.Song => $"Set one cover for these {count} songs. It sets the album's cover.",
        _ => null,
    };

    /// <summary>
    /// The album whose cover one choice sets, when every ticked row is a song of the same album; null otherwise (songs of several albums,
    /// or any other kind, share no picture role).
    /// </summary>
    public static Guid? SharedArtworkTarget(IReadOnlyList<BulkEditRow> rows, string? pageMediaType = null)
    {
        if (!AllSongs(rows, pageMediaType) || rows[0].ParentId is not { } album || album == Guid.Empty || rows.Any(row => row.ParentId != album))
        {
            return null;
        }

        return album;
    }

    /// <summary>The Explicit mark's key; its box is a switch, not a text box.</summary>
    public const string ExplicitKey = "explicit";

    /// <summary>What a field with no value is written as, so staging "Off" on items that were never marked is not counted as a change.</summary>
    public static string NormalizeForCompare(string key, string value) =>
        string.Equals(key, ExplicitKey, StringComparison.OrdinalIgnoreCase) && value.Length == 0 ? "false" : value;

    public static string SelectedLabel(int count) => $"{count} selected";

    public static string SelectLabel(string title) => $"Select {title}";

    private static bool AllEpisodes(IReadOnlyList<BulkEditRow> rows) =>
        rows.Count > 0 && rows.All(row => row.Kind == DetailEntityType.TvEpisode);

    private static bool AllVideos(IReadOnlyList<BulkEditRow> rows, string? pageMediaType) =>
        rows.Count > 0
        && rows.All(row => IsSelectableKind(row.Kind) && string.Equals(row.MediaType ?? pageMediaType, "Videos", StringComparison.OrdinalIgnoreCase));

    private static bool AllSongs(IReadOnlyList<BulkEditRow> rows, string? pageMediaType) =>
        rows.Count > 0 && rows.All(row => IsSong(row, pageMediaType));

    private static string? MediaTypeOf(IReadOnlyList<BulkEditRow> rows, string? pageMediaType) =>
        rows.Select(row => row.MediaType).FirstOrDefault(type => !string.IsNullOrWhiteSpace(type)) ?? pageMediaType;
}
