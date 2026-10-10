using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Adapters;

public sealed partial class ReconciliationAdapter
{
    internal async Task<IReadOnlyList<ReconciliationResult>> ReconcileAsync(
        string query,
        Dictionary<string, string>? propertyConstraints = null,
        CancellationToken ct = default,
        MediaType mediaType = MediaType.Unknown,
        List<PropertyConstraint>? multiValueConstraints = null,
        string? fileLanguage = null)
    {
        if (_reconciler is null || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var request = BuildManualSearchRequest(
            query, mediaType, fileLanguage,
            propertyConstraints, multiValueConstraints);

        try
        {
            return await _reconciler.ReconcileAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Provider}: ReconcileAsync failed for query '{Query}'", Name, query);
            return [];
        }
    }

    /// <summary>
    /// SINGLE SOURCE OF TRUTH for building a manual Wikidata search
    /// <see cref="ReconciliationRequest"/>. Every code path that performs a
    /// text-based Wikidata reconciliation — manual search via <see cref="SearchAsync"/>,
    /// the bridge worker's text fallback via <see cref="ReconcileBatchAsync"/>,
    /// the multi-language path used by stand-alone callers, and internal
    /// fallbacks inside <c>ResolveBridgeAsync</c>/<c>ResolveMusicAlbumAsync</c> —
    /// MUST go through this builder. Adding a new wrapper that constructs
    /// <c>ReconciliationRequest</c> directly will silently drift from the
    /// canonical settings (cleaners, type filter, multi-value constraints,
    /// hierarchy depth, language list) and re-introduce the bugs that the
    /// unification work was created to prevent. Extend this method instead.
    /// </summary>
    private ReconciliationRequest BuildManualSearchRequest(
        string query,
        MediaType mediaType,
        string? fileLanguage,
        Dictionary<string, string>? propertyConstraints,
        List<PropertyConstraint>? multiValueConstraints,
        int? limitOverride = null,
        IReadOnlyList<string>? typeQidsOverride = null)
    {
        var metadataLanguage = _configLoader?.LoadCore().Language.Metadata ?? "en";

        // Build language list — file language first (when present and different
        // from the metadata language), then metadata language. The library
        // performs concurrent multi-language search and dedupes by QID.
        string? singleLanguage = null;
        List<string>? languages = null;
        var fileLang = NormalizeOptionalLang(fileLanguage);
        var metaLang = NormalizeLang(metadataLanguage);
        if (!string.IsNullOrEmpty(fileLang)
            && !string.Equals(fileLang, metaLang, StringComparison.OrdinalIgnoreCase))
        {
            languages = [fileLang, metaLang];
        }
        else
        {
            singleLanguage = metaLang;
        }

        // Type filter from instance_of_classes config — pre-filters CirrusSearch
        // by media type so a TV episode title can never resolve to a literary work.
        // Callers may override (e.g. ResolveMusicAlbumAsync uses the narrower
        // MusicAlbum class list rather than the broader Music list).
        IReadOnlyList<string>? typeQids = typeQidsOverride;
        if (typeQids is null && mediaType != MediaType.Unknown)
        {
            var mediaTypeKey = mediaType.ToString();
            if (CurrentConfig.InstanceOfClasses.TryGetValue(mediaTypeKey, out var classes) && classes.Count > 0)
            {
                typeQids = classes;
            }
        }

        // Merge multi-value and single-value property constraints — multi-value
        // takes precedence when both target the same P-code.
        List<PropertyConstraint>? allConstraints = null;
        if (multiValueConstraints is { Count: > 0 } || propertyConstraints is { Count: > 0 })
        {
            allConstraints = [];
            if (multiValueConstraints is { Count: > 0 })
            {
                allConstraints.AddRange(multiValueConstraints);
            }

            if (propertyConstraints is { Count: > 0 })
            {
                var multiValuePIds = multiValueConstraints?.Select(c => c.PropertyId).ToHashSet() ?? [];
                allConstraints.AddRange(
                    propertyConstraints
                        .Where(kvp => !multiValuePIds.Contains(kvp.Key))
                        .Select(kvp => new PropertyConstraint(kvp.Key, kvp.Value)));
            }
        }

        return new ReconciliationRequest
        {
            Query = query,
            Limit = limitOverride ?? _config.Reconciliation.MaxCandidates,
            Language = singleLanguage,
            Languages = languages,
            DiacriticInsensitive = true,
            Cleaners = QueryCleaners.All(),
            Types = typeQids,
            TypeHierarchyDepth = 1,
            Properties = allConstraints
        };
    }

