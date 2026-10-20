using System.Globalization;
using System.Text.Json;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using static MediaEngine.Api.Services.Collections.CollectionResponseFormatting;

namespace MediaEngine.Api.Services.Libraries.Moves;

/// <summary>An album's tracks: the Works under it and the tracks its provider manifest lists that the library does not have.</summary>
public sealed class AlbumTrackMoveListProvider(ILibraryMoveRepository moves) : IMoveListProvider
{
    public string ListKind => LibraryMoveListKinds.Tracks;

    public bool Supports(LibraryMoveDestinationInfo destination) => destination.Kind == LibraryMoveKinds.Album;

    public async Task<IReadOnlyList<MoveListEntry>> GetEntriesAsync(LibraryMoveDestinationInfo destination, CancellationToken ct = default)
    {
        var children = (await moves.GetChildrenAsync(destination.Id, ct).ConfigureAwait(false))
            .Where(child => !child.IsUnmatched)
            .ToList();
        var entries = new List<(double Sort, MoveListEntry Entry)>();
        foreach (var child in children)
        {
            var track = child.Ordinal;
            var disc = child.DiscNumber is > 1 ? child.DiscNumber : null;
            var sort = child.OrdinalSort ?? (track.HasValue ? OrdinalNormalizer.Normalize(null, child.DiscNumber ?? 1, track.Value).SortValue ?? track.Value : double.MaxValue);
            entries.Add((sort, new MoveListEntry(
                $"work:{child.WorkId:N}",
                TrackNumber(disc, track),
                child.Title,
                child.DurationSeconds,
                child.HasFile,
                child.HasFile ? child.WorkId : null,
                child.HasFile ? child.Title : null,
                child.HasFile,
                new LibraryMoveTarget(ExistingWorkId: child.WorkId, Ordinal: child.Ordinal, OrdinalSort: child.OrdinalSort, DiscNumber: child.DiscNumber))));
        }

        foreach (var missing in ReadManifest(destination.ManifestJson, children))
        {
            entries.Add(missing);
        }

        return [.. entries.OrderBy(entry => entry.Sort).ThenBy(entry => entry.Entry.Title, StringComparer.OrdinalIgnoreCase).Select(entry => entry.Entry)];
    }

    private static IEnumerable<(double Sort, MoveListEntry Entry)> ReadManifest(string? manifestJson, IReadOnlyList<LibraryMoveChild> children)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            yield break;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifestJson);
        }
        catch (JsonException)
        {
            // A malformed provider manifest is enrichment that did not arrive; the album's own tracks are still a list.
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            var index = 0;
            var claimed = new HashSet<(int Disc, int Track)>();
            foreach (var element in tracks.EnumerateArray())
            {
                index++;
                var title = ReadJsonString(element, "title", "trackName", "name");
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var trackNumber = ReadJsonInt(element, "track_number", "trackNumber", "number") ?? ReadJsonInt(element, "ordinal", "position") ?? index;
                var disc = ReadJsonInt(element, "disc_number", "discNumber") ?? 1;
                var sort = OrdinalNormalizer.Normalize(null, disc, trackNumber).SortValue ?? trackNumber;
                var normalized = MoveTitleMatcher.Normalize(title);

                // The library already has a row for this track when one sits at the same place or carries the same title.
                var covered = children.Any(child =>
                    (child.OrdinalSort is { } childSort && Math.Abs(childSort - sort) < 0.0001d)
                    || (child.OrdinalSort is null && child.Ordinal == trackNumber && (child.DiscNumber ?? 1) == disc)
                    || MoveTitleMatcher.Normalize(child.Title) == normalized);
                if (covered || !claimed.Add((disc, trackNumber)))
                {
                    continue;
                }

                yield return (sort, new MoveListEntry(
                    $"track:{disc.ToString(CultureInfo.InvariantCulture)}:{trackNumber.ToString(CultureInfo.InvariantCulture)}",
                    TrackNumber(disc > 1 ? disc : null, trackNumber),
                    title.Trim(),
                    ReadChildDurationSeconds(element),
                    false,
                    null,
                    null,
                    false,
                    new LibraryMoveTarget(Ordinal: trackNumber, OrdinalSort: sort, DiscNumber: disc)));
            }
        }
    }

    private static string TrackNumber(int? disc, int? track) =>
        track is null ? "-" : disc is > 1 ? $"D{disc.Value.ToString(CultureInfo.InvariantCulture)} T{track.Value.ToString(CultureInfo.InvariantCulture)}" : track.Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A season's episodes: the Works under it, and the episodes the show's provider lists for that season that the library does not
/// have. Season 0 is the Specials. The known list is the same one the detail page's season rail shows missing episodes from.
/// </summary>
public sealed class SeasonEpisodeMoveListProvider(ILibraryMoveRepository moves, ISeriesManifestRepository manifests) : IMoveListProvider
{
    public string ListKind => LibraryMoveListKinds.Episodes;

    public bool Supports(LibraryMoveDestinationInfo destination) => destination.Kind == LibraryMoveKinds.Season;

