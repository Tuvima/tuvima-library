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
    private LyricsTitleMatcher? _lyricsMatcher;

    private LyricsTitleMatcher LyricsMatcher =>
        _lyricsMatcher ??= new LyricsTitleMatcher(_text.Search.Lyrics.Match, _logger, Name);

    /// <summary>What one lyrics lookup came to, for the single line it writes to the log.</summary>
    private enum LyricsOutcome
    {
        Found,
        Instrumental,
        NoMatch,
        LengthTooDifferent,
        SourceUnreachable,
        MissingDetails,
    }

    /// <summary>Counts kept while a lookup runs so its outcome can be named.</summary>
    private sealed class LyricsTally
    {
        public LyricsOutcome Outcome { get; set; } = LyricsOutcome.NoMatch;

        public int Candidates { get; set; }

        public int TitleMatches { get; set; }

        public int LengthRejected { get; set; }

        public int Requests { get; set; }

        public int Failures { get; set; }
    }

    /// <summary>A fetched answer; <see cref="Failed"/> separates "the source could not be reached" from "no answer for this song".</summary>
    private readonly record struct LyricsAnswer(string? Json, bool Failed);

    private async Task<IReadOnlyList<TextTrackCandidate>> SearchLyricsAsync(TextTrackLookup lookup, CancellationToken ct)
    {
        if (!IsEnabled || !CanHandle(lookup.MediaType))
        {
            return [];
        }

        var tally = new LyricsTally();
        var found = await FindLyricsAsync(lookup, tally, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "{Provider} lyrics lookup for \"{Title}\" by \"{Artist}\" ({Length}s): {Outcome}, {Candidates} candidate(s) looked at",
            Name, lookup.Title, lookup.Artist, lookup.DurationSeconds, tally.Outcome, tally.Candidates);

        // A lookup whose requests failed in transport and found nothing says nothing about the song, so it ends as a
        // retryable failure of the operation (the same signal the subtitle search gives), never as "no lyrics".
        if (tally.Outcome == LyricsOutcome.SourceUnreachable)
        {
            throw new TextTrackLookupException(
                "ProviderUnavailable", Message(TextTrackMessages.Unreachable, "{display_name} could not be reached."));
        }

        return found;
    }

    private async Task<IReadOnlyList<TextTrackCandidate>> FindLyricsAsync(TextTrackLookup lookup, LyricsTally tally, CancellationToken ct)
    {
        var values = LookupValues(lookup);
        if (_text.Search.Needs.Any(need => !values.ContainsKey(need)))
        {
            tally.Outcome = LyricsOutcome.MissingDetails;
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
            var answer = await GetJsonAsync(candidateUrl, ct).ConfigureAwait(false);
            tally.Requests++;
            if (answer.Failed)
            {
                tally.Failures++;
            }

            var json = answer.Json;
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

            JsonElement? picked;
            if (parsed.RootElement.ValueKind == JsonValueKind.Array)
            {
                picked = PickBestListEntry(parsed.RootElement, lookup, rules, tally);
            }
            else
            {
                tally.Candidates++;
                picked = parsed.RootElement;
            }

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
            tally.Outcome = tally.Requests == 0 ? LyricsOutcome.MissingDetails
                : tally.Failures == tally.Requests ? LyricsOutcome.SourceUnreachable
                // A request failed in transport and nothing that did answer matched the title: the miss may be the failure's, so it is not reported as "no match".
                : tally.Failures > 0 && tally.TitleMatches == 0 ? LyricsOutcome.SourceUnreachable
                : tally.TitleMatches > 0 && tally.LengthRejected >= tally.TitleMatches ? LyricsOutcome.LengthTooDifferent
                : LyricsOutcome.NoMatch;
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
            tally.Outcome = LyricsOutcome.LengthTooDifferent;
            return [];
        }

        tally.Outcome = instrumental ? LyricsOutcome.Instrumental : LyricsOutcome.Found;

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
    private JsonElement? PickBestListEntry(
        JsonElement list, TextTrackLookup lookup, TextTrackLyricsConfiguration rules, LyricsTally tally)
    {
        var matcher = LyricsMatcher;
        var match = rules.Match;
        JsonElement? best = null;
        var bestScore = double.MinValue;
        foreach (var entry in list.EnumerateArray())
        {
            tally.Candidates++;
            if (!HasLyricsOrInstrumental(entry, rules))
            {
                continue;
            }

            var score = 0d;
            var titled = false;
            if (!string.IsNullOrWhiteSpace(lookup.Title) && TryPath(entry, match.TitlePath, out var name)
                && name.ValueKind == JsonValueKind.String)
            {
                if (!matcher.Matches(name.GetString(), lookup.Title))
                {
                    continue;
                }

                titled = true;
                score += 1;
            }

            if (!string.IsNullOrWhiteSpace(lookup.Artist) && TryPath(entry, match.ArtistPath, out var artist)
                && artist.ValueKind == JsonValueKind.String
                && matcher.Matches(artist.GetString(), lookup.Artist))
            {
                score += 1;
            }

            if (titled)
            {
                tally.TitleMatches++;
            }

            var duration = ScoreDuration(entry, lookup.DurationSeconds);
            if (duration is < 0)
            {
                if (titled)
                {
                    tally.LengthRejected++;
                }

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
    private async Task<LyricsAnswer> GetJsonAsync(string url, CancellationToken ct)
    {
        var queryHash = ComputeHash(url);
        var cacheKey = $"{Name}:{queryHash}";
        var useCache = _text.Search.ResponseCache && _cache is not null && _config.CacheTtlHours is > 0;
        if (useCache)
        {
            var cached = await _cache!.FindAsync(cacheKey, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                return new LyricsAnswer(cached.ResponseJson, false);
            }
        }

        if (_health.IsDown(Name))
        {
            return new LyricsAnswer(null, true);
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
                    return new LyricsAnswer(null, false);
                }

                throw new HttpRequestException($"HTTP {(int)status}", null, status);
            }

            if (useCache)
            {
                await _cache!.UpsertAsync(cacheKey, _config.ProviderId ?? Name, queryHash, json, null, _config.CacheTtlHours!.Value, ct)
                    .ConfigureAwait(false);
            }

            await _health.ReportSuccessAsync(Name, ct).ConfigureAwait(false);
            return new LyricsAnswer(json, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "{Provider} lyrics lookup failed", Name);
            await _health.ReportFailureAsync(Name, ex.Message, ct).ConfigureAwait(false);
            return new LyricsAnswer(null, true);
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
