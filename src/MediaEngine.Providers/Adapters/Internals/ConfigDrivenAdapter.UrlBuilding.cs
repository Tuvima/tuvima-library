using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Adapters;

public sealed partial class ConfigDrivenAdapter
{
    /// <summary>
    /// Builds a cache key from the provider ID and the request URL hash.
    /// </summary>
    private string BuildCacheKey(string url) =>
        $"{_providerId}:{ComputeSha256(url)}";

    /// <summary>
    /// Computes a SHA-256 hash of the input string (for URL dedup).
    /// </summary>
    private static string ComputeSha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // -- URL building --------------------------------------------------------

    private string BuildUrl(SearchStrategyConfig strategy, ProviderLookupRequest request, int? limitOverride = null)
    {
        var baseUrl = ResolveBaseUrl(request);
        var template = strategy.UrlTemplate;

        // Clean the title for search: strip trailing (YYYY) and SxxExx patterns.
        // For TV strategies, prefer ShowName (the series/show title) over Title
        // (which may be the episode title extracted from the filename).
        var isEpisodicStrategy = strategy.MediaTypes?.Contains("TV") == true;
        var rawTitle = isEpisodicStrategy
            && !string.IsNullOrWhiteSpace(request.ShowName)
            ? request.ShowName
            : request.Title;
        var searchTitle = CleanTitleForSearch(rawTitle) ?? rawTitle;
        var yearFromTitle = request.Year
            ?? ExtractYearFromTitle(request.Title)
            ?? request.Hints?.GetValueOrDefault("year");

        // Build {query} placeholder from query_template if specified.
        var query = string.Empty;
        if (strategy.Query is not null)
        {
            query = BuildConfiguredQuery(strategy.Query, searchTitle, request);
        }
        else if (!string.IsNullOrEmpty(strategy.QueryTemplate))
        {
            query = BuildConfiguredQuery(strategy.QueryTemplate, searchTitle, request);
        }

        // Replace all placeholders in the URL template.
        var url = template;
        url = ReplacePlaceholder(url, "{base_url}", baseUrl, encode: false);
        url = ReplacePlaceholder(url, "{query}", query, encode: true);
        url = ReplacePlaceholder(url, "{title}", searchTitle, encode: true);
        url = ReplacePlaceholder(url, "{author}", request.Author, encode: true);
        url = ReplacePlaceholder(url, "{isbn}", request.Isbn, encode: true);
        url = ReplacePlaceholder(url, "{asin}", request.Asin, encode: true);
        url = ReplacePlaceholder(url, "{narrator}", request.Narrator, encode: true);
        url = ReplacePlaceholder(url, "{apple_books_id}", request.AppleBooksId, encode: true);
        url = ReplacePlaceholder(url, "{audible_id}", request.AudibleId, encode: true);
        url = ReplacePlaceholder(url, "{tmdb_id}", request.TmdbId, encode: true);
        url = ReplacePlaceholder(url, "{imdb_id}", request.ImdbId, encode: true);
        url = ReplacePlaceholder(url, "{show_name}", request.ShowName, encode: true);
        url = ReplacePlaceholder(url, "{album}", request.Album, encode: true);
        url = ReplacePlaceholder(url, "{artist}", request.Artist, encode: true);
        url = ReplacePlaceholder(url, "{director}", request.Director, encode: true);
        url = ReplacePlaceholder(url, "{composer}", request.Composer, encode: true);
        url = ReplacePlaceholder(url, "{season_number}", request.SeasonNumber, encode: true);
        url = ReplacePlaceholder(url, "{episode_number}", request.EpisodeNumber, encode: true);
        url = ReplacePlaceholder(url, "{track_number}", request.TrackNumber, encode: true);
        url = ReplacePlaceholder(url, "{series}", request.Series, encode: true);
        url = ReplacePlaceholder(url, "{genre}", request.Genre, encode: true);
        url = ReplacePlaceholder(url, "{api_key}", EffectiveApiKey, encode: true);
        url = ReplacePlaceholder(url, "{client_key}", _config.HttpClient?.ClientKey, encode: true);
        url = ReplacePlaceholder(url, "{access_token}", _config.HttpClient?.AccessToken, encode: true);
        url = ReplacePlaceholder(url, "{lang}", ResolveTemplateLanguage(request, template), encode: true);
        url = ReplacePlaceholder(url, "{country}", request.Country.ToUpperInvariant(), encode: true);
        url = ReplacePlaceholder(url, "{year}", yearFromTitle ?? string.Empty, encode: true);
        url = ReplacePlaceholder(url, "{tvdb_id}", ResolveRequestField(request, BridgeIdKeys.TvdbId), encode: true);
        url = ReplacePlaceholder(url, "{musicbrainz_id}", ResolveRequestField(request, BridgeIdKeys.MusicBrainzId), encode: true);
        url = ReplacePlaceholder(
            url,
            "{musicbrainz_release_group_id}",
            ResolveRequestField(request, BridgeIdKeys.MusicBrainzReleaseGroupId),
            encode: true);
        url = ReplacePlaceholder(url, "{comic_vine_id}", ResolveRequestField(request, BridgeIdKeys.ComicVineId), encode: true);

        // {limit} — replaced with the caller-supplied override (fetch path uses fetch_limit,
        // search path uses the manual search limit). Falls back to max_results or 25.
        var resolvedLimit = limitOverride
            ?? (strategy.MaxResults > 0 ? strategy.MaxResults : 25);
        url = ReplacePlaceholder(url, "{limit}", resolvedLimit.ToString(), encode: false);

        if (request.PriorProviderBridgeIds is { Count: > 0 })
        {
            foreach (var (key, value) in request.PriorProviderBridgeIds)
            {
                var placeholder = $"{{{key}}}";
                if (url.Contains(placeholder, StringComparison.Ordinal) && !string.IsNullOrEmpty(value))
                {
                    url = ReplacePlaceholder(url, placeholder, value, encode: true);
                }
            }
        }

        // Generic hint-based placeholder resolution — any remaining {key} placeholders
        // are resolved from the Hints dictionary, enabling zero-code config additions.
        if (request.Hints is { Count: > 0 })
        {
            foreach (var (key, value) in request.Hints)
            {
                var placeholder = $"{{{key}}}";
                if (url.Contains(placeholder, StringComparison.Ordinal) && !string.IsNullOrEmpty(value))
                {
                    url = ReplacePlaceholder(url, placeholder, value, encode: true);
                }
            }
        }

        return url;
    }