    /// <summary>
    /// Reconciles an entity name against Wikidata, searching in multiple languages concurrently.
    /// When <paramref name="fileLanguage"/> differs from the configured metadata language,
    /// uses the library's <c>Languages</c> parameter for concurrent multi-language search
    /// with built-in deduplication.
    /// When <paramref name="mediaType"/> is specified, CirrusSearch pre-filters by
    /// the configured <c>instance_of_classes</c> for that media type.
    /// </summary>
    /// <remarks>
    /// SOURCE OF TRUTH: All manual search requests must flow through
    /// <see cref="BuildManualSearchRequest"/>. Do not construct
    /// <c>ReconciliationRequest</c> instances by hand in new code; extend the
    /// builder instead. Parity is enforced by <c>WikidataParityTests</c>.
    /// </remarks>
    internal async Task<IReadOnlyList<ReconciliationResult>> ReconcileMultiLanguageAsync(
        string query,
        string? fileLanguage,
        Dictionary<string, string>? propertyConstraints = null,
        CancellationToken ct = default,
        MediaType mediaType = MediaType.Unknown)
    {
        if (_reconciler is null || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var request = BuildManualSearchRequest(
            query, mediaType, fileLanguage,
            propertyConstraints, multiValueConstraints: null);

        try
        {
            return await _reconciler.ReconcileAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Provider}: ReconcileMultiLanguageAsync failed for query '{Query}'", Name, query);
            return [];
        }
    }

    /// <summary>
    /// Reconciles multiple entities in parallel using the library's batch method.
    /// </summary>
    /// <param name="requests">List of (QueryId, Query, PropertyConstraints) tuples.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary keyed by QueryId.</returns>
    /// <remarks>
    /// SOURCE OF TRUTH: All manual search requests must flow through
    /// <see cref="BuildManualSearchRequest"/>. Do not construct
    /// <c>ReconciliationRequest</c> instances by hand in new code; extend the
    /// builder instead. Parity is enforced by <c>WikidataParityTests</c>.
    /// </remarks>
    internal async Task<Dictionary<string, IReadOnlyList<ReconciliationResult>>> ReconcileBatchAsync(
        IReadOnlyList<(string QueryId, string Query, Dictionary<string, string>? PropertyConstraints, MediaType MediaType)> requests,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, IReadOnlyList<ReconciliationResult>>(StringComparer.Ordinal);
        if (requests.Count == 0 || _reconciler is null)
        {
            return result;
        }

        // Per-request build via the single source of truth so batch
        // reconciliation can never drift from manual/single reconciliation.
        var libRequests = requests
            .Select(r => BuildManualSearchRequest(
                r.Query, r.MediaType, fileLanguage: null,
                r.PropertyConstraints, multiValueConstraints: null))
            .ToList();

        try
        {
            var batchResults = await _reconciler.ReconcileBatchAsync(libRequests, ct).ConfigureAwait(false);
            for (int i = 0; i < requests.Count && i < batchResults.Count; i++)
            {
                result[requests[i].QueryId] = batchResults[i];
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Provider}: ReconcileBatchAsync failed", Name);
        }

        return result;
    }

