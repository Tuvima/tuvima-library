using System.Globalization;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Library;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Components.Listen;

/// <summary>
/// The one place that turns the three current song view models into <see cref="SongRow"/>s (spec music-table-and-reader-frame D2):
/// a song from the Songs and Albums lists, a track on an album page and a song in a playlist.
/// </summary>
public static class SongRowMapper
{
    /// <summary>Maps a song from the Songs or Albums lists. The # column is the song's own track number, when it has one.</summary>
    public static SongRow FromWork(WorkViewModel work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return BuildFromWork(work, SongRowSource.Work, playlistItemId: null, number: ParseNumber(work.TrackNumber));
    }

    /// <summary>
    /// Maps a song in a playlist. <paramref name="item"/> is null for the built-in lists (All music, Favorite songs, Recently added),
    /// which have no items of their own to move or remove. The # column is the song's place in the list, counted from 1.
    /// </summary>
    public static SongRow FromPlaylistItem(CollectionItemDto? item, WorkViewModel work, int index)
    {
        ArgumentNullException.ThrowIfNull(work);
        return BuildFromWork(work, SongRowSource.PlaylistItem, item?.Id, number: index + 1);
    }

    /// <summary>
    /// Maps a track on an album page. A track added anyway (<c>IsUnmatched</c>) has no number and lands in the Unnumbered group;
    /// a track with no track number falls back to its chapter index.
    /// </summary>
    public static SongRow FromGroupingItem(MediaGroupingItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var seconds = item.DurationSeconds is > 0
            ? item.DurationSeconds
            : LibraryHelpers.NormalizeDurationSeconds(item.Duration);

        return new SongRow
        {
            Id = item.Id,
            Source = SongRowSource.Grouping,
            Title = item.Title,
            Artist = NullIfBlank(item.Artist),
            CoverUrl = NullIfBlank(item.ArtworkUrl),
            DurationSeconds = seconds is > 0 ? seconds : null,
            IsOwned = item.IsOwned,
            IsUnnumbered = item.IsUnmatched,
            Number = item.IsUnmatched ? null : ParseNumber(item.TrackNumber) ?? item.ChapterIndex,
            IsExplicit = item.IsExplicit,
            WorkId = Guid.TryParse(item.Id, out var workId) ? workId : null,
            AssetId = NullIfBlank(item.AssetId),
        };
    }

    private static SongRow BuildFromWork(WorkViewModel work, SongRowSource source, Guid? playlistItemId, int? number)
    {
        var seconds = ReadDurationSeconds(work);
        return new SongRow
        {
            Id = work.Id.ToString("D"),
            Source = source,
            Title = DisplayTitle(work),
            Artist = StringHelpers.FirstNonBlank(work.Artist, work.Author),
            Album = NullIfBlank(work.Album),
            Genre = StringHelpers.FirstNonBlank(work.Genres.FirstOrDefault(), work.Genre),
            CoverUrl = NullIfBlank(work.CoverUrl),
            DurationSeconds = seconds > 0 ? seconds : null,
            DateAdded = work.CreatedAt == default ? null : work.CreatedAt,
            Plays = ReadPlays(work),
            Number = number,
            IsUpdatingDetails = work.IsUpdatingDetails,
            WorkId = work.Id,
            PlaylistItemId = playlistItemId,
            CollectionId = work.CollectionId,
            ArtistPersonId = work.ArtistPersonId,
        };
    }

    // Same rule as the Songs table today: an "Untitled" title falls back to the file's own names before giving up.
    private static string DisplayTitle(WorkViewModel work)
        => StringHelpers.FirstNonBlank(
               IsUntitled(work.Title) ? null : work.Title,
               Canonical(work, "track_title"),
               Canonical(work, "track_name"),
               Canonical(work, "song_title"),
               Canonical(work, "name"),
               Canonical(work, "file_title"),
               Canonical(work, "file_name"))
           ?? work.Title;

    // The file's measured length wins; provider lengths only fill gaps (the plain "duration" and "runtime" fields hold minutes).
    private static long ReadDurationSeconds(WorkViewModel work)
        => LibraryHelpers.NormalizeTrackDurationSeconds(
            Canonical(work, "duration_sec"),
            Canonical(work, "duration_seconds"),
            Canonical(work, "duration"),
            Canonical(work, "runtime")) ?? 0;

    private static int ReadPlays(WorkViewModel work)
        => int.TryParse(Canonical(work, "play_count", "plays"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var plays) && plays > 0
            ? plays
            : 0;

    private static int? ParseNumber(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? parsed : null;

    private static string? Canonical(WorkViewModel work, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = work.CanonicalValues.FirstOrDefault(item =>
                string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsUntitled(string? value)
        => string.IsNullOrWhiteSpace(value)
           || value.Trim().StartsWith("Untitled", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
