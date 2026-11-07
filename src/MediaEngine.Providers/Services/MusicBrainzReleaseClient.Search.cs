using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>One release in a MusicBrainz search: the facts that tell pressings of an album apart.</summary>
public sealed record MusicBrainzReleaseSummary(
    string ReleaseId,
    string? ReleaseGroupId,
    string Title,
    string? Artist,
    string? Year,
    string? Label,
    string? Format,
    string? Country,
    int TrackCount,
    bool HasFrontCover);

/// <summary>One track of a release, with the recording's own note that tells recordings of a song apart.</summary>
public sealed record MusicBrainzTrackDetail(
    string? RecordingId,
    string? ReleaseTrackId,
    int Disc,
    int Track,
    string Title,
    string? Artist,
    double? LengthSeconds,
    string? Disambiguation);

public sealed record MusicBrainzDiscDetail(int Number, string? Title, IReadOnlyList<MusicBrainzTrackDetail> Tracks);

/// <summary>A release opened: the summary and every disc with its tracks.</summary>
public sealed record MusicBrainzReleaseDetail(MusicBrainzReleaseSummary Summary, IReadOnlyList<MusicBrainzDiscDetail> Discs);

/// <summary>
/// The reads the Match tab's music flow needs (universal editor v5, 3c "Music, step by step"): the releases of an album, and one release's
/// discs and tracks with lengths and recording disambiguation. Every request goes through the provider's rate limiter (one a second), and
/// answers are kept for the provider's own <c>cache_ttl_hours</c>; an empty answer is never kept, so a failed or empty search is asked again.
/// </summary>
public sealed partial class MusicBrainzReleaseClient
{
    /// <summary>
    /// Searches releases by album (and artist), or, with no album, by the song's recordings and the releases they appear on. Null when the
    /// provider did not answer; an empty list when it answered with nothing.
    /// </summary>
    public async Task<IReadOnlyList<MusicBrainzReleaseSummary>?> SearchReleasesAsync(
        string? artist, string? album, string? song, int limit, CancellationToken ct)
    {
        var byRelease = !string.IsNullOrWhiteSpace(album);
        if (!byRelease && string.IsNullOrWhiteSpace(song))
        {
            return [];
        }

        var query = byRelease
            ? Lucene(("release", album), ("artist", artist))
            : Lucene(("recording", song), ("artist", artist));
        var url = $"{BaseUrl()}/{(byRelease ? "release" : "recording")}?query={Uri.EscapeDataString(query)}&fmt=json&limit={limit.ToString(CultureInfo.InvariantCulture)}";
        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        return byRelease ? ParseReleaseSearch(json) : ParseRecordingSearch(json);
    }

    /// <summary>One release with its discs and tracks; null when the provider did not answer or has no such release.</summary>
    public async Task<MusicBrainzReleaseDetail?> FetchReleaseDetailAsync(string releaseId, CancellationToken ct)
    {
        if (!Guid.TryParse(releaseId, out var parsed))
        {
            return null;
        }

        var id = parsed.ToString("D", CultureInfo.InvariantCulture);
        var url = $"{BaseUrl()}/release/{id}?inc=recordings%2Bartist-credits%2Blabels%2Brelease-groups&fmt=json";
        return ParseReleaseDetail(await GetJsonAsync(url, ct).ConfigureAwait(false), id);
    }

    /// <summary>Releases from a release search answer, best first as MusicBrainz ranked them.</summary>
    public static IReadOnlyList<MusicBrainzReleaseSummary> ParseReleaseSearch(JsonNode? json) =>
        [.. (json?["releases"]?.AsArray() ?? []).Select(node => Summary(node, null)).OfType<MusicBrainzReleaseSummary>()];

    /// <summary>The releases the recordings of a recording search appear on, each once, in the order found.</summary>
    public static IReadOnlyList<MusicBrainzReleaseSummary> ParseRecordingSearch(JsonNode? json)
    {
        var found = new List<MusicBrainzReleaseSummary>();
        foreach (var recording in json?["recordings"]?.AsArray() ?? [])
        {
            var credit = ReadArtistCredit(recording?["artist-credit"]?.AsArray());
            foreach (var release in recording?["releases"]?.AsArray() ?? [])
            {
                if (Summary(release, credit) is { } summary && found.All(item => item.ReleaseId != summary.ReleaseId))
                {
                    found.Add(summary);
                }
            }
        }

        return found;
    }

    /// <summary>A release answer read into discs and tracks; null when it is not the expected release or lists no tracks.</summary>
    public static MusicBrainzReleaseDetail? ParseReleaseDetail(JsonNode? json, string expectedReleaseId)
    {
        if (json is null || !string.Equals(Text(json, "id"), expectedReleaseId, StringComparison.OrdinalIgnoreCase)
            || Summary(json, null) is not { } summary)
        {
            return null;
        }

        var discs = new List<MusicBrainzDiscDetail>();
        var ordinal = 0;
        foreach (var medium in (json["media"]?.AsArray() ?? []).OfType<JsonNode>().OrderBy(node => Whole(node, "position") ?? int.MaxValue))
        {
            ordinal++;
            var disc = Whole(medium, "position") ?? ordinal;
            var tracks = new List<MusicBrainzTrackDetail>();
            var position = 0;
            foreach (var track in (medium["tracks"]?.AsArray() ?? []).OfType<JsonNode>().OrderBy(node => Whole(node, "position") ?? int.MaxValue))
            {
                position++;
                var recording = track["recording"];
                var title = Text(track, "title") ?? Text(recording, "title");
                if (title is null)
                {
                    continue;
                }

                var millis = Long(track, "length") ?? Long(recording, "length");
                tracks.Add(new MusicBrainzTrackDetail(
                    Text(recording, "id"),
                    Text(track, "id"),
                    disc,
                    Whole(track, "position") ?? position,
                    title,
                    ReadArtistCredit(track["artist-credit"]?.AsArray()) ?? ReadArtistCredit(recording?["artist-credit"]?.AsArray()),
                    millis is > 0 ? Math.Round(millis.Value / 1000d, 3) : null,
                    Text(recording, "disambiguation")));
            }

            if (tracks.Count > 0)
            {
                discs.Add(new MusicBrainzDiscDetail(disc, Text(medium, "title"), tracks));
            }
        }

        return discs.Count == 0 ? null : new MusicBrainzReleaseDetail(summary, discs);
    }

