using System.Globalization;
using System.Text.Json.Nodes;
using MediaEngine.Contracts.Review;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Looks a film file up in TMDB's TV catalogue when the movie search found nothing. Some titles that
/// look like films (a web miniseries, a TV special) exist on TMDB only as TV entries.
///
/// The result is only ever a suggestion for the Review Queue — callers must never apply it
/// automatically. A suggestion needs all of: a strong title match, a first-air year within one year of
/// the file's year, and a short-run series (TMDB type "Miniseries", or one season with at most six
/// episodes). Anything else returns <c>null</c>.
/// </summary>
public sealed class TmdbTvSuggestionService
{
    /// <summary>
    /// Word-overlap score a TMDB name must reach against the file title. Matches the "strong title"
    /// level the retail worker already uses (<c>RetailMatchWorker</c> enrichment gate).
    /// </summary>
    internal const double StrongTitleThreshold = 0.85;

    /// <summary>Largest episode count of a single-season series still treated as film-like.</summary>
    internal const int MaxFilmLikeEpisodes = 6;

    /// <summary>How many TMDB search results are examined before giving up.</summary>
    private const int MaxCandidatesExamined = 5;

    private const string PosterUrlPrefix = "https://image.tmdb.org/t/p/w500";

    private readonly TmdbRetailClient _client;
    private readonly ILogger<TmdbTvSuggestionService> _logger;

    public TmdbTvSuggestionService(TmdbRetailClient client, ILogger<TmdbTvSuggestionService> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Returns a qualifying TMDB TV suggestion for <paramref name="title"/> and
    /// <paramref name="fileYear"/>, or <c>null</c>. Provider failures are logged as warnings and
    /// treated as "no suggestion"; cancellation propagates.
    /// </summary>
    public async Task<MovieTvSuggestionDto?> FindAsync(
        string? title,
        string? fileYear,
        string apiKey,
        string language,
        string country,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)
            || string.IsNullOrWhiteSpace(apiKey)
            || !int.TryParse(fileYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            || year < 1800)
        {
            return null;
        }

        try
        {
            var results = await _client
                .SearchShowCandidatesAsync(title, apiKey, language, country, ct)
                .ConfigureAwait(false);

            var candidates = results
                .Select(node => (Node: node, Score: ScoreTitle(title, node)))
                .Where(item => item.Score >= StrongTitleThreshold
                    && YearMatches(year, ReadFirstAirYear(item.Node)))
                .OrderByDescending(item => item.Score)
                .Take(MaxCandidatesExamined)
                .ToList();

            foreach (var (node, _) in candidates)
            {
                var tvId = node["id"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(tvId))
                {
                    continue;
                }

                var details = await _client
                    .FetchShowDetailsAsync(tvId, apiKey, language, country, ct)
                    .ConfigureAwait(false);
                if (details is null)
                {
                    continue;
                }

                var suggestion = TryBuildSuggestion(tvId, details, year);
                if (suggestion is not null)
                {
                    _logger.LogInformation(
                        "TMDB lists '{Title}' ({Year}) as {Type} tv/{TvId} '{Name}' with {Episodes} episode(s); suggesting Move to TV",
                        title, year, suggestion.Type, tvId, suggestion.Name, suggestion.Episodes);
                    return suggestion;
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "TMDB TV suggestion lookup failed for '{Title}'; the film stays unmatched without a suggestion",
                title);
            return null;
        }
    }

    /// <summary>Applies the year and run-length rules to a TMDB TV details document.</summary>
    internal static MovieTvSuggestionDto? TryBuildSuggestion(string tvId, JsonNode details, int fileYear)
    {
        var name = details["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var firstAirYear = ReadFirstAirYear(details);
        if (!YearMatches(fileYear, firstAirYear))
        {
            return null;
        }

        var type = details["type"]?.GetValue<string>();
        var seasons = ReadInt(details["number_of_seasons"]);
        var episodes = ReadInt(details["number_of_episodes"]);
        if (!IsFilmLikeRun(type, seasons, episodes))
        {
            return null;
        }

        var posterPath = details["poster_path"]?.GetValue<string>();
        return new MovieTvSuggestionDto
        {
            TmdbTvId = tvId,
            Name = name.Trim(),
            FirstAirYear = firstAirYear,
            Type = type,
            Seasons = seasons,
            Episodes = episodes,
            PosterUrl = string.IsNullOrWhiteSpace(posterPath) ? null : PosterUrlPrefix + posterPath,
            Overview = details["overview"]?.GetValue<string>(),
        };
    }

    internal static bool IsFilmLikeRun(string? type, int seasons, int episodes) =>
        string.Equals(type, "Miniseries", StringComparison.OrdinalIgnoreCase)
        || (seasons == 1 && episodes is > 0 and <= MaxFilmLikeEpisodes);

    internal static bool YearMatches(int fileYear, int? firstAirYear) =>
        firstAirYear is { } airYear && Math.Abs(airYear - fileYear) <= 1;

    private static double ScoreTitle(string fileTitle, JsonNode node)
    {
        var best = 0.0;
        foreach (var field in new[] { "name", "original_name" })
        {
            var candidate = node[field]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                best = Math.Max(best, RetailTextSimilarity.ComputeWordOverlap(fileTitle, candidate));
            }
        }

        return best;
    }

    private static int? ReadFirstAirYear(JsonNode node)
    {
        var firstAirDate = node["first_air_date"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(firstAirDate)
            && firstAirDate.Length >= 4
            && int.TryParse(firstAirDate.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    private static int ReadInt(JsonNode? node)
    {
        try
        {
            return node?.GetValue<int?>() ?? 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // TMDB documents these as integers; a differently typed value means "unknown", not a failure.
            return 0;
        }
    }
}
