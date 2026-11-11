using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Components.Library;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Pages;

public partial class ListenPage
{
    private IReadOnlyList<WorkViewModel> ApplySongFilters(IEnumerable<WorkViewModel> works)
    {
        var filtered = works;

        if (!string.IsNullOrWhiteSpace(_songSearch))
        {
            filtered = filtered.Where(work =>
                DisplayTrackTitle(work).Contains(_songSearch, StringComparison.OrdinalIgnoreCase)
                || (work.Artist?.Contains(_songSearch, StringComparison.OrdinalIgnoreCase) ?? false)
                || (work.Album?.Contains(_songSearch, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return filtered.ToList();
    }

    private List<LibraryItemViewModel> BuildTrackGridItems(IEnumerable<WorkViewModel> works) =>
        works.Select(MapTrackToLibraryItem).ToList();

    private LibraryItemViewModel MapTrackToLibraryItem(WorkViewModel work)
    {
        var durationSeconds = GetTrackDurationSeconds(work);
        var artist = StringHelpers.FirstNonBlankOr("-", work.Artist, work.Author, null);
        var comments = GetCanonicalValue(work, "comment", "comments", "description");
        var releaseDate = ParseCanonicalDate(GetCanonicalValue(work, "release_date", "date_released"));
        var lastPlayedAt = ParseCanonicalDate(GetCanonicalValue(work, "last_played", "played_at"));
        var dateModified = ParseCanonicalDate(GetCanonicalValue(work, "date_modified", "modified_at", "updated_at"));

        return new LibraryItemViewModel
        {
            EntityId = work.Id,
            Title = DisplayTrackTitle(work),
            OriginalTitle = work.OriginalTitle,
            Artist = string.Equals(artist, "-", StringComparison.Ordinal) ? null : artist,
            Album = work.Album,
            AlbumArtist = GetCanonicalValue(work, "album_artist"),
            Composer = GetCanonicalValue(work, "composer"),
            Comments = comments,
            Genre = work.Genres.FirstOrDefault() ?? work.Genre,
            Duration = LibraryHelpers.FormatDuration(durationSeconds, fallback: "0:00"),
            DurationSeconds = durationSeconds,
            TrackNumber = work.TrackNumber,
            DiscNumber = GetCanonicalValue(work, "disc_number"),
            Rating = GetCanonicalValue(work, "rating", "album_rating"),
            Year = work.Year,
            SortTitle = GetCanonicalValue(work, "sort_title") ?? DisplayTrackTitle(work),
            SortArtist = GetCanonicalValue(work, "sort_artist") ?? artist,
            SortAlbum = GetCanonicalValue(work, "sort_album") ?? work.Album,
            Kind = GetCanonicalValue(work, "kind") ?? "Audio File",
            MediaType = "Music",
            CoverUrl = work.CoverUrl,
            CoverThumbUrl = work.CoverUrl,
            CreatedAt = work.CreatedAt,
            DateModified = dateModified,
            LastPlayedAt = lastPlayedAt,
            ReleaseDate = releaseDate,
            PlayCount = GetPlayCount(work),
            IsFavorite = _favoriteWorkIds.Contains(work.Id),
        };
    }

    private void ToggleSongSort(string column)
    {
        column = NormalizeSongSortColumn(column);
        if (string.Equals(NormalizeSongSortColumn(_songSortColumn), column, StringComparison.OrdinalIgnoreCase))
        {
            _songSortDescending = !_songSortDescending;
            return;
        }

        _songSortColumn = column;
        _songSortDescending = column is "dateAdded" or "plays" or "time" or "favorite";
    }

    private string SongSortIndicator(string column)
    {
        column = NormalizeSongSortColumn(column);
        if (!string.Equals(NormalizeSongSortColumn(_songSortColumn), column, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return _songSortDescending ? AppMaterialIcons.Outlined.ArrowDownward : AppMaterialIcons.Outlined.ArrowUpward;
    }

    private IReadOnlyList<WorkViewModel> SortSongs(IEnumerable<WorkViewModel> works)
    {
        var ordered = NormalizeSongSortColumn(_songSortColumn) switch
        {
            "title" => _songSortDescending
                ? works.OrderByDescending(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                : works.OrderBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase),
            "artist" => _songSortDescending
                ? works.OrderByDescending(work => work.Artist ?? work.Author, StringComparer.OrdinalIgnoreCase).ThenByDescending(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                : works.OrderBy(work => work.Artist ?? work.Author, StringComparer.OrdinalIgnoreCase).ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase),
            "album" => _songSortDescending
                ? works.OrderByDescending(work => work.Album, StringComparer.OrdinalIgnoreCase).ThenByDescending(work => ParseTrackNumber(work.TrackNumber))
                : works.OrderBy(work => work.Album, StringComparer.OrdinalIgnoreCase).ThenBy(work => ParseTrackNumber(work.TrackNumber)),
            "genre" => _songSortDescending
                ? works.OrderByDescending(work => work.Genres.FirstOrDefault(), StringComparer.OrdinalIgnoreCase).ThenByDescending(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                : works.OrderBy(work => work.Genres.FirstOrDefault(), StringComparer.OrdinalIgnoreCase).ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase),
            "favorite" => _songSortDescending
                ? works.OrderByDescending(work => _favoriteWorkIds.Contains(work.Id)).ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                : works.OrderBy(work => _favoriteWorkIds.Contains(work.Id)).ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase),
            "plays" => _songSortDescending
                ? works.OrderByDescending(GetPlayCount).ThenByDescending(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                : works.OrderBy(GetPlayCount).ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase),
            "time" => _songSortDescending
                ? works.OrderByDescending(GetDurationSortKey).ThenByDescending(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                : works.OrderBy(GetDurationSortKey).ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase),
            _ => _songSortDescending
                ? works.OrderByDescending(work => work.CreatedAt).ThenByDescending(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase)
                : works.OrderBy(work => work.CreatedAt).ThenBy(DisplayTrackTitle, StringComparer.OrdinalIgnoreCase),
        };

        return ordered.ToList();
    }

    private static string NormalizeSongSortColumn(string? column) =>
        (column ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "date_added" => "dateAdded",
            "track_number" => "title",
            _ => string.IsNullOrWhiteSpace(column) ? "dateAdded" : column,
        };

    private static bool IsMusicWork(WorkViewModel work)
    {
        var mediaType = work.MediaType ?? string.Empty;
        return mediaType.Contains("Music", StringComparison.OrdinalIgnoreCase)
               || string.Equals(mediaType, "Audio", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMusicWork(string? mediaType)
        => (mediaType ?? string.Empty).Contains("Music", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mediaType, "Audio", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudiobookWork(WorkViewModel work)
        => (work.MediaType ?? string.Empty).Contains("Audiobook", StringComparison.OrdinalIgnoreCase)
           || string.Equals(work.MediaType, "M4B", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudiobookWork(string? mediaType)
        => (mediaType ?? string.Empty).Contains("Audiobook", StringComparison.OrdinalIgnoreCase)
           || string.Equals(mediaType, "M4B", StringComparison.OrdinalIgnoreCase);

    private static int ParseTrackNumber(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : int.MaxValue;

    private static int ParseYear(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static int GetDurationSortKey(WorkViewModel work)
    {
        var durationSeconds = GetTrackDurationSeconds(work);
        return durationSeconds > 0 ? (int)Math.Min(durationSeconds, int.MaxValue) : 0;
    }

    private static string GetTrackDuration(WorkViewModel work)
        => LibraryHelpers.FormatDuration(GetTrackDurationSeconds(work), fallback: "0:00");

    private static string DisplayTrackTitle(WorkViewModel work)
        => StringHelpers.FirstNonBlank(
               IsUntitled(work.Title) ? null : work.Title,
               GetCanonicalValue(work, "track_title"),
               GetCanonicalValue(work, "track_name"),
               GetCanonicalValue(work, "song_title"),
               GetCanonicalValue(work, "name"),
               GetCanonicalValue(work, "file_title"),
               GetCanonicalValue(work, "file_name"))
           ?? work.Title;

    private static long GetTrackDurationSeconds(WorkViewModel work)
        => LibraryHelpers.NormalizeTrackDurationSeconds(
            GetCanonicalValue(work, "duration_sec"),
            GetCanonicalValue(work, "duration_seconds"),
            GetCanonicalValue(work, "duration"),
            GetCanonicalValue(work, "runtime")) ?? 0;

    private static string? GetCanonicalValue(WorkViewModel work, params string[] keys)
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

    private static DateTimeOffset? ParseCanonicalDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool IsUntitled(string? value)
        => string.IsNullOrWhiteSpace(value)
           || value.Trim().StartsWith("Untitled", StringComparison.OrdinalIgnoreCase);

    private static string FormatDateAdded(DateTimeOffset createdAt)
        => createdAt == default ? "-" : createdAt.LocalDateTime.ToString("M/d/yyyy", CultureInfo.CurrentCulture);

    private static int GetPlayCount(WorkViewModel work)
    {
        var value = work.CanonicalValues.FirstOrDefault(item =>
            string.Equals(item.Key, "play_count", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Key, "plays", StringComparison.OrdinalIgnoreCase))?.Value;

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plays) ? plays : 0;
    }

    private static string GetPlayCountDisplay(WorkViewModel work)
        => GetPlayCount(work) > 0 ? GetPlayCount(work).ToString(CultureInfo.InvariantCulture) : "-";
}