    /// <summary>
    /// Extends a set of QIDs with property values via the Data Extension API.
    /// </summary>
    /// <param name="qids">Wikidata Q-identifiers to extend.</param>
    /// <param name="propertyCodes">P-codes to fetch (e.g. ["P50", "P577"]).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>>> ExtendAsync(
        IReadOnlyList<string> qids,
        IReadOnlyList<string> propertyCodes,
        CancellationToken ct = default)
    {
        if (qids.Count == 0 || propertyCodes.Count == 0)
        {
            return new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>>(StringComparer.OrdinalIgnoreCase);
        }

        if (_reconciler is null)
        {
            _logger.LogWarning("{Provider}: WikidataReconciler not available — cannot extend", Name);
            return new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>>(StringComparer.OrdinalIgnoreCase);
        }

        var language = _configLoader?.LoadCore().Language.Metadata ?? "en";

        // Build a cache key from qids + properties + language.
        var cacheInput = $"extend-direct:{language}:{string.Join(",", qids)}:{string.Join(",", propertyCodes)}";
        var cacheKey = BuildCacheKey(cacheInput);

        // Check cache first.
        if (_responseCache is not null)
        {
            var cached = await _responseCache.FindAsync(cacheKey, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                _logger.LogDebug("{Provider}: extend cache HIT", Name);
                var deserialized = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<WikidataClaim>>>>(cached.ResponseJson, JsonOpts);
                if (deserialized is not null)
                {
                    return deserialized.ToDictionary(
                        kvp => kvp.Key,
                        kvp => (IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>)kvp.Value.ToDictionary(
                            p => p.Key,
                            p => (IReadOnlyList<WikidataClaim>)p.Value,
                            StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        try
        {
            var results = await _reconciler.GetPropertiesAsync(
                qids, propertyCodes, language, ct).ConfigureAwait(false);

            // Cache the results.
            if (_responseCache is not null && results.Count > 0)
            {
                var json = JsonSerializer.Serialize(results, JsonOpts);
                var queryHash = ComputeSha256(cacheInput);
                await _responseCache.UpsertAsync(
                    cacheKey, _providerId.ToString(), queryHash,
                    json, null, _config.CacheTtlHours, ct).ConfigureAwait(false);
            }

            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Provider}: GetPropertiesAsync failed for {Count} QIDs",
                Name, qids.Count);
            return new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// Filters reconciliation candidates by media type using P31 (instance_of) lookups.
    /// Walks P279 (subclass_of) up to 3 levels for unknown classes, caching learned mappings.
    /// Candidates with no P31 data that match any expected class are retained.
    /// </summary>
    public async Task<IReadOnlyList<ReconciliationResult>> FilterByMediaTypeAsync(
        IReadOnlyList<ReconciliationResult> candidates,
        MediaType mediaType,
        CancellationToken ct = default,
        string? titleHint = null,
        string? authorHint = null,
        string? isbnHint = null,
        string? yearHint = null)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var mediaTypeKey = mediaType.ToString();
        var config = CurrentConfig;
        if (!config.InstanceOfClasses.TryGetValue(mediaTypeKey, out var expectedClasses)
            || expectedClasses.Count == 0)
        {
            _logger.LogDebug("{Provider}: no instance_of classes configured for {MediaType}, skipping filter",
                Name, mediaTypeKey);
            return candidates;
        }

        var expectedSet = new HashSet<string>(expectedClasses, StringComparer.OrdinalIgnoreCase);

        // Build the exclusion set — entity types that should never match for this media type.
        var excludedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (config.ExcludeClasses.TryGetValue(mediaTypeKey, out var excludedClasses)
            && excludedClasses.Count > 0)
        {
            foreach (var qid in excludedClasses)
            {
                excludedSet.Add(qid);
            }
        }

        var qids = candidates.Select(c => c.Id).ToList();

        // ── Wide-net property fetch ─────────────────────────────────────────
        // Fetch P31 (type), P50 (author), P212 (ISBN-13), P957 (ISBN-10),
        // P629 (edition_or_translation_of) in one batched call. These power the
        // three-step scoring: type filter → property validation → weighted scoring.
        // P629 is used to demote translations/editions in favour of original works.
        var fetchProps = new List<string> { "P31", "P50", "P175", "P86", "P676", "P212", "P957", "P629", "P577" };
        var propsByQid = await ExtendAsync(qids, fetchProps, ct).ConfigureAwait(false);

        // ── Resolve entity labels for person-property references ────────────
        // GetPropertiesAsync may leave EntityLabel null for entity references,
        // storing only QIDs in RawValue. Batch-resolve labels so the author
        // fuzzy-matching in Step 2 can compare readable names ("Queen", not "Q15862").
        var personPropCodes = new[] { "P50", "P175", "P86", "P676" };
        var personQids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, props) in propsByQid)
        {
            foreach (var pCode in personPropCodes)
            {
                if (props.TryGetValue(pCode, out var claims))
                {
                    foreach (var c in claims)
                    {
                        if (string.IsNullOrWhiteSpace(c.Value?.EntityLabel)
                            && c.Value?.RawValue is string raw && raw.StartsWith('Q'))
                        {
                            personQids.Add(raw);
                        }
                    }
                }
            }
        }

        var personLabelMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (personQids.Count > 0 && _reconciler is not null)
        {
            try
            {
                // Labels.GetBatchAsync filters malformed QIDs internally
                // (one bad QID no longer drops the whole batch) and only fetches
                // the label payload — no claims, sitelinks, descriptions.
                var language = _configLoader?.LoadCore().Language.Metadata ?? "en";
                var labels = await _reconciler.Labels
                    .GetBatchAsync(personQids.ToList(), language, withFallbackLanguage: true, ct)
                    .ConfigureAwait(false);
                foreach (var (qid, label) in labels)
                {
                    if (!string.IsNullOrWhiteSpace(label))
                    {
                        personLabelMap[qid] = label;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to resolve person entity labels for {Count} QIDs", personQids.Count);
            }
        }

        // ── Step 1: Type filter (P31) ───────────────────────────────────────
        var typeFiltered = new List<ReconciliationResult>();
        foreach (var candidate in candidates)
        {
            if (!propsByQid.TryGetValue(candidate.Id, out var props)
                || !props.TryGetValue("P31", out var p31Values)
                || p31Values.Count == 0)
            {
                _logger.LogDebug(
                    "{Provider}: candidate {QID} '{Label}' dropped — no P31 data",
                    Name, candidate.Id, candidate.Name);
                continue;
            }

            // Extract QIDs from P31 claims — check both EntityId and RawValue
            // (some Wikidata API responses put entity references in RawValue).
            var instanceOfQids = p31Values
                .Select(c =>
                    c.Value?.EntityId
                    ?? (c.Value?.RawValue is string raw && raw.StartsWith('Q') ? raw : null))
                .Where(qid => qid is not null)
                .Select(qid => qid!)
                .ToList();

            if (excludedSet.Count > 0 && instanceOfQids.Any(qid => excludedSet.Contains(qid)))
            {
                _logger.LogDebug(
                    "{Provider}: candidate {QID} '{Label}' excluded — P31 in exclude_classes",
                    Name, candidate.Id, candidate.Name);
                continue;
            }

            if (instanceOfQids.Any(qid => expectedSet.Contains(qid)))
            {
                typeFiltered.Add(candidate);
            }
            else
            {
                _logger.LogDebug(
                    "{Provider}: candidate {QID} '{Label}' dropped — P31 [{P31}] not in {MediaType} classes",
                    Name, candidate.Id, candidate.Name,
                    string.Join(", ", instanceOfQids), mediaTypeKey);
            }
        }

        // ── Step 2 & 3: Property validation + weighted scoring ──────────────
        // Score each surviving candidate:
        //   +100 (instant match) if ISBN matches
        //   +50  if title fuzzy-matches the candidate label
        //   +30  if author fuzzy-matches the candidate's P50 author
        var scored = new List<(ReconciliationResult Candidate, double Score)>();
        foreach (var candidate in typeFiltered)
        {
            double score = 0.0;
            propsByQid.TryGetValue(candidate.Id, out var cProps);

            // ISBN match (+100 — instant confirmation)
            if (!string.IsNullOrWhiteSpace(isbnHint) && cProps is not null)
            {
                var candidateIsbns = new List<string>();
                if (cProps.TryGetValue("P212", out var p212))
                {
                    candidateIsbns.AddRange(p212.Where(c => c.Value?.RawValue is not null).Select(c => c.Value!.RawValue!));
                }

                if (cProps.TryGetValue("P957", out var p957))
                {
                    candidateIsbns.AddRange(p957.Where(c => c.Value?.RawValue is not null).Select(c => c.Value!.RawValue!));
                }

                var normalizedHint = isbnHint.Replace("-", "").Replace(" ", "");
                if (candidateIsbns.Any(isbn =>
                    string.Equals(isbn.Replace("-", "").Replace(" ", ""),
                        normalizedHint, StringComparison.OrdinalIgnoreCase)))
                {
                    score += 100.0;
                    _logger.LogDebug(
                        "{Provider}: candidate {QID} '{Label}' — ISBN match (+100)",
                        Name, candidate.Id, candidate.Name);
                }
            }

            // Title match (+50 scaled by similarity)
            if (!string.IsNullOrWhiteSpace(titleHint))
            {
                var titleSimilarity = _fuzzy.ComputeTokenSetRatio(titleHint, candidate.Name);
                score += 50.0 * titleSimilarity;
            }

            // Author/performer match (+30 scaled by best P50 or P175 similarity)
            // Supports multi-author files: "Neil Gaiman & Terry Pratchett" is split
            // and each name matched independently against P50 entries. Score = matched/total.
            if (!string.IsNullOrWhiteSpace(authorHint) && cProps is not null)
            {
                // Split file author into individual names
                var fileAuthors = RetailHints.SplitAuthors(authorHint);

                // Collect all Wikidata author/performer/composer labels.
                // Entity references store the QID in RawValue; the resolved
                // human-readable label lives in EntityLabel (populated by
                // ResolveClaimsEntityLabelsAsync). Use EntityLabel first.
                var wikidataAuthors = new List<string>();
                foreach (var pCode in new[] { "P50", "P175", "P86", "P676" })
                {
                    if (cProps.TryGetValue(pCode, out var pValues))
                    {
                        foreach (var claim in pValues)
                        {
                            var label = claim.Value?.EntityLabel;
                            if (string.IsNullOrWhiteSpace(label)
                                && claim.Value?.RawValue is string rawQid
                                && rawQid.StartsWith('Q'))
                            {
                                personLabelMap.TryGetValue(rawQid, out label);
                            }
                            label ??= claim.Value?.RawValue;
                            if (!string.IsNullOrWhiteSpace(label)
                                && !label.StartsWith('Q'))
                            {
                                wikidataAuthors.Add(label);
                            }
                        }
                    }
                }

                double bestAuthorMatch = 0.0;

                if (wikidataAuthors.Count > 0)
                {
                    // Multi-author matching: for each file author, find the best
                    // matching Wikidata author. Proportional scoring.
                    int matched = 0;
                    var usedIndices = new HashSet<int>();
                    foreach (var fa in fileAuthors)
                    {
                        double bestSim = 0.0;
                        int bestIdx = -1;
                        for (int i = 0; i < wikidataAuthors.Count; i++)
                        {
                            if (usedIndices.Contains(i))
                            {
                                continue;
                            }

                            var sim = _fuzzy.ComputeTokenSetRatio(fa, wikidataAuthors[i]);
                            if (sim > bestSim)
                            {
                                bestSim = sim;
                                bestIdx = i;
                            }
                        }
                        if (bestSim >= 0.70 && bestIdx >= 0)
                        {
                            matched++;
                            usedIndices.Add(bestIdx);
                        }
                    }

                    bestAuthorMatch = (double)matched / Math.Max(fileAuthors.Count, wikidataAuthors.Count);

                    // Also try the original full-string comparison (handles single-author case)
                    foreach (var wdAuthor in wikidataAuthors)
                    {
                        var fullStringSim = _fuzzy.ComputeTokenSetRatio(authorHint, wdAuthor);
                        if (fullStringSim > bestAuthorMatch)
                        {
                            bestAuthorMatch = fullStringSim;
                        }
                    }
                }

                score += 30.0 * bestAuthorMatch;

                if (bestAuthorMatch < 0.3)
                {
                    score -= 35.0;
                    _logger.LogDebug(
                        "{Provider}: candidate {QID} '{Label}' — author mismatch penalty (-35, best={Best:F2})",
                        Name, candidate.Id, candidate.Name, bestAuthorMatch);
                }
            }

            if (!string.IsNullOrWhiteSpace(authorHint) && cProps is not null
                && !cProps.ContainsKey("P50") && !cProps.ContainsKey("P175")
                && !cProps.ContainsKey("P86") && !cProps.ContainsKey("P676"))
            {
                score -= 40.0;
                _logger.LogDebug(
                    "{Provider}: candidate {QID} '{Label}' — no author properties penalty (-40)",
                    Name, candidate.Id, candidate.Name);
            }

            if (!string.IsNullOrWhiteSpace(yearHint))
            {
                var hintYear = ParseComparableYear(yearHint);
                var candidateYear = GetCandidateYear(cProps);
                if (hintYear.HasValue && candidateYear.HasValue)
                {
                    var diff = Math.Abs(candidateYear.Value - hintYear.Value);
                    if (diff == 0)
                    {
                        score += mediaType is MediaType.Movies or MediaType.TV ? 24.0 : 14.0;
                    }
                    else if (diff == 1)
                    {
                        score += 6.0;
                    }
                    else if (diff >= 5)
                    {
                        score -= mediaType is MediaType.Movies or MediaType.TV ? 50.0 : 24.0;
                    }
                    else if (diff >= 2)
                    {
                        score -= mediaType is MediaType.Movies or MediaType.TV ? 30.0 : 14.0;
                    }

                    _logger.LogDebug(
                        "{Provider}: candidate {QID} '{Label}' — year hint {HintYear} vs candidate {CandidateYear} (diff={Diff})",
                        Name, candidate.Id, candidate.Name, hintYear, candidateYear, diff);
                }
                else if (hintYear.HasValue && mediaType is MediaType.Movies or MediaType.TV)
                {
                    score -= 10.0;
                    _logger.LogDebug(
                        "{Provider}: candidate {QID} '{Label}' — no year data penalty (-10)",
                        Name, candidate.Id, candidate.Name);
                }
            }

            // Translation/edition penalty (-40 if P629 is present).
            // P629 (edition_or_translation_of) indicates this candidate is a derivative
            // of another work — prefer the original. This breaks ties when the original
            // and its translations both match the query equally.
            if (cProps is not null
                && cProps.TryGetValue("P629", out var p629Values) && p629Values.Count > 0)
            {
                score -= 40.0;
                _logger.LogDebug(
                    "{Provider}: candidate {QID} '{Label}' — translation/edition penalty (-40, P629 present)",
                    Name, candidate.Id, candidate.Name);
            }

            _logger.LogDebug(
                "{Provider}: candidate {QID} '{Label}' — total composite={Score:F1}",
                Name, candidate.Id, candidate.Name, score);

            scored.Add((candidate, score));
        }

        // Rank by composite score (highest first).
        // Persist the composite score into ReconciliationResult.Score so that callers
        // (FetchWorkAsync threshold check, SearchAsync confidence display) use the
        // enriched score rather than the stale API label-only score.
        var maxComposite = scored.Count > 0 ? scored.Max(s => s.Score) : 1.0;
        var normFactor = maxComposite > 0 ? 100.0 / Math.Max(maxComposite, 80.0) : 1.0;

        var result = scored
            .OrderByDescending(s => s.Score)
            .Select(s =>
            {
                var compositeNorm = Math.Min(100.0, s.Score * normFactor);
                // Weighted blend: 85% composite (type-aware), 15% original API score.
                // This ensures type filtering and title/author matching have real influence
                // rather than being overridden by the raw Wikidata label-match score.
                var blended = (compositeNorm * 0.85) + (s.Candidate.Score * 0.15);
                return new ReconciliationResult
                {
                    Id = s.Candidate.Id,
                    Name = s.Candidate.Name,
                    Description = s.Candidate.Description,
                    Score = blended,
                    Match = s.Candidate.Match,
                    Types = s.Candidate.Types,
                };
            })
            .ToList();

        if (result.Count > 0)
        {
            var topEntry = scored.OrderByDescending(s => s.Score).First();
            _logger.LogDebug(
                "{Provider}: Scoring top={QID} '{Label}' (composite {Composite:F1}, blended {Blended:F1}) — " +
                "kept {Kept}/{Total} candidates for {MediaType}",
                Name, topEntry.Candidate.Id, topEntry.Candidate.Name,
                topEntry.Score, result[0].Score, result.Count, candidates.Count, mediaTypeKey);
        }
        else
        {
            _logger.LogDebug("{Provider}: FilterByMediaType({MediaType}) kept 0/{Total} candidates",
                Name, mediaTypeKey, candidates.Count);
        }

        return result;
    }

}
