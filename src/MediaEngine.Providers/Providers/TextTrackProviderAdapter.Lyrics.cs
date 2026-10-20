using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain.Contracts;
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
        if (_text.Search.Needs.Any(need => !values.ContainsKey(need)) || SelectRequest(lookup.MediaType, values) is not { } request)
        {
            return [];
        }

        var baseUrl = _config.Endpoints.GetValueOrDefault("api")?.TrimEnd('/') ?? string.Empty;
        var url = baseUrl + BuildPath(request, values);
        var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var rules = _text.Search.Lyrics;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
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
        if (string.IsNullOrWhiteSpace(lyrics) && !instrumental)
        {
            return [];
        }

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
            var json = await _rateLimiter.ExecuteAsync(Name, _config.RateLimit,
                token => client.GetStringAsync(url, token), ct).ConfigureAwait(false);
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
