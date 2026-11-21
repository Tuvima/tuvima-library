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

        if (!string.IsNullOrWhiteSpace(lookup.SearchText))
        {
            return await SearchLyricsByTextAsync(lookup, ct).ConfigureAwait(false);
        }

        var tally = new LyricsTally();
        var found = await FindLyricsAsync(lookup, tally, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "{Provider} lyrics lookup for \"{Title}\" by \"{Artist}\" ({Length}s) on album \"{Album}\": {Outcome}, {Candidates} candidate(s) looked at",
            Name, lookup.Title, lookup.Artist, lookup.DurationSeconds, lookup.Album, tally.Outcome, tally.Candidates);

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
        // or the exact lookup finds nothing. "Not found" is an ordinary empty answer, never an error. The first request
        // that yields any acceptable entry ends the search, and every acceptable entry it holds is kept, best first, so the
        // person can choose among the close matches (issue 1256).
        var found = new List<(double Score, TextTrackCandidate Candidate)>();
        var accepted = false;
        foreach (var request in MatchingRequests(lookup.MediaType, values))
        {
            var candidateUrl = baseUrl + BuildPath(request, values);
            var answer = await GetJsonAsync(candidateUrl, ct).ConfigureAwait(false);
            tally.Requests++;
            _logger.LogDebug(
                "{Provider} lyrics request {Request} for \"{Title}\" by \"{Artist}\" (album \"{Album}\"): {Answer}",
                Name, request.Path + "?" + string.Join('&', request.Query.Select(pair => pair[0])), lookup.Title, lookup.Artist, lookup.Album,
                answer.Failed ? "could not be reached" : string.IsNullOrWhiteSpace(answer.Json) ? "nothing" : "an answer");
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

            using (parsed)
            {
                var entries = parsed.RootElement.ValueKind == JsonValueKind.Array
                    ? parsed.RootElement.EnumerateArray().ToList()
                    : [parsed.RootElement];
                foreach (var entry in entries)
                {
                    tally.Candidates++;
                    if (ScoreEntry(entry, lookup, rules, tally, parsed.RootElement.ValueKind == JsonValueKind.Array) is not { } score)
                    {
                        continue;
                    }

                    accepted = true;
                    if (LyricsCandidateFrom(entry, candidateUrl, lookup, rules, tally, score) is { } candidate)
                    {
                        found.Add((score, candidate));
                    }
                }
            }

            if (accepted)
            {
                break;
            }
        }

        if (found.Count == 0 && accepted)
        {
            // The source's entry was the song's but its timed lyrics are too far off in length (the outcome already says so).
            return [];
        }

        if (found.Count == 0)
        {
            tally.Outcome = tally.Requests == 0 ? LyricsOutcome.MissingDetails
                : tally.Failures == tally.Requests ? LyricsOutcome.SourceUnreachable
                // A request failed in transport and nothing that did answer matched the title: the miss may be the failure's, so it is not reported as "no match".
                : tally.Failures > 0 && tally.TitleMatches == 0 ? LyricsOutcome.SourceUnreachable
                : tally.TitleMatches > 0 && tally.LengthRejected >= tally.TitleMatches ? LyricsOutcome.LengthTooDifferent
                : LyricsOutcome.NoMatch;
            return [];
        }

        tally.Outcome = found.All(item => item.Candidate.IsInstrumental) ? LyricsOutcome.Instrumental : LyricsOutcome.Found;
        return found
            .OrderByDescending(item => item.Score)
            .Select(item => item.Candidate)
            .DistinctBy(item => item.SourceId)
            .Take(MaxTextSearchResults)
            .ToList();
    }

    /// <summary>
    /// The result a source's entry makes: its lyrics (best format first), whether it is instrumental, and how well its length fits the
    /// song's. <c>null</c> when a timed entry is too different in length (the outcome says so).
    /// </summary>
    private TextTrackCandidate? LyricsCandidateFrom(
        JsonElement root, string url, TextTrackLookup lookup, TextTrackLyricsConfiguration rules, LyricsTally tally, double? matchScore = null)
    {
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
            return null;
        }

        var confidence = durationScore.HasValue
            ? strict ? Math.Max(0, durationScore.Value) : Math.Max(duration.LenientFloor, durationScore.Value)
            : duration.DefaultConfidence;
        return new TextTrackCandidate(
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
            IsInstrumental: instrumental,
            MatchScore: matchScore);
    }

    /// <summary>The most results one text search offers (the caller shows fewer).</summary>
    private const int MaxTextSearchResults = 10;

    /// <summary>
    /// A search by words a person typed: the source is asked for the text as written (the song's own title, artist and length are not
    /// sent), and every entry whose title, artist or album carries all of the words is offered, because the person is choosing among them.
    /// </summary>
    private async Task<IReadOnlyList<TextTrackCandidate>> SearchLyricsByTextAsync(TextTrackLookup lookup, CancellationToken ct)
    {
        // The song's own length must not reject the song a person went looking for instead.
        lookup = lookup with { Title = null, Artist = null, Album = null, Year = null, DurationSeconds = null };
        var values = LookupValues(lookup);
        var request = MatchingRequests(lookup.MediaType, values).FirstOrDefault();
        if (request is null)
        {
            _logger.LogInformation("{Provider} lyrics search by text skipped: the source has no search by text", Name);
            return [];
        }

        var rules = _text.Search.Lyrics;
        var url = (_config.Endpoints.GetValueOrDefault("api")?.TrimEnd('/') ?? string.Empty) + BuildPath(request, values);
        var answer = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (answer.Failed)
        {
            throw new TextTrackLookupException(
                "ProviderUnavailable", Message(TextTrackMessages.Unreachable, "{display_name} could not be reached."));
        }

        if (string.IsNullOrWhiteSpace(answer.Json))
        {
            return [];
        }

        try
        {
            using var parsed = JsonDocument.Parse(answer.Json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var matcher = LyricsMatcher;
            var tally = new LyricsTally();
            var found = new List<TextTrackCandidate>();
            foreach (var entry in parsed.RootElement.EnumerateArray())
            {
                tally.Candidates++;
                if (!HasLyricsOrInstrumental(entry, rules)
                    || !matcher.MatchesSearchText(lookup.SearchText, ReadString(entry, rules.Match.TitlePath), ReadString(entry, rules.Match.ArtistPath)))
                {
                    continue;
                }

                if (LyricsCandidateFrom(entry, url, lookup, rules, tally) is { } candidate)
                {
                    found.Add(candidate);
                    if (found.Count >= MaxTextSearchResults)
                    {
                        break;
                    }
                }
            }

            _logger.LogInformation(
                "{Provider} lyrics search by text \"{Text}\": {Candidates} entries looked at, {Found} offered", Name, lookup.SearchText, tally.Candidates, found.Count);
            return found;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "{Provider} lyrics answer was not JSON", Name);
            return [];
        }
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
    /// How well one source entry fits the song, or <c>null</c> when it must not be offered as the song's own lyrics. The Library only offers an
    /// entry whose title and artist both match the song's (issue 1187); length (beyond the rejection limit rejects) and album then rank the rest.
    /// </summary>
    private double? ScoreEntry(JsonElement entry, TextTrackLookup lookup, TextTrackLyricsConfiguration rules, LyricsTally tally, bool fromList)
    {
        var matcher = LyricsMatcher;
        var match = rules.Match;
        if (!HasLyricsOrInstrumental(entry, rules))
        {
            return null;
        }

        var score = 0d;
        var titled = false;
        if (!string.IsNullOrWhiteSpace(lookup.Title) && TryPath(entry, match.TitlePath, out var name)
            && name.ValueKind == JsonValueKind.String)
        {
            if (!matcher.Matches(name.GetString(), lookup.Title))
            {
                return null;
            }

            titled = true;
            score += 1;
        }

        if (!string.IsNullOrWhiteSpace(lookup.Artist) && TryPath(entry, match.ArtistPath, out var artist)
            && artist.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(artist.GetString()))
        {
            if (!matcher.Matches(artist.GetString(), lookup.Artist))
            {
                return null;
            }

            score += 1;
        }

        if (titled)
        {
            tally.TitleMatches++;
        }

        // The album is a tie-breaker only: the same song often sits on several albums and a compilation must still be offered.
        if (!string.IsNullOrWhiteSpace(lookup.Album) && TryPath(entry, "albumName", out var album)
            && album.ValueKind == JsonValueKind.String && matcher.Matches(album.GetString(), lookup.Album))
        {
            score += 0.5;
        }

        // An exact answer's length is judged with its format (a timed entry too far off is dropped, plain text is kept), so only a list entry is rejected here.
        var duration = ScoreDuration(entry, lookup.DurationSeconds);
        if (fromList && duration is < 0)
        {
            if (titled)
            {
                tally.LengthRejected++;
            }

            return null;
        }

        return score + Math.Max(0, duration ?? 0.5);
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
