using System.Globalization;
using System.Text.Json.Nodes;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>Lists the episode numbers TMDB has for one season of a TV show.</summary>
public interface ITmdbSeasonEpisodeCatalogue
{
    /// <summary>
    /// Returns the season's <c>episode_number</c> values, or null when the list is unavailable
    /// (TMDB not configured, the request failed, or the answer could not be read).
    /// </summary>
    Task<IReadOnlySet<int>?> GetSeasonEpisodeNumbersAsync(
        string tmdbTvId,
        int seasonNumber,
        CancellationToken ct = default);
}

/// <summary>
/// TMDB-backed season list. Credentials and locale come from the live pipeline configuration, the same
/// source the retail worker uses for its TV lookups.
/// </summary>
public sealed class TmdbSeasonEpisodeCatalogue(
    TmdbRetailClient client,
    IPipelineExecutionSnapshotProvider snapshots,
    ILogger<TmdbSeasonEpisodeCatalogue> logger) : ITmdbSeasonEpisodeCatalogue
{
    public async Task<IReadOnlySet<int>?> GetSeasonEpisodeNumbersAsync(
        string tmdbTvId,
        int seasonNumber,
        CancellationToken ct = default)
    {
        var snapshot = snapshots.Current;
        if (!ProviderExecutionFilter.IsEnabled("tmdb", snapshot.Providers))
        {
            logger.LogInformation("TMDB is not enabled, so its season episode list is unavailable");
            return null;
        }

        var tmdb = snapshot.Providers.FirstOrDefault(provider =>
            string.Equals(provider.Name, "tmdb", StringComparison.OrdinalIgnoreCase));
        var apiKey = !string.IsNullOrWhiteSpace(tmdb?.HttpClient?.ApiKeyOverride)
            ? tmdb!.HttpClient!.ApiKeyOverride
            : tmdb?.HttpClient?.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogInformation("TMDB is not configured with an API key, so its season episode list is unavailable");
            return null;
        }

        var core = snapshot.Core;
        var language = (string.IsNullOrWhiteSpace(core.Language.Metadata) ? "en" : core.Language.Metadata.Trim())
            .Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)[0]
            .ToLowerInvariant();
        var country = string.IsNullOrWhiteSpace(core.Country) ? "US" : core.Country.Trim().ToUpperInvariant();

        var season = await client
            .FetchSeasonDetailsAsync(tmdbTvId, seasonNumber, apiKey, language, country, ct)
            .ConfigureAwait(false);
        if (season is null)
        {
            // The client has already logged why (not found, HTTP failure, timeout or unreadable JSON).
            return null;
        }

        if (season is not JsonObject document || document["episodes"] is not JsonArray episodes)
        {
            logger.LogWarning(
                "TMDB returned no readable episode list for tv_id={TvId} season={Season}", tmdbTvId, seasonNumber);
            return null;
        }

        var numbers = new HashSet<int>();
        foreach (var episode in episodes)
        {
            if (int.TryParse(
                    episode?["episode_number"]?.ToString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var number))
            {
                numbers.Add(number);
            }
        }

        return numbers;
    }
}
