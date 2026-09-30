using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

public sealed record MusicBrainzAlbumRelease(
    string ReleaseId,
    string ManifestJson,
    int TrackCount,
    string? CoverUrl);

/// <summary>
/// Resolves the exact MusicBrainz release selected during track identification.
/// Album manifests must use this release identity rather than repeat a looser
/// title search that can drift to a compilation or expanded edition.
/// </summary>
public sealed class MusicBrainzReleaseClient(
    IHttpClientFactory httpFactory,
    IProviderRateLimiterCoordinator rateLimiter,
    ILogger<MusicBrainzReleaseClient> logger,
    IPipelineExecutionSnapshotProvider? configurationSnapshots = null)
{
    public async Task<MusicBrainzAlbumRelease?> FetchReleaseAsync(
        string releaseId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(releaseId, out var parsedReleaseId))
        {
            return null;
        }

        var normalizedReleaseId = parsedReleaseId.ToString("D", CultureInfo.InvariantCulture);
        var configuredEndpoint = configurationSnapshots?.Current.Providers
            .FirstOrDefault(provider => string.Equals(provider.Name, "musicbrainz", StringComparison.OrdinalIgnoreCase))?
            .Endpoints.GetValueOrDefault("api");
        var baseUrl = string.IsNullOrWhiteSpace(configuredEndpoint)
            ? "https://musicbrainz.org/ws/2"
            : configuredEndpoint.TrimEnd('/');
        var url = $"{baseUrl}/release/{normalizedReleaseId}?inc=recordings%2Bartist-credits&fmt=json";

        try
        {
            using var client = httpFactory.CreateClient("musicbrainz");
            var json = await rateLimiter.ExecuteAsync(
                "musicbrainz",
                ProviderRateLimitDefaults.MusicBrainz,
                token => client.GetFromJsonAsync<JsonNode>(url, token),
                ct).ConfigureAwait(false);

            return BuildRelease(json, normalizedReleaseId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "MusicBrainz exact release lookup failed for release {ReleaseId}",
                normalizedReleaseId);
            return null;
        }
    }

    public static MusicBrainzAlbumRelease? BuildRelease(
        JsonNode? release,
        string expectedReleaseId)
    {
        if (release is null)
        {
            return null;
        }

        var releaseId = release["id"]?.GetValue<string>();
        if (!string.Equals(releaseId, expectedReleaseId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tracks = new JsonArray();
        var globalOrdinal = 0;
        var media = release["media"]?.AsArray();
        if (media is null)
        {
            return null;
        }

        foreach (var medium in media
                     .Where(node => node is not null)
                     .OrderBy(node => node!["position"]?.GetValue<int?>() ?? int.MaxValue))
        {
            var discNumber = medium!["position"]?.GetValue<int?>() ?? 1;
            var mediumTracks = medium["tracks"]?.AsArray();
            if (mediumTracks is null)
            {
                continue;
            }

            foreach (var track in mediumTracks
                         .Where(node => node is not null)
                         .OrderBy(node => node!["position"]?.GetValue<int?>() ?? int.MaxValue))
            {
                var title = track!["title"]?.GetValue<string>()
                    ?? track["recording"]?["title"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                globalOrdinal++;
                var trackNumber = track["position"]?.GetValue<int?>() ?? globalOrdinal;
                var durationMillis = track["length"]?.GetValue<long?>()
                    ?? track["recording"]?["length"]?.GetValue<long?>();
                var item = new JsonObject
                {
                    ["title"] = title,
                    ["ordinal"] = globalOrdinal,
                    ["track_number"] = trackNumber,
                    ["disc_number"] = discNumber,
                    ["source"] = "musicbrainz",
                };

                if (durationMillis is > 0)
                {
                    item["duration_seconds"] = Math.Round(durationMillis.Value / 1000d, 3);
                }

                var recordingId = track["recording"]?["id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(recordingId))
                {
                    item["musicbrainz_recording_id"] = recordingId;
                }

                tracks.Add(item);
            }
        }

        if (tracks.Count == 0)
        {
            return null;
        }

        var albumTitle = release["title"]?.GetValue<string>();
        var artist = ReadArtistCredit(release["artist-credit"]?.AsArray());
        var manifest = new JsonObject
        {
            ["schema"] = "music_album_tracks_v1",
            ["source"] = MusicBrainzAlbumManifestJson.Source,
            ["provider_collection_id"] = releaseId,
            ["album"] = albumTitle,
            ["artist"] = artist,
            ["tracks"] = tracks,
        }.ToJsonString();

        var hasFrontCover =
            release["cover-art-archive"]?["front"]?.GetValue<bool?>() == true
            || release["cover-art-archive"]?["artwork"]?.GetValue<bool?>() == true;
        var coverUrl = hasFrontCover
            ? $"https://coverartarchive.org/release/{releaseId}/front-500"
            : null;

        return new MusicBrainzAlbumRelease(releaseId!, manifest, tracks.Count, coverUrl);
    }

    private static string? ReadArtistCredit(JsonArray? credits)
    {
        if (credits is null)
        {
            return null;
        }

        var parts = credits
            .Where(node => node is not null)
            .Select(node =>
                (node!["name"]?.GetValue<string>()
                 ?? node["artist"]?["name"]?.GetValue<string>()
                 ?? string.Empty)
                + (node["joinphrase"]?.GetValue<string>() ?? string.Empty))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        return parts.Count == 0 ? null : string.Concat(parts);
    }
}

public static class MusicBrainzAlbumManifestJson
{
    public const string Source = "musicbrainz_release";

    public static bool IsCompleteForRelease(string? json, string? releaseId = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("source", out var source)
                || !string.Equals(source.GetString(), Source, StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("provider_collection_id", out var providerCollection)
                || string.IsNullOrWhiteSpace(providerCollection.GetString())
                || (!string.IsNullOrWhiteSpace(releaseId)
                    && !string.Equals(
                        providerCollection.GetString(),
                        releaseId,
                        StringComparison.OrdinalIgnoreCase))
                || !root.TryGetProperty("tracks", out var tracks)
                || tracks.ValueKind != JsonValueKind.Array
                || tracks.GetArrayLength() == 0)
            {
                return false;
            }

            return tracks.EnumerateArray().All(track =>
                track.TryGetProperty("title", out var title)
                && !string.IsNullOrWhiteSpace(title.GetString())
                && TryReadPositiveInt(track, "ordinal")
                && TryReadPositiveInt(track, "track_number"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns whether an exact MusicBrainz release manifest contains a recording.
    /// A null result means the manifest is not a usable MusicBrainz release manifest;
    /// callers can distinguish unavailable evidence from a confirmed mismatch.
    /// </summary>
    public static bool? ContainsRecording(string? json, string? recordingId)
    {
        if (string.IsNullOrWhiteSpace(json)
            || string.IsNullOrWhiteSpace(recordingId)
            || !IsCompleteForRelease(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var tracks = document.RootElement.GetProperty("tracks");
            return tracks.EnumerateArray().Any(track =>
                track.TryGetProperty("musicbrainz_recording_id", out var candidate)
                && string.Equals(candidate.GetString(), recordingId, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadPositiveInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
            && value.TryGetInt32(out var number)
            && number > 0;
}

public static class MusicAlbumManifestJson
{
    public static bool IsComplete(string? json)
        => AppleAlbumManifestJson.IsCompleteForCollection(json)
            || MusicBrainzAlbumManifestJson.IsCompleteForRelease(json);
}
