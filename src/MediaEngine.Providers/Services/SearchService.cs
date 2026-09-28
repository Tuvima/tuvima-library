using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Orchestrates user-triggered metadata search across Wikidata (Universe mode)
/// and retail providers (Retail mode).
///
/// Universe search: returns text-first Wikidata candidates and named comparison metadata.
///
/// Retail search: calls the relevant retail providers (filtered by media type) via
/// SearchAsync to return title/cover matches without requiring a Wikidata QID.
/// </summary>
public sealed class SearchService : ISearchService
{
    private readonly IReadOnlyList<IExternalMetadataProvider> _providers;
    private readonly IConfigurationLoader _configLoader;
    private readonly IFuzzyMatchingService _fuzzy;
    private readonly IRetailMatchScoringService _retailScoring;
    private readonly RetailCandidateScorer _candidateScorer;
    private readonly ILogger<SearchService> _logger;

    // Providers that should not be used for retail search
    private static readonly HashSet<string> ExcludedFromRetail = new(StringComparer.OrdinalIgnoreCase)
    {
        "wikidata", "wikidata_reconciliation"
    };

    /// <summary>
    /// Media-type-to-description-keyword mapping for post-filtering Wikidata candidates.
    /// Candidates whose Description does not contain any of these keywords are removed.
    /// </summary>
    private static readonly Dictionary<string, string[]> MediaTypeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Epub"] = ["novel", "book", "literary work", "written work", "light novel", "short story collection", "novella", "poetry collection", "anthology"],
        ["Audiobook"] = ["novel", "book", "literary work", "written work", "audiobook", "light novel", "novella", "poetry collection", "anthology"],
        ["Books"] = ["novel", "book", "literary work", "written work", "light novel", "short story collection", "novella", "poetry collection", "anthology"],
        ["Movies"] = ["film", "movie", "animated film", "short film", "documentary film"],
        ["TV"] = ["television series", "web series", "animated series", "miniseries", "television film"],
        ["Music"] = ["album", "single", "musical work", "song", "extended play"],
        ["Comics"] = ["comic book", "manga", "graphic novel", "comic book series", "manhwa"],
    };

    public SearchService(
        IEnumerable<IExternalMetadataProvider> providers,
        IConfigurationLoader configLoader,
        IFuzzyMatchingService fuzzy,
        IRetailMatchScoringService retailScoring,
        ILogger<SearchService> logger,
        RetailCandidateScorer? candidateScorer = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(configLoader);
        ArgumentNullException.ThrowIfNull(fuzzy);
        ArgumentNullException.ThrowIfNull(retailScoring);
        ArgumentNullException.ThrowIfNull(logger);

        var providerList = providers.ToList();

        _providers = providerList;
        _configLoader = configLoader;
        _fuzzy = fuzzy;
        _retailScoring = retailScoring;
        _candidateScorer = candidateScorer ?? new RetailCandidateScorer();
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<SearchUniverseResult> SearchUniverseAsync(
        SearchUniverseRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new SearchUniverseResult([], request.Query, request.MediaType);
        }

        var mediaType = MediaTypeParser.Parse(request.MediaType);

        var provConfigs = _configLoader.LoadAllProviders();

        // Find the enabled Wikidata Reconciliation provider.
        var wikidataProvider = ProviderExecutionFilter.EnabledProviders(_providers, provConfigs)
            .FirstOrDefault(p =>
                p.Name.Contains("wikidata", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("reconciliation", StringComparison.OrdinalIgnoreCase));

        if (wikidataProvider is null)
        {
            _logger.LogWarning("No Wikidata/Reconciliation provider registered. Universe search unavailable.");
            return new SearchUniverseResult([], request.Query, request.MediaType);
        }

        // Build endpoint map for cover art enrichment
        var (language, country) = GetConfiguredLocale();
        var providerEndpoints = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pc in provConfigs)
        {
            providerEndpoints[pc.Name] = new Dictionary<string, string>(pc.Endpoints, StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            // Search Wikidata via Reconciliation API.
            // The Wikidata wbsearchentities API uses the full query string for matching —
            // Written-work creator hints improve ambiguous title searches, but
            // exact QID searches and film title searches must stay focused on
            // the user-entered identity text.
            var searchQuery = request.Query.Trim();
            var isExactQidQuery = IsExactWikidataQid(searchQuery);
            var shouldUseCreatorHint = ShouldUseCreatorHintForUniverseQuery(mediaType, isExactQidQuery);
            if (shouldUseCreatorHint
                && !string.IsNullOrWhiteSpace(request.LocalAuthor)
                && !searchQuery.Contains(request.LocalAuthor, StringComparison.OrdinalIgnoreCase))
            {
                searchQuery = $"{searchQuery} {request.LocalAuthor}";
            }

            var providerRequest = new ProviderLookupRequest
            {
                EntityId = Guid.NewGuid(),
                EntityType = EntityType.Work,
                MediaType = mediaType,
                Title = searchQuery,
                Author = shouldUseCreatorHint ? request.LocalAuthor : null,
                BaseUrl = GetProviderBaseUrl(wikidataProvider.Name, providerEndpoints),
                Language = language,
                Country = country,
            };

            var searchResults = await wikidataProvider.SearchAsync(
                providerRequest, request.MaxCandidates, ct).ConfigureAwait(false);

            if (searchResults.Count == 0)
            {
                return new SearchUniverseResult([], request.Query, request.MediaType);
            }

            var candidates = new List<UniverseCandidate>();

            foreach (var result in searchResults)
            {
                // Skip items without a valid QID
                var qid = result.ProviderItemId ?? "";
                if (string.IsNullOrEmpty(qid) || !qid.StartsWith('Q'))
                {
                    continue;
                }

                var qidCandidate = new QidCandidate
                {
                    Qid = qid,
                    Label = result.Title,
                    Description = result.Description,
                    ResolutionTier = "title_search",
                    InstanceOf = ExtractInstanceOfFromDescription(result.Description),
                };

                var enriched = BuildUniverseCandidate(qidCandidate, mediaType, result.ExtraFields);

                candidates.Add(enriched);
            }

            // If local context provided, score and re-rank candidates using
            // the unified retail match scoring service (same as pipeline).
            if (!string.IsNullOrWhiteSpace(request.LocalTitle))
            {
                var fileHints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["title"] = request.LocalTitle,
                };
                if (!string.IsNullOrWhiteSpace(request.LocalAuthor))
                {
                    fileHints["author"] = request.LocalAuthor;
                }

                if (!string.IsNullOrWhiteSpace(request.LocalYear))
                {
                    fileHints["year"] = request.LocalYear;
                }

                foreach (var c in candidates)
                {
                    var scores = _retailScoring.ScoreCandidate(
                        fileHints, c.Label, c.Author, c.Year, mediaType);
                    c.MatchScores = ToFieldMatchResult(scores);
                }
                candidates = candidates.OrderByDescending(c => c.MatchScores?.CompositeScore ?? 0.0).ToList();
            }

            return new SearchUniverseResult(candidates, request.Query, request.MediaType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Universe search failed for query '{Query}'", request.Query);
            return new SearchUniverseResult([], request.Query, request.MediaType);
        }
    }

    /// <inheritdoc/>
    public Task<SearchRetailResult> SearchRetailAsync(
        SearchRetailRequest request,
        CancellationToken ct = default) =>
        SearchRetailCoreAsync(request, useAutomaticMatching: false, ct);

    internal Task<SearchRetailResult> SearchRetailAutomaticAsync(
        SearchRetailRequest request,
        CancellationToken ct = default) =>
        SearchRetailCoreAsync(request, useAutomaticMatching: true, ct);

    private async Task<SearchRetailResult> SearchRetailCoreAsync(
        SearchRetailRequest request,
        bool useAutomaticMatching,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new SearchRetailResult([], request.Query, request.MediaType);
        }

        var mediaType = MediaTypeParser.Parse(request.MediaType);
        var retailProviders = GetRetailProviders(mediaType, useAutomaticMatching);

        if (retailProviders.Count == 0)
        {
            return new SearchRetailResult([], request.Query, request.MediaType);
        }

        // Build endpoint map for URLs/country/language
        var provConfigs = _configLoader.LoadAllProviders();
        var (language, country) = GetConfiguredLocale();
        var providerEndpoints = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pc in provConfigs)
        {
            providerEndpoints[pc.Name] = new Dictionary<string, string>(pc.Endpoints, StringComparer.OrdinalIgnoreCase);
        }

        var candidates = new List<RetailCandidate>();
        var providerErrors = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Call each retail provider in parallel
        var tasks = retailProviders.Select(p => useAutomaticMatching
            ? FetchAutomaticProviderCandidateAsync(
                p, request.Query, mediaType, providerEndpoints, request.SearchFields, ct)
            : SearchProviderAsync(
                p, request.Query, mediaType, providerEndpoints, Math.Clamp(request.MaxCandidates, 25, 50), request.SearchFields, ct, providerErrors));

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var providerResults in results)
        {
            candidates.AddRange(providerResults);
        }

        // ── Unified retail match scoring ─────────────────────────────────────
        // Score each candidate against file metadata using the same service
        // the pipeline uses, ensuring consistent confidence numbers everywhere.
        if (!string.IsNullOrWhiteSpace(request.LocalTitle))
        {
            var fileHints = BuildFileHints(request);

            foreach (var c in candidates)
            {
                var extMeta = new CandidateExtendedMetadata
                {
                    Description = c.Description,
                    Genres = c.ExtraFields.TryGetValue("genre", out var g) ? [g] : null,
                    Language = c.ExtraFields.GetValueOrDefault("language"),
                    Series = c.ExtraFields.GetValueOrDefault(MetadataFieldConstants.Series),
                    IssueNumber = c.ExtraFields.GetValueOrDefault("issue_number")
                        ?? c.ExtraFields.GetValueOrDefault(MetadataFieldConstants.SeriesPosition)
                        ?? c.ExtraFields.GetValueOrDefault("issue"),
                };
                var structuralBonus = ComputeRetailSearchStructuralBonus(mediaType, fileHints, extMeta);
                var scores = _retailScoring.ScoreCandidate(
                    fileHints, c.Title, c.Author, c.Year, mediaType,
                    extendedMetadata: extMeta,
                    structuralBonus: structuralBonus);

                c.MatchScores = ToFieldMatchResult(scores);
                if (useAutomaticMatching)
                {
                    var pipeline = _configLoader.LoadPipelines().GetPipelineForMediaType(request.MediaType);
                    var hydration = _configLoader.LoadHydration();
                    var decision = _candidateScorer.EvaluateDecision(
                        fileHints,
                        c.Title,
                        c.Author,
                        c.Year,
                        scores,
                        scores.CompositeScore,
                        pipeline.Scoring.AutoAcceptThreshold ?? hydration.RetailAutoAcceptThreshold,
                        pipeline.Scoring.AmbiguousThreshold ?? hydration.RetailAmbiguousThreshold,
                        "editor_preview",
                        mediaType: mediaType,
                        extendedMetadata: extMeta);
                    c.CompositeScore = decision.FinalScore;
                    if (c.ExtraFields is Dictionary<string, string> mutableFields)
                    {
                        mutableFields["automatic_outcome"] = decision.Outcome;
                        mutableFields["threshold_path"] = decision.ThresholdPath;
                        if (decision.RejectionReasons.Count > 0)
                        {
                            mutableFields["rejection_reasons"] = string.Join(",", decision.RejectionReasons);
                        }
                    }
                }
                else
                {
                    c.CompositeScore = scores.CompositeScore;
                }
            }

            candidates = candidates.OrderByDescending(c => c.CompositeScore).ToList();
        }
        else
        {
            // No local context — composite equals provider confidence
            foreach (var c in candidates)
            {
                c.CompositeScore = c.Confidence;
            }
        }

        return new SearchRetailResult(candidates.Take(Math.Clamp(request.MaxCandidates, 1, 50)).ToList(), request.Query, request.MediaType) { ProviderErrors = providerErrors.ToArray() };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static UniverseCandidate BuildUniverseCandidate(
        QidCandidate candidate,
        MediaType mediaType,
        IReadOnlyDictionary<string, string>? sourceMetadata)
    {
        // Parse year from the Wikidata description heuristically
        // (e.g. "1965 novel by Frank Herbert" → year="1965")
        string? year = ExtractYearFromDescription(candidate.Description);
        var metadata = BuildMediaTypeMetadata(candidate, mediaType, sourceMetadata);
        var author = GetNamedMetadataValue(metadata, "author", "creator")
            ?? ExtractAuthorFromDescription(candidate.Description);

        return new UniverseCandidate
        {
            Qid = candidate.Qid,
            Label = candidate.Label,
            Description = candidate.Description,
            InstanceOf = candidate.InstanceOf ?? ExtractInstanceOfFromDescription(candidate.Description),
            Year = year,
            Author = author,
            CoverUrl = null,
            ResolutionTier = candidate.ResolutionTier,
            Confidence = EstimateConfidence(candidate.ResolutionTier),
            BridgeIds = new Dictionary<string, string>(),
            MediaType = mediaType.ToString(),
            MediaTypeMetadata = metadata,
        };
    }

    private static IReadOnlyDictionary<string, string>? BuildMediaTypeMetadata(
        QidCandidate candidate,
        MediaType mediaType,
        IReadOnlyDictionary<string, string>? sourceMetadata)
    {
        var metadata = sourceMetadata?
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (mediaType is MediaType.Books or MediaType.Audiobooks)
        {
            var author = ExtractAuthorFromDescription(candidate.Description);
            if (!string.IsNullOrWhiteSpace(author))
            {
                metadata.TryAdd("author", author);
            }
        }

        return metadata.Count == 0 ? null : metadata;
    }

    private static string? GetNamedMetadataValue(
        IReadOnlyDictionary<string, string>? metadata,
        params string[] keys)
    {
        if (metadata is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (metadata.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value)
                && !IsExactWikidataQid(value.Trim()))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private async Task<IEnumerable<RetailCandidate>> SearchProviderAsync(
        IExternalMetadataProvider provider,
        string query,
        MediaType mediaType,
        Dictionary<string, Dictionary<string, string>> providerEndpoints,
        int maxCandidates,
        IReadOnlyDictionary<string, string>? searchFields,
        CancellationToken ct,
        System.Collections.Concurrent.ConcurrentBag<string> providerErrors)
    {
        try
        {
            var (language, country) = GetConfiguredLocale();
            // When structured search fields are provided, extract title/author
            // for the provider query template (e.g. "{title} {author}").
            // Falls back to the combined query string when fields are absent.
            string? fieldTitle = null, fieldAuthor = null;
            string? fieldShowName = null, fieldAlbum = null, fieldArtist = null;
            string? fieldDirector = null, fieldComposer = null, fieldGenre = null;
            string? fieldSeries = null, fieldNarrator = null;
            string? fieldSeasonNumber = null, fieldEpisodeNumber = null, fieldTrackNumber = null;
            string? fieldIsbn = null, fieldAsin = null;
            if (searchFields is { Count: > 0 })
            {
                searchFields.TryGetValue("title", out fieldTitle);
                // Also check show_name as a title source
                if (fieldTitle is null)
                {
                    searchFields.TryGetValue("show_name", out fieldTitle);
                }
                if (!searchFields.TryGetValue("author", out fieldAuthor))
                {
                    searchFields.TryGetValue("artist", out fieldAuthor);
                }

                searchFields.TryGetValue("show_name", out fieldShowName);
                searchFields.TryGetValue("album", out fieldAlbum);
                searchFields.TryGetValue("artist", out fieldArtist);
                searchFields.TryGetValue("director", out fieldDirector);
                searchFields.TryGetValue("composer", out fieldComposer);
                searchFields.TryGetValue("genre", out fieldGenre);
                searchFields.TryGetValue("series", out fieldSeries);
                searchFields.TryGetValue("narrator", out fieldNarrator);
                searchFields.TryGetValue("season_number", out fieldSeasonNumber);
                searchFields.TryGetValue("episode_number", out fieldEpisodeNumber);
                searchFields.TryGetValue("track_number", out fieldTrackNumber);
                searchFields.TryGetValue("isbn", out fieldIsbn);
                searchFields.TryGetValue("asin", out fieldAsin);
                // ShowName fallback: series → show_name (matches RetailMatchWorker)
                if (fieldShowName is null)
                {
                    fieldShowName = fieldSeries;
                }
            }

            var providerRequest = new ProviderLookupRequest
            {
                EntityId = Guid.NewGuid(),
                EntityType = EntityType.Work,
                MediaType = mediaType,
                Title = fieldTitle ?? query,
                Author = fieldAuthor,
                ShowName = fieldShowName,
                Album = fieldAlbum,
                Artist = fieldArtist,
                Director = fieldDirector,
                Composer = fieldComposer,
                Genre = fieldGenre,
                Series = fieldSeries,
                Narrator = fieldNarrator,
                SeasonNumber = fieldSeasonNumber,
                EpisodeNumber = fieldEpisodeNumber,
                TrackNumber = fieldTrackNumber,
                Isbn = fieldIsbn,
                Asin = fieldAsin,
                BaseUrl = GetProviderBaseUrl(provider.Name, providerEndpoints),
                Language = language,
                Country = country,
                Hints = searchFields is { Count: > 0 }
                    ? new Dictionary<string, string>(searchFields)
                    : null,
            };

            var results = await provider.SearchAsync(providerRequest, maxCandidates, ct).ConfigureAwait(false);

            return results.Select(r => new RetailCandidate
            {
                ProviderId = provider.ProviderId.ToString(),
                ProviderName = provider.Name,
                ProviderItemId = r.ProviderItemId,
                Title = r.Title,
                Year = r.Year,
                Author = r.Author,
                Description = r.Description,
                CoverUrl = r.ThumbnailUrl,
                Confidence = r.Confidence,
                ExtraFields = r.ExtraFields ?? new Dictionary<string, string>(),
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            providerErrors.Add($"{provider.Name} search could not complete. Please retry.");
            _logger.LogWarning(ex, "Retail search failed for provider '{Provider}', query '{Query}'",
                provider.Name, query);
            return [];
        }
    }

    private async Task<IEnumerable<RetailCandidate>> FetchAutomaticProviderCandidateAsync(
        IExternalMetadataProvider provider,
        string query,
        MediaType mediaType,
        Dictionary<string, Dictionary<string, string>> providerEndpoints,
        IReadOnlyDictionary<string, string>? searchFields,
        CancellationToken ct)
    {
        try
        {
            var fields = searchFields ?? new Dictionary<string, string>();
            var (language, country) = GetConfiguredLocale();
            var title = fields.GetValueOrDefault(MetadataFieldConstants.Title)
                ?? fields.GetValueOrDefault(MetadataFieldConstants.ShowName)
                ?? query;
            var author = fields.GetValueOrDefault(MetadataFieldConstants.Author)
                ?? fields.GetValueOrDefault(MetadataFieldConstants.Artist);
            var request = new ProviderLookupRequest
            {
                EntityId = Guid.NewGuid(),
                EntityType = EntityType.MediaAsset,
                MediaType = mediaType,
                Title = title,
                Author = author,
                Year = fields.GetValueOrDefault(MetadataFieldConstants.Year),
                Narrator = fields.GetValueOrDefault(MetadataFieldConstants.Narrator),
                ShowName = fields.GetValueOrDefault(MetadataFieldConstants.ShowName)
                    ?? fields.GetValueOrDefault(MetadataFieldConstants.Series),
                Album = fields.GetValueOrDefault(MetadataFieldConstants.Album),
                Artist = fields.GetValueOrDefault(MetadataFieldConstants.Artist),
                Composer = fields.GetValueOrDefault(MetadataFieldConstants.Composer),
                Director = fields.GetValueOrDefault(MetadataFieldConstants.Director),
                SeasonNumber = fields.GetValueOrDefault(MetadataFieldConstants.SeasonNumber),
                EpisodeNumber = fields.GetValueOrDefault(MetadataFieldConstants.EpisodeNumber),
                TrackNumber = fields.GetValueOrDefault(MetadataFieldConstants.TrackNumber),
                Series = fields.GetValueOrDefault(MetadataFieldConstants.Series),
                Genre = fields.GetValueOrDefault(MetadataFieldConstants.Genre),
                Isbn = fields.GetValueOrDefault(BridgeIdKeys.Isbn),
                Asin = fields.GetValueOrDefault(BridgeIdKeys.Asin),
                Hints = new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase),
                BaseUrl = GetProviderBaseUrl(provider.Name, providerEndpoints),
                Language = language,
                FileLanguage = fields.GetValueOrDefault(MetadataFieldConstants.Language),
                Country = country,
            };

            var claims = await provider.FetchAsync(request, ct).ConfigureAwait(false);
            if (claims.Count == 0)
            {
                return [];
            }

            var extraFields = claims
                .Where(claim => !string.IsNullOrWhiteSpace(claim.Key) && !string.IsNullOrWhiteSpace(claim.Value))
                .GroupBy(claim => claim.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
            var candidateTitle = extraFields.GetValueOrDefault(MetadataFieldConstants.Title);
            if (string.IsNullOrWhiteSpace(candidateTitle))
            {
                return [];
            }

            return
            [
                new RetailCandidate
                {
                    ProviderId = provider.ProviderId.ToString(),
                    ProviderName = provider.Name,
                    ProviderItemId = extraFields.GetValueOrDefault("provider_item_id"),
                    Title = candidateTitle,
                    Year = extraFields.GetValueOrDefault(MetadataFieldConstants.Year),
                    Author = extraFields.GetValueOrDefault(MetadataFieldConstants.Author)
                        ?? extraFields.GetValueOrDefault(MetadataFieldConstants.Artist),
                    Director = extraFields.GetValueOrDefault(MetadataFieldConstants.Director),
                    Description = extraFields.GetValueOrDefault(MetadataFieldConstants.Description),
                    CoverUrl = extraFields.GetValueOrDefault(MetadataFieldConstants.CoverUrl)
                        ?? extraFields.GetValueOrDefault("cover"),
                    Confidence = claims.Max(claim => claim.Confidence),
                    ExtraFields = extraFields,
                }
            ];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Automatic retail preview failed for provider '{Provider}', query '{Query}'",
                provider.Name,
                query);
            return [];
        }
    }

    private IReadOnlyList<IExternalMetadataProvider> GetRetailProviders(
        MediaType mediaType,
        bool useAutomaticMatching = false)
    {
        var allConfigs = _configLoader.LoadAllProviders();
        var enabled = ProviderExecutionFilter.EnabledProviders(_providers, allConfigs)
            .Where(p =>
            {
                if (ExcludedFromRetail.Contains(p.Name))
                {
                    return false;
                }

                if (!p.CanHandle(mediaType) || !p.CanHandle(EntityType.Work))
                {
                    return false;
                }

                return true;
            })
            .ToList();

        if (!useAutomaticMatching)
        {
            return enabled;
        }

        var pipeline = _configLoader.LoadPipelines().GetPipelineForMediaType(mediaType.ToString());
        var configuredOrder = pipeline.Providers
            .Where(entry => !entry.RequiresIdentity || entry.UseAsIdentityFallback)
            .OrderBy(entry => entry.Rank)
            .Take(Math.Max(1, pipeline.MaxProviderAttempts))
            .Select((entry, index) => (entry.Name, index))
            .ToDictionary(item => item.Name, item => item.index, StringComparer.OrdinalIgnoreCase);

        return enabled
            .Where(provider => configuredOrder.ContainsKey(provider.Name))
            .OrderBy(provider => configuredOrder[provider.Name])
            .ToList();
    }

    private static string GetProviderBaseUrl(
        string providerName,
        Dictionary<string, Dictionary<string, string>> providerEndpoints)
    {
        if (providerEndpoints.TryGetValue(providerName, out var endpoints))
        {
            if (endpoints.TryGetValue(providerName, out var url))
            {
                return url.TrimEnd('/');
            }

            if (endpoints.TryGetValue("api", out var apiUrl))
            {
                return apiUrl.TrimEnd('/');
            }

            if (endpoints.Count > 0)
            {
                return endpoints.Values.First().TrimEnd('/');
            }
        }
        return string.Empty;
    }

    private static bool IsExactWikidataQid(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            value.Trim(),
            @"^Q[1-9]\d*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool ShouldUseCreatorHintForUniverseQuery(MediaType mediaType, bool isExactQidQuery) =>
        !isExactQidQuery && mediaType is MediaType.Books or MediaType.Audiobooks or MediaType.Comics;

    private static double EstimateConfidence(string? tier) => tier switch
    {
        "bridge" => 0.95,
        "structured_sparql" => 0.75,
        "title_search" => 0.55,
        _ => 0.50,
    };

    /// <summary>Heuristic: extract a 4-digit year from a Wikidata description like "1965 novel by...".</summary>
    private static string? ExtractYearFromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(description, @"\b(1[5-9]\d{2}|20[0-2]\d)\b");
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// Heuristic: extract the entity-type label from a Wikidata description like
    /// "1965 novel by Frank Herbert" → "novel". Falls back to the second word if the
    /// first is a year. Returns null if nothing recognisable is found.
    /// </summary>
    private static readonly string[] InstanceOfKeywords =
    [
        "novel", "book", "novella", "short story", "anthology", "literary work", "written work",
        "film", "movie", "animated film", "short film", "documentary", "documentary film",
        "television series", "television film", "web series", "animated series", "miniseries", "TV series",
        "album", "studio album", "compilation album", "live album", "EP", "single", "song", "musical work",
        "comic book", "comic book series", "graphic novel", "manga", "manhwa",

        "video game",
    ];

    private static string? ExtractInstanceOfFromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        // Longest-first match so "comic book series" beats "comic book".
        foreach (var kw in InstanceOfKeywords.OrderByDescending(k => k.Length))
        {
            if (description.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return kw;
            }
        }
        return null;
    }

    /// <summary>Heuristic: extract author from "... by Author Name" pattern.</summary>
    private static string? ExtractAuthorFromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            description, @"\bby\s+([A-Z][a-zA-Z\s\-\.]{2,40}?)(?:\s*[,;(]|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Builds file hint dictionary from a retail search request for use with
    /// <see cref="IRetailMatchScoringService"/>.
    /// </summary>
    private static Dictionary<string, string> BuildFileHints(SearchRetailRequest request)
    {
        var hints = request.FileHints is { Count: > 0 }
            ? new Dictionary<string, string>(request.FileHints, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(request.LocalTitle))
        {
            hints.TryAdd("title", request.LocalTitle);
        }

        if (!string.IsNullOrWhiteSpace(request.LocalAuthor))
        {
            hints.TryAdd("author", request.LocalAuthor);
        }

        if (!string.IsNullOrWhiteSpace(request.LocalYear))
        {
            hints.TryAdd("year", request.LocalYear);
        }

        return hints;
    }

    private static double ComputeRetailSearchStructuralBonus(
        MediaType mediaType,
        IReadOnlyDictionary<string, string> fileHints,
        CandidateExtendedMetadata candidate)
    {
        if (mediaType != MediaType.Comics)
        {
            return 0.0;
        }

        var fileSeries = fileHints.GetValueOrDefault(MetadataFieldConstants.Series);
        var fileIssue = fileHints.GetValueOrDefault("issue_number")
            ?? fileHints.GetValueOrDefault(MetadataFieldConstants.SeriesPosition)
            ?? fileHints.GetValueOrDefault("issue");

        if (string.IsNullOrWhiteSpace(fileSeries)
            || string.IsNullOrWhiteSpace(fileIssue)
            || string.IsNullOrWhiteSpace(candidate.Series)
            || string.IsNullOrWhiteSpace(candidate.IssueNumber))
        {
            return 0.0;
        }

        return AreEquivalentSearchText(fileSeries, candidate.Series)
            && RetailHints.AreEquivalentOrdinals(fileIssue, candidate.IssueNumber)
            ? 0.35
            : 0.0;
    }

    private static bool AreEquivalentSearchText(string left, string right)
        => string.Equals(
            RetailTextSimilarity.NormalizeComparableText(left),
            RetailTextSimilarity.NormalizeComparableText(right),
            StringComparison.Ordinal);

    private (string Language, string Country) GetConfiguredLocale()
    {
        var core = _configLoader.LoadCore();
        var language = string.IsNullOrWhiteSpace(core.Language.Metadata)
            ? "en"
            : core.Language.Metadata.Trim();
        var country = string.IsNullOrWhiteSpace(core.Country)
            ? "US"
            : core.Country.Trim().ToUpperInvariant();
        return (language, country);
    }

    /// <summary>
    /// Maps <see cref="FieldMatchScores"/> (from RetailMatchScoringService) to
    /// <see cref="FieldMatchResult"/> (used by search result display).
    /// </summary>
    private static FieldMatchResult ToFieldMatchResult(FieldMatchScores scores)
    {
        return new FieldMatchResult
        {
            TitleScore = scores.TitleScore,
            AuthorScore = scores.AuthorScore,
            YearScore = scores.YearScore,
            FormatScore = scores.FormatScore,
            CoverScore = scores.CoverArtScore,
            CompositeScore = scores.CompositeScore,
            TitleVerdict = ToVerdict(scores.TitleScore),
            AuthorVerdict = scores.AuthorScore < 0 ? FieldMatchVerdict.NotAvailable : ToVerdict(scores.AuthorScore),
            YearVerdict = scores.YearScore < 0 ? FieldMatchVerdict.NotAvailable : ToVerdict(scores.YearScore),
            FormatVerdict = ToVerdict(scores.FormatScore),
            CoverVerdict = scores.CoverArtScore < 0 ? FieldMatchVerdict.NotAvailable : ToVerdict(scores.CoverArtScore),
        };
    }

    private static FieldMatchVerdict ToVerdict(double score) => score switch
    {
        >= 0.95 => FieldMatchVerdict.Exact,
        >= 0.70 => FieldMatchVerdict.Close,
        _ => FieldMatchVerdict.Mismatch,
    };
}
