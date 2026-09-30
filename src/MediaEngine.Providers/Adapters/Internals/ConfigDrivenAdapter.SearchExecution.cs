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
    private async Task<IReadOnlyList<SearchResultItem>> ExecuteSearchStrategyAsync(
        SearchStrategyConfig strategy,
        ProviderLookupRequest request,
        int limit,
        CancellationToken ct)
    {
        // Manual multi-result search — use the full requested limit.
        var url = BuildUrl(strategy, request, limit);
        _logger.LogInformation("{Provider}/{Strategy}: SEARCH", Name, strategy.Name);

        using var client = _httpFactory.CreateClient(_config.Name);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);

        // Apply bearer API key header if configured.
        if (_config.HttpClient is { ApiKeyDelivery: "bearer" }
            && !string.IsNullOrWhiteSpace(EffectiveApiKey))
        {
            httpRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", EffectiveApiKey);
        }
        else if (_config.HttpClient is { ApiKeyDelivery: "basic" }
            && !string.IsNullOrWhiteSpace(_config.HttpClient.Username)
            && !string.IsNullOrWhiteSpace(_config.HttpClient.Password))
        {
            var credentials = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(
                    $"{_config.HttpClient.Username}:{_config.HttpClient.Password}"));
            httpRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic", credentials);
        }

        using var response = await _rateLimiter.ExecuteAsync(
            Name,
            _config.RateLimit,
            token => client.SendAsync(httpRequest, token),
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "{Provider}/{Strategy}: HTTP {StatusCode}",
            Name, strategy.Name, (int)response.StatusCode);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound
            && strategy.Tolerate404)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content
            .ReadFromJsonAsync<System.Text.Json.Nodes.JsonNode>(cancellationToken: ct)
            .ConfigureAwait(false);

        if (json is null)
        {
            return [];
        }

        // Navigate to results array.
        var resultsNode = JsonPathEvaluator.Evaluate(json, strategy.ResultsPath!);
        if (resultsNode is not System.Text.Json.Nodes.JsonArray arr || arr.Count == 0)
        {
            return [];
        }

        var items = new List<SearchResultItem>();
        var count = Math.Min(arr.Count, limit);

        for (int i = 0; i < count; i++)
        {
            var resultNode = arr[i];
            if (resultNode is null)
            {
                continue;
            }

            var item = ExtractSearchResultItem(resultNode, request, strategy);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        _logger.LogDebug(
            "{Provider}/{Strategy}: search returned {Count} items",
            Name, strategy.Name, items.Count);

        // Return all items — the caller (SearchService or HydrationPipeline)
        // handles ranking and selection. For the resolve tab, users need to
        // see multiple editions with different covers, narrators, and years.
        // For automated pipelines, the scoring service picks the best match.
        return items;
    }

    /// <summary>
    /// Extracts a <see cref="SearchResultItem"/> from a single JSON result object
    /// using the configured field mappings. Looks for title, description, year,
    /// cover/thumbnail, and a provider item ID.
    /// <para>
    /// The lookup request is used to compute a per-result match score so that the first
    /// result is not always scored identically to the tenth. For comics, series and issue
    /// number are stronger identity signals than issue-title text.
    /// </para>
    /// </summary>
    private SearchResultItem? ExtractSearchResultItem(
        System.Text.Json.Nodes.JsonNode resultNode,
        ProviderLookupRequest request,
        SearchStrategyConfig? strategy = null)
    {
        var filteredMappings = FilterMappingsByMediaType(_config.FieldMappings, request.MediaType);
        if (filteredMappings.Count == 0)
        {
            return null;
        }

        // When the strategy has release selection (e.g. MusicBrainz recordings with nested
        // releases), pick the best release so source-routed mappings resolve correctly.
        JsonNode? releaseNode = strategy?.ReleaseSelection is not null
            ? ApplyReleaseSelection(resultNode, strategy.ReleaseSelection, request)
            : null;

        string? title = null;
        string? author = null;
        string? description = null;
        string? year = null;
        string? thumbnailUrl = null;
        string? providerItemId = null;
        var extraFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in filteredMappings)
        {
            // Route to the correct source node (recording vs release) when configured.
            var sourceNode = mapping.Source?.ToLowerInvariant() switch
            {
                "release" => releaseNode ?? resultNode,
                _ => resultNode,
            };

            var node = JsonPathEvaluator.Evaluate(sourceNode, mapping.JsonPath);
            if (node is null)
            {
                _logger.LogDebug("{Provider}: mapping '{Key}' (path '{Path}') — node not found",
                    Name, mapping.ClaimKey, mapping.JsonPath);
                continue;
            }

            // Check condition if configured.
            if (mapping.Condition is not null && !PassesFilters(sourceNode, [mapping.Condition]))
            {
                _logger.LogDebug("{Provider}: mapping '{Key}' — condition not met", Name, mapping.ClaimKey);
                continue;
            }

            // Use ApplyTransform for array-aware extraction (array_join, etc.),
            // then fall back to GetStringValue for simple scalars.
            string? raw;
            if (!string.IsNullOrEmpty(mapping.Transform))
            {
                var values = ApplyTransform(node, mapping);
                raw = values.Count > 0 ? values[0] : null;
            }
            else
            {
                raw = JsonPathEvaluator.GetStringValue(node);
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                _logger.LogDebug("{Provider}: mapping '{Key}' (path '{Path}') — value is null or empty",
                    Name, mapping.ClaimKey, mapping.JsonPath);
                continue;
            }

            _logger.LogDebug("{Provider}: mapping '{Key}' ? '{Value}'",
                Name, mapping.ClaimKey, raw.Length > 80 ? raw[..80] + "…" : raw);

            switch (mapping.ClaimKey.ToLowerInvariant())
            {
                case "title":
                    title ??= raw;
                    break;
                case "author":
                    author ??= raw;
                    break;
                case "description":
                    description ??= raw;
                    break;
                case "year":
                    year ??= raw;
                    break;
                case "cover":
                    thumbnailUrl ??= raw;
                    break;
                // Provider-specific IDs for direct follow-up lookup.
                case BridgeIdKeys.Isbn:
                case BridgeIdKeys.Asin:
                case BridgeIdKeys.AppleBooksId:
                case BridgeIdKeys.GoodreadsId:
                case BridgeIdKeys.AudibleId:
                case BridgeIdKeys.TmdbId:
                case BridgeIdKeys.ImdbId:
                case BridgeIdKeys.ComicVineId:
                case BridgeIdKeys.MusicBrainzId:
                case BridgeIdKeys.SpotifyId:
                    providerItemId ??= raw;
                    if (string.Equals(mapping.ClaimKey, BridgeIdKeys.ComicVineId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Name, "comicvine", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(strategy?.Name, "volume_search", StringComparison.OrdinalIgnoreCase))
                    {
                        // Comic Vine returns a volume's identifier in the same
                        // `id` field it uses for issues. The selected search
                        // strategy establishes the scope, so retain this as a
                        // run/container identifier for hierarchy alignment.
                        extraFields.TryAdd(BridgeIdKeys.ComicVineVolumeId, raw);
                    }
                    // Preserve every provider identifier for candidate review. The
                    // first identifier remains the primary apply key, while album
                    // collection/release identifiers are required to load richer
                    // evidence such as a candidate track list.
                    extraFields.TryAdd(mapping.ClaimKey, raw);
                    break;
                default:
                    // Collect any other mapped fields (album, track_number, duration, etc.)
                    extraFields.TryAdd(mapping.ClaimKey, raw);
                    break;
            }
        }

        // Must have at least a title to be a valid result.
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // Compute a per-result match score based on how closely the result's
        // title (and author) match the original search query.
        // This ensures result 1 scores higher than result 8 when the provider
        // returns them in relevance order but with identical field weights.
        var confidence = ComputeSearchResultConfidence(request, title, author, extraFields);

        _logger.LogInformation(
            "{Provider}: extracted result Title='{Title}' Author='{Author}' Year='{Year}' " +
            "HasDesc={HasDesc} HasCover={HasCover} ExtraFields={ExtraCount} Score={Score:P0}",
            Name, title, author ?? "—", year ?? "—",
            description is not null, thumbnailUrl is not null, extraFields.Count, confidence);

        return new SearchResultItem(
            Title: title,
            Author: author,
            Description: description,
            Year: year,
            ThumbnailUrl: thumbnailUrl,
            ProviderItemId: providerItemId,
            Confidence: confidence,
            ProviderName: Name,
            ExtraFields: extraFields.Count > 0 ? extraFields : null);
    }

    /// <summary>
    /// Computes a per-result match score (0.0–1.0) by comparing the search
    /// <paramref name="query"/> against the result's <paramref name="title"/>
    /// using word-level overlap similarity.
    ///
    /// <para>
    /// Algorithm:
    /// <list type="bullet">
    ///   <item>Tokenise both query and title into lowercase words (=2 chars).</item>
    ///   <item>Coverage = query words found in title / total query words.</item>
    ///   <item>Precision = title words found in query / total title words.</item>
    ///   <item>Score = harmonic mean (F1) of coverage and precision × 0.85.</item>
    ///   <item>+0.12 bonus for exact (normalised) title match.</item>
    ///   <item>+0.05 bonus if any author token appears in the query.</item>
    ///   <item>Minimum 0.05 when the result has a title but no query is given.</item>
    /// </list>
    /// </para>
    /// </summary>
    private static double ComputeSearchResultConfidence(
        ProviderLookupRequest request,
        string title,
        string? author,
        IReadOnlyDictionary<string, string> extraFields)
    {
        if (request.MediaType == MediaType.Comics)
        {
            var fileSeries = request.Series ?? request.Hints?.GetValueOrDefault(MetadataFieldConstants.Series);
            var fileIssue = GetComicIssueHint(request);
            var candidateSeries = extraFields.GetValueOrDefault(MetadataFieldConstants.Series);
            var candidateIssue = extraFields.GetValueOrDefault("issue_number")
                ?? extraFields.GetValueOrDefault(MetadataFieldConstants.SeriesPosition)
                ?? extraFields.GetValueOrDefault("issue");

            if (!string.IsNullOrWhiteSpace(fileSeries)
                && !string.IsNullOrWhiteSpace(fileIssue)
                && !string.IsNullOrWhiteSpace(candidateSeries)
                && !string.IsNullOrWhiteSpace(candidateIssue)
                && AreEquivalentComicText(fileSeries, candidateSeries)
                && AreEquivalentComicOrdinals(fileIssue, candidateIssue))
            {
                return 1.0;
            }
        }

        return ComputeQueryMatchScore(request.Title, title, author);
    }

    private static double ComputeQueryMatchScore(string? query, string? title, string? author)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(title))
        {
            return 0.50; // No query context — neutral score.
        }

        var queryTokens = TokenizeText(query);
        var titleTokens = TokenizeText(title);

        if (queryTokens.Count == 0 || titleTokens.Count == 0)
        {
            return 0.50;
        }

        // Exact normalised match ? perfect score.
        if (string.Equals(
                string.Join(' ', queryTokens.Order()),
                string.Join(' ', titleTokens.Order()),
                StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        // Coverage: fraction of query words that appear in the title.
        int coverageHits = queryTokens.Count(q => titleTokens.Contains(q));
        double coverage = (double)coverageHits / queryTokens.Count;

        // Precision: fraction of title words that appear in the query.
        int precisionHits = titleTokens.Count(t => queryTokens.Contains(t));
        double precision = (double)precisionHits / titleTokens.Count;

        // F1 (harmonic mean) scaled to 0.85 ceiling.
        double f1 = (coverage + precision) > 0
            ? 2.0 * coverage * precision / (coverage + precision)
            : 0.0;
        double score = f1 * 0.85;

        // Author tokens in query ? small bonus.
        if (!string.IsNullOrWhiteSpace(author))
        {
            var authorTokens = TokenizeText(author);
            bool authorInQuery = authorTokens.Any(a => queryTokens.Contains(a));
            if (authorInQuery)
            {
                score += 0.05;
            }
        }

        return Math.Clamp(score, 0.05, 1.0);
    }

    /// <summary>
    /// Tokenises text into a lowercase word set suitable for similarity comparison.
    /// Strips punctuation and filters out single-character tokens.
    /// </summary>
    private static HashSet<string> TokenizeText(string text)
        => [.. text.ToLowerInvariant()
            .Split([' ', ',', '.', '-', ':', ';', '\'', '"', '(', ')', '[', ']', '!', '?'],
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)];

    // -- Strategy execution --------------------------------------------------

    private async Task<IReadOnlyList<ProviderClaim>> ExecuteStrategyAsync(
        SearchStrategyConfig strategy,
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        // Automatic single-result match — request only as many results as we need.
        var fetchLimit = strategy.FetchLimit > 0 ? strategy.FetchLimit : 5;
        var url = BuildUrl(strategy, request, fetchLimit);
        _logger.LogDebug("{Provider}/{Strategy}: FETCH", Name, strategy.Name);

        // -- Response cache check ---------------------------------------------
        var cacheKey = BuildCacheKey(url);
        var cacheTtlHours = _config.CacheTtlHours ?? 168; // Default: 7 days

        if (_responseCache is not null)
        {
            var cached = await _responseCache.FindAsync(cacheKey, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                _logger.LogDebug(
                    "{Provider}/{Strategy}: cache HIT", Name, strategy.Name);

                var cachedJson = JsonNode.Parse(cached.ResponseJson);
                if (cachedJson is not null)
                {
                    var resultNode = await NavigateToResultAsync(cachedJson, strategy, request, ct)
                        .ConfigureAwait(false);
                    if (resultNode is not null)
                    {
                        return await ExtractAndValidateClaimsAsync(strategy, request, resultNode, ct)
                            .ConfigureAwait(false);
                    }
                }
            }
        }

        // -- HTTP call with provider-level rate limiting ----------------------
        using var client = _httpFactory.CreateClient(_config.Name);

        // ETag conditional revalidation for expired entries.
        string? existingEtag = null;
        if (_responseCache is not null)
        {
            existingEtag = await _responseCache.FindExpiredEtagAsync(cacheKey, ct)
                .ConfigureAwait(false);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(existingEtag))
        {
            httpRequest.Headers.IfNoneMatch.Add(
                new System.Net.Http.Headers.EntityTagHeaderValue($"\"{existingEtag}\""));
        }

        // Apply bearer API key header if configured.
        if (_config.HttpClient is { ApiKeyDelivery: "bearer" }
            && !string.IsNullOrWhiteSpace(EffectiveApiKey))
        {
            httpRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", EffectiveApiKey);
        }
        // Apply HTTP Basic Authentication if configured.
        else if (_config.HttpClient is { ApiKeyDelivery: "basic" }
            && !string.IsNullOrWhiteSpace(_config.HttpClient.Username)
            && !string.IsNullOrWhiteSpace(_config.HttpClient.Password))
        {
            var credentials = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(
                    $"{_config.HttpClient.Username}:{_config.HttpClient.Password}"));
            httpRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic", credentials);
        }

        using var response = await _rateLimiter.ExecuteAsync(
            Name,
            _config.RateLimit,
            token => client.SendAsync(httpRequest, token),
            ct).ConfigureAwait(false);

        // ETag 304: cache is still valid — refresh expiry and use it.
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified
            && _responseCache is not null)
        {
            _logger.LogDebug(
                "{Provider}/{Strategy}: 304 Not Modified — refreshing cache",
                Name, strategy.Name);
            await _responseCache.RefreshExpiryAsync(cacheKey, cacheTtlHours, ct)
                .ConfigureAwait(false);

            // Re-read the now-refreshed cached response.
            var refreshed = await _responseCache.FindAsync(cacheKey, ct)
                .ConfigureAwait(false);
            if (refreshed is not null)
            {
                var cachedJson = JsonNode.Parse(refreshed.ResponseJson);
                if (cachedJson is not null)
                {
                    var resultNode = await NavigateToResultAsync(cachedJson, strategy, request, ct)
                        .ConfigureAwait(false);
                    if (resultNode is not null)
                    {
                        return await ExtractAndValidateClaimsAsync(strategy, request, resultNode, ct)
                            .ConfigureAwait(false);
                    }
                }
            }
            return [];
        }

        // Tolerate 404 for direct-lookup APIs (e.g. Audnexus).
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound
            && strategy.Tolerate404)
        {
            _logger.LogDebug(
                "{Provider}/{Strategy}: 404 tolerated", Name, strategy.Name);
            return [];
        }

        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(ct)
            .ConfigureAwait(false);

        // Cache the response.
        if (_responseCache is not null && !string.IsNullOrEmpty(responseBody))
        {
            var etag = response.Headers.ETag?.Tag?.Trim('"');
            var queryHash = ComputeSha256(url);
            await _responseCache.UpsertAsync(
                cacheKey, _providerId.ToString(), queryHash,
                responseBody, etag, cacheTtlHours, ct)
                .ConfigureAwait(false);
        }

        var json = JsonNode.Parse(responseBody);
        if (json is null)
        {
            return [];
        }

        // Navigate to result object.
        var resultObj = await NavigateToResultAsync(json, strategy, request, ct)
            .ConfigureAwait(false);
        if (resultObj is null)
        {
            return [];
        }

        return await ExtractAndValidateClaimsAsync(strategy, request, resultObj, ct)
            .ConfigureAwait(false);
    }

}
