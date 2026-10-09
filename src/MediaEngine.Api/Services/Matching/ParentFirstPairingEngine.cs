using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Matching;

public enum PairingMediaKind { TvEpisode, MusicReleaseTrack }
public enum PairingBand { Exact, Strong, Review, NoMatch }

/// <summary>Original file evidence stays separate from its current library assignment.</summary>
public sealed record PairingAssetEvidence(
    Guid AssetId,
    string FileName,
    string? Title = null,
    string? CurrentChildId = null,
    string? ScopedChildId = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    int? DiscNumber = null,
    int? TrackNumber = null,
    int? AbsoluteNumber = null,
    TimeSpan? Duration = null,
    DateOnly? Date = null,
    bool IsCombined = false,
    bool IsSplitPart = false);

/// <summary>Child identity is scoped to a specific provider parent and catalogue order.</summary>
public sealed record PairingCatalogueChild(
    string ChildId,
    string ParentId,
    string Provider,
    string Title,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    int? DiscNumber = null,
    int? TrackNumber = null,
    int? AbsoluteNumber = null,
    TimeSpan? Duration = null,
    DateOnly? Date = null,
    string? RecordingId = null);

public sealed record PairingRequest(
    PairingMediaKind MediaKind,
    string Provider,
    string ParentId,
    IReadOnlyList<PairingAssetEvidence> Assets,
    IReadOnlyList<PairingCatalogueChild> Catalogue,
    bool CatalogueComplete,
    string Order = "default");

public sealed record PairingCandidate(
    PairingCatalogueChild Child,
    PairingBand Band,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Conflicts);

public sealed record PairingRow(
    PairingAssetEvidence Asset,
    PairingCandidate? Proposed,
    IReadOnlyList<PairingCandidate> Alternatives,
    PairingBand Band,
    bool CanPreselect,
    string? Limitation);

public sealed record PairingPreview(IReadOnlyList<PairingRow> Rows, bool CatalogueComplete);

/// <summary>
/// Pure, side-effect-free child proposal logic. It never mutates identity or assumes
/// that two versions of the same episode/track constitute a collision.
/// </summary>
public static class ParentFirstPairingEngine
{
    private const int AmbiguityMargin = 20;