    private static string BuildConfiguredQuery(
        QueryCompositionConfig config,
        string? searchTitle,
        ProviderLookupRequest request)
    {
        var clauses = new List<string>();
        foreach (var clause in config.Clauses)
        {
            var value = string.Equals(clause.Value, MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase)
                ? searchTitle
                : ResolveRequestField(request, clause.Value);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (clause.Required)
                {
                    return string.Empty;
                }

                continue;
            }

            var formattedValue = FormatQueryValue(value, config.Syntax, clause.Match);
            clauses.Add(string.IsNullOrWhiteSpace(clause.Field)
                ? formattedValue
                : $"{clause.Field}:{formattedValue}");
        }

        var separator = string.IsNullOrWhiteSpace(config.Operator)
            ? " "
            : $" {config.Operator.Trim()} ";
        return string.Join(separator, clauses);
    }

    private static string FormatQueryValue(string value, string syntax, string match)
    {
        var escaped = value.Trim()
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        if (string.Equals(syntax, "lucene", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(match, "phrase", StringComparison.OrdinalIgnoreCase))
        {
            escaped = Regex.Replace(escaped, @"([+\-!(){}\[\]^~*?:/]|&&|\|\|)", @"\$1");
        }

        return string.Equals(match, "phrase", StringComparison.OrdinalIgnoreCase)
            ? $"\"{escaped}\""
            : escaped;
    }

    private static string BuildConfiguredQuery(
        string template,
        string? searchTitle,
        ProviderLookupRequest request)
    {
        var query = template;
        query = ReplacePlaceholder(query, "{title}", searchTitle, encode: false);
        query = ReplacePlaceholder(query, "{author}", request.Author, encode: false);
        query = ReplacePlaceholder(query, "{narrator}", request.Narrator, encode: false);
        query = ReplacePlaceholder(query, "{show_name}", request.ShowName, encode: false);
        query = ReplacePlaceholder(query, "{album}", request.Album, encode: false);
        query = ReplacePlaceholder(query, "{artist}", request.Artist, encode: false);
        query = ReplacePlaceholder(query, "{director}", request.Director, encode: false);
        query = ReplacePlaceholder(query, "{composer}", request.Composer, encode: false);
        // Remove dangling Lucene operators when optional fields are empty.
        // e.g. "{title} AND artist:{author}" becomes just the title.
        query = Regex.Replace(query, @"\s+AND\s+\w+:\s*$", string.Empty, RegexOptions.IgnoreCase);
        query = Regex.Replace(query, @"^\s*AND\s+\w+:\s*", string.Empty, RegexOptions.IgnoreCase);
        return Regex.Replace(query.Trim(), @"\s+", " ");
    }

    private string ResolveBaseUrl(ProviderLookupRequest request)
    {
        // Prefer BaseUrl from the harvesting service (populated from config endpoints).
        if (!string.IsNullOrEmpty(request.BaseUrl))
        {
            return request.BaseUrl.TrimEnd('/');
        }

        // Fall back to the first endpoint in the provider config.
        if (_config.Endpoints.Count > 0)
        {
            var first = _config.Endpoints.Values.First();
            return first.TrimEnd('/');
        }

        return string.Empty;
    }

    private static string ReplacePlaceholder(string template, string placeholder, string? value, bool encode)
    {
        if (!template.Contains(placeholder, StringComparison.Ordinal))
        {
            return template;
        }

        var replacement = value ?? string.Empty;
        if (encode && !string.IsNullOrEmpty(replacement))
        {
            replacement = Uri.EscapeDataString(replacement);
        }

        return template.Replace(placeholder, replacement, StringComparison.Ordinal);
    }

    /// <summary>
    /// Strips trailing year suffixes like "(2017)" and TV episode designations like "S01E01"
    /// from titles so that search APIs receive clean query strings.
    /// </summary>
    internal static string? CleanTitleForSearch(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        // Strip trailing (YYYY) — e.g. "Blade Runner 2049 (2017)" ? "Blade Runner 2049"
        var cleaned = MediaEngine.Domain.Services.ReleaseTitleHints.Parse(title).Title ?? title;

        // Strip trailing SxxExx — e.g. "Breaking Bad S01E01" ? "Breaking Bad"
        cleaned = Regex.Replace(cleaned, @"\s*S\d{1,2}E\d{1,2}\s*$", string.Empty, RegexOptions.IgnoreCase);

        return cleaned.Trim();
    }

    /// <summary>
    /// Extracts a four-digit year from a trailing "(YYYY)" suffix if present.
    /// Returns null when no year suffix is found.
    /// </summary>
    internal static string? ExtractYearFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return MediaEngine.Domain.Services.ReleaseTitleHints.Parse(title).Year;
    }
}
