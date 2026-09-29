using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

public sealed record TmdbShowSearchResult(string? TvId, string? PosterPath, string? MatchedShowName);

public sealed class TmdbRetailClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly RetailRequestBuilder _requestBuilder;
    private readonly IProviderRateLimiterCoordinator _rateLimiter;
    private readonly ILogger<TmdbRetailClient> _logger;

    public TmdbRetailClient(
        IHttpClientFactory httpFactory,
        RetailRequestBuilder requestBuilder,
        IProviderRateLimiterCoordinator rateLimiter,
        ILogger<TmdbRetailClient> logger)
    {
        _httpFactory = httpFactory;
        _requestBuilder = requestBuilder;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task<TmdbShowSearchResult> SearchShowAsync(
        string? showName,
        int? yearHint,
        string apiKey,
        string language,
        string country,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(showName))
        {
            return new(null, null, null);
        }

        var url = _requestBuilder.BuildTmdbTvSearchUrl(showName, yearHint, apiKey, language, country);
        var fallbackUrl = _requestBuilder.BuildTmdbTvSearchUrl(showName, null, apiKey, language, country);

        try
        {
            using var client = _httpFactory.CreateClient("tmdb");
            var json = await _rateLimiter.ExecuteAsync(
                "tmdb",
                ProviderRateLimitDefaults.Tmdb,
                token => client.GetFromJsonAsync<JsonNode>(url, token),
                ct).ConfigureAwait(false);
            var results = json?["results"]?.AsArray();

            if ((results is null || results.Count == 0) && yearHint.HasValue)
            {
                _logger.LogInformation(
                    "TV: TMDB year-filtered search returned 0 results for '{ShowName}' (year={Year}); retrying unfiltered",
                    showName, yearHint.Value);
                json = await _rateLimiter.ExecuteAsync(
                    "tmdb",
                    ProviderRateLimitDefaults.Tmdb,
                    token => client.GetFromJsonAsync<JsonNode>(fallbackUrl, token),
                    ct).ConfigureAwait(false);
                results = json?["results"]?.AsArray();
            }

            if (results is null || results.Count == 0)
            {
                return new(null, null, null);
            }

            double bestScore = 0.0;
            string? bestId = null;
            string? bestPosterPath = null;
            string? bestMatchedShowName = null;

            foreach (var result in results)
            {
                if (result is null)
                {
                    continue;
                }

                var resultName = result["name"]?.GetValue<string>()
                    ?? result["original_name"]?.GetValue<string>();
                var resultId = result["id"]?.GetValue<long?>()?.ToString();

                if (string.IsNullOrWhiteSpace(resultName) || resultId is null)
                {
                    continue;
                }

                var nameScore = RetailTextSimilarity.ComputeWordOverlap(showName, resultName);
                var yearBonus = ComputeYearBonus(result, yearHint);
                var score = Math.Clamp(nameScore + yearBonus, 0.0, 1.0);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = resultId;
                    bestPosterPath = result["poster_path"]?.GetValue<string>();
                    bestMatchedShowName = resultName;
                }
            }

            if (bestScore >= 0.40)
            {
                _logger.LogInformation(
                    "TV: TMDB show search matched tv_id={Id} (score={Score:F2}) for '{ShowName}'{YearHint}",
                    bestId, bestScore, showName,
                    yearHint.HasValue ? $" (year={yearHint.Value})" : "");
                return new(bestId, bestPosterPath, bestMatchedShowName);
            }

            _logger.LogInformation(
                "TV: TMDB show search - best score {Score:F2} below threshold for '{ShowName}'",
                bestScore, showName);
            return new(null, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "RetailMatchWorker: TMDB show search failed for '{ShowName}'", showName);
            return new(null, null, null);
        }
    }

    public async Task<JsonNode?> FetchShowDetailsAsync(
        string tvId,
        string apiKey,
        string language,
        string country,
        CancellationToken ct)
    {
        var url = _requestBuilder.BuildTmdbTvDetailsUrl(tvId, apiKey, language, country);

        try
        {
            using var client = _httpFactory.CreateClient("tmdb");
            using var response = await _rateLimiter.ExecuteAsync(
                "tmdb",
                ProviderRateLimitDefaults.Tmdb,
                token => client.GetAsync(url, token),
                ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RetailMatchWorker: TMDB show detail fetch failed for tv_id={TvId}", tvId);
            return null;
        }
    }

    public async Task<IReadOnlyList<JsonNode>> FetchSeasonEpisodesAsync(
        string tvId,
        int seasonNumber,
        string apiKey,
        string language,
        string country,
        CancellationToken ct)
    {
        var url = _requestBuilder.BuildTmdbSeasonUrl(tvId, seasonNumber, apiKey, language, country);

        try
        {
            using var client = _httpFactory.CreateClient("tmdb");
            using var response = await _rateLimiter.ExecuteAsync(
                "tmdb",
                ProviderRateLimitDefaults.Tmdb,
                token => client.GetAsync(url, token),
                ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct)
                .ConfigureAwait(false);
            var episodes = json?["episodes"]?.AsArray();
            if (episodes is null)
            {
                return [];
            }

            var result = new List<JsonNode>();
            foreach (var ep in episodes)
            {
                if (ep is not null)
                {
                    result.Add(ep);
                }
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "RetailMatchWorker: TMDB season fetch failed for tv_id={TvId} season={Season}",
                tvId, seasonNumber);
            return [];
        }
    }

    public async Task<JsonNode?> FetchEpisodeCreditsAsync(string tvId, int season, int episode,
        string apiKey, string language, string country, CancellationToken ct)
    {
        var seasonUrl = _requestBuilder.BuildTmdbSeasonUrl(tvId, season, apiKey, language, country);
        var parts = seasonUrl.Split('?', 2);
        var url = $"{parts[0]}/episode/{episode}/credits?{parts[1]}";
        using var client = _httpFactory.CreateClient("tmdb");
        using var response = await _rateLimiter.ExecuteAsync("tmdb", ProviderRateLimitDefaults.Tmdb,
            token => client.GetAsync(url, token), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct).ConfigureAwait(false);
    }

    private static double ComputeYearBonus(JsonNode result, int? yearHint)
    {
        if (!yearHint.HasValue)
        {
            return 0.0;
        }

        var firstAirDate = result["first_air_date"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(firstAirDate)
            || firstAirDate.Length < 4
            || !int.TryParse(firstAirDate.AsSpan(0, 4), out var resultYear))
        {
            return 0.0;
        }

        var diff = Math.Abs(resultYear - yearHint.Value);
        return diff switch
        {
            0 => 0.25,
            1 => 0.10,
            <= 5 => 0.0,
            _ => -0.20,
        };
    }
}