    public static PairingPreview Preview(PairingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.ParentId))
        {
            throw new ArgumentException("A provider and exact target parent are required.", nameof(request));
        }
        if (request.MediaKind == PairingMediaKind.TvEpisode
            && !string.Equals(request.Order, "default", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("TV pairing uses TheTVDB default order.", nameof(request));
        }

        var catalogue = request.Catalogue
            .Where(child => child.ParentId.Equals(request.ParentId, StringComparison.OrdinalIgnoreCase)
                && child.Provider.Equals(request.Provider, StringComparison.OrdinalIgnoreCase))
            .OrderBy(child => request.MediaKind == PairingMediaKind.TvEpisode ? child.SeasonNumber : child.DiscNumber)
            .ThenBy(child => request.MediaKind == PairingMediaKind.TvEpisode ? child.EpisodeNumber : child.TrackNumber)
            .ThenBy(child => child.ChildId, StringComparer.Ordinal)
            .ToArray();
        var rows = request.Assets.Select(asset => Pair(asset, catalogue, request)).ToArray();
        return new PairingPreview(rows, request.CatalogueComplete);
    }

    private static PairingRow Pair(PairingAssetEvidence asset, PairingCatalogueChild[] catalogue, PairingRequest request)
    {
        var combinedByName = request.MediaKind == PairingMediaKind.TvEpisode
            && EpisodePatterns.SeasonEpisode().Match(Path.GetFileNameWithoutExtension(asset.FileName)).Groups["ep2"].Success;
        if (asset.IsCombined || combinedByName || asset.IsSplitPart)
        {
            return new PairingRow(asset, null, [], PairingBand.Review, false,
                    asset.IsCombined || combinedByName ? "Combined media needs an explicit supported multi-child mapping."
                        : "Split media needs an explicit supported part mapping.");
        }

        var ranked = catalogue.Select(child => Score(asset, child, request.MediaKind))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Child.ChildId, StringComparer.Ordinal)
            .ToArray();
        if (ranked.Length == 0)
        {
            return new PairingRow(asset, null, [], request.CatalogueComplete ? PairingBand.NoMatch : PairingBand.Review,
                    false, request.CatalogueComplete ? "No child has supporting evidence."
                        : "The catalogue may be incomplete; refresh before ruling out a match.");
        }

        var top = ranked[0];
        var next = ranked.Skip(1).FirstOrDefault();
        var ambiguous = next is not null && top.Score - next.Score < AmbiguityMargin;
        var conflicts = top.Conflicts.ToList();
        if (ambiguous)
        {
            conflicts.Add("Another catalogue child has similarly strong evidence.");
        }
        if (!request.CatalogueComplete)
        {
            conflicts.Add("The catalogue is incomplete; other candidates may be missing.");
        }

        var band = conflicts.Count > 0 ? PairingBand.Review : top.Band;
        var proposed = new PairingCandidate(top.Child, band, top.Reasons, conflicts);
        var alternatives = ranked.Skip(1).Take(4)
            .Select(item => new PairingCandidate(item.Child, item.Band, item.Reasons, item.Conflicts))
            .ToArray();
        return new PairingRow(asset, proposed, alternatives, band,
            request.CatalogueComplete && !ambiguous && conflicts.Count == 0 && band is PairingBand.Exact or PairingBand.Strong,
            null);
    }

    private static ScoredChild Score(PairingAssetEvidence asset, PairingCatalogueChild child, PairingMediaKind kind)
    {
        var score = 0;
        var reasons = new List<string>();
        var conflicts = new List<string>();
        var scopedIdMatch = !string.IsNullOrWhiteSpace(asset.ScopedChildId)
            && asset.ScopedChildId.Equals(child.ChildId, StringComparison.OrdinalIgnoreCase);
        if (scopedIdMatch)
        {
            score += 100;
            reasons.Add("The file's scoped provider child ID matches this catalogue entry.");
        }

        var sourceTitle = asset.Title;
        if (string.IsNullOrWhiteSpace(sourceTitle))
        {
            sourceTitle = ExtractFileTitle(asset.FileName, kind);
        }
        if (!string.IsNullOrWhiteSpace(sourceTitle))
        {
            var normalized = NormalizeTitle(sourceTitle);
            var candidateTitle = NormalizeTitle(child.Title);
            if (normalized == candidateTitle)
            {
                score += 75;
                reasons.Add("The title matches.");
            }
            else if (normalized.Length >= 4 && candidateTitle.Length >= 4
                && (normalized.Contains(candidateTitle, StringComparison.Ordinal)
                    || candidateTitle.Contains(normalized, StringComparison.Ordinal)))
            {
                score += 20;
                reasons.Add("The titles partly agree.");
            }
            else if (asset.Title is not null || sourceTitle != asset.FileName)
            {
                conflicts.Add("The source title disagrees with this catalogue title.");
            }
        }

        var numbered = false;
        var positionDisagrees = false;
        if (kind == PairingMediaKind.TvEpisode)
        {
            var season = asset.SeasonNumber;
            var episode = asset.EpisodeNumber;
            var filenameMatch = EpisodePatterns.SeasonEpisode().Match(Path.GetFileNameWithoutExtension(asset.FileName));
            if (filenameMatch.Success)
            {
                if (int.TryParse(filenameMatch.Groups["season"].Value, out var filenameSeason))
                {
                    season ??= filenameSeason;
                }
                if (int.TryParse(filenameMatch.Groups["ep1"].Value, out var filenameEpisode))
                {
                    episode ??= filenameEpisode;
                }
                if (filenameMatch.Groups["ep2"].Success)
                {
                    conflicts.Add("The filename names more than one episode.");
                }
            }
            if (season.HasValue && episode.HasValue)
            {
                numbered = true;
                if (season == child.SeasonNumber && episode == child.EpisodeNumber)
                {
                    score += 60;
                    reasons.Add("Season and episode numbers match the default TheTVDB order.");
                }
                else
                {
                    positionDisagrees = true;
                }
            }
            if (asset.AbsoluteNumber.HasValue && child.AbsoluteNumber.HasValue
                && asset.AbsoluteNumber == child.AbsoluteNumber)
            {
                score += 10;
                reasons.Add("Absolute numbering supports this episode; default season order remains canonical.");
            }
        }
        else if (asset.DiscNumber.HasValue && asset.TrackNumber.HasValue)
        {
            numbered = true;
            if (asset.DiscNumber == child.DiscNumber && asset.TrackNumber == child.TrackNumber)
            {
                score += 60;
                reasons.Add("Disc and release-track positions match this exact release.");
            }
            else
            {
                positionDisagrees = true;
            }
        }

        if (asset.Duration.HasValue && child.Duration.HasValue)
        {
            var difference = Math.Abs((asset.Duration.Value - child.Duration.Value).TotalSeconds);
            if (difference <= 3)
            {
                score += 10;
                reasons.Add("Durations agree within three seconds.");
            }
            else if (difference > 30)
            {
                conflicts.Add("Durations differ by more than thirty seconds.");
            }
        }
        if (asset.Date.HasValue && child.Date.HasValue && asset.Date == child.Date)
        {
            score += 5;
            reasons.Add("Dates agree.");
        }

        // A recording is not a release-track: repeated recordings on one release
        // remain separate candidates and must be selected by position/context.
        var hasTitle = reasons.Any(reason => reason.StartsWith("The title", StringComparison.Ordinal));
        if (positionDisagrees && (scopedIdMatch || hasTitle))
        {
            conflicts.Add(kind == PairingMediaKind.TvEpisode
                    ? "The source season or episode number disagrees with this candidate."
                    : "The source disc or track number disagrees with this release-track candidate.");
        }
        var band = scopedIdMatch && conflicts.Count == 0 ? PairingBand.Exact
            : numbered && score >= 60 && (hasTitle || string.IsNullOrWhiteSpace(sourceTitle)) ? PairingBand.Strong
            : !numbered && hasTitle && score >= 75 ? PairingBand.Strong
            : PairingBand.Review;
        return new ScoredChild(child, score, band, reasons, conflicts);
    }

    private static string? ExtractFileTitle(string fileName, PairingMediaKind kind)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (kind == PairingMediaKind.TvEpisode)
        {
            var match = EpisodePatterns.SeasonEpisode().Match(stem);
            if (!match.Success)
            {
                return null;
            }
            var after = stem[(match.Index + match.Length)..].Trim(' ', '.', '_', '-');
            if (after is "4K" or "720p" or "1080p" or "2160p"
                || after.StartsWith("1080p.", StringComparison.OrdinalIgnoreCase)
                || after.StartsWith("2160p.", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return after.Length > 0 ? after : null;
        }
        var index = 0;
        while (index < stem.Length && (char.IsDigit(stem[index]) || stem[index] is ' ' or '-' or '_' or '.'))
        {
            index++;
        }
        return index > 0 && index < stem.Length ? stem[index..] : null;
    }

    private static string NormalizeTitle(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record ScoredChild(PairingCatalogueChild Child, int Score, PairingBand Band,
        IReadOnlyList<string> Reasons, IReadOnlyList<string> Conflicts);
}

/// <summary>Pure adapters for the existing exact-parent provider catalogue shapes.</summary>
public static class ParentFirstCatalogueAdapters
{
    public static IReadOnlyList<PairingCatalogueChild> FromTvdbDefaultEpisodes(string seriesId, IEnumerable<JsonNode> episodes)
    {
        return episodes.Select(node => new PairingCatalogueChild(
                ChildId: node["id"]?.ToString() ?? string.Empty,
                ParentId: seriesId,
                Provider: "tvdb",
                Title: node["name"]?.ToString() ?? string.Empty,
                SeasonNumber: ReadInt(node["seasonNumber"]),
                EpisodeNumber: ReadInt(node["number"]),
                AbsoluteNumber: ReadInt(node["absoluteNumber"]),
                Duration: ReadInt(node["runtime"]) is { } minutes ? TimeSpan.FromMinutes(minutes) : null,
                Date: DateOnly.TryParse(node["aired"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var aired) ? aired : null))
            .Where(child => child.ChildId.Length > 0 && child.SeasonNumber.HasValue && child.EpisodeNumber.HasValue)
            .OrderBy(child => child.SeasonNumber).ThenBy(child => child.EpisodeNumber).ToArray();
    }

    public static IReadOnlyList<PairingCatalogueChild> FromMusicBrainzReleaseManifest(string releaseId, string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("provider_collection_id", out var providerId)
            || !string.Equals(providerId.GetString(), releaseId, StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("source", out var source)
            || source.GetString() != "musicbrainz_release"
            || !root.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return tracks.EnumerateArray().Select(track =>
            {
                var disc = ReadInt(track, "disc_number");
                var number = ReadInt(track, "track_number");
                var title = ReadString(track, "title");
                var providerTrackId = ReadString(track, "musicbrainz_release_track_id");
                var childId = Guid.TryParse(providerTrackId, out var trackId) && trackId != Guid.Empty
                    ? trackId.ToString("D", CultureInfo.InvariantCulture) : string.Empty;
                return new PairingCatalogueChild(childId, releaseId, "musicbrainz", title ?? string.Empty,
                    DiscNumber: disc, TrackNumber: number,
                    Duration: ReadDouble(track, "duration_seconds") is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                    RecordingId: ReadString(track, "musicbrainz_recording_id"));
            })
            .Where(child => child.ChildId.Length > 0 && child.Title.Length > 0)
            .GroupBy(child => child.ChildId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .OrderBy(child => child.DiscNumber).ThenBy(child => child.TrackNumber).ToArray();
    }

    private static int? ReadInt(JsonNode? node) => int.TryParse(node?.ToString(), CultureInfo.InvariantCulture, out var value) ? value : null;
    private static int? ReadInt(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.TryGetInt32(out var number) ? number : null;
    private static double? ReadDouble(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.TryGetDouble(out var number) ? number : null;
    private static string? ReadString(JsonElement element, string key) => element.TryGetProperty(key, out var value) ? value.GetString() : null;
}
