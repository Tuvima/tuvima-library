using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Adapters;

public sealed partial class ConfigDrivenAdapter
{
    private async Task<IReadOnlyList<ProviderClaim>> EnrichClaimsWithTmdbDetailsAsync(
        IReadOnlyList<ProviderClaim> claims,
        JsonNode resultNode,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        var mediaType = request.MediaType;
        if (!string.Equals(Name, "tmdb", StringComparison.OrdinalIgnoreCase)
            || mediaType is not (MediaType.Movies or MediaType.TV)
            || string.IsNullOrWhiteSpace(EffectiveApiKey))
        {
            return claims;
        }

        var tmdbId = claims.FirstOrDefault(c =>
            string.Equals(c.Key, BridgeIdKeys.TmdbId, StringComparison.OrdinalIgnoreCase))?.Value
            ?? resultNode["id"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture)
            ?? resultNode["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            return claims;
        }

        var endpoint = mediaType == MediaType.TV ? "tv" : "movie";
        var baseUrl = _config.Endpoints.GetValueOrDefault("api") ?? "https://api.themoviedb.org/3";
        var appendToResponse = mediaType == MediaType.TV
            ? "aggregate_credits,content_ratings,external_ids,images"
            : "credits,release_dates,external_ids,images";
        var url = $"{baseUrl.TrimEnd('/')}/{endpoint}/{Uri.EscapeDataString(tmdbId)}?language=en-US&append_to_response={appendToResponse}&api_key={Uri.EscapeDataString(EffectiveApiKey!)}";

        try
        {
            var detailCacheKey = BuildCacheKey(url);
            var cacheTtlHours = _config.CacheTtlHours ?? 168;
            JsonNode? details = null;

            if (_responseCache is not null)
            {
                var cached = await _responseCache.FindAsync(detailCacheKey, ct).ConfigureAwait(false);
                if (cached is not null)
                {
                    details = JsonNode.Parse(cached.ResponseJson);
                }
            }

            if (details is null)
            {
                using var client = _httpFactory.CreateClient(_config.Name);
                using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return claims;
                }

                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                details = JsonNode.Parse(responseBody);

                if (_responseCache is not null && !string.IsNullOrWhiteSpace(responseBody))
                {
                    var etag = response.Headers.ETag?.Tag?.Trim('"');
                    await _responseCache.UpsertAsync(
                        detailCacheKey,
                        _providerId.ToString(),
                        ComputeSha256(url),
                        responseBody,
                        etag,
                        cacheTtlHours,
                        ct).ConfigureAwait(false);
                }
            }

            if (details is null)
            {
                return claims;
            }

            var enriched = claims.ToList();
            AddIfMissing(enriched, MetadataFieldConstants.Description, details["overview"]?.GetValue<string>(), 0.85);
            AddIfMissing(enriched, MetadataFieldConstants.ShortDescription, details["overview"]?.GetValue<string>(), 0.84);
            AddIfMissing(enriched, MetadataFieldConstants.Tagline, details["tagline"]?.GetValue<string>(), ClaimConfidence.ProviderNativeTagline);
            AddIfMissing(enriched, MetadataFieldConstants.Runtime, details["runtime"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture), 0.90);
            AddIfMissing(enriched, "content_rating", ExtractTmdbContentRating(details, mediaType), 0.88);
            if (mediaType == MediaType.Movies)
            {
                AddTmdbMovieCollectionClaims(enriched, details);
                await EnrichTmdbMovieCollectionSequenceAsync(enriched, details, request, ct)
                    .ConfigureAwait(false);
            }

            AddTmdbProductionClaims(enriched, details, mediaType);
            AddTmdbExternalIdClaims(enriched, details);
            AddTmdbCastClaims(enriched, details, mediaType);
            AddTmdbCrewClaims(enriched, details, mediaType);

            return enriched;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "{Provider}: detail enrichment failed for {MediaType} id {TmdbId}", Name, mediaType, tmdbId);
            return claims;
        }
    }

    private static void AddIfMissing(List<ProviderClaim> claims, string key, string? value, double confidence)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (claims.Any(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        claims.Add(new ProviderClaim(key, value, confidence));
    }

    private static void AddTmdbMovieCollectionClaims(List<ProviderClaim> claims, JsonNode details)
    {
        var collection = details["belongs_to_collection"];
        if (collection is null)
        {
            return;
        }

        var collectionId = collection["id"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture)
            ?? collection["id"]?.GetValue<string>();
        var collectionName = collection["name"]?.GetValue<string>();

        AddIfMissing(claims, "tmdb_collection_id", collectionId, 1.0);
        AddIfMissing(claims, "tmdb_collection_name", collectionName, 0.94);
        AddIfMissing(claims, MetadataFieldConstants.Series, collectionName, 0.90);
    }

    private async Task EnrichTmdbMovieCollectionSequenceAsync(
        List<ProviderClaim> claims,
        JsonNode details,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        var collection = details["belongs_to_collection"];
        if (collection is null)
        {
            return;
        }

        var collectionId = collection["id"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture)
            ?? collection["id"]?.GetValue<string>();
        var movieId = details["id"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture)
            ?? details["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(collectionId) || string.IsNullOrWhiteSpace(movieId))
        {
            return;
        }

        var baseUrl = _config.Endpoints.GetValueOrDefault("api") ?? ResolveBaseUrl(request);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(EffectiveApiKey))
        {
            return;
        }

        var language = $"{request.Language.ToLowerInvariant()}-{request.Country.ToUpperInvariant()}";
        var url = $"{baseUrl.TrimEnd('/')}/collection/{Uri.EscapeDataString(collectionId)}?language={Uri.EscapeDataString(language)}&api_key={Uri.EscapeDataString(EffectiveApiKey!)}";

        try
        {
            var collectionDetails = await FetchJsonWithCacheAsync(url, ct).ConfigureAwait(false);
            var parts = collectionDetails?["parts"]?.AsArray()
                .Where(part => part is not null)
                .Select(part => new TmdbCollectionPart(
                    Id: part?["id"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture) ?? part?["id"]?.GetValue<string>() ?? string.Empty,
                    Title: StringHelpers.FirstNonBlank(part?["title"]?.GetValue<string>(), part?["name"]?.GetValue<string>()) ?? string.Empty,
                    ReleaseDate: ParseTmdbReleaseDate(part?["release_date"]?.GetValue<string>())))
                .Where(part => !string.IsNullOrWhiteSpace(part.Id))
                .OrderBy(part => part.ReleaseDate ?? DateOnly.MaxValue)
                .ThenBy(part => part.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (parts is null or { Count: 0 })
            {
                return;
            }

            var position = parts.FindIndex(part => string.Equals(part.Id, movieId, StringComparison.OrdinalIgnoreCase));
            if (position < 0)
            {
                return;
            }

            AddIfMissing(claims, MetadataFieldConstants.SeriesPosition, (position + 1).ToString(CultureInfo.InvariantCulture), 0.90);
            AddIfMissing(claims, MetadataFieldConstants.SequenceTotal, parts.Count.ToString(CultureInfo.InvariantCulture), 0.90);
            AddIfMissing(claims, MetadataFieldConstants.SequenceTotalScope, SequenceCountScope.MainSequence.ToString(), 0.90);
            AddIfMissing(claims, MetadataFieldConstants.SequenceFormat, SequenceFormat.Standard.ToString(), 0.80);

            var collectionName = collection["name"]?.GetValue<string>();
            var manifest = new ProviderSequenceManifest
            {
                Provider = "tmdb",
                ContainerId = $"tmdb:collection:{collectionId}",
                ContainerLabel = collectionName,
                ExternalIdKey = BridgeIdKeys.TmdbId,
                MediaType = MediaType.Movies.ToString(),
                IsAuthoritative = true,
                Items = parts.Select((part, index) => new ProviderSequenceManifestItem
                {
                    ExternalId = part.Id,
                    Title = part.Title,
                    Ordinal = (index + 1).ToString(CultureInfo.InvariantCulture),
                    ReleaseDate = part.ReleaseDate?.ToString("O", CultureInfo.InvariantCulture),
                }).ToList(),
            };
            AddIfMissing(
                claims,
                MetadataFieldConstants.SequenceManifestJson,
                JsonSerializer.Serialize(manifest),
                1.0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(
                ex,
                "{Provider}: TMDB collection sequence enrichment failed for collection {CollectionId}",
                Name,
                collectionId);
        }
    }

    private async Task<JsonNode?> FetchJsonWithCacheAsync(string url, CancellationToken ct)
    {
        var cacheKey = BuildCacheKey(url);
        var cacheTtlHours = _config.CacheTtlHours ?? 168;

        if (_responseCache is not null)
        {
            var cached = await _responseCache.FindAsync(cacheKey, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                return JsonNode.Parse(cached.ResponseJson);
            }
        }

        using var client = _httpFactory.CreateClient(_config.Name);
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (_responseCache is not null && !string.IsNullOrWhiteSpace(responseBody))
        {
            var etag = response.Headers.ETag?.Tag?.Trim('"');
            await _responseCache.UpsertAsync(
                cacheKey,
                _providerId.ToString(),
                ComputeSha256(url),
                responseBody,
                etag,
                cacheTtlHours,
                ct).ConfigureAwait(false);
        }

        return string.IsNullOrWhiteSpace(responseBody) ? null : JsonNode.Parse(responseBody);
    }

    private static DateOnly? ParseTmdbReleaseDate(string? value)
        => DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static string? ExtractTmdbContentRating(JsonNode details, MediaType mediaType)
    {
        var results = mediaType == MediaType.TV
            ? details["content_ratings"]?["results"]?.AsArray()
            : details["release_dates"]?["results"]?.AsArray();
        if (results is null)
        {
            return null;
        }

        foreach (var country in new[] { "US", "GB", "CA", "AU" })
        {
            var countryNode = results.FirstOrDefault(node =>
                string.Equals(node?["iso_3166_1"]?.GetValue<string>(), country, StringComparison.OrdinalIgnoreCase));
            var rating = mediaType == MediaType.TV
                ? countryNode?["rating"]?.GetValue<string>()
                : countryNode?["release_dates"]?.AsArray()
                    .Select(node => node?["certification"]?.GetValue<string>())
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(rating))
            {
                return rating;
            }
        }

        return null;
    }
    private static void AddTmdbCastClaims(List<ProviderClaim> claims, JsonNode details, MediaType mediaType)
    {
        var castArray = mediaType == MediaType.TV
            ? details["aggregate_credits"]?["cast"]?.AsArray()
            : details["credits"]?["cast"]?.AsArray();

        if (castArray is null)
        {
            return;
        }

        foreach (var castNode in castArray
            .Where(node => node is not null)
            .OrderBy(node => node?["order"]?.GetValue<int?>() ?? int.MaxValue)
            .ThenBy(node => node?["name"]?.GetValue<string>() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var name = castNode?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            claims.Add(new ProviderClaim(MetadataFieldConstants.CastMember, name, 0.90));
            AddIfPresent(claims, "cast_member_character", ExtractTmdbCharacterName(castNode, mediaType), 0.90);

            var tmdbPersonId = castNode?["id"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture)
                ?? castNode?["id"]?.GetValue<string>();
            AddIfPresent(claims, "cast_member_tmdb_id", tmdbPersonId, 0.92);
            AddIfPresent(claims, "cast_member_tmdb_identity",
                string.IsNullOrWhiteSpace(tmdbPersonId) ? null : $"{tmdbPersonId}::{name}", 0.92);

            var profilePath = castNode?["profile_path"]?.GetValue<string>();
            AddIfPresent(claims, "cast_member_profile_url", BuildTmdbProfileUrl(profilePath), 0.90);
        }
    }

    private static string? ExtractTmdbCharacterName(JsonNode? castNode, MediaType mediaType)
    {
        if (castNode is null)
        {
            return null;
        }

        if (mediaType == MediaType.TV)
        {
            var roles = castNode["roles"]?.AsArray()
                .Where(role => role is not null)
                .OrderBy(role => role?["episode_count"]?.GetValue<int?>() ?? 0)
                .Reverse()
                .Select(role => role?["character"]?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            if (roles is { Count: > 0 })
            {
                return string.Join(" / ", roles);
            }
        }

        return castNode["character"]?.GetValue<string>();
    }

    private static void AddIfPresent(List<ProviderClaim> claims, string key, string? value, double confidence)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            claims.Add(new ProviderClaim(key, value, confidence));
        }
    }

    private static string? BuildTmdbProfileUrl(string? profilePath)
        => string.IsNullOrWhiteSpace(profilePath)
            ? null
            : $"https://image.tmdb.org/t/p/original/{profilePath.TrimStart('/')}";

    private static void AddTmdbProductionClaims(List<ProviderClaim> claims, JsonNode details, MediaType mediaType)
    {
        if (mediaType == MediaType.TV)
        {
            var network = details["networks"]?.AsArray()
                .Where(node => node is not null)
                .Select(node => new
                {
                    Name = node?["name"]?.GetValue<string>(),
                    LogoPath = node?["logo_path"]?.GetValue<string>(),
                })
                .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Name));

            AddIfMissing(claims, MetadataFieldConstants.Network, network?.Name, 0.88);
            AddIfMissing(claims, "network_logo_url", BuildTmdbProfileUrl(network?.LogoPath), 0.84);
        }

        var companies = details["production_companies"]?.AsArray()
            .Where(node => node is not null)
            .Select(node => new
            {
                Name = node?["name"]?.GetValue<string>(),
                LogoPath = node?["logo_path"]?.GetValue<string>(),
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Take(5)
            .ToList();

        if (companies is not { Count: > 0 })
        {
            return;
        }

        var studio = companies.First();
        AddIfMissing(claims, "studio", studio.Name, 0.88);
        AddIfMissing(claims, "studio_logo_url", BuildTmdbProfileUrl(studio.LogoPath), 0.84);
        AddIfMissing(claims, "production_company", string.Join("; ", companies.Select(item => item.Name)), 0.86);
    }

    private static void AddTmdbExternalIdClaims(List<ProviderClaim> claims, JsonNode details)
    {
        var externalIds = details["external_ids"];
        if (externalIds is null)
        {
            return;
        }

        AddIfMissing(claims, BridgeIdKeys.ImdbId, externalIds["imdb_id"]?.GetValue<string>(), 0.98);
        AddIfMissing(claims, BridgeIdKeys.TvdbId, externalIds["tvdb_id"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture)
            ?? externalIds["tvdb_id"]?.GetValue<string>(), 0.98);
        AddIfMissing(claims, BridgeIdKeys.WikidataQid, externalIds["wikidata_id"]?.GetValue<string>(), 0.98);
    }

    private static void AddTmdbCrewClaims(List<ProviderClaim> claims, JsonNode details, MediaType mediaType)
    {
        var crewArray = mediaType == MediaType.TV
            ? details["aggregate_credits"]?["crew"]?.AsArray()
            : details["credits"]?["crew"]?.AsArray();

        if (crewArray is null)
        {
            return;
        }

        foreach (var crewNode in crewArray.Where(node => node is not null))
        {
            var name = crewNode?["name"]?.GetValue<string>();
            var role = ResolveTmdbCrewRole(crewNode);
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(role))
            {
                continue;
            }

            var key = role.ToLowerInvariant() switch
            {
                "director" => "director",
                "screenwriter" => "screenwriter",
                "composer" => "composer",
                "producer" => "producer",
                _ => null,
            };
            if (key is null)
            {
                continue;
            }

            claims.Add(new ProviderClaim(key, name, 0.88));

            var tmdbPersonId = crewNode?["id"]?.GetValue<long?>()?.ToString(CultureInfo.InvariantCulture)
                ?? crewNode?["id"]?.GetValue<string>();
            AddIfPresent(claims, $"{key}_tmdb_id", tmdbPersonId, 0.92);

            var profilePath = crewNode?["profile_path"]?.GetValue<string>();
            AddIfPresent(claims, $"{key}_profile_url", BuildTmdbProfileUrl(profilePath), 0.90);
        }
    }

    private static string? ResolveTmdbCrewRole(JsonNode? crewNode)
    {
        var job = crewNode?["job"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(job))
        {
            job = crewNode?["jobs"]?.AsArray()
                .Select(node => node?["job"]?.GetValue<string>())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        if (string.IsNullOrWhiteSpace(job))
        {
            return null;
        }

        if (job.Contains("Director", StringComparison.OrdinalIgnoreCase))
        {
            return "Director";
        }

        if (job.Contains("Screenplay", StringComparison.OrdinalIgnoreCase)
            || job.Contains("Writer", StringComparison.OrdinalIgnoreCase)
            || job.Contains("Story", StringComparison.OrdinalIgnoreCase))
        {
            return "Screenwriter";
        }

        if (job.Contains("Composer", StringComparison.OrdinalIgnoreCase)
            || job.Contains("Music", StringComparison.OrdinalIgnoreCase))
        {
            return "Composer";
        }

        if (job.Contains("Producer", StringComparison.OrdinalIgnoreCase))
        {
            return "Producer";
        }

        return null;
    }

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

    // -- Result navigation ---------------------------------------------------

}
