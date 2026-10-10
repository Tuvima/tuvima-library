using System.Globalization;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using WorkDto = MediaEngine.Contracts.Collections.WorkDto;

namespace MediaEngine.Api.Services.Collections;

/// <summary>
/// Pure, stateless formatting/parsing/classification helpers extracted from
/// <c>CollectionEndpoints</c>. None of these touch the database, the filesystem, or
/// any provider — they only reshape data that has already been fetched. Kept as a
/// static class (no constructor dependencies) so call sites in the endpoint file can
/// keep using the unqualified method names via <c>using static</c>.
/// </summary>
public static class CollectionResponseFormatting
{
    public static bool TryGetRelationshipAggregation(
        Collection collection,
        string relationshipType,
        out CollectionCatalogAggregation aggregation)
    {
        var relationship = collection.Relationships
            .FirstOrDefault(candidate => string.Equals(candidate.RelType, relationshipType, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(candidate.RelQid));
        if (relationship is null)
        {
            aggregation = default!;
            return false;
        }

        aggregation = new CollectionCatalogAggregation(
            $"{relationshipType}:{NormalizeCatalogQid(relationship.RelQid)}",
            StringHelpers.FirstNonBlank(relationship.RelLabel, collection.DisplayName));
        return true;
    }

    public static string NormalizeCatalogQid(string qid)
    {
        var value = qid.Contains('/') ? qid.Split('/')[^1] : qid;
        if (value.Contains("::", StringComparison.Ordinal))
        {
            value = value.Split("::", 2)[0];
        }

        return value.Trim();
    }

    public static bool IsGeneratedSeriesCollection(Collection collection)
        => collection.CollectionType is CollectionType.Universe
            or CollectionType.Series
            or CollectionType.ContentGroup;

    public sealed class CollectionArtworkItemRow
    {
        public Guid WorkId { get; init; }
        public string? Title { get; init; }
        public string? MediaType { get; init; }
        public string? CoverUrl { get; init; }
        public string? PrimaryColor { get; init; }
        public string? SecondaryColor { get; init; }
        public string? AccentColor { get; init; }
        public string? LocalImagePath { get; init; }
    }

    public static string? NormalizeCollectionArtworkMimeType(string? contentType, string extension)
    {
        if (string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase) || extension == ".png")
        {
            return "image/png";
        }

        if (string.Equals(contentType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentType, "image/jpg", StringComparison.OrdinalIgnoreCase)
            || extension is ".jpg" or ".jpeg")
        {
            return "image/jpeg";
        }

        return null;
    }

