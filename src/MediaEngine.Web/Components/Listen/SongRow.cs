using MediaEngine.Web.Components.Library;

namespace MediaEngine.Web.Components.Listen;

/// <summary>Which of the three current view models a <see cref="SongRow"/> was mapped from.</summary>
public enum SongRowSource
{
    /// <summary>A song from the Songs or Albums lists (<c>WorkViewModel</c>).</summary>
    Work,

    /// <summary>A track on an album page (<c>MediaGroupingItemViewModel</c>).</summary>
    Grouping,

    /// <summary>A song in a playlist: the work plus the playlist's own item (<c>CollectionItemDto</c>).</summary>
    PlaylistItem,
}

/// <summary>
/// The one row model the shared song table draws (spec music-table-and-reader-frame D2). It holds what is shown and the ids a host
/// needs to play, queue and open the song; what to do with them stays with the page that hosts the table.
/// </summary>
public sealed record SongRow
{
    /// <summary>The row's key: the work id for a song, the track's own id on an album page.</summary>
    public required string Id { get; init; }

    public required SongRowSource Source { get; init; }

    public required string Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public string? Genre { get; init; }

    public string? CoverUrl { get; init; }

    /// <summary>The song's length in seconds; null when unknown.</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>When the song was added to the library; null when unknown.</summary>
    public DateTimeOffset? DateAdded { get; init; }

    public int Plays { get; init; }

    /// <summary>False for a track the album lists but the person does not have (shown dimmed, with no actions).</summary>
    public bool IsOwned { get; init; } = true;

    /// <summary>
    /// The number shown in the # column: the track number on a song and on an album page, the place in the list in a playlist.
    /// Null when the song has none.
    /// </summary>
    public int? Number { get; init; }

    /// <summary>True for a track added anyway: no number, held in the album page's Unnumbered group at the end.</summary>
    public bool IsUnnumbered { get; init; }

    public bool IsExplicit { get; init; }

    /// <summary>True while the song is still being identified ("Updating details"); its Edit is disabled.</summary>
    public bool IsUpdatingDetails { get; init; }

    /// <summary>The work to play, queue and open; null when the row's id is not a work id.</summary>
    public Guid? WorkId { get; init; }

    /// <summary>The playlist's own item id, used to remove or move the song within the playlist; null outside a playlist.</summary>
    public Guid? PlaylistItemId { get; init; }

    /// <summary>The media asset to play on an album page, when the track carries one.</summary>
    public string? AssetId { get; init; }

    /// <summary>The album the song belongs to, for Go to album; null when unknown.</summary>
    public Guid? CollectionId { get; init; }

    /// <summary>The artist's person id, for Go to artist; null when unknown.</summary>
    public Guid? ArtistPersonId { get; init; }

    /// <summary>The length as m:ss or h:mm:ss, or a dash when unknown (never 0:00).</summary>
    public string DurationText => DurationSeconds is > 0
        ? LibraryHelpers.FormatDuration((long)Math.Round(DurationSeconds.Value, MidpointRounding.AwayFromZero))
        : "–";
}
