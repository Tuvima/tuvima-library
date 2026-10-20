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

    public static LibraryMoveEntry Entry(string key, string number, string title, Guid? takenBy = null, string? takenTitle = null, bool canAddVersion = false) => new()
    {
        EntryKey = key,
        Number = number,
        Title = title,
        LengthSeconds = 240,
        TakenByItemId = takenBy,
        TakenByTitle = takenTitle,
        CanAddVersion = canAddVersion,
    };

    public static LibraryMovePreviewRow Row(
        Guid? id = null,
        string title = "Paranoid Android",
        string? suggested = "t2",
        string reason = "Same title",
        LibraryMoveFileChange? fileChange = null,
        string? fileNote = null,
        string? blocked = null) => new()
        {
            ItemId = id ?? SongId,
            Title = title,
            CurrentPlacementLabel = "The Best Of, track 3",
            SuggestedEntryKey = suggested,
            SuggestionReason = suggested is null ? string.Empty : reason,
            FileChange = fileChange,
            FileNote = fileNote,
            BlockedReason = blocked,
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
