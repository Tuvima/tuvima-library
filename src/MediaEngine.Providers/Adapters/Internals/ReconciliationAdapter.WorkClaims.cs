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
    private async Task<IReadOnlyList<ProviderClaim>> FetchWorkAsync(
        ProviderLookupRequest request,
        CancellationToken ct)
    {
        // Use PreResolvedQid if provided — skip reconciliation entirely.
        var qid = request.PreResolvedQid;
        string? reconciliationLabel = null;
        var metadataLanguage = _configLoader?.LoadCore().Language.Metadata ?? request.Language;
        var displayLanguage = ResolveDisplayLanguage(metadataLanguage, request.FileLanguage);

        if (string.IsNullOrWhiteSpace(qid))
        {
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return [];
            }

            // The library's Cleaners = QueryCleaners.All() and DiacriticInsensitive = true
            // handle title cleaning and diacritics normalization automatically.
            // Additionally strip classic subtitle patterns ("; or," convention common in
            // 18th/19th-century literature) that confuse CirrusSearch — e.g.
            // "Frankenstein; or, The Modern Prometheus" → "Frankenstein".
            var searchTitle = Regex.Replace(request.Title, @"\s*;\s+or,\s+.*$", string.Empty,
                RegexOptions.IgnoreCase).Trim();
            if (string.IsNullOrWhiteSpace(searchTitle))
            {
                searchTitle = request.Title;
            }

            // For books and audiobooks, strip edition markers and genre-subtitle suffixes
            // that confuse CirrusSearch: "(Unabridged)", ": A Novel", "- A Memoir", etc.
            if (request.MediaType is MediaType.Audiobooks or MediaType.Books)
            {
                var cleaned = CleanAudiobookTitle(searchTitle);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    searchTitle = cleaned;
                }
            }

            // Guard: do not attempt reconciliation when media type is unknown.
            // Unknown media type means P31 filtering cannot be applied, leading to
            // misidentification (e.g. novels matching video games or sculptures).
            // The item will be routed to the review queue by the caller.
            if (request.MediaType == MediaType.Unknown)
            {
                _logger.LogInformation(
                    "{Provider}: skipping reconciliation for '{Title}' — media type is Unknown, item requires manual classification",
                    Name, request.Title);
                return [];
            }

            // Build author property constraint for better reconciliation scoring.
            // Multi-author files pass all authors via PropertyConstraint.Values for
            // proportional matching (v0.10.0 feature).
            Dictionary<string, string>? constraints = null;
            List<PropertyConstraint>? multiValueConstraints = null;
            if (!string.IsNullOrWhiteSpace(request.Author))
            {
                var authors = RetailHints.SplitAuthors(request.Author);
                if (authors.Count > 1)
                {
                    multiValueConstraints =
                    [
                        new PropertyConstraint { PropertyId = "P50", Values = authors }
                    ];
                }
                else
                {
                    constraints = new Dictionary<string, string> { ["P50"] = request.Author };
                }
            }

            var candidates = await ReconcileAsync(
                searchTitle,
                constraints,
                ct,
                request.MediaType,
                multiValueConstraints,
                request.FileLanguage).ConfigureAwait(false);

            if (candidates.Count == 0)
            {
                _logger.LogDebug("{Provider}: no reconciliation candidates for '{Title}'",
                    Name, request.Title);
                return [];
            }

            // Always apply P31 type filtering (Unknown media type already returned above).
            var filtered = await FilterByMediaTypeAsync(
                    candidates, request.MediaType, ct,
                    request.Title, request.Author, request.Isbn, request.Year)
                    .ConfigureAwait(false);
            if (filtered.Count == 0)
            {
                // Type-constrained retry: append media type hint to query so that
                // Wikidata surfaces the correct entity type (e.g. "Shogun television series"
                // finds Q56276181 instead of the novel Q131767 dominating the plain search).
                var typeHint = request.MediaType switch
                {
                    MediaType.Books => "novel book",
                    MediaType.Audiobooks => "audiobook",
                    MediaType.Movies => "film movie",
                    MediaType.TV => "television series",
                    MediaType.Music => "song music",
                    MediaType.Comics => "comic manga",
                    _ => null
                };

                if (typeHint is not null)
                {
                    _logger.LogDebug(
                        "{Provider}: P31 filter eliminated all {Count} candidates for '{Title}' ({MediaType}), retrying with type hint",
                        Name, candidates.Count, request.Title, request.MediaType);

                    var retryQuery = $"{searchTitle} {typeHint}";
                    var retryCandidates = await ReconcileAsync(
                        retryQuery,
                        null,
                        ct,
                        request.MediaType,
                        fileLanguage: request.FileLanguage).ConfigureAwait(false);

                    if (retryCandidates.Count > 0)
                    {
                        filtered = await FilterByMediaTypeAsync(
                            retryCandidates, request.MediaType, ct,
                            request.Title, request.Author, request.Isbn, request.Year)
                            .ConfigureAwait(false);
                    }
                }

                if (filtered.Count == 0)
                {
                    _logger.LogInformation(
                        "{Provider}: no candidates survived P31 filter for '{Title}' ({MediaType}), sending to review",
                        Name, request.Title, request.MediaType);
                    return [];
                }
            }

            // Accept the top candidate if it meets the auto-accept threshold.
            var top = filtered[0];
            if (top.Score < _config.Reconciliation.ReviewThreshold)
            {
                _logger.LogDebug(
                    "{Provider}: top candidate '{Label}' ({QID}) score {Score} below review threshold",
                    Name, top.Name, top.Id, top.Score);
                return [];
            }

            // Wikidata Reconciliation API is trusted as the sole identity authority.
            // Its matching engine handles aliases, alternate spellings, and language
            // variants (e.g. "1984" → "Nineteen Eighty-Four") internally.
            // A score >= review_threshold is sufficient to accept the candidate.

            qid = top.Id;
            var matchedLabel = top.MatchedLabel ?? top.Name;
            reconciliationLabel = await FetchDisplayLabelAsync(qid, displayLanguage, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(reconciliationLabel)
                && IsSameLanguage(request.FileLanguage, displayLanguage))
            {
                reconciliationLabel = matchedLabel;
            }
        }
        else
        {
            // Manual QID selection: fetch the Wikidata label to use as reconciliation title.
            // Without this, the title claim at ReconciliationTitle confidence (0.98) is never emitted,
            // and the title falls through to Data Extension at lower confidence (0.90).
            // Labels.GetAsync replaces a GetPropertiesAsync(qid, [$"L{lang}"]) call with
            // a single label-only fetch. Avoid arbitrary language fallback for
            // display metadata; an English library should not surface a
            // foreign-language label just because English is missing.
            try
            {
                var label = await FetchDisplayLabelAsync(qid, displayLanguage, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(label))
                {
                    reconciliationLabel = label;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "{Provider}: Failed to fetch label for pre-resolved QID {Qid}", Name, qid);
            }
        }

        // ── Step 2 & 3: Audiobook Edition Pivot ──────────────────────────────────
        // For audiobooks, the master work QID (e.g. Dune = Q190192) does not carry
        // an audiobook ISBN — only its audiobook edition item (P747 + P31 filter) does.
        // Pivot to the edition QID so that Data Extension returns the audiobook-specific
        // P212 (ISBN-13) and other edition-level bridge IDs (P5749 / ASIN, P6395 / Apple Books ID).
        string masterWorkQid = qid; // preserve the master work QID for claims
        string? audiobookEditionQid = null;

        if (request.MediaType == MediaType.Audiobooks && _reconciler is not null)
        {
            // Walk P747 (has_edition_or_translation) from the master work to find
            // audiobook editions, then rank by narrator match when a hint is provided.
            // Previously a standalone ResolveAudiobookEditionQidAsync helper; inlined
            // in G2 of the slimdown follow-up because the caller is single-use and
            // Stage 2 already ran upstream for the master work QID.
            var narratorHint = request.Narrator;

            try
            {
                var audiobookClasses = GetAudiobookEditionClasses();

                var pivotLanguage = _configLoader?.LoadCore().Language.Metadata ?? "en";
                var editions = await _reconciler.Editions
                    .GetEditionsAsync(qid, audiobookClasses, pivotLanguage, ct)
                    .ConfigureAwait(false);

                if (editions.Count == 0)
                {
                    _logger.LogDebug(
                        "{Provider}: audiobook 3-step pivot — no edition found for master work {MasterQID}; " +
                        "falling back to master work for Data Extension",
                        Name, qid);
                }
                else if (!string.IsNullOrWhiteSpace(narratorHint) && editions.Count > 1)
                {
                    // Rank by narrator fuzzy match on P175 (performer) of each edition.
                    var ranked = editions
                        .Select(e => (Edition: e, Score: _fuzzy.ComputeTokenSetRatio(narratorHint, GetEditionNarrator(e) ?? "")))
                        .OrderByDescending(x => x.Score)
                        .First();

                    audiobookEditionQid = ranked.Edition.EntityId;
                    _logger.LogInformation(
                        "{Provider}: audiobook 3-step pivot — master work {MasterQID} → edition {EditionQID} " +
                        "(narrator hint: '{Narrator}', {Count} candidates ranked)",
                        Name, qid, audiobookEditionQid, narratorHint, editions.Count);
                }
                else
                {
                    audiobookEditionQid = editions[0].EntityId;
                    _logger.LogInformation(
                        "{Provider}: audiobook 3-step pivot — master work {MasterQID} → edition {EditionQID}; " +
                        "Data Extension will target the edition for audiobook-specific bridge IDs",
                        Name, qid, audiobookEditionQid);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "{Provider}: audiobook edition pivot failed for master work {QID}",
                    Name, qid);
            }
        }

        // Extend the resolved QID with work properties.
        var workProps = _config.DataExtension.WorkProperties;
        var language = displayLanguage;

        var claims = new List<ProviderClaim>
        {
            // Always emit the master work QID as the canonical wikidata_qid.
            // This ensures Collection grouping is based on the creative work, not the edition.
            new(BridgeIdKeys.WikidataQid, masterWorkQid, 1.0)
        };

        // When we pivoted to an audiobook edition, also emit the edition QID as a separate
        // claim so other parts of the pipeline can reference it (e.g. for cover art lookup).
        if (audiobookEditionQid is not null)
        {
            claims.Add(new ProviderClaim("audiobook_edition_qid", audiobookEditionQid, 1.0));
        }

        // Emit the reconciliation match label as the display title claim. Source-language
        // labels are retained separately as original_title when they differ from the
        // configured metadata language.
        // A book's name from Wikidata only replaces the title from the store when it is the same title in the
        // same language; a translation or an unrelated name never overwrites what the file and store agree on.
        if (!string.IsNullOrWhiteSpace(reconciliationLabel)
            && (!IsBookLike(request.MediaType) || TitleAgreesWithFile(request.Title, [], reconciliationLabel)))
        {
            claims.Add(new ProviderClaim(MetadataFieldConstants.Title, reconciliationLabel, ClaimConfidence.ReconciliationTitle));
        }

        // extProps holds the master work extension properties — used by pen name detection and
        // edition bridge ID resolution below. Set inside both branches.
        IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>? extProps = null;

        if (audiobookEditionQid is not null)
        {
            // ── Dual Data Extension for audiobooks ──
            // Audiobook editions on Wikidata often lack P50 (author) and P577 (year) —
            // those live on the master work. Run TWO calls:
            // 1. Master work QID → core properties (author, year, title, genre, series)
            // 2. Edition QID → edition-specific properties (performer, ASIN, duration, ISBN)

            // Master work: core properties + language labels
            var masterProps = workProps.Core.ToList();
            masterProps.Add($"L{language}");
            masterProps.Add($"D{language}");

            var masterExtensions = await ExtendAsync([masterWorkQid], masterProps, ct).ConfigureAwait(false);
            masterExtensions.TryGetValue(masterWorkQid, out extProps);
            if (extProps is not null)
            {
                claims.AddRange(ExtensionToClaims(masterWorkQid, extProps, _config.DataExtension.PropertyLabels, isWork: true, castMemberLimit: _config.Reconciliation.CastMemberLimit, metadataLanguage: language));
            }

            // Edition: edition-specific properties + bridges
            var editionProps = (_config.DataExtension.AudiobookEditionProperties ?? [])
                .Concat(workProps.Bridges)
                .Concat(workProps.Editions)
                .Distinct()
                .ToList();

            if (editionProps.Count > 0)
            {
                var editionExtensions = await ExtendAsync([audiobookEditionQid], editionProps, ct).ConfigureAwait(false);
                if (editionExtensions.TryGetValue(audiobookEditionQid, out var editionEntityProps))
                {
                    claims.AddRange(ExtensionToClaims(audiobookEditionQid, editionEntityProps, _config.DataExtension.PropertyLabels, isWork: true, castMemberLimit: _config.Reconciliation.CastMemberLimit, metadataLanguage: language, editionScopedDates: true));
                }
            }

            _logger.LogDebug(
                "{Provider}: dual Data Extension for audiobook — master {MasterQID} ({MasterCount} props) + " +
                "edition {EditionQID} ({EditionCount} props)",
                Name, masterWorkQid, masterProps.Count, audiobookEditionQid, editionProps.Count);
        }
        else
        {
            // ── Standard single Data Extension ──
            var allProps = workProps.Core
                .Concat(workProps.Bridges)
                .Concat(workProps.Editions)
                .Distinct()
                .ToList();

            allProps.Add($"L{language}");
            allProps.Add($"D{language}");

            if (allProps.Count == 0)
            {
                return [new ProviderClaim(BridgeIdKeys.WikidataQid, masterWorkQid, 1.0)];
            }

            _logger.LogInformation(
                "{Provider}: Data Extension for {QID} — requesting {Count} properties: [{Props}]",
                Name, qid, allProps.Count, string.Join(", ", allProps));

            var extensions = await ExtendAsync([qid], allProps, ct).ConfigureAwait(false);
            extensions.TryGetValue(qid, out extProps);

            if (extProps is not null)
            {
                _logger.LogInformation(
                    "{Provider}: Data Extension returned {PropCount} properties for {QID}: [{Keys}]",
                    Name, extProps.Count, qid,
                    string.Join(", ", extProps.Keys));
                claims.AddRange(ExtensionToClaims(qid, extProps, _config.DataExtension.PropertyLabels, isWork: true, castMemberLimit: _config.Reconciliation.CastMemberLimit, metadataLanguage: language));
            }
            else
            {
                _logger.LogWarning(
                    "{Provider}: Data Extension returned NO properties for {QID} (extensions had {Count} entities)",
                    Name, qid, extensions.Count);
            }
        }

        // Resolve award categories to their declared parent program using the
        // generic Wikidata hierarchy. No family is guessed when P361 is absent.
        await AppendAwardFamilyClaimsAsync(claims, ct).ConfigureAwait(false);

        // Author pseudonym detection. The Tuvima.Wikidata author resolver handles
        // solo pen names, enumerated pseudonyms, and collective pseudonyms.
        if (!string.IsNullOrWhiteSpace(request.Author) && _reconciler is not null)
        {
            try
            {
                var authorResolution = await _reconciler.Authors.ResolveAsync(
                    new AuthorResolutionRequest
                    {
                        RawAuthorString = request.Author,
                        WorkQidHint = masterWorkQid,
                        Language = language,
                        DetectPseudonyms = true,
                    }, ct).ConfigureAwait(false);

                claims.AddRange(BuildResolvedAuthorPseudonymClaims(authorResolution));

                foreach (var resolved in authorResolution.Authors)
                {
                    if (string.IsNullOrWhiteSpace(resolved.Qid))
                    {
                        continue;
                    }

                    // Pattern 1 — solo pen name resolved via reverse P742.
                    // Example: "Richard Bachman" → resolved.RealNameQid is
                    // Stephen King's QID (Q39829), and resolved.Qid is set to
                    // the same value (no separate entity for the pen name).
                    if (!string.IsNullOrWhiteSpace(resolved.RealNameQid))
                    {
                        _logger.LogInformation(
                            "{Provider}: Pattern 1 pen name — '{Pseudonym}' → real author QID {RealQid}",
                            Name, resolved.OriginalName, resolved.RealNameQid);
                    }

                    // Pattern 2 — author has P742 (pseudonym) string claims.
                    // Example: "Stephen King" → resolved.Pseudonyms = ["Richard Bachman", ...].
                    if (resolved.Pseudonyms is { Count: > 0 })
                    {
                        _logger.LogInformation(
                            "{Provider}: Pattern 2 P742 enumeration — '{Author}' uses pen name(s): {PenNames}",
                            Name, resolved.CanonicalName ?? resolved.OriginalName,
                            string.Join(", ", resolved.Pseudonyms));
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "{Provider}: Authors.ResolveAsync pseudonym augmentation failed for '{Author}'",
                    Name, request.Author);
            }
        }

        // ── Pen name preservation via embedded-author mismatch ────────────────
        // Safety net for when P742 data is missing or the pen name detection
        // block above could not resolve a shared pen name. If the request carries
        // an embedded author name (from file metadata / local_filesystem provider)
        // that does NOT fuzzy-match any of the "author" claims emitted so far from
        // Wikidata P50, it is very likely a pen name situation — the file was
        // credited to the pen name but Wikidata lists the real people.
        //
        // In that case we:
        //   1. Re-key the Wikidata P50 real-name "author" claims as "author_real_name"
        //      so they are available for person enrichment but do NOT compete with the
        //      canonical author field in the priority cascade.
        //   2. Emit the embedded author name at Wikidata authority confidence (0.95)
        //      so the credited pen name wins as the canonical author.
        if (!string.IsNullOrWhiteSpace(request.Author) && extProps is not null
            && extProps.TryGetValue("P50", out var p50AuthorRefs)
            && p50AuthorRefs.Count > 0)
        {
            // Collect the author labels that ExtensionToClaims already emitted.
            var wikiAuthorClaims = claims
                .Where(c => string.Equals(c.Key, MetadataFieldConstants.Author, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (wikiAuthorClaims.Count > 0)
            {
                // Check whether the embedded author matches ANY of the Wikidata author labels.
                var embeddedAuthor = request.Author;

                // Cross-script detection: if the embedded author and the Wikidata
                // labels use fundamentally different scripts (e.g. CJK vs Latin),
                // this is a language/script mismatch — NOT a pen name situation.
                // The Wikidata English label should win through Tier A normally.
                bool isCrossScriptMismatch = wikiAuthorClaims.Count > 0
                    && ContainsNonLatinScript(embeddedAuthor)
                       != ContainsNonLatinScript(wikiAuthorClaims[0].Value);

                bool embeddedMatchesAnyWikiAuthor = isCrossScriptMismatch
                    || wikiAuthorClaims
                        .Any(c => _fuzzy.ComputeTokenSetRatio(embeddedAuthor, c.Value) >= 0.80);

                if (!embeddedMatchesAnyWikiAuthor)
                {
                    // Check if the pen name detection block already added a pen name author
                    // at confidence 0.95 — if so, no further action is needed.
                    bool penNameAlreadyEmitted = wikiAuthorClaims
                        .Any(c => string.Equals(c.Value, embeddedAuthor, StringComparison.OrdinalIgnoreCase)
                                  || _fuzzy.ComputeTokenSetRatio(embeddedAuthor, c.Value) >= 0.90);

                    if (!penNameAlreadyEmitted)
                    {
                        // Re-key the existing Wikidata P50 real-name "author" and "author_qid"
                        // claims so they don't compete in the canonical field elections.
                        for (int i = 0; i < claims.Count; i++)
                        {
                            if (string.Equals(claims[i].Key, MetadataFieldConstants.Author, StringComparison.OrdinalIgnoreCase))
                            {
                                claims[i] = new ProviderClaim("author_real_name", claims[i].Value, claims[i].Confidence);
                            }
                            else if (string.Equals(claims[i].Key, "author_qid", StringComparison.OrdinalIgnoreCase))
                            {
                                claims[i] = new ProviderClaim("author_real_name_qid", claims[i].Value, claims[i].Confidence);
                            }
                        }

                        // Emit the embedded (credited) author name at high confidence so it
                        // wins as the canonical author value in the priority cascade.
                        claims.Add(new ProviderClaim(MetadataFieldConstants.Author, embeddedAuthor, ClaimConfidence.EmbeddedAuthor));

                        // Resolve the pen name's QID via Reconciliation lookup so person
                        // enrichment creates a Person for the pen name, not the real authors.
                        try
                        {
                            var penNameCandidates = await ReconcileAsync(embeddedAuthor, null, ct).ConfigureAwait(false);
                            var bestMatch = penNameCandidates
                                .Where(c => c.Match || c.Score >= 80)
                                .OrderByDescending(c => c.Score)
                                .FirstOrDefault();

                            if (bestMatch is not null)
                            {
                                claims.Add(new ProviderClaim("author_qid", $"{bestMatch.Id}::{embeddedAuthor}", ClaimConfidence.EmbeddedAuthor));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug("{Provider}: pen name QID lookup failed for \"{PenName}\": {Message}",
                                Name, embeddedAuthor, ex.Message);
                        }

                        _logger.LogInformation(
                            "{Provider}: embedded author \"{EmbeddedAuthor}\" does not match Wikidata P50 " +
                            "authors for QID {QID} — treating as pen name. Real author claims re-keyed to " +
                            "\"author_real_name\"/\"author_real_name_qid\"; credited pen name emitted as canonical author.",
                            Name, embeddedAuthor, masterWorkQid);
                    }
                }
            }
        }

        // ── Edition bridge ID resolution ─────────────────────────────────────
        // Wikidata stores ISBNs and other bridge IDs on edition items (P747),
        // not on the work itself. If key bridge IDs are still missing after
        // the work/edition fetch, look them up via P747 on the master work.
        //
        // When the audiobook 3-step pivot has already targeted an edition QID,
        // the Data Extension call above directly targeted that edition and should
        // already have returned its bridge IDs (P212, P5749 etc.). In that case,
        // most or all of these properties will already be in `claims` and this block
        // will find nothing missing. It still runs as a safety net for any gaps.
        if (extProps is not null)
        {
            var editionBridgeProps = new[] { "P212", "P957", "P5749", "P6395", "P2969", "P648" }
                .Where(p => _config.DataExtension.PropertyLabels.ContainsKey(p))
                .ToList();

            // When we pivoted to an audiobook edition, extProps is from that edition — it won't
            // have P747 pointing to further sub-editions. For the standard bridge fallback,
            // we need P747 from the master work. Fetch it separately in that case.
            IReadOnlyDictionary<string, IReadOnlyList<WikidataClaim>>? editionSourceProps = extProps;
            if (audiobookEditionQid is not null)
            {
                try
                {
                    var masterExtensions = await ExtendAsync([masterWorkQid], ["P747"], ct).ConfigureAwait(false);
                    if (masterExtensions.TryGetValue(masterWorkQid, out var masterProps2))
                    {
                        editionSourceProps = masterProps2;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("{Provider}: master work P747 fetch failed for {QID}: {Message}",
                        Name, masterWorkQid, ex.Message);
                }
            }

            if (editionBridgeProps.Count > 0
                && editionSourceProps is not null
                && editionSourceProps.TryGetValue("P747", out var editionRefs)
                && editionRefs.Count > 0)
            {
                // Determine which bridge IDs are still missing from the work/edition-level fetch.
                var emittedKeys = new HashSet<string>(
                    claims.Select(c => c.Key), StringComparer.OrdinalIgnoreCase);

                var missingProps = editionBridgeProps
                    .Where(p => !emittedKeys.Contains(_config.DataExtension.PropertyLabels[p]))
                    .ToList();

                if (missingProps.Count > 0)
                {
                    var editionQids = editionRefs
                        .Where(c => c.Value?.EntityId is not null)
                        .Select(c => c.Value!.EntityId!)
                        .Distinct()
                        .Take(10)
                        .ToList();

                    if (editionQids.Count > 0)
                    {
                        try
                        {
                            // Include P31 in the request to enable media-type filtering.
                            var propsWithP31 = missingProps.Contains("P31")
                                ? missingProps
                                : [.. missingProps, "P31"];

                            var editionDataMap = await ExtendAsync(editionQids, propsWithP31, ct)
                                .ConfigureAwait(false);

                            // Filter editions by media type: audiobooks get audiobook-class
                            // editions only, books get non-audiobook editions.
                            var filteredEditions = FilterEditionsByMediaType(editionDataMap, request.MediaType);

                            foreach (var propCode in missingProps)
                            {
                                var claimKey = _config.DataExtension.PropertyLabels[propCode];

                                foreach (var (_, edProps2) in filteredEditions)
                                {
                                    if (!edProps2.TryGetValue(propCode, out var vals)
                                        || vals.Count == 0)
                                    {
                                        continue;
                                    }

                                    var firstVal = vals.FirstOrDefault();
                                    if (firstVal is null)
                                    {
                                        continue;
                                    }

                                    var strVal = firstVal.Value?.RawValue ?? firstVal.Value?.EntityId;
                                    if (!string.IsNullOrWhiteSpace(strVal))
                                    {
                                        // Normalize bridge ID values for clean storage.
                                        var normalized = IdentifierNormalizationService.NormalizeRaw(propCode, strVal);
                                        if (!string.IsNullOrWhiteSpace(normalized))
                                        {
                                            claims.Add(new ProviderClaim(claimKey, normalized, ClaimConfidence.WikidataProperty));
                                            break;
                                        }
                                    }
                                }
                            }

                            _logger.LogDebug("{Provider}: edition bridge resolution added bridge IDs for {QID}",
                                Name, masterWorkQid);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug("{Provider}: edition bridge ID resolution failed for {QID}: {Message}",
                                Name, masterWorkQid, ex.Message);
                        }
                    }
                }
            }
        }

        _logger.LogInformation(
            "{Provider}: fetched {Count} work claims for {QID} (audiobook edition pivot: {Pivoted})",
            Name, claims.Count, masterWorkQid, audiobookEditionQid is not null);

        // Wikipedia description.
        // Fetch a rich Wikipedia description for this work using the resolved QID.
        // Failures never block; an empty list is returned and execution continues.
        var wikiWorkClaims = await FetchWikipediaDescriptionAsync(masterWorkQid, language, ct)
            .ConfigureAwait(false);
        claims.AddRange(wikiWorkClaims);

        // Original title for foreign-language files.
        // When the file's detected language differs from the configured metadata
        // language, fetch the Wikidata entity label in the file's language and
        // emit it as "original_title" only. The display title should continue to
        // use the configured metadata language.
        if (!string.IsNullOrEmpty(request.FileLanguage) && _reconciler is not null)
        {
            var fileLang = NormalizeOptionalLang(request.FileLanguage);
            var metaLang = NormalizeLang(metadataLanguage);

            if (!string.IsNullOrWhiteSpace(fileLang)
                && !string.Equals(fileLang, metaLang, StringComparison.OrdinalIgnoreCase))
            {
                // Pure label fetch in the file's native language.
                // Labels.GetAsync replaces a full GetEntitiesAsync call (which
                // also fetches sitelinks, descriptions, claims) with a single
                // label-only payload. withFallbackLanguage: false because we
                // ONLY want the original-language title; falling back to
                // English would defeat the purpose of original_title.
                try
                {
                    var fileLangLabel = await _reconciler.Labels
                        .GetAsync(masterWorkQid, fileLang, withFallbackLanguage: false, ct)
                        .ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(fileLangLabel))
                    {
                        claims.Add(new ProviderClaim(MetadataFieldConstants.OriginalTitle, fileLangLabel, ClaimConfidence.OriginalTitle));
                        if (!string.IsNullOrWhiteSpace(reconciliationLabel)
                            && !string.Equals(reconciliationLabel, fileLangLabel, StringComparison.OrdinalIgnoreCase))
                        {
                            claims.Add(new ProviderClaim("alternate_title", reconciliationLabel, ClaimConfidence.AlternateTitle));
                        }
                        _logger.LogDebug(
                            "{Provider}: original_title '{OriginalTitle}' emitted for {QID} in file language '{Lang}'",
                            Name, fileLangLabel, masterWorkQid, fileLang);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex,
                        "{Provider}: failed to fetch original_title for {QID} in language '{Lang}'",
                        Name, masterWorkQid, fileLang);
                }
            }
        }

        // ── Wikidata aliases as alternate_title claims ────────────────────────
        // Wikidata entities carry aliases — common alternate names for the work
        // (e.g. "Sen to Chihiro no Kamikakushi" is an alias for "Spirited Away",
        // "1984" is an alias for "Nineteen Eighty-Four"). Emitting them as
        // alternate_title claims populates the FTS5 search index so users can find
        // works by any of their known names, including romanized CJK titles.
        //
        // The entity is fetched in the metadata language so aliases reflect the
        // configured display language. Each alias is emitted as a separate claim
        // at confidence 0.85 — lower than the primary title (0.98) so it does not
        // compete as the canonical title but is still indexed for search.
        //
        // Aliases already equal to an emitted title or original_title are skipped
        // to avoid redundant storage.
        if (_reconciler is not null)
        {
            try
            {
                var aliasEntities = await _reconciler
                    .GetEntitiesAsync([masterWorkQid], language, ct)
                    .ConfigureAwait(false);

                if (aliasEntities.TryGetValue(masterWorkQid, out var aliasEntity)
                    && aliasEntity.Aliases is { Count: > 0 })
                {
                    // Collect values already emitted as title or original_title to avoid duplicates.
                    var emittedTitles = claims
                        .Where(c => string.Equals(c.Key, MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(c.Key, MetadataFieldConstants.OriginalTitle, StringComparison.OrdinalIgnoreCase))
                        .Select(c => c.Value)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var aliasesEmitted = 0;
                    foreach (var alias in aliasEntity.Aliases)
                    {
                        if (string.IsNullOrWhiteSpace(alias))
                        {
                            continue;
                        }

                        if (emittedTitles.Contains(alias))
                        {
                            continue;
                        }

                        claims.Add(new ProviderClaim("alternate_title", alias, ClaimConfidence.AlternateTitle));
                        aliasesEmitted++;
                    }

                    if (aliasesEmitted > 0)
                    {
                        _logger.LogDebug(
                            "{Provider}: emitted {Count} alias(es) as alternate_title for {QID}",
                            Name, aliasesEmitted, masterWorkQid);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex,
                    "{Provider}: failed to fetch aliases for {QID}",
                    Name, masterWorkQid);
            }
        }

        // ── Child entity discovery ────────────────────────────────────────────
        // After Stage 2 resolves a QID for a TV show, music album, or comic series,
        // discover child entities (episodes, tracks, issues) from Wikidata and store
        // them as claims on the parent entity. This enables the Dashboard to show
        // episode/track/issue listings without additional API calls.
        // Music is left out on purpose: Wikidata only needs to know the artist and the album to
        // connect things, and the album's track list comes from the retail provider's manifest.
        if (_reconciler is not null
            && WalksChildEntities(request.MediaType))
        {
            try
            {
                var language2 = _configLoader?.LoadCore().Language.Metadata ?? "en";
                claims.AddRange(
                    await DiscoverChildEntitiesAsync(masterWorkQid, request.MediaType, language2, ct)
                        .ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "{Provider}: child entity discovery failed for {QID} ({MediaType}) — skipping",
                    Name, masterWorkQid, request.MediaType);
            }
        }

        return claims;
    }

    // ── Private: DiscoverChildEntitiesAsync ──────────────────────────────────

    private const int MaxChildEntities = 500;
    private const int MaxTvSeasons = 20;

    // ─────────────────────────────────────────────────────────────────────────
    // Tuvima.Wikidata child discovery facade.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Discovers child entities (TV episodes, music tracks, comic issues) for a parent QID
    /// and returns them as <see cref="ProviderClaim"/> entries. The count claims and a
    /// serialized JSON blob are stored using the existing metadata_claims system.
    /// Wrapped in try/catch by the caller — exceptions here never fail the main pipeline.
    /// <para>
    /// Delegates traversal to <c>_reconciler.Children.GetChildEntitiesAsync(ChildEntityRequest)</c>,
    /// which handles the season-to-episode walk for TV, track ordering for Music,
    /// and reverse traversal for Comics. The library owns property selection.
    /// </summary>
}
