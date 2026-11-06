using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Providers;

public sealed partial class TextTrackProviderAdapter
{
    private async Task<IReadOnlyList<TextTrackCandidate>> SearchLyricsAsync(TextTrackLookup lookup, CancellationToken ct)
    {
        if (!IsEnabled || !CanHandle(lookup.MediaType))
        {
            return [];
        }

        var values = LookupValues(lookup);
        if (_text.Search.Needs.Any(need => !values.ContainsKey(need)))
        {
            return [];
        }

        var baseUrl = _config.Endpoints.GetValueOrDefault("api")?.TrimEnd('/') ?? string.Empty;
        var rules = _text.Search.Lyrics;

        // Requests are tried best first: an exact lookup when every detail is known, then a search when one is missing
        // or the exact lookup finds nothing. "Not found" is an ordinary empty answer, never an error.
        string? url = null;
        JsonElement root = default;
        JsonDocument? doc = null;
        foreach (var request in MatchingRequests(lookup.MediaType, values))
        {
            var candidateUrl = baseUrl + BuildPath(request, values);
            var json = await GetJsonAsync(candidateUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            JsonDocument parsed;
            try
            {
                parsed = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "{Provider} lyrics answer was not JSON", Name);
                continue;
            }

            var picked = parsed.RootElement.ValueKind == JsonValueKind.Array
                ? PickBestListEntry(parsed.RootElement, lookup, rules)
                : parsed.RootElement;
            if (picked is { } element && HasLyricsOrInstrumental(element, rules))
            {
                doc = parsed;
                root = element;
                url = candidateUrl;
                break;
            }

            parsed.Dispose();
        }

        if (doc is null || url is null)
        {
            return [];
        }

        using var _ = doc;
        var format = rules.FallbackFormat;
        string? lyrics = null;
        foreach (var source in rules.Sources)
        {
            format = source.Format;
            lyrics = TryPath(root, source.Path, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
            if (!string.IsNullOrWhiteSpace(lyrics))
            {
                break;
            }
        }

        var instrumental = !string.IsNullOrEmpty(rules.InstrumentalPath) && ReadBool(root, rules.InstrumentalPath) == true;
        var sourceId = !string.IsNullOrEmpty(rules.IdPath) && TryPath(root, rules.IdPath, out var id) ? id.ToString() : ComputeHash(url);
        var durationScore = ScoreDuration(root, lookup.DurationSeconds);
        var duration = rules.Duration;
        var strict = string.Equals(format, duration.StrictFormat, StringComparison.OrdinalIgnoreCase);
        if (strict && durationScore is < 0)
        {
            return [];
        }

        var confidence = durationScore.HasValue
            ? strict ? Math.Max(0, durationScore.Value) : Math.Max(duration.LenientFloor, durationScore.Value)
            : duration.DefaultConfidence;
        return
        [
            new TextTrackCandidate(
                Provider: Name,
                Kind: Kind,
                SourceId: sourceId,
                SourceUrl: url,
                Language: lookup.Language ?? _text.Language.Unknown,
                SourceFormat: format,
                Confidence: confidence,
                IsHearingImpaired: false,
                DurationMatchScore: durationScore,
                Payload: lyrics,
                IsInstrumental: instrumental),
        ];
    }

    /// <summary>The requests that fit the media type and have every value they need, in file order.</summary>
    private IEnumerable<TextTrackRequestConfiguration> MatchingRequests(MediaType mediaType, IReadOnlyDictionary<string, string> values) =>
        _text.Search.Requests.Where(request =>
            (string.IsNullOrEmpty(request.WhenMediaType)
                || string.Equals(request.WhenMediaType, mediaType.ToString(), StringComparison.OrdinalIgnoreCase))
            && request.Needs.All(values.ContainsKey));

    private bool HasLyricsOrInstrumental(JsonElement entry, TextTrackLyricsConfiguration rules) =>
        entry.ValueKind == JsonValueKind.Object
        && (rules.Sources.Any(source => TryPath(entry, source.Path, out var node)
                && node.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(node.GetString()))
            || (!string.IsNullOrEmpty(rules.InstrumentalPath) && ReadBool(entry, rules.InstrumentalPath) == true));

    /// <summary>
    /// From a list of search hits, the one whose title matches and whose length is closest to the song's; hits whose
    /// length is beyond the rejection limit are skipped. <c>null</c> when none is good enough.
    /// </summary>
    private JsonElement? PickBestListEntry(JsonElement list, TextTrackLookup lookup, TextTrackLyricsConfiguration rules)
    {
        JsonElement? best = null;
        var bestScore = double.MinValue;
        foreach (var entry in list.EnumerateArray())
        {
            if (!HasLyricsOrInstrumental(entry, rules))
            {
                continue;
            }

            var score = 0d;
            if (!string.IsNullOrWhiteSpace(lookup.Title) && entry.TryGetProperty("trackName", out var name)
                && name.ValueKind == JsonValueKind.String)
            {
                var found = name.GetString() ?? string.Empty;
                if (!string.Equals(found.Trim(), lookup.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                score += 1;
            }

            if (!string.IsNullOrWhiteSpace(lookup.Artist) && entry.TryGetProperty("artistName", out var artist)
                && artist.ValueKind == JsonValueKind.String
                && string.Equals(artist.GetString()?.Trim(), lookup.Artist.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }

            var duration = ScoreDuration(entry, lookup.DurationSeconds);
            if (duration is < 0)
            {
                continue;
            }

            score += duration ?? 0.5;
            if (score > bestScore)
            {
                bestScore = score;
                best = entry;
            }
        }

        return best;
    }

    /// <summary>Fetches the answer, remembering it when the file asks for the shared response cache.</summary>
    private async Task<string?> GetJsonAsync(string url, CancellationToken ct)
    {
        var queryHash = ComputeHash(url);
        var cacheKey = $"{Name}:{queryHash}";
        var useCache = _text.Search.ResponseCache && _cache is not null && _config.CacheTtlHours is > 0;
        if (useCache)
        {
            var cached = await _cache!.FindAsync(cacheKey, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached.ResponseJson;
            }
        }

        if (_health.IsDown(Name))
        {
            return null;
        }

        try
        {
            using var client = _httpFactory.CreateClient(Name);
            var (status, json) = await _rateLimiter.ExecuteAsync(Name, _config.RateLimit, async token =>
            {
                using var response = await client.GetAsync(url, token).ConfigureAwait(false);
                var body = response.IsSuccessStatusCode
                    ? await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)
                    : null;
                return (response.StatusCode, body);
            }, ct).ConfigureAwait(false);

            if (json is null)
            {
                // A request the provider refuses (400, 422) or has no answer for (404) is about this song, not an outage:
                // it means "no lyrics here" and must not count toward marking the whole provider down.
                if ((int)status is >= 400 and < 500 && (int)status != 429 && (int)status != 408)
                {
                    _logger.LogDebug("{Provider} had no lyrics for the request (HTTP {Status})", Name, (int)status);
                    await _health.ReportSuccessAsync(Name, ct).ConfigureAwait(false);
                    return null;
                }

                throw new HttpRequestException($"HTTP {(int)status}", null, status);
            }

            if (useCache)
            {
                await _cache!.UpsertAsync(cacheKey, _config.ProviderId ?? Name, queryHash, json, null, _config.CacheTtlHours!.Value, ct)
                    .ConfigureAwait(false);
            }

            await _health.ReportSuccessAsync(Name, ct).ConfigureAwait(false);
            return json;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "{Provider} lyrics lookup failed", Name);
            await _health.ReportFailureAsync(Name, ex.Message, ct).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>1 within the perfect window, falling to 0 at the rejection limit, -1 beyond it; <c>null</c> when unknown.</summary>
    private double? ScoreDuration(JsonElement root, double? expected)
    {
        var rules = _text.Search.Lyrics.Duration;
        if (expected is not > 0 || string.IsNullOrEmpty(rules.Path) || !TryPath(root, rules.Path, out var duration)
            || !duration.TryGetDouble(out var actual))
        {
            return null;
        }

        var delta = Math.Abs(actual - expected.Value);
        return delta > rules.RejectBeyondSeconds
            ? -1
            : delta <= rules.FullWithinSeconds ? 1 : Math.Max(0, 1 - delta / rules.RejectBeyondSeconds);
    }

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
