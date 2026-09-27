using System.Globalization;
using System.Text;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Storage.Services;

/// <summary>
/// Decides whether a newly-ingested file belongs under an existing parent
/// Work (album, show, season, comic series) or stands alone,
/// and returns the resolved <see cref="Guid"/> of the Work it should be
/// attached to.
///
/// The resolver is the single source of truth for parent/child placement —
/// the chain factory used to do this with title+author dedup, and that
/// fragile path has been removed entirely. Per-media-type strategies live
/// in this one file:
///
/// <list type="bullet">
///   <item><b>Movies</b> — always Standalone.</item>
///   <item><b>Music</b> — parent key = (artist | album). Tracks become
///     children at <c>ordinal = track_number</c>; title fallback when no
///     track number.</item>
///   <item><b>TV</b> — three levels: Show parent → Season parent → Episode
///     child. Show parent key = show_name. Season is keyed by
///     <c>(show_id, season_number)</c> via the parent_work_id+ordinal index.</item>
///   <item><b>Comics</b> — parent key = series. Issues are children at
///     <c>ordinal = issue_number</c>.</item>
///   <item><b>Books / Audiobooks in series</b> — parent key = (series | author).
///     Volumes are children at <c>ordinal = series_position</c>. Items
///     without a series fall through to Standalone.</item>
/// </list>
///
/// The resolver is intentionally idempotent: calling
/// <see cref="ResolveAsync"/> twice for the same file metadata returns the
/// same Work id without creating duplicates.
/// </summary>
public sealed class HierarchyResolver
{
    private readonly IWorkRepository _works;
    private readonly ILogger<HierarchyResolver>? _logger;

    public HierarchyResolver(IWorkRepository works, ILogger<HierarchyResolver>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(works);
        _works = works;
        _logger = logger;
    }

    /// <summary>
    /// Resolves (or creates) the Work that owns the file described by
    /// <paramref name="metadata"/>. The returned id is the leaf-most Work
    /// — the track for music, the episode for TV, the issue for comics —
    /// suitable for attaching an Edition + MediaAsset.
    /// </summary>
    public async Task<ResolverResult> ResolveAsync(
        MediaType mediaType,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        metadata ??= new Dictionary<string, string>();

        return mediaType switch
        {
            MediaType.Music => await ResolveMusicAsync(metadata, ct),
            MediaType.TV => await ResolveTvAsync(metadata, ct),
            MediaType.Comics => await ResolveComicsAsync(metadata, ct),

            MediaType.Books or MediaType.Audiobooks => await ResolveBookOrAudiobookAsync(mediaType, metadata, ct),
            _ => await ResolveStandaloneAsync(mediaType, ct),
        };
    }

    // ── Per-media-type strategies ─────────────────────────────────────────────

    private async Task<ResolverResult> ResolveMusicAsync(
        IReadOnlyDictionary<string, string> meta, CancellationToken ct)
    {
        var artist = Get(meta, "album_artist") ?? Get(meta, "artist");
        var album = Get(meta, "album");
        var title = Get(meta, "title");
        var track = OrdinalNormalizer.NormalizeDiscTrack(
            Get(meta, MetadataFieldConstants.DiscNumber),
            Get(meta, MetadataFieldConstants.TrackNumber) ?? Get(meta, "track"));

        if (string.IsNullOrWhiteSpace(album))
        {
            return await CreateStandaloneAsync(MediaType.Music, ct);
        }

        var parentKey = MakeKey(artist, album);
        var parentId = await FindOrCreateParentAsync(MediaType.Music, parentKey, null, null, ct);

        return await FindOrCreateChildAsync(
            MediaType.Music,
            parentId,
            OrdinalNormalizer.IntegerOrdinal(track.SortValue),
            track.SortValue,
            title,
            ct);
    }

    private async Task<ResolverResult> ResolveTvAsync(
        IReadOnlyDictionary<string, string> meta, CancellationToken ct)
    {
        var show = Get(meta, "show_name") ?? Get(meta, "series");
        var season = ParseInt(Get(meta, MetadataFieldConstants.SeasonNumber) ?? Get(meta, "season"));
        var episode = ParseInt(Get(meta, MetadataFieldConstants.EpisodeNumber) ?? Get(meta, "episode"));
        var epTitle = Get(meta, "episode_title") ?? Get(meta, "title");

        if (string.IsNullOrWhiteSpace(show))
        {
            return await CreateStandaloneAsync(MediaType.TV, ct);
        }

        // Level 1: Show parent.
        var showKey = MakeKey(show);
        var showId = await FindOrCreateParentAsync(MediaType.TV, showKey, null, null, ct);

        // Level 2: Season parent (keyed by show_id + season_number, not parent_key).
        // When season is missing we treat the episode as a direct child of the show
        // — rare but defensible for miniseries / specials.
        if (season is null)
        {
            return await FindOrCreateChildAsync(MediaType.TV, showId, episode, episode, epTitle, ct);
        }

        var seasonKey = MakeKey(show, $"S{season:D2}");
        var seasonId = await _works.GetOrCreateParentAsync(
            MediaType.TV, seasonKey, showId, season, season, ct);

        // Level 3: Episode child under the season.
        return await FindOrCreateChildAsync(MediaType.TV, seasonId, episode, episode, epTitle, ct);
    }