    public async Task<IReadOnlyList<MoveListEntry>> GetEntriesAsync(LibraryMoveDestinationInfo destination, CancellationToken ct = default)
    {
        var season = destination.SeasonNumber ?? 0;
        var children = (await moves.GetChildrenAsync(destination.Id, ct).ConfigureAwait(false))
            .Where(child => !child.IsUnmatched)
            .ToList();
        var entries = new List<(double Sort, MoveListEntry Entry)>();
        foreach (var child in children)
        {
            entries.Add((child.OrdinalSort ?? child.Ordinal ?? double.MaxValue, new MoveListEntry(
                $"work:{child.WorkId:N}",
                EpisodeNumber(season, child.OrdinalSort ?? child.Ordinal),
                child.Title,
                child.DurationSeconds,
                child.HasFile,
                child.HasFile ? child.WorkId : null,
                child.HasFile ? child.Title : null,
                child.HasFile,
                new LibraryMoveTarget(ExistingWorkId: child.WorkId, Ordinal: child.Ordinal, OrdinalSort: child.OrdinalSort, DiscNumber: child.DiscNumber))));
        }

        if (destination.CollectionId is { } collectionId
            && await manifests.GetViewByCollectionIdAsync(collectionId, ct).ConfigureAwait(false) is { } view)
        {
            var childIds = children.Select(child => child.WorkId).ToHashSet();
            foreach (var item in view.Items)
            {
                if (!item.IsCollection
                    && SeasonOfSeriesQid(item.SeriesQid) == season
                    && EpisodeOf(item) is { } episode
                    && !(item.LinkedWorkId is { } linked && childIds.Contains(linked))
                    && !children.Any(child => (child.OrdinalSort ?? child.Ordinal) is { } at && Math.Abs(at - episode) < 0.0001d))
                {
                    var owned = string.Equals(item.OwnershipState, "Owned", StringComparison.OrdinalIgnoreCase) && item.LinkedWorkId.HasValue;
                    entries.Add((episode, new MoveListEntry(
                        $"manifest:{item.Id:N}",
                        EpisodeNumber(season, episode),
                        string.IsNullOrWhiteSpace(item.ItemLabel) ? item.ItemQid : item.ItemLabel.Trim(),
                        NormalizeAudioDurationSeconds(item.Duration),
                        owned,
                        owned ? item.LinkedWorkId : null,
                        owned ? item.ItemLabel : null,
                        false,
                        new LibraryMoveTarget(ManifestItemId: item.Id, Ordinal: OrdinalNormalizer.IntegerOrdinal(episode), OrdinalSort: episode))));
                }
            }
        }

        return [.. entries.OrderBy(entry => entry.Sort).ThenBy(entry => entry.Entry.Title, StringComparer.OrdinalIgnoreCase).Select(entry => entry.Entry)];
    }

    // Provider manifests are scoped per season: the series id ends in ":season:N".
    private static int? SeasonOfSeriesQid(string? seriesQid)
    {
        const string marker = ":season:";
        var at = seriesQid?.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        return at >= 0 && int.TryParse(seriesQid![(at + marker.Length)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var season)
            ? season
            : null;
    }

    private static double? EpisodeOf(SeriesManifestItemDto item)
    {
        if (item.ParsedOrdinal is { } parsed)
        {
            return parsed;
        }

        return OrdinalNormalizer.Normalize(item.RawOrdinal).SortValue;
    }

    private static string EpisodeNumber(int season, double? episode) =>
        episode is null
            ? $"S{season.ToString(CultureInfo.InvariantCulture)}"
            : $"S{season.ToString(CultureInfo.InvariantCulture)} E{episode.Value.ToString("0.####", CultureInfo.InvariantCulture)}";
}

/// <summary>A book, comic or movie series' positions (decimals such as 1.5 are allowed): its Works and the entries its series list knows.</summary>
public sealed class SeriesPositionMoveListProvider(ILibraryMoveRepository moves, ISeriesManifestRepository manifests) : IMoveListProvider
{
    public string ListKind => LibraryMoveListKinds.Positions;

    public bool Supports(LibraryMoveDestinationInfo destination) => destination.Kind == LibraryMoveKinds.Series;

