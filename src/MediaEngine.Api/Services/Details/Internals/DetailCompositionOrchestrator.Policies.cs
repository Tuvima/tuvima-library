using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using static MediaEngine.Api.Services.Details.Internals.DetailPresentationPolicy;
using SeriesManifestItemDto = MediaEngine.Domain.Models.SeriesManifestItemDto;
using SeriesManifestViewDto = MediaEngine.Domain.Models.SeriesManifestViewDto;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    private static string? BuildSequencePositionSummary(
        DetailEntityType type,
        SequenceItemViewModel current,
        string containerTitle,
        SequenceLabels labels)
    {
        if (type == DetailEntityType.TvEpisode)
        {
            return SeriesDisplayFormatter.FormatEpisodePosition(
                current.GroupTitle?.Replace("Season ", string.Empty, StringComparison.OrdinalIgnoreCase),
                FirstText(current.PositionText, current.PositionLabel, current.PositionNumber?.ToString(CultureInfo.InvariantCulture)),
                containerTitle);
        }

        var position = FirstText(current.PositionText, current.PositionLabel, current.PositionNumber?.ToString(CultureInfo.InvariantCulture));
        return SeriesDisplayFormatter.FormatPosition(labels.ItemLabel, position, containerTitle);
    }

    private static string? FormatSeasonEpisode(string? season, string? episode)
    {
        season = NormalizeEpisodeOrdinal(season);
        episode = NormalizeEpisodeOrdinal(episode);
        if (string.IsNullOrWhiteSpace(season) && string.IsNullOrWhiteSpace(episode))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(season))
        {
            return $"Episode {episode}";
        }

        if (string.IsNullOrWhiteSpace(episode))
        {
            return $"Season {season}";
        }

        return $"S{season} E{episode}";
    }

    private static string? NormalizeEpisodeOrdinal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var normalized = trimmed.TrimStart('0');
        return normalized.Length == 0 ? "0" : normalized;
    }

    private static string? FormatIssue(string? position)
        => string.IsNullOrWhiteSpace(position) ? null : $"Issue #{position}";

    private static DetailEntityType MapMediaTypeToEntityType(string? mediaType)
    {
        if (mediaType?.Contains("movie", StringComparison.OrdinalIgnoreCase) == true || mediaType?.StartsWith("video", StringComparison.OrdinalIgnoreCase) == true)
        {
            return DetailEntityType.Movie;
        }

        if (mediaType?.Contains("tv", StringComparison.OrdinalIgnoreCase) == true)
        {
            return DetailEntityType.TvEpisode;
        }

        if (mediaType?.Contains("music", StringComparison.OrdinalIgnoreCase) == true)
        {
            return DetailEntityType.MusicAlbum;
        }

        if (mediaType?.Contains("audio", StringComparison.OrdinalIgnoreCase) == true)
        {
            return DetailEntityType.Audiobook;
        }

        if (mediaType?.Contains("comic", StringComparison.OrdinalIgnoreCase) == true)
        {
            return DetailEntityType.ComicIssue;
        }

        return DetailEntityType.Book;
    }

    private static DetailEntityType MapCreditToEntityType(PersonLibraryCreditDto credit)
        => credit.CollectionId.HasValue && credit.MediaType?.Contains("tv", StringComparison.OrdinalIgnoreCase) == true
            ? DetailEntityType.TvShow
            : MapMediaTypeToEntityType(credit.MediaType);

    internal static string CreditDisplayId(PersonLibraryCreditDto credit)
        => MapCreditToEntityType(credit) == DetailEntityType.TvShow && credit.CollectionId.HasValue
            ? credit.CollectionId.Value.ToString("D")
            : credit.WorkId.ToString("D");

    internal static string BuildCreditRoute(PersonLibraryCreditDto credit)
    {
        var entityType = MapCreditToEntityType(credit);
        if (entityType == DetailEntityType.TvShow && credit.CollectionId.HasValue)
        {
            return $"/details/tvshow/{credit.CollectionId.Value:D}?context=watch";
        }

        if (entityType == DetailEntityType.MusicAlbum)
        {
            return $"/details/musicalbum/{credit.WorkId:D}?context=listen";
        }

        return $"/details/work/{credit.WorkId:D}?context={DetailLane(entityType)}";
    }

    private static string DetailLane(DetailEntityType entityType)
        => entityType switch
        {
            DetailEntityType.Movie or DetailEntityType.MovieSeries or DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode => "watch",
            DetailEntityType.MusicAlbum or DetailEntityType.Audiobook => "listen",
            DetailEntityType.Book or DetailEntityType.BookSeries or DetailEntityType.ComicIssue or DetailEntityType.ComicSeries => "read",
            _ => "default",
        };

    private static string PersonMediaGroupKey(string? mediaType, DetailPresentationContext context)
    {
        if (context == DetailPresentationContext.Listen && mediaType?.Contains("music", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Music";
        }

        if (context == DetailPresentationContext.Watch && (mediaType?.Contains("movie", StringComparison.OrdinalIgnoreCase) == true || mediaType?.Contains("tv", StringComparison.OrdinalIgnoreCase) == true))
        {
            return "Movies & TV";
        }

        if (mediaType?.Contains("audio", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Audiobooks";
        }

        if (mediaType?.Contains("book", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Books";
        }

        if (mediaType?.Contains("music", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Music";
        }

        return StringHelpers.FirstNonBlankOr(string.Empty, mediaType, "Works");
    }

    private static int PersonMediaGroupPriority(string key, DetailPresentationContext context)
        => (context, key) switch
        {
            (DetailPresentationContext.Listen, "Music") => 0,
            (DetailPresentationContext.Watch, "Movies & TV") => 0,
            (DetailPresentationContext.Read, "Books") => 0,
            (DetailPresentationContext.Read, "Audiobooks") => 1,
            _ => 5,
        };

    private static int PersonRolePriority(string role, DetailPresentationContext context)
        => (context, role.ToLowerInvariant()) switch
        {
            (DetailPresentationContext.Listen, "primary artist" or "artist" or "performer") => 0,
            (DetailPresentationContext.Listen, "composer") => 1,
            (DetailPresentationContext.Listen, "narrator") => 2,
            (DetailPresentationContext.Listen, "author") => 3,
            (DetailPresentationContext.Watch, "actor" or "voice actor") => 0,
            (DetailPresentationContext.Watch, "director") => 1,
            (DetailPresentationContext.Watch, "screenwriter" or "writer") => 2,
            (DetailPresentationContext.Watch, "producer") => 3,
            (DetailPresentationContext.Read, "author") => 0,
            (DetailPresentationContext.Read, "screenwriter" or "writer" or "illustrator") => 1,
            (DetailPresentationContext.Read, "narrator") => 2,
            _ => 10 + PersonRoleRank(role),
        };

    private static string FormatEntityType(DetailEntityType entityType) => entityType switch
    {
        DetailEntityType.TvShow => "TV Show",
        DetailEntityType.TvSeason => "TV Season",
        DetailEntityType.TvEpisode => "TV Episode",
        DetailEntityType.MovieSeries => "Movie Series",
        DetailEntityType.BookSeries => "Book Series",
        DetailEntityType.ComicIssue => "Comic Issue",
        DetailEntityType.ComicSeries => "Comic Series",
        DetailEntityType.MusicAlbum => "Album",
        _ => entityType.ToString(),
    };

    private static string ToTabLabel(string key, DetailEntityType entityType) => (key, entityType) switch
    {
        ("people", _) => "Cast",
        ("media", DetailEntityType.MovieSeries) => "Films",
        ("media", _) => "Media in Library",
        ("works", DetailEntityType.BookSeries) => "Books",
        ("sequence", _) => "Order",
        ("movies-tv", _) => "Movies & TV",
        ("appears-on", _) => "Appears On",
        _ => string.Join(" ", key.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..])),
    };

    private static string? GetValue(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string? FirstText(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? NormalizeHeroSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = string.Join(
            ' ',
            value.Replace("\r", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalized.Length <= 260 ? normalized : normalized[..260].TrimEnd() + "...";
    }


    private static IReadOnlyList<MetadataPill> MaybePill(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : [new MetadataPill { Label = value }];

    private static string BuildPersonCreditEntityId(Guid? personId, string? qid, string name)
        => personId?.ToString("D")
            ?? NormalizeQid(qid)
            ?? name;

    private static string ResolveCollectionTitle(
        DetailEntityType entityType,
        string? displayName,
        IReadOnlyDictionary<string, string> rootValues,
        IReadOnlyDictionary<string, string> values)
    {
        if (entityType == DetailEntityType.TvShow)
        {
            return StringHelpers.FirstNonBlankOr(string.Empty,
                GetValue(rootValues, MetadataFieldConstants.Title),
                GetValue(rootValues, MetadataFieldConstants.ShowName),
                GetValue(values, MetadataFieldConstants.Title),
                GetValue(values, MetadataFieldConstants.ShowName),
                StripUniverseSuffix(displayName),
                displayName,
                "TV Show");
        }

        if (entityType is DetailEntityType.BookSeries or DetailEntityType.ComicSeries or DetailEntityType.MovieSeries)
        {
            var structuralTitle = StringHelpers.FirstNonBlankOr(string.Empty,
                GetValue(rootValues, MetadataFieldConstants.Series),
                GetValue(values, MetadataFieldConstants.Series),
                displayName,
                GetValue(values, MetadataFieldConstants.Title),
                FormatEntityType(entityType));
            return SeriesDisplayFormatter.NormalizeContainerTitle(structuralTitle, isStructuralSeries: true)
                ?? structuralTitle;
        }

        var containerTitle = SeriesDisplayFormatter.NormalizeContainerTitle(
                StringHelpers.FirstNonBlankOr(string.Empty, displayName, GetValue(values, MetadataFieldConstants.Title), "Collection"),
                isStructuralSeries: false)
            ?? "Collection";
        return entityType == DetailEntityType.MusicAlbum
            ? TrimWrappingQuotationMarks(containerTitle)
            : containerTitle;
    }

    private static string TrimWrappingQuotationMarks(string value)
    {
        var title = value.Trim();
        if (title.Length < 2)
        {
            return title;
        }

        var isQuoted = (title[0] == '"' && title[^1] == '"')
            || (title[0] == '\u201c' && title[^1] == '\u201d')
            || (title[0] == '\u2018' && title[^1] == '\u2019');
        return isQuoted ? title[1..^1].Trim() : title;
    }

    private static string? StripUniverseSuffix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const string suffix = " universe";
        var trimmed = value.Trim();
        return trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^suffix.Length].Trim()
            : trimmed;
    }

    private static bool LooksLikeAggregateContributorName(string value)
        => value.Contains(" & ", StringComparison.Ordinal)
            || value.Contains(" and ", StringComparison.OrdinalIgnoreCase)
            || value.Contains(" + ", StringComparison.Ordinal);

    private static IReadOnlyList<ContributorEntry> DeduplicateContributorEntries(IReadOnlyList<ContributorEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ContributorEntry>();
        foreach (var entry in entries.OrderBy(e => e.SortOrder))
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            var key = NormalizeQid(entry.Qid) ?? entry.Name.Trim();
            if (seen.Add(key))
            {
                result.Add(entry with { Name = entry.Name.Trim(), Qid = NormalizeQid(entry.Qid), SortOrder = result.Count });
            }
        }

        return result;
    }

    private static string? ResolveCompanionQidFromCanonical(
        IReadOnlyDictionary<string, string> canonicalValues,
        string canonicalArrayKey,
        string name,
        int index)
    {
        var raw = GetValue(canonicalValues, canonicalArrayKey + MetadataFieldConstants.CompanionQidSuffix);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parsed = SplitCanonicalSegments(raw)
            .Select(ParseQidLabel)
            .Where(value => !string.IsNullOrWhiteSpace(value.Qid))
            .ToList();

        var byName = parsed.FirstOrDefault(value =>
            !string.IsNullOrWhiteSpace(value.Label)
            && value.Label.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(byName.Qid))
        {
            return byName.Qid;
        }

        return index >= 0 && index < parsed.Count ? parsed[index].Qid : null;
    }

    private static IReadOnlyList<string> SplitCanonicalSegments(string value)
    {
        return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static (string? Qid, string? Label) ParseQidLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var trimmed = value.Trim();
        var delimiter = trimmed.IndexOf("::", StringComparison.Ordinal);
        if (delimiter > 0)
        {
            return (NormalizeQid(trimmed[..delimiter]), StringHelpers.FirstNonBlankOr(string.Empty, trimmed[(delimiter + 2)..], null));
        }

        return (NormalizeQid(trimmed), null);
    }

    private static string? NormalizeQid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var delimiter = trimmed.IndexOf("::", StringComparison.Ordinal);
        if (delimiter > 0)
        {
            trimmed = trimmed[..delimiter].Trim();
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static IReadOnlyList<string> SplitNames(string value)
        => value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Initials(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant(),
        };
    }

    private static int? TryParseInt(string? value)
        => TryParseSeriesPosition(value);

    private static int? TryParseSeriesPosition(string? value)
    {
        var parsed = TryParseSeriesPositionSort(value);
        return ToDisplayPositionNumber(parsed);
    }

    private static double? TryParseSeriesPositionSort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
        {
            return parsedInt;
        }

        var numericText = new string(trimmed
            .SkipWhile(c => !char.IsDigit(c))
            .TakeWhile(c => char.IsDigit(c) || c is '.' or ',')
            .Select(c => c == ',' ? '.' : c)
            .ToArray());

        if (double.TryParse(numericText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedDouble))
        {
            return parsedDouble;
        }

        return null;
    }

    private static int? ToDisplayPositionNumber(double? value)
        => value.HasValue && IsWholeNumber(value.Value)
            ? Convert.ToInt32(Math.Round(value.Value, MidpointRounding.AwayFromZero))
            : null;

    private static bool IsWholeNumber(double value)
        => Math.Abs(value - Math.Round(value, MidpointRounding.AwayFromZero)) < 0.0001d;

    private static string? FormatSequenceSort(double? value)
        => value.HasValue
            ? value.Value.ToString(IsWholeNumber(value.Value) ? "0" : "0.###", CultureInfo.InvariantCulture)
            : null;

    private static string? SequencePositionKey(double? value)
        => value.HasValue
            ? value.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : null;

    private static string? ExtractQid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var qid = value.Trim();
        if (qid.Contains("::", StringComparison.Ordinal))
        {
            qid = qid.Split("::", 2, StringSplitOptions.TrimEntries)[0];
        }

        if (qid.Contains('/', StringComparison.Ordinal))
        {
            qid = qid[(qid.LastIndexOf('/') + 1)..];
        }

        return string.IsNullOrWhiteSpace(qid) ? null : qid;
    }

    private static bool IsWikidataQid(string? value)
    {
        var qid = ExtractQid(value);
        return qid is { Length: > 1 }
            && qid[0] == 'Q'
            && qid.Skip(1).All(char.IsDigit);
    }

    private static string? NormalizeSequenceContainerId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ExtractQid(value) ?? value.Trim();
    }

    private static bool SequenceContainerIdEquals(string? left, string? right)
    {
        var normalizedLeft = NormalizeSequenceContainerId(left);
        var normalizedRight = NormalizeSequenceContainerId(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft)
            && !string.IsNullOrWhiteSpace(normalizedRight)
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FormatSequenceContainerTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var textInfo = CultureInfo.CurrentCulture.TextInfo;
        return string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length == 0 || word.All(char.IsUpper)
                ? word
                : textInfo.ToTitleCase(word)));
    }

    private static string? StringValue(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        if (value is byte[] bytes)
        {
            return bytes.Length == 16
                ? GuidSql.FromDb(bytes).ToString("D")
                : Encoding.UTF8.GetString(bytes);
        }

        var text = Convert.ToString(value);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? ResolveCollectionArtworkUrl(string? value, string? assetIdValue, string kind, string? state)
    {
        if (!Guid.TryParse(assetIdValue, out var assetId))
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        // Collection and TV-show detail pages are composed from representative
        // child works. Their downloaded artwork is stored on the child asset, so
        // route the same local image stream URLs used by work/movie detail pages.
        return DisplayArtworkUrlResolver.Resolve(value, assetId, kind, state);
    }

    private static string? ResolveOwnedCollectionCoverUrl(string? value, string? assetIdValue, string? state)
    {
        var resolved = ResolveCollectionArtworkUrl(value, assetIdValue, "cover", state);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        // An owned asset can already serve an extracted cover even when an
        // older metadata row has not recorded cover_state yet. Detail pages
        // use this same managed endpoint, so collection arrays should not
        // regress to a blank tile for the identical work.
        return Guid.TryParse(assetIdValue, out var assetId)
            ? $"/stream/{assetId:D}/cover"
            : null;
    }

    private static bool IsManagedArtworkUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.StartsWith("/", StringComparison.Ordinal);

    private static int? IntValue(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l => checked((int)l),
            _ => int.TryParse(Convert.ToString(value), out var parsed) ? parsed : null,
        };
    }

    private static double? DoubleValue(object? value)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        return value switch
        {
            double d => d,
            float f => f,
            decimal m => (double)m,
            int i => i,
            long l => l,
            _ => double.TryParse(Convert.ToString(value), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
        };
    }

}
