using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Verifies direct external-ID links without changing catalog identity or local
/// episode ordering. Unproven episodes remain available for manual subtitles.
/// </summary>
public sealed class TvEpisodeCrosswalk(
    IConfigurationLoader configuration,
    IProviderConfigurationRepository providerConfiguration,
    IHttpClientFactory httpFactory,
    IProviderRateLimiterCoordinator rateLimiter,
    ILogger<TvEpisodeCrosswalk> logger) : ITvEpisodeCrosswalk
{
    private readonly ConcurrentDictionary<string, TmdbEpisodeIdentity> _cache = new();
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);

    public async Task<TmdbEpisodeIdentity?> ResolveAsync(
        TvdbEpisodeIdentity identity,
        string? expectedTmdbShowId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var tvdbShowId = PositiveId(identity.ShowId);
        var tvdbEpisodeId = PositiveId(identity.EpisodeId);
        var expectedShowId = PositiveId(expectedTmdbShowId);
        if (tvdbShowId is null || tvdbEpisodeId is null
            || (!string.IsNullOrWhiteSpace(expectedTmdbShowId) && expectedShowId is null))
            return null;

        try
        {
            // Reload before reading the cache: removing or disabling a key must
            // stop access immediately, and rotation must revalidate evidence.
            var config = configuration.LoadProvider("tmdb");
            if (config?.Enabled == false) return null;
            var apiKey = config?.HttpClient?.ApiKeyOverride;
            if (string.IsNullOrWhiteSpace(apiKey)) apiKey = config?.HttpClient?.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
                apiKey = await providerConfiguration.GetDecryptedValueAsync(
                    WellKnownProviders.Tmdb.ToString(), "api_key", ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey)) return null;

            var credentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
            var cacheKey = $"{tvdbShowId}:{tvdbEpisodeId}:{expectedShowId}:{identity.Order}:{credentialHash}";
            if (_cache.TryGetValue(cacheKey, out var cached)
                && cached.VerifiedAt is { } verifiedAt && DateTimeOffset.UtcNow - verifiedAt < CacheLifetime)
                return cached;

            using var client = httpFactory.CreateClient("tmdb");
            var showResults = await FetchAsync(client,
                $"/find/{tvdbShowId}?external_source=tvdb_id", apiKey, ct).ConfigureAwait(false);
            var show = UniqueResult(showResults, "tv_results");
            var tmdbShowId = PositiveId(show?["id"]?.ToString());
            if (tmdbShowId is null || (expectedShowId is not null && expectedShowId != tmdbShowId))
                return null;

            var showExternalIds = await FetchAsync(client,
                $"/tv/{tmdbShowId}/external_ids", apiKey, ct).ConfigureAwait(false);
            if (PositiveId(showExternalIds?["tvdb_id"]?.ToString()) != tvdbShowId)
                return null;

            var episodeResults = await FetchAsync(client,
                $"/find/{tvdbEpisodeId}?external_source=tvdb_id", apiKey, ct).ConfigureAwait(false);
            var episode = UniqueResult(episodeResults, "tv_episode_results");
            var tmdbEpisodeId = PositiveId(episode?["id"]?.ToString());
            if (tmdbEpisodeId is null || PositiveId(episode?["show_id"]?.ToString()) != tmdbShowId
                || !TryPosition(episode?["season_number"], out var season)
                || !TryPosition(episode?["episode_number"], out var number) || number == 0)
                return null;

            var episodeExternalIds = await FetchAsync(client,
                $"/tv/{tmdbShowId}/season/{season}/episode/{number}/external_ids", apiKey, ct)
                .ConfigureAwait(false);
            if (PositiveId(episodeExternalIds?["id"]?.ToString()) != tmdbEpisodeId
                || PositiveId(episodeExternalIds?["tvdb_id"]?.ToString()) != tvdbEpisodeId)
                return null;

            var result = new TmdbEpisodeIdentity(tmdbShowId, tmdbEpisodeId, season, number,
                VerifiedAt: DateTimeOffset.UtcNow);
            if (_cache.Count >= 1000) _cache.Clear();
            _cache[cacheKey] = result;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Exception messages can contain credentialed request URLs.
            logger.LogDebug("TV subtitle identity crosswalk unavailable ({FailureType})", ex.GetType().Name);
            return null;
        }
    }

    private async Task<JsonNode?> FetchAsync(HttpClient client, string path, string apiKey, CancellationToken ct)
    {
        var separator = path.Contains('?') ? '&' : '?';
        var url = $"https://api.themoviedb.org/3{path}{separator}api_key={Uri.EscapeDataString(apiKey)}";
        using var response = await rateLimiter.ExecuteAsync("tmdb", ProviderRateLimitDefaults.Tmdb,
            token => client.GetAsync(url, token), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct).ConfigureAwait(false);
    }

    private static JsonObject? UniqueResult(JsonNode? response, string key) =>
        response?[key] is JsonArray { Count: 1 } results ? results[0] as JsonObject : null;

    private static string? PositiveId(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture) : null;

    private static bool TryPosition(JsonNode? node, out int number) =>
        int.TryParse(node?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0;
}
