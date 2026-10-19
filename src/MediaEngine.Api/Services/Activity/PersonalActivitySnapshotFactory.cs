using System.Globalization;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Activity;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Activity;

/// <summary>
/// Takes the snapshot an activity entry keeps of its item from the work's current canonical values (never the file
/// name), so history still reads correctly after the item is deleted.
/// </summary>
internal static class PersonalActivitySnapshotFactory
{
    /// <summary>The separator between the season and episode and the episode's name.</summary>
    private const string EpisodeSeparator = " · ";

    /// <summary>The snapshot for a playback entry. The cover is added separately.</summary>
    public static PersonalActivitySnapshot FromPlayback(DisplayWorkRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var kind = DisplayMediaRules.NormalizeDisplayKind(row.MediaType);
        var mediaType = DisplayMediaRules.NormalizeMediaType(row.MediaType);
        var year = ParseYear(row.Year);
        return kind switch
        {
            "TV" => new PersonalActivitySnapshot(
                mediaType,
                First(row.ShowName, row.Series, row.Title) ?? "Untitled",
                EpisodeLabel(row.SeasonNumber, row.EpisodeNumber, row.Title, row.ShowName),
                null, year, row.WorkId, row.AssetId, null),
            "Music" => new PersonalActivitySnapshot(
                mediaType,
                First(row.Title) ?? "Untitled",
                First(row.Artist, row.ArtistPersonName),
                First(row.Album), year, row.WorkId, row.AssetId, null),
            "Movie" => new PersonalActivitySnapshot(
                mediaType, First(row.Title) ?? "Untitled", null, null, year, row.WorkId, row.AssetId, null),
            _ => new PersonalActivitySnapshot(
                mediaType,
                First(row.Title) ?? "Untitled",
                First(row.Author, row.Artist),
                null, year, row.WorkId, row.AssetId, null),
        };
    }

    /// <summary>
    /// The snapshot for something added to My list: the title itself (a movie, a show, a book, an album or a series),
    /// not an episode. Null when the visible works do not include it.
    /// </summary>
    public static PersonalActivitySnapshot? FromMyList(
        ProfileEntityKind entityKind,
        Guid entityId,
        IReadOnlyList<DisplayWorkRow> works)
    {
        ArgumentNullException.ThrowIfNull(works);
        var row = works.FirstOrDefault(candidate => candidate.WorkId == entityId)
            ?? works.FirstOrDefault(candidate => candidate.RootWorkId == entityId)
            ?? works.FirstOrDefault(candidate => candidate.CollectionId == entityId);
        if (row is null)
        {
            return null;
        }

        var mediaType = DisplayMediaRules.NormalizeMediaType(row.MediaType);
        var year = ParseYear(row.Year);
        var title = entityKind switch
        {
            ProfileEntityKind.Collection or ProfileEntityKind.Playlist => First(row.CollectionTitle, row.Series, row.Title),
            ProfileEntityKind.TvShow => First(row.ShowName, row.Series, row.Title),
            ProfileEntityKind.Album => First(row.Album, row.Title),
            _ => row.WorkId == entityId ? First(row.Title) : First(row.ShowName, row.Series, row.Title),
        };
        var subtitle = entityKind switch
        {
            ProfileEntityKind.Album => First(row.Artist, row.ArtistPersonName),
            ProfileEntityKind.Book or ProfileEntityKind.Comic or ProfileEntityKind.Audiobook => First(row.Author, row.Artist),
            _ => null,
        };
        return new PersonalActivitySnapshot(mediaType, title ?? "Untitled", subtitle, null, year, entityId, row.AssetId, null);
    }

    internal static string? EpisodeLabel(string? season, string? episode, string? episodeTitle, string? showName)
    {
        var numbers = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(season))
        {
            numbers.Add("S" + season.Trim());
        }

        if (!string.IsNullOrWhiteSpace(episode))
        {
            numbers.Add("E" + episode.Trim());
        }

        var name = string.IsNullOrWhiteSpace(episodeTitle) ||
                   string.Equals(episodeTitle.Trim(), showName?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : episodeTitle.Trim();
        return (numbers.Count, name) switch
        {
            (0, null) => null,
            (0, _) => name,
            (_, null) => string.Join(' ', numbers),
            _ => string.Join(' ', numbers) + EpisodeSeparator + name,
        };
    }

    private static string? First(params string?[] values) => StringHelpers.FirstNonBlank(values)?.Trim();

    private static int? ParseYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 4)
        {
            return null;
        }

        return int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is > 0 and < 10000
            ? year
            : null;
    }
}