    public async Task<IReadOnlyList<MoveListEntry>> GetEntriesAsync(LibraryMoveDestinationInfo destination, CancellationToken ct = default)
    {
        var children = (await moves.GetChildrenAsync(destination.Id, ct).ConfigureAwait(false))
            .Where(child => !child.IsUnmatched)
            .ToList();
        var entries = new List<(double Sort, MoveListEntry Entry)>();
        foreach (var child in children)
        {
            var position = child.OrdinalSort ?? child.Ordinal;
            entries.Add((position ?? double.MaxValue, new MoveListEntry(
                $"work:{child.WorkId:N}",
                position is null ? "-" : Format(position.Value),
                child.Title,
                child.DurationSeconds,
                child.HasFile,
                child.HasFile ? child.WorkId : null,
                child.HasFile ? child.Title : null,
                child.HasFile,
                new LibraryMoveTarget(ExistingWorkId: child.WorkId, Ordinal: child.Ordinal, OrdinalSort: child.OrdinalSort, DiscNumber: child.DiscNumber))));
        }

        if (destination.CollectionId is { } collectionId
            && await manifests.GetViewByCollectionIdAsync(collectionId, ct).ConfigureAwait(false) is { } view)
        {
            var childIds = children.Select(child => child.WorkId).ToHashSet();
            foreach (var item in view.Items)
            {
                if (item.IsCollection
                    || item.ParsedOrdinal is not { } position
                    || !string.Equals(item.MembershipScope, SeriesMembershipScopeNames.MainSequence, StringComparison.OrdinalIgnoreCase)
                    || (item.LinkedWorkId is { } linked && childIds.Contains(linked))
                    || children.Any(child => (child.OrdinalSort ?? child.Ordinal) is { } at && Math.Abs(at - position) < 0.0001d))
                {
                    continue;
                }

                var owned = string.Equals(item.OwnershipState, "Owned", StringComparison.OrdinalIgnoreCase) && item.LinkedWorkId.HasValue;
                entries.Add((position, new MoveListEntry(
                    $"manifest:{item.Id:N}",
                    Format(position),
                    string.IsNullOrWhiteSpace(item.ItemLabel) ? item.ItemQid : item.ItemLabel.Trim(),
                    null,
                    owned,
                    owned ? item.LinkedWorkId : null,
                    owned ? item.ItemLabel : null,
                    false,
                    new LibraryMoveTarget(ManifestItemId: item.Id, Ordinal: OrdinalNormalizer.IntegerOrdinal(position), OrdinalSort: position))));
            }
        }

        return [.. entries.OrderBy(entry => entry.Sort).ThenBy(entry => entry.Entry.Title, StringComparer.OrdinalIgnoreCase).Select(entry => entry.Entry)];
    }

    private static string Format(double position) => position.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>An audiobook's parts: each file it holds, and the part numbers its files say it should have that are missing.</summary>
public sealed class AudiobookPartMoveListProvider(ILibraryMoveRepository moves) : IMoveListProvider
{
    // A guard against a bad part count in a file's tags producing a huge list.
    private const int MaxParts = 500;

    public string ListKind => LibraryMoveListKinds.Parts;

    public bool Supports(LibraryMoveDestinationInfo destination) => destination.Kind == LibraryMoveKinds.Audiobook;

    public async Task<IReadOnlyList<MoveListEntry>> GetEntriesAsync(LibraryMoveDestinationInfo destination, CancellationToken ct = default)
    {
        var parts = (await moves.GetPartsAsync(destination.Id, ct).ConfigureAwait(false))
            .Where(part => !part.IsUnmatched && part.PartNumber.HasValue)
            .ToList();
        var entries = new List<(double Sort, MoveListEntry Entry)>();
        foreach (var part in parts)
        {
            entries.Add((part.PartNumber!.Value, new MoveListEntry(
                $"edition:{part.EditionId:N}",
                part.PartNumber.Value.ToString(CultureInfo.InvariantCulture),
                part.Title,
                part.DurationSeconds,
                true,
                part.EditionId,
                part.Title,
                true,
                new LibraryMoveTarget(PartNumber: part.PartNumber, TakenPartEditionId: part.EditionId))));
        }

        var count = Math.Min(parts.Max(part => part.PartCount) ?? 0, MaxParts);
        var have = parts.Select(part => part.PartNumber!.Value).ToHashSet();
        for (var number = 1; number <= count; number++)
        {
            if (!have.Contains(number))
            {
                entries.Add((number, new MoveListEntry(
                    $"part:{number.ToString(CultureInfo.InvariantCulture)}",
                    number.ToString(CultureInfo.InvariantCulture),
                    $"Part {number.ToString(CultureInfo.InvariantCulture)}",
                    null,
                    false,
                    null,
                    null,
                    false,
                    new LibraryMoveTarget(PartNumber: number))));
            }
        }

        return [.. entries.OrderBy(entry => entry.Sort).Select(entry => entry.Entry)];
    }
}

/// <summary>Title comparison for the Match step's best guess: case, punctuation and edition words ("remastered") do not matter.</summary>
public static class MoveTitleMatcher
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = System.Text.RegularExpressions.Regex.Replace(value.Trim().ToLowerInvariant(), @"\s*[\(\[\{].*?[\)\]\}]\s*", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\b(remaster(ed)?|remix|mono|stereo|explicit|clean|single version|album version)\b", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[^\p{L}\p{Nd}]+", " ");
        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>0 to 1: 1 for the same title, then a title inside the other, then the share of words in common.</summary>
    public static double Similarity(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0)
        {
            return 0;
        }

        if (a == b)
        {
            return 1;
        }

        if (Math.Min(a.Length, b.Length) >= 4 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)))
        {
            return 0.85;
        }

        var wordsA = a.Split(' ').ToHashSet();
        var wordsB = b.Split(' ').ToHashSet();
        var shared = wordsA.Intersect(wordsB).Count();
        return shared == 0 ? 0 : (double)shared / wordsA.Union(wordsB).Count();
    }
}