    private async Task<ResolverResult> ResolveComicsAsync(
        IReadOnlyDictionary<string, string> meta, CancellationToken ct)
    {
        var series = Get(meta, "series");
        var issue = OrdinalNormalizer.Normalize(
            Get(meta, MetadataFieldConstants.IssueNumber)
            ?? Get(meta, MetadataFieldConstants.SeriesPosition)
            ?? Get(meta, "issue"));
        var title = Get(meta, "title");

        if (string.IsNullOrWhiteSpace(series))
        {
            return await CreateStandaloneAsync(MediaType.Comics, ct);
        }

        var parentKey = MakeKey(series);
        var parentId = await FindOrCreateParentAsync(MediaType.Comics, parentKey, null, null, ct);
        return await FindOrCreateChildAsync(
            MediaType.Comics,
            parentId,
            OrdinalNormalizer.IntegerOrdinal(issue.SortValue),
            issue.SortValue,
            title,
            ct);
    }

    private async Task<ResolverResult> ResolveBookOrAudiobookAsync(
        MediaType mediaType,
        IReadOnlyDictionary<string, string> meta,
        CancellationToken ct)
    {
        var series = Get(meta, "series");
        var author = Get(meta, "author") ?? Get(meta, "creator");
        var position = OrdinalNormalizer.Normalize(Get(meta, MetadataFieldConstants.SeriesPosition) ?? Get(meta, "series_index"));
        var title = Get(meta, "title");

        if (mediaType == MediaType.Audiobooks && Get(meta, "audiobook_recording_key") is { } recordingKey)
        {
            Guid? seriesId = string.IsNullOrWhiteSpace(series) ? null
                : await FindOrCreateParentAsync(mediaType, MakeKey(NormalizePersonNameForKey(author), series), null, null, ct);
            var recordingId = await _works.GetOrCreateAudiobookRecordingAsync(recordingKey, seriesId, position.SortValue, ct);
            return new ResolverResult(recordingId, seriesId, seriesId.HasValue ? WorkKind.Child : WorkKind.Standalone,
                OrdinalNormalizer.IntegerOrdinal(position.SortValue), NewlyCreated: false);
        }

        if (mediaType == MediaType.Audiobooks && string.IsNullOrWhiteSpace(series))
        {
            var bookTitle = Get(meta, "book_title") ?? Get(meta, "album");
            var part = ParseInt(Get(meta, "audiobook_part_number"));
            var partCount = ParseInt(Get(meta, "audiobook_part_count"));
            if (!string.IsNullOrWhiteSpace(bookTitle) && partCount is > 1)
            {
                var audiobookKey = MakeKey(NormalizePersonNameForKey(author), bookTitle);
                var audiobookParentId = await FindOrCreateParentAsync(mediaType, audiobookKey, null, null, ct);
                return await FindOrCreateChildAsync(mediaType, audiobookParentId, part, part, title, ct);
            }
        }

        if (string.IsNullOrWhiteSpace(series))
        {
            return await CreateStandaloneAsync(mediaType, ct);
        }

        var parentKey = MakeKey(NormalizePersonNameForKey(author), series);
        var parentId = await FindOrCreateParentAsync(mediaType, parentKey, null, null, ct);
        return await FindOrCreateChildAsync(
            mediaType,
            parentId,
            OrdinalNormalizer.IntegerOrdinal(position.SortValue),
            position.SortValue,
            title,
            ct);
    }

    private async Task<ResolverResult> ResolveStandaloneAsync(
        MediaType mediaType, CancellationToken ct)
        => await CreateStandaloneAsync(mediaType, ct);

    // ── Shared helpers ────────────────────────────────────────────────────────

    private async Task<Guid> FindOrCreateParentAsync(
        MediaType mediaType,
        string parentKey,
        Guid? grandparent,
        int? ordinal,
        CancellationToken ct)
    {
        var newId = await _works.GetOrCreateParentAsync(mediaType, parentKey, grandparent, ordinal, ordinal, ct);
        _logger?.LogDebug(
            "HierarchyResolver: resolved {MediaType} parent {WorkId} key='{Key}'",
            mediaType, newId, parentKey);
        return newId;
    }