    private static MusicBrainzReleaseSummary? Summary(JsonNode? node, string? fallbackArtist)
    {
        var id = Text(node, "id");
        var title = Text(node, "title");
        if (id is null || title is null || !Guid.TryParse(id, out _))
        {
            return null;
        }

        var media = (node!["media"]?.AsArray() ?? []).OfType<JsonNode>().ToList();
        var trackCount = Whole(node, "track-count") ?? media.Sum(medium => Whole(medium, "track-count") ?? medium["tracks"]?.AsArray().Count ?? 0);
        var date = Text(node, "date");
        var labels = (node["label-info"]?.AsArray() ?? []).Select(info => Text(info?["label"], "name")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var front = node["cover-art-archive"]?["front"]?.GetValue<bool?>() == true || node["cover-art-archive"]?["artwork"]?.GetValue<bool?>() == true;
        return new MusicBrainzReleaseSummary(
            id,
            Text(node["release-group"], "id"),
            title,
            ReadArtistCredit(node["artist-credit"]?.AsArray()) ?? fallbackArtist,
            date is { Length: >= 4 } && date[..4].All(char.IsAsciiDigit) ? date[..4] : null,
            labels.Count == 0 ? null : string.Join(", ", labels),
            FormatOf(media),
            Text(node, "country"),
            trackCount,
            front);
    }

    // "CD" for one disc, "2 × CD" for two of the same, "CD + Vinyl" for different ones.
    private static string? FormatOf(IReadOnlyList<JsonNode> media)
    {
        var groups = media
            .Select(medium => Text(medium, "format"))
            .OfType<string>()
            .GroupBy(format => format, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Count() > 1 ? $"{group.Count().ToString(CultureInfo.InvariantCulture)} × {group.Key}" : group.Key)
            .ToList();
        return groups.Count == 0 ? null : string.Join(" + ", groups);
    }

    private static string Lucene(params (string Field, string? Value)[] clauses) =>
        string.Join(" AND ", clauses
            .Where(clause => !string.IsNullOrWhiteSpace(clause.Value))
            .Select(clause => $"{clause.Field}:\"{clause.Value!.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""));

    private string BaseUrl()
    {
        var configured = configurationSnapshots?.Current.Providers
            .FirstOrDefault(provider => string.Equals(provider.Name, "musicbrainz", StringComparison.OrdinalIgnoreCase))?
            .Endpoints.GetValueOrDefault("api");
        return string.IsNullOrWhiteSpace(configured) ? "https://musicbrainz.org/ws/2" : configured.TrimEnd('/');
    }

    // The answers kept for the provider's cache_ttl_hours, by address; bounded so a long session cannot grow it without end.
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, JsonNode Json)> _kept = new(StringComparer.Ordinal);

    private const int MaxKeptAnswers = 100;

    // One rate-limited GET, kept for the provider's cache_ttl_hours when it answered with something. Null when it did not answer.
    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        if (_kept.TryGetValue(url, out var kept))
        {
            if (kept.Expires > DateTimeOffset.UtcNow)
            {
                return kept.Json;
            }

            _kept.TryRemove(url, out _);
        }

        try
        {
            using var client = httpFactory.CreateClient("musicbrainz");
            var json = await rateLimiter.ExecuteAsync(
                "musicbrainz",
                ProviderRateLimitDefaults.MusicBrainz,
                token => client.GetFromJsonAsync<JsonNode>(url, token),
                ct).ConfigureAwait(false);
            var hours = configurationSnapshots?.Current.Providers
                .FirstOrDefault(provider => string.Equals(provider.Name, "musicbrainz", StringComparison.OrdinalIgnoreCase))?.CacheTtlHours;
            if (json is not null && hours is > 0 && HasContent(json))
            {
                if (_kept.Count >= MaxKeptAnswers)
                {
                    _kept.Clear();
                }

                _kept[url] = (DateTimeOffset.UtcNow.AddHours(hours.Value), json);
            }

            return json;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "MusicBrainz did not answer for {Url}", url);
            return null;
        }
    }

    // An answer with no releases, recordings or media is an empty answer: it is never kept.
    private static bool HasContent(JsonNode json) =>
        json["releases"]?.AsArray().Count > 0 || json["recordings"]?.AsArray().Count > 0 || json["media"]?.AsArray().Count > 0;

    private static string? Text(JsonNode? node, string key) =>
        node?[key] is JsonValue value && value.ToString().Trim() is { Length: > 0 } text ? text : null;

    private static int? Whole(JsonNode? node, string key) =>
        int.TryParse(Text(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static long? Long(JsonNode? node, string key) =>
        long.TryParse(Text(node, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;
}
