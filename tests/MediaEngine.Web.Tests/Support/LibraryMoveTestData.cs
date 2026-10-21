using MediaEngine.Contracts.Library;

namespace MediaEngine.Web.Tests.Support;

/// <summary>Move previews the Match step and the Move area tests share: a song going into OK Computer.</summary>
internal static class LibraryMoveTestData
{
    public static readonly Guid SongId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid OtherSongId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    public static readonly Guid AlbumId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    public static LibraryMoveDestination Album(bool hasList = true) => new()
    {
        Id = AlbumId,
        Kind = LibraryMoveDestinationKinds.Album,
        Label = "OK Computer",
        ParentLabel = "Radiohead",
        HasList = hasList,
    };

    public static LibraryMoveEntry Entry(
        string key, string number, string title, Guid? takenBy = null, string? takenTitle = null, bool canAddVersion = false,
        bool joinsExistingTitle = false) => new()
        {
            EntryKey = key,
            Number = number,
            Title = title,
            LengthSeconds = 240,
            TakenByItemId = takenBy,
            TakenByTitle = takenTitle,
            CanAddVersion = canAddVersion,
            JoinsExistingTitle = joinsExistingTitle,
        };

    public static LibraryMovePreviewRow Row(
        Guid? id = null,
        string title = "Paranoid Android",
        string? suggested = "t2",
        string reason = "Same title",
        LibraryMoveFileChange? fileChange = null,
        string? fileNote = null,
        string? blocked = null,
        bool canMatch = true,
        string? mergeBlocked = null) => new()
        {
            ItemId = id ?? SongId,
            Title = title,
            CurrentPlacementLabel = "The Best Of, track 3",
            SuggestedEntryKey = suggested,
            SuggestionReason = suggested is null ? string.Empty : reason,
            FileChange = fileChange,
            FileNote = fileNote,
            BlockedReason = blocked,
            CanMatch = canMatch,
            MergeBlockedReason = mergeBlocked,
        };

    /// <summary>An album with three tracks. Track 1 has a file already unless <paramref name="takenTrackOne"/> is false.</summary>
    public static LibraryMovePreview AlbumPreview(
        IEnumerable<LibraryMovePreviewRow>? rows = null,
        bool takenTrackOne = true,
        bool canAddVersion = true,
        string token = "token-1",
        IEnumerable<string>? warnings = null) => new()
        {
            Destination = Album(),
            ListKind = LibraryMoveListKinds.Tracks,
            Entries =
            [
                Entry("t1", "1", "Airbag", takenTrackOne ? OtherSongId : null, takenTrackOne ? "Airbag (live)" : null, canAddVersion),
                Entry("t2", "2", "Paranoid Android"),
                Entry("t3", "3", "Subterranean Homesick Alien"),
            ],
            Rows = [.. rows ?? [Row()]],
            Warnings = [.. warnings ?? ["Leaves The Best Of"]],
            PreviewToken = token,
        };

    /// <summary>
    /// The album the song already sits in, as the Match step opens on it to change a number: the song holds track 2 (its own entry), track 1
    /// is another song's and track 3 is a catalogue row (an existing title) nobody has a file for.
    /// </summary>
    public static LibraryMovePreview AlbumHoldingTheSongPreview(IEnumerable<LibraryMovePreviewRow>? rows = null, string token = "token-here") => new()
    {
        Destination = Album(),
        ListKind = LibraryMoveListKinds.Tracks,
        Entries =
        [
            Entry("t1", "1", "Airbag", OtherSongId, "Airbag (live)", canAddVersion: true, joinsExistingTitle: true),
            Entry("t2", "2", "Paranoid Android", SongId, "Paranoid Android", canAddVersion: true, joinsExistingTitle: true),
            Entry("t3", "3", "Subterranean Homesick Alien", joinsExistingTitle: true),
            Entry("t4", "4", "Exit Music"),
        ],
        Rows = [.. rows ?? [Row(suggested: null)]],
        Warnings = [],
        PreviewToken = token,
    };

    /// <summary>An album that has no list of tracks at all: nothing to match a song against.</summary>
    public static LibraryMovePreview AlbumWithoutTracksPreview(string token = "token-empty") => new()
    {
        Destination = Album(),
        ListKind = LibraryMoveListKinds.Tracks,
        Entries = [],
        Rows = [Row(suggested: null)],
        Warnings = [],
        PreviewToken = token,
    };

    /// <summary>A book, comic or movie Series: no list and no free move, only Add anyway (details overlay D20).</summary>
    public static LibraryMovePreview SeriesPreview(string token = "token-series") => new()
    {
        Destination = new LibraryMoveDestination
        {
            Id = Guid.Parse("dddddddd-0000-0000-0000-000000000001"),
            Kind = LibraryMoveDestinationKinds.Series,
            Label = "The Dune Chronicles",
            HasList = false,
        },
        ListKind = LibraryMoveListKinds.None,
        Entries = [],
        Rows = [Row(title: "A Dune Novella", suggested: null)],
        Warnings = ["Leaves Loose books"],
        AddAnywayOnly = true,
        Note = "Series are built from each title's match. Use Match and Identity to re-match it, or add it here without a number.",
        PreviewToken = token,
    };

    /// <summary>A hand-picked collection or Videos series: no list, so the move is free.</summary>
    public static LibraryMovePreview NoListPreview(string token = "token-free") => new()
    {
        Destination = new LibraryMoveDestination
        {
            Id = Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            Kind = LibraryMoveDestinationKinds.Collection,
            Label = "Road trip",
            HasList = false,
        },
        ListKind = LibraryMoveListKinds.None,
        Entries = [],
        Rows = [Row(suggested: null)],
        Warnings = [],
        PreviewToken = token,
    };
}