    private async Task<ResolverResult> FindOrCreateChildAsync(
        MediaType mediaType,
        Guid parentId,
        int? ordinal,
        double? ordinalSort,
        string? title,
        CancellationToken ct)
    {
        // Prefer ordinal lookup — it's the indexed path and tolerates
        // re-tagged titles. Fall back to title match when no ordinal.
        if (ordinalSort is { } sort)
        {
            var bySort = await _works.FindChildByOrdinalSortAsync(parentId, sort, ct);
            if (bySort is { } existingId)
            {
                await _works.PromoteCatalogToOwnedAsync(existingId, ct);
                await _works.UpdateOrdinalSortAsync(existingId, sort, ct);
                return new ResolverResult(existingId, parentId, WorkKind.Child, ordinal, NewlyCreated: false);
            }
        }

        if (ordinal is { } o
            && (ordinalSort is null || Math.Abs(ordinalSort.Value - o) < 0.0001d))
        {
            var byOrdinal = await _works.FindChildByOrdinalAsync(parentId, o, ct);
            if (byOrdinal is { } existingId)
            {
                // Catalog row hit: promote to owned and return.
                await _works.PromoteCatalogToOwnedAsync(existingId, ct);
                await _works.UpdateOrdinalSortAsync(existingId, ordinalSort, ct);
                return new ResolverResult(existingId, parentId, WorkKind.Child, o, NewlyCreated: false);
            }
        }
        else if (!string.IsNullOrWhiteSpace(title))
        {
            var byTitle = await _works.FindChildByTitleAsync(parentId, title, ct);
            if (byTitle is { } existingId)
            {
                await _works.PromoteCatalogToOwnedAsync(existingId, ct);
                await _works.UpdateOrdinalSortAsync(existingId, ordinalSort, ct);
                return new ResolverResult(existingId, parentId, WorkKind.Child, null, NewlyCreated: false);
            }
        }

        var newId = await _works.GetOrCreateChildAsync(mediaType, parentId, ordinal, ordinalSort, ct);
        return new ResolverResult(newId, parentId, WorkKind.Child, ordinal, NewlyCreated: true);
    }

    private async Task<ResolverResult> CreateStandaloneAsync(
        MediaType mediaType, CancellationToken ct)
    {
        var id = await _works.InsertStandaloneAsync(mediaType, ct);
        return new ResolverResult(id, null, WorkKind.Standalone, null, NewlyCreated: true);
    }

    // ── Normalization ─────────────────────────────────────────────────────────

    private static string? Get(IReadOnlyDictionary<string, string> meta, string key)
    {
        if (meta.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
        {
            return v;
        }

        return null;
    }

    private static string? NormalizePersonNameForKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var parts = value.Split(',', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && !string.IsNullOrWhiteSpace(parts[0])
            && !string.IsNullOrWhiteSpace(parts[1])
            && !parts[1].Contains(',', StringComparison.Ordinal)
            ? $"{parts[1]} {parts[0]}"
            : value;
    }

    private static int? ParseInt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Tolerate "01", "1", "1/12", "01 of 12" — take the leading integer.
        var sb = new StringBuilder();
        foreach (var ch in raw.Trim())
        {
            if (char.IsDigit(ch))
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0)
            {
                break;
            }
        }
        return sb.Length > 0 && int.TryParse(sb.ToString(), out var n) ? n : null;
    }

    /// <summary>
    /// Builds a normalized parent_key by lowercasing, trimming, collapsing
    /// whitespace, stripping diacritics, and joining parts with '|'.
    /// Two slightly different spellings of the same album/show/series will
    /// hash to different keys — that's intentional. The resolver doesn't
    /// fuzzy-match; it relies on the file metadata being consistent across
    /// siblings (which it almost always is when files come from the same
    /// rip, season, or batch download).
    /// </summary>
    private static string MakeKey(params string?[] parts)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            if (!first)
            {
                sb.Append('|');
            }

            sb.Append(Normalize(part));
            first = false;
        }
        return sb.ToString();
    }

    private static string Normalize(string value)
    {
        // Decompose to strip diacritics, then collapse whitespace and lowercase.
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        bool prevSpace = false;
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                prevSpace = true;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(ch));
                prevSpace = false;
            }
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// The leaf-most Work the chain factory should attach an Edition to,
/// plus enough context for callers to schedule downstream work
/// (parent-level enrichment, hierarchy events, etc.).
/// </summary>
public sealed record ResolverResult(
    Guid WorkId,
    Guid? ParentWorkId,
    WorkKind WorkKind,
    int? Ordinal,
    bool NewlyCreated);