    public static string GetCollectionArtworkMimeType(string path) =>
        string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)
            ? "image/png"
            : "image/jpeg";

    public sealed class CollectionSearchRow
    {
        public Guid WorkId { get; init; }
        public Guid? CollectionId { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string? Author { get; init; }
        public string CollectionDisplayName { get; init; } = string.Empty;
        public string? CoverUrl { get; init; }
    }

    public static string? GetCanonical(WorkDto? w, string key)
    {
        var raw = w?.CanonicalValues
            .FirstOrDefault(cv => cv.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        return raw;
    }

    public static string ResolveContentGroupPreviewShape(
        string primaryMediaType,
        string? imageUrl,
        string? coverUrl,
        string? backgroundUrl,
        string? bannerUrl,
        int? coverWidth,
        int? coverHeight)
    {
        if (string.Equals(primaryMediaType, "Music", StringComparison.OrdinalIgnoreCase))
        {
            return "square";
        }

        if (string.Equals(imageUrl, backgroundUrl, StringComparison.OrdinalIgnoreCase)
            || string.Equals(imageUrl, bannerUrl, StringComparison.OrdinalIgnoreCase))
        {
            return "wide";
        }

        if (string.Equals(imageUrl, coverUrl, StringComparison.OrdinalIgnoreCase)
            && coverWidth is > 0
            && coverHeight is > 0)
        {
            var ratio = coverWidth.Value / (double)coverHeight.Value;
            if (ratio >= 1.32)
            {
                return "wide";
            }

            if (ratio >= 0.86)
            {
                return "square";
            }
        }

        return "portrait";
    }

    /// <param name="subtitleLanguages">
    /// The primary asset's <c>subtitle_languages</c> array rows; the scalar canonical values on
    /// <paramref name="work"/> never carry this multi-valued key.
    /// </param>
    public static PlaybackTechnicalSummary? BuildPlaybackSummaryFromWork(
        WorkDto work,
        IReadOnlyList<string> subtitleLanguages)
    {
        string? Canonical(string key) => GetCanonical(work, key);

        var summary = new PlaybackTechnicalSummary
        {
            VideoResolutionLabel = FormatResolution(
                ParseNullableInt(Canonical("video_width")),
                ParseNullableInt(Canonical("video_height"))),
            VideoCodec = NormalizeCodec(Canonical("video_codec")),
            AudioLanguage = SplitValues(Canonical("audio_language")).FirstOrDefault(),
            AudioCodec = NormalizeCodec(Canonical("audio_codec")),
            AudioChannels = FormatAudioChannels(Canonical("audio_channels")),
            SubtitleSummary = FormatSubtitleSummary(subtitleLanguages),
            AudioLanguages = SplitValues(Canonical("audio_language")),
            SubtitleLanguages = subtitleLanguages,
        };

        if (string.IsNullOrWhiteSpace(summary.VideoResolutionLabel)
            && string.IsNullOrWhiteSpace(summary.VideoCodec)
            && string.IsNullOrWhiteSpace(summary.AudioLanguage)
            && string.IsNullOrWhiteSpace(summary.AudioCodec)
            && string.IsNullOrWhiteSpace(summary.AudioChannels)
            && string.IsNullOrWhiteSpace(summary.SubtitleSummary))
        {
            return null;
        }

        return summary;
    }

    public static string? NormalizeReleaseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, out var parsed))
        {
            return parsed.ToString("MMMM d, yyyy");
        }

        return value.Length > 10 && DateTime.TryParse(value, out var parsedDate)
            ? parsedDate.ToString("MMMM d, yyyy")
            : value;
    }

    public static int? ParseDisplayYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var prefixLength = Math.Min(4, trimmed.Length);
        return int.TryParse(trimmed.AsSpan(0, prefixLength), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
               && year is >= 1000 and <= 9999
            ? year
            : null;
    }

    public static int? ParseNullableInt(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;

    public static List<string> SplitValues(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split(['|', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    public static string? FormatResolution(int? width, int? height)
    {
        if (width is null || height is null || width <= 0 || height <= 0)
        {
            return null;
        }

        var h = height.Value;
        return h switch
        {
            >= 2160 => "2160p",
            >= 1440 => "1440p",
            >= 1080 => "1080p",
            >= 720 => "720p",
            >= 480 => "480p",
            _ => $"{h}p",
        };
    }

    public static string? FormatAudioChannels(string? value)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            return null;
        }

        return parsed switch
        {
            1 => "Mono",
            2 => "2.0",
            _ => $"{parsed - 1}.1",
        };
    }

    public static string? FormatSubtitleSummary(IReadOnlyList<string> languages)
    {
        if (languages.Count == 0)
        {
            return null;
        }

        if (languages.Count == 1)
        {
            return languages[0];
        }

        return $"{languages[0]} + {languages.Count - 1} more";
    }

    public static string? NormalizeCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return null;
        }

        return codec.ToLowerInvariant() switch
        {
            "h264" => "H.264",
            "hevc" => "HEVC",
            "aac" => "AAC",
            "ac3" => "AC3",
            "eac3" => "EAC3",
            "dts" => "DTS",
            "truehd" => "TrueHD",
            "opus" => "Opus",
            "flac" => "FLAC",
            "subrip" => "SRT",
            _ => codec.ToUpperInvariant(),
        };
    }

    public static string? ReadJsonString(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    public static int? ReadJsonInt(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public static double? ReadJsonDouble(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    public static double? ReadChildDurationSeconds(JsonElement element)
    {
        var seconds = ReadJsonDouble(element, "duration_seconds", "durationSeconds");
        if (seconds is > 0)
        {
            return seconds;
        }

        var millis = ReadJsonDouble(element, "duration_ms", "durationMillis", "trackTimeMillis");
        if (millis is > 0)
        {
            return millis.Value / 1000d;
        }

        return NormalizeAudioDurationSeconds(ReadJsonString(element, "duration", "runtime"));
    }

    public static double? NormalizeAudioDurationSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span) && span.TotalSeconds > 0)
        {
            return span.TotalSeconds;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric) && numeric > 0)
        {
            return numeric >= 60000 ? numeric / 1000d : numeric;
        }

        return null;
    }

    public static string? FormatAudioDuration(double? seconds, string? fallback)
    {
        if (seconds is > 0)
        {
            var span = TimeSpan.FromSeconds(seconds.Value);
            return span.TotalHours >= 1
                ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        }

        var fallbackSeconds = NormalizeAudioDurationSeconds(fallback);
        if (fallbackSeconds is > 0)
        {
            return FormatAudioDuration(fallbackSeconds, null);
        }

        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    public static string? FirstCanonicalValue(IReadOnlyList<CanonicalValue> values, params string[] keys)
        => values
            .FirstOrDefault(value => keys.Any(key => string.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase))
                && !string.IsNullOrWhiteSpace(value.Value))
            ?.Value;

    // Work overload: unlike the list overload above it does not skip blank values.
    // Both live here so `using static` resolves the whole overload set — a copy left
    // behind in an endpoint file would hide these imports and break resolution.
    public static string? FirstCanonicalValue(Work work, params string[] keys)
        => work.CanonicalValues
            .FirstOrDefault(c => keys.Any(key => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)))
            ?.Value;

    public static CollectionPalette ResolvePalette(
        IReadOnlyList<CanonicalValue> canonicalValues,
        CollectionPaletteReadModel? storedPalette)
    {
        var primary = FirstCanonicalValue(canonicalValues,
            MetadataFieldConstants.ArtworkPrimaryHex,
            "cover_primary_hex",
            "primary_color");
        var secondary = FirstCanonicalValue(canonicalValues,
            MetadataFieldConstants.ArtworkSecondaryHex,
            "cover_secondary_hex",
            "secondary_color");
        var accent = FirstCanonicalValue(canonicalValues,
            MetadataFieldConstants.ArtworkAccentHex,
            "cover_accent_hex",
            "accent_color",
            "dominant_color");

        var colors = new List<string>();
        AddColor(colors, primary);
        AddColor(colors, secondary);
        AddColor(colors, accent);

        if (storedPalette is not null
            && (string.IsNullOrWhiteSpace(primary)
                || string.IsNullOrWhiteSpace(secondary)
                || string.IsNullOrWhiteSpace(accent)))
        {
            primary ??= storedPalette.PrimaryHex;
            secondary ??= storedPalette.SecondaryHex;
            accent ??= storedPalette.AccentHex;
            AddColor(colors, storedPalette.PrimaryHex);
            AddColor(colors, storedPalette.SecondaryHex);
            AddColor(colors, storedPalette.AccentHex);
        }

        return new CollectionPalette(primary, secondary, accent, colors);
    }

    private static void AddColor(List<string> colors, string? color)
    {
        if (!string.IsNullOrWhiteSpace(color) && !colors.Contains(color, StringComparer.OrdinalIgnoreCase))
        {
            colors.Add(color);
        }
    }

    public sealed record CollectionPalette(
        string? PrimaryColor,
        string? SecondaryColor,
        string? AccentColor,
        List<string> DominantColors);

    public sealed class AssetPaletteRow
    {
        public string? PrimaryHex { get; init; }
        public string? SecondaryHex { get; init; }
        public string? AccentHex { get; init; }
    }

    public static IReadOnlyList<ContentGroupDto> NormalizeSystemViewGroups(
        IReadOnlyList<ContentGroupDto> groups,
        string? mediaType,
        string? groupField)
    {
        if (groups.Count == 0)
        {
            return groups;
        }

        var normalizedGroups = IsMusicAlbumSystemView(mediaType, groupField)
            ? groups
                .GroupBy(group => BuildSystemViewGroupIdentity(group, mediaType, groupField), StringComparer.OrdinalIgnoreCase)
                .Select(group => (Key: group.Key, Items: (IReadOnlyList<ContentGroupDto>)group.ToList()))
                .ToList()
            : groups
                .Select(group => (
                    Key: BuildSystemViewGroupIdentity(group, mediaType, groupField),
                    Items: (IReadOnlyList<ContentGroupDto>)[group]))
                .ToList();

        return normalizedGroups
            .Select(group =>
            {
                var preferred = group.Items
                    .OrderByDescending(ScoreSystemViewGroup)
                    .ThenByDescending(item => item.CreatedAt)
                    .First();

                var seasonCounts = group.Items
                    .Where(item => item.SeasonCount.HasValue)
                    .Select(item => item.SeasonCount!.Value)
                    .ToList();

                var albumCounts = group.Items
                    .Where(item => item.AlbumCount.HasValue)
                    .Select(item => item.AlbumCount!.Value)
                    .ToList();

                var earliestYears = group.Items
                    .Where(item => item.EarliestYear.HasValue)
                    .Select(item => item.EarliestYear!.Value)
                    .ToList();

                var latestYears = group.Items
                    .Where(item => item.LatestYear.HasValue)
                    .Select(item => item.LatestYear!.Value)
                    .ToList();

                return new ContentGroupDto
                {
                    CollectionId = SystemViewGroupIdentity.CreateId(preferred, mediaType, groupField),
                    RootWorkId = preferred.RootWorkId,
                    DisplayName = preferred.DisplayName.Trim(),
                    WikidataQid = preferred.WikidataQid,
                    PrimaryMediaType = preferred.PrimaryMediaType,
                    WorkCount = group.Items.Max(item => item.WorkCount),
                    DistinctTitleCount = group.Items
                        .Where(item => item.DistinctTitleCount.HasValue)
                        .Select(item => item.DistinctTitleCount!.Value)
                        .DefaultIfEmpty(group.Items.Max(item => item.WorkCount))
                        .Max(),
                    PreviewItems = group.Items
                        .SelectMany(item => item.PreviewItems)
                        .DistinctBy(item => item.WorkId)
                        .Take(4)
                        .ToList(),
                    CoverUrl = preferred.CoverUrl,
                    BackgroundUrl = preferred.BackgroundUrl,
                    BannerUrl = preferred.BannerUrl,
                    HeroUrl = null,
                    LogoUrl = preferred.LogoUrl,
                    CoverAspectClass = preferred.CoverAspectClass,
                    SquareAspectClass = preferred.SquareAspectClass,
                    BackgroundAspectClass = preferred.BackgroundAspectClass,
                    BannerAspectClass = preferred.BannerAspectClass,
                    CoverWidthPx = preferred.CoverWidthPx,
                    CoverHeightPx = preferred.CoverHeightPx,
                    SquareWidthPx = preferred.SquareWidthPx,
                    SquareHeightPx = preferred.SquareHeightPx,
                    BackgroundWidthPx = preferred.BackgroundWidthPx,
                    BackgroundHeightPx = preferred.BackgroundHeightPx,
                    BannerWidthPx = preferred.BannerWidthPx,
                    BannerHeightPx = preferred.BannerHeightPx,
                    Description = preferred.Description,
                    Tagline = preferred.Tagline,
                    Creator = preferred.Creator,
                    Director = preferred.Director,
                    Writer = preferred.Writer,
                    ReleaseDate = preferred.ReleaseDate,
                    UniverseStatus = preferred.UniverseStatus,
                    CreatedAt = preferred.CreatedAt,
                    ArtistPhotoUrl = preferred.ArtistPhotoUrl,
                    ArtistPersonId = preferred.ArtistPersonId,
                    PersonPhotoUrl = preferred.PersonPhotoUrl,
                    PersonId = preferred.PersonId,
                    PersonRoles = preferred.PersonRoles,
                    Network = preferred.Network,
                    Year = preferred.Year,
                    EarliestYear = earliestYears.Count == 0 ? null : earliestYears.Min(),
                    LatestYear = latestYears.Count == 0 ? null : latestYears.Max(),
                    SeasonCount = seasonCounts.Count == 0 ? null : seasonCounts.Max(),
                    AlbumCount = albumCounts.Count == 0 ? null : albumCounts.Max(),
                };
            })
            .ToList();
    }

    public static bool IsMusicAlbumSystemView(string? mediaType, string? groupField)
        => string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase)
           && string.Equals(groupField, "album", StringComparison.OrdinalIgnoreCase);

    public sealed class MusicSystemViewGroupRow
    {
        public string GroupName { get; init; } = string.Empty;
        public string? Creator { get; init; }
        public int WorkCount { get; init; }
        public int DistinctTitleCount { get; init; }
        public int AlbumCount { get; init; }
        public Guid? FirstAssetId { get; init; }
        public string? Year { get; init; }
        public string? Description { get; init; }
        public string? CoverAspectClass { get; init; }
    }

    public static int CountDistinctWorkTitles(IEnumerable<Work> works)
    {
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var work in works)
        {
            var dto = work.ToContract();
            var title = GetCanonical(dto, "title") ?? GetCanonical(dto, "original_title");
            titles.Add(NormalizeDistinctTitle(title) ?? work.Id.ToString("N"));
        }

        return titles.Count;
    }

    public static string? NormalizeDistinctTitle(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : string.Join(' ', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static string BuildSystemViewGroupIdentity(ContentGroupDto group, string? mediaType, string? groupField)
    {
        var name = NormalizeSystemViewIdentity(group.DisplayName);
        if (string.Equals(mediaType, "Music", StringComparison.OrdinalIgnoreCase)
            && string.Equals(groupField, "album", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name}|{NormalizeSystemViewIdentity(group.Creator)}";
        }

        return string.Join("|",
            name,
            NormalizeSystemViewIdentity(group.Creator),
            NormalizeSystemViewIdentity(group.Network),
            NormalizeSystemViewIdentity(group.Year));
    }

    public static string NormalizeSystemViewIdentity(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "(blank)"
            : value.Trim().ToLowerInvariant();

    public static int ScoreSystemViewGroup(ContentGroupDto group)
    {
        var score = 0;
        score += string.IsNullOrWhiteSpace(group.CoverUrl) ? 0 : 8;
        score += string.IsNullOrWhiteSpace(group.ArtistPhotoUrl) ? 0 : 8;
        score += string.IsNullOrWhiteSpace(group.PersonPhotoUrl) ? 0 : 8;
        score += string.IsNullOrWhiteSpace(group.Description) ? 0 : 4;
        score += string.IsNullOrWhiteSpace(group.Creator) ? 0 : 2;
        return score + group.WorkCount;
    }

    public static Guid CreateDeterministicSystemViewGroupId(string value) => Hashing.DeterministicGuid(value);

    /// <summary>
    /// Lineage-aware variant of <see cref="ResolveEntityMetadata"/> used by the
    /// <c>/resolve/by-name</c> endpoint.  For each Work this reads canonical values
    /// from both the asset row (Self-scoped fields: title, track_number) and from
    /// the topmost parent Work row (Parent-scoped fields: artist, album, genre,
    /// year).  Cover art is resolved via <c>/stream/{assetId}/cover</c> from the
    /// asset ID rather than canonical_values.  This mirrors the LibraryItemRepository
    /// pattern so that music items have correct artist/album/cover values even
    /// after the lineage-aware write splits them onto the album Work's entity_id.
    /// </summary>
    public sealed record CollectionMediaLookupRow(
        Guid WorkId,
        string MediaType,
        string? WorkKind,
        int? Ordinal,
        Guid? AssetId,
        string Title,
        string? Creator,
        string? Year,
        string? ArtworkUrl,
        string? ShowName,
        string? SeasonNumber,
        string? Album,
        string? Artist);

    public sealed class GeneratedCollectionItemRow
    {
        public Guid WorkId { get; init; }
        public string Title { get; init; } = string.Empty;
        public object? Creator { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public string? CoverUrl { get; init; }
        public int SortOrder { get; init; }
    }

    public sealed record CollectionDisplayWorkRow(Guid WorkId);

    public sealed record CollectionCatalogAggregation(string Key, string? Label);

    public sealed record CollectionManagementCatalogCandidate(
        Collection Collection,
        CollectionCatalogClassification Classification,
        CollectionCatalogAggregation? Grouping,
        IReadOnlyList<Guid> WorkIds,
        int ItemCount,
        CollectionMediaCounts MediaCounts);
}
