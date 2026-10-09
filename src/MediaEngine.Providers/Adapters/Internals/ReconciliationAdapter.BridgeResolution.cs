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
    private async Task<(IReadOnlyList<ProviderClaim> Claims, IReadOnlyDictionary<string, string> CollectedBridgeIds, IReadOnlyList<string> InstanceOfQids)>
        BuildClaimsForResolvedQidAsync(
            string resolvedQid,
            bool isEdition,
            string? workQid,
            string? editionQid,
            string? fileLanguage,
            CancellationToken ct)
    {
        var collectedBridgeIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var claims = new List<ProviderClaim>();
        var instanceOfQids = new List<string>();
        var language = ResolveDisplayLanguage(TryGetMetadataLanguage(), fileLanguage);

        try
        {
            var extensions = await ExtendAsync([resolvedQid], BridgeResolutionPCodes, ct).ConfigureAwait(false);

            if (extensions.TryGetValue(resolvedQid, out var resolvedProps))
            {
                if (resolvedProps.TryGetValue("P31", out var p31Values))
                {
                    foreach (var p31 in p31Values)
                    {
                        var qid = p31.Value?.EntityId ?? p31.Value?.RawValue;
                        if (!string.IsNullOrWhiteSpace(qid) && qid.StartsWith('Q'))
                        {
                            instanceOfQids.Add(qid);
                        }
                    }
                }

                foreach (var pCode in BridgeResolutionPCodes)
                {
                    if (!resolvedProps.TryGetValue(pCode, out var pValues) || pValues.Count == 0)
                    {
                        continue;
                    }

                    var firstVal = pValues[0].Value;
                    if (firstVal is null)
                    {
                        continue;
                    }

                    var rawVal = firstVal.RawValue ?? firstVal.EntityId;
                    if (string.IsNullOrWhiteSpace(rawVal))
                    {
                        continue;
                    }

                    var normalized = IdentifierNormalizationService.NormalizeRaw(pCode, rawVal);
                    if (string.IsNullOrWhiteSpace(normalized))
                    {
                        continue;
                    }

                    // Convert P-code → bridge claim key (e.g. "P212" → "isbn_13") so the
                    // dictionary stores the same keys that bridge_ids.id_type uses.
                    // P-codes without a label mapping (e.g. P1085 LibraryThing) are
                    // stored as the raw P-code, which BridgeIdHelper.IsBridgeId rejects
                    // — those are not Stage 2 bridge identifiers we care about.
                    var claimKey = pCode;
                    if (_config.DataExtension.PropertyLabels.TryGetValue(pCode, out var label)
                        && !string.IsNullOrWhiteSpace(label))
                    {
                        claimKey = label;
                    }

                    if (!BridgeIdHelper.IsBridgeId(claimKey))
                    {
                        continue;
                    }

                    collectedBridgeIds[claimKey] = normalized;
                }

                claims.AddRange(ExtensionToClaims(
                    resolvedQid,
                    resolvedProps,
                    _config.DataExtension.PropertyLabels,
                    isWork: true,
                    castMemberLimit: _config.Reconciliation.CastMemberLimit,
                    metadataLanguage: language,
                    editionScopedDates: isEdition));

                if (resolvedProps.TryGetValue("P175", out var performers))
                {
                    var artistQids = performers.Select(p => p.Value?.EntityId).Where(q => !string.IsNullOrWhiteSpace(q)).Cast<string>().Distinct().ToArray();
                    if (artistQids.Length > 0)
                    {
                        var artistProperties = await ExtendAsync(artistQids, ["P434"], ct).ConfigureAwait(false);
                        foreach (var properties in artistProperties.Values)
                        {
                            if (properties.TryGetValue("P434", out var artistIds))
                            {
                                foreach (var artistId in artistIds)
                                {
                                    if (!string.IsNullOrWhiteSpace(artistId.Value?.RawValue))
                                    {
                                        claims.Add(new ProviderClaim("musicbrainz_artist_id", artistId.Value.RawValue, 1));
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (isEdition
                && !string.IsNullOrWhiteSpace(workQid)
                && !string.Equals(workQid, resolvedQid, StringComparison.OrdinalIgnoreCase))
            {
                var workDateExtensions = await ExtendAsync([workQid], ["P577"], ct).ConfigureAwait(false);
                if (workDateExtensions.TryGetValue(workQid, out var workDateProps))
                {
                    claims.AddRange(ExtensionToClaims(
                        workQid,
                        workDateProps,
                        _config.DataExtension.PropertyLabels,
                        isWork: true,
                        castMemberLimit: 0,
                        metadataLanguage: language));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Provider}: BuildClaimsForResolvedQidAsync — Data Extension failed for {QID}",
                Name, resolvedQid);
        }

        // Always emit the wikidata_qid claim for the work; matches the legacy
        // path's "Insert(0, ...)" behaviour.
        var effectiveWorkQid = workQid ?? resolvedQid;
        claims.Insert(0, new ProviderClaim(BridgeIdKeys.WikidataQid, effectiveWorkQid, 1.0));

        if (isEdition && !string.IsNullOrWhiteSpace(editionQid))
        {
            claims.Add(new ProviderClaim("edition_qid", editionQid, 1.0));
        }

        return (claims, collectedBridgeIds, instanceOfQids);
    }

    /// <summary>
    /// Single-request entry point for the library-backed path. Wraps a single
    /// <see cref="WikidataResolveRequest"/> in a one-element list and delegates
    /// to <see cref="ResolveBatchAsyncViaLibraryAsync"/> so both code paths
    /// share the same mapping + telemetry logic.
    /// </summary>
    private async Task<WikidataResolveResult> ResolveAsyncViaLibraryAsync(
        WikidataResolveRequest request,
        CancellationToken ct)
    {
        var batched = await ResolveBatchAsyncViaLibraryAsync([request], ct).ConfigureAwait(false);
        return batched.TryGetValue(request.CorrelationKey, out var r) ? r : WikidataResolveResult.NotFound;
    }

    /// <summary>
    /// Batched library-backed Wikidata identity resolution.
    /// 1) Builds a <see cref="BridgeResolutionRequest"/> per input via <see cref="BuildBridgeResolutionRequest"/>.
    /// 2) Dispatches the whole list to <c>_reconciler.Bridge.ResolveBatchAsync</c>
    ///    (which natively groups by natural key so one unique ISBN/album/text hint shares one provider lookup).
    /// 3) For each resolved entry, calls <see cref="BuildClaimsForResolvedQidAsync"/>
    ///    to populate the <c>Claims</c> + <c>CollectedBridgeIds</c> the consumer
    ///    contract requires.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, WikidataResolveResult>> ResolveBatchAsyncViaLibraryAsync(
        IReadOnlyList<WikidataResolveRequest> requests,
        CancellationToken ct)
    {
        var results = new Dictionary<string, WikidataResolveResult>(StringComparer.Ordinal);
        if (requests is null || requests.Count == 0 || _reconciler is null)
        {
            return results;
        }

        // Initialise every correlation key to NotFound so callers always get an entry.
        foreach (var r in requests)
        {
            results[r.CorrelationKey] = WikidataResolveResult.NotFound;
        }

        // Build the library request set + remember which input each library request
        // came from so resolved QIDs can be mapped back to their job context.
        var inputByCorrelationKey = requests.ToDictionary(r => r.CorrelationKey, StringComparer.Ordinal);
        var libRequests = new List<BridgeResolutionRequest>(requests.Count);
        foreach (var r in requests)
        {
            var libReq = BuildBridgeResolutionRequest(r);
            if (libReq is not null)
            {
                libRequests.Add(libReq);
            }
        }

        if (libRequests.Count == 0)
        {
            return results;
        }

        // ── Pass 1: dispatch built requests to the library ──────────────────
        IReadOnlyDictionary<string, BridgeResolutionResult> libResults;
        try
        {
            libResults = await CollectBridgeStreamAsync(libRequests, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Provider}: Bridge.ResolveBatchAsync failed", Name);
            return results;
        }

        await PopulateResultsAsync(libResults, results, inputByCorrelationKey, ct).ConfigureAwait(false);

        var fallbackRequests = requests
            .Where(r => r.AllowConstrainedTextFallback
                        && HasRealBridgeIds(r)
                        && (!results.TryGetValue(r.CorrelationKey, out var result) || !result.Found))
            .Select(r => BuildConstrainedTextFallbackRequest(r, BuildBridgeResolutionRequest))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

        if (fallbackRequests.Count > 0)
        {
            try
            {
                var fallbackResults = await CollectBridgeStreamAsync(fallbackRequests, ct).ConfigureAwait(false);
                await PopulateResultsAsync(fallbackResults, results, inputByCorrelationKey, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Provider}: constrained text fallback failed", Name);
            }
        }

        return results;
    }

    /// <summary>
    /// Walks the result of a <c>Stage2.ResolveBatchAsync</c> call, fetches
    /// claims for every resolved QID, and writes the mapped
    /// <see cref="WikidataResolveResult"/> into the shared results dictionary.
    /// Existing Found entries are NOT overwritten so callers can run a
    /// pass 1 + pass 2 sequence safely.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, BridgeResolutionResult>> CollectBridgeStreamAsync(
        IReadOnlyList<BridgeResolutionRequest> requests,
        CancellationToken ct)
    {
        var results = new Dictionary<string, BridgeResolutionResult>(StringComparer.Ordinal);
        await foreach (var result in _reconciler!.Bridge.ResolveBatchStreamAsync(requests, ct).ConfigureAwait(false))
        {
            results[result.CorrelationKey] = result;
        }

        return results;
    }

    private async Task PopulateResultsAsync(
        IReadOnlyDictionary<string, BridgeResolutionResult> libResults,
        Dictionary<string, WikidataResolveResult> results,
        IReadOnlyDictionary<string, WikidataResolveRequest>? inputByCorrelationKey,
        CancellationToken ct)
    {
        foreach (var (correlationKey, libResult) in libResults)
        {
            if (results.TryGetValue(correlationKey, out var existing) && existing.Found)
            {
                continue;
            }

            if (!libResult.Found || string.IsNullOrWhiteSpace(libResult.ResolvedEntityQid))
            {
                _logger.LogInformation(
                    "{Provider}: Stage2 — {Key} not resolved", Name, correlationKey);
                results[correlationKey] = BuildUnresolvedResult(libResult);
                continue;
            }

            WikidataResolveRequest? input = null;
            if (inputByCorrelationKey is not null)
            {
                inputByCorrelationKey.TryGetValue(correlationKey, out input);
            }

            if (libResult.MatchedBy == BridgeResolutionStrategy.TextSearch
                && input?.AllowConstrainedTextFallback != true)
            {
                _logger.LogWarning("{Provider}: rejected disallowed text fallback for {Key}", Name, correlationKey);
                results[correlationKey] = BuildUnresolvedResult(libResult);
                continue;
            }

            var accepted = await SelectAcceptedBridgeCandidateAsync(
                correlationKey,
                libResult,
                input,
                ct).ConfigureAwait(false);

            if (accepted is null)
            {
                results[correlationKey] = BuildUnresolvedResult(libResult);
                continue;
            }

            var finalQid = accepted.FinalQid;
            var finalWorkQid = accepted.FinalWorkQid;
            var finalIsEdition = accepted.FinalIsEdition;
            var finalEditionQid = accepted.FinalEditionQid;
            var claims = accepted.Claims;
            var collectedBridgeIds = accepted.CollectedBridgeIds;
            var selectedCandidate = accepted.SelectedCandidate;
            var bridgeRollup = accepted.BridgeRollup;
            var bridgeSeries = accepted.BridgeSeries;
            var bridgeRelationships = accepted.BridgeRelationships;
            claims = AddImmediateBridgeSeriesClaims(claims, bridgeSeries);

            // ── P31 media-type validation ─────────────────────────────────────
            // After the library resolves a QID (via bridge ID or text search),
            // verify the entity's P31 is valid for the requested media type.
            // Without this check, an ISBN shared between a novel and its film
            // adaptation can resolve to the film entity instead of the book.
            /*
            if (false)
            {
                if (!ValidateP31ForMediaType(instanceOfQids, finalWorkQid, input.MediaType))
                {
                    _logger.LogInformation(
                        "{Provider}: Stage2 — rejected {Key} → {QID}: P31 does not match {MediaType}",
                        Name, correlationKey, finalWorkQid, input.MediaType);
                    continue; // leave as NotFound — text fallback will retry
                }

                if (!IsResolvedYearCompatible(input.Year, claims, input.MediaType)
                    // A verified release may be a reissue; its date is not the original album date.
                    && !(input.MediaType == MediaType.Music
                        && input.BridgeIds?.TryGetValue("musicbrainz_release_id", out var releaseId) == true
                        && claims.Any(claim => claim.Key == "musicbrainz_release_id"
                            && string.Equals(claim.Value, releaseId, StringComparison.OrdinalIgnoreCase))))
                {
                    _logger.LogInformation(
                        "{Provider}: Stage2 — rejected {Key} → {QID}: year mismatch for {MediaType} (hint={HintYear}, resolved={ResolvedYear})",
                        Name,
                        correlationKey,
                        finalWorkQid,
                        input.MediaType,
                        input.Year,
                        GetResolvedClaimsYear(claims));
                    continue;
                }
            }

            */
            if (input is { MediaType: MediaType.Comics })
            {
                var preferredComicSeriesQid = GetPreferredComicSeriesQid(input, finalQid, claims);
                if (!string.IsNullOrWhiteSpace(preferredComicSeriesQid))
                {
                    var childQid = finalQid;
                    finalQid = preferredComicSeriesQid;
                    finalIsEdition = false;
                    finalWorkQid = preferredComicSeriesQid;
                    finalEditionQid = null;

                    var (parentClaims, parentCollectedBridgeIds, parentInstanceOfQids) = await BuildClaimsForResolvedQidAsync(
                        finalQid,
                        finalIsEdition,
                        finalWorkQid,
                        finalEditionQid,
                        input.FileLanguage,
                        ct).ConfigureAwait(false);
                    claims = parentClaims;
                    collectedBridgeIds = parentCollectedBridgeIds;

                    if (!ValidateP31ForMediaType(parentInstanceOfQids, finalWorkQid, input.MediaType, input.ResolutionScope))
                    {
                        _logger.LogInformation(
                            "{Provider}: Stage2 — rejected normalized comics parent {Key} → {QID}: P31 does not match {MediaType}",
                            Name, correlationKey, finalWorkQid, input.MediaType);
                        continue;
                    }

                    _logger.LogInformation(
                        "{Provider}: Stage2 — normalized comics result {Key} from {ChildQid} to parent series {ParentQid}",
                        Name, correlationKey, childQid, finalQid);
                }
            }

            results[correlationKey] = new WikidataResolveResult
            {
                Found = true,
                Qid = finalQid,
                IsEdition = finalIsEdition,
                WorkQid = finalWorkQid,
                EditionQid = finalEditionQid,
                Claims = claims,
                CollectedBridgeIds = collectedBridgeIds,
                MatchedBy = MapBridgeResolutionStrategy(libResult.MatchedBy),
                PrimaryBridgeIdType = selectedCandidate?.MatchedBridgeIdType,
                BridgeDiagnostics = libResult.Diagnostics,
                RankedBridgeCandidates = libResult.Candidates,
                BridgeRollup = bridgeRollup,
                BridgeSeries = bridgeSeries,
                BridgeRelationships = bridgeRelationships,
            };

            _logger.LogInformation(
                "{Provider}: Stage2 — resolved {Key} → {QID} via {Strategy} " +
                "(isEdition={IsEdition}, claims={ClaimCount}, bridgeIds={BridgeCount})",
                Name, correlationKey, finalQid, libResult.MatchedBy,
                finalIsEdition, claims.Count, collectedBridgeIds.Count);
        }
    }

    private static WikidataResolveResult BuildUnresolvedResult(BridgeResolutionResult result) => new()
    {
        Found = false,
        MatchedBy = MapBridgeResolutionStrategy(result.MatchedBy),
        BridgeDiagnostics = result.Diagnostics,
        RankedBridgeCandidates = result.Candidates,
        BridgeRollup = result.Rollup,
        BridgeSeries = result.Series,
        BridgeRelationships = result.Relationships,
    };

    private async Task<AcceptedBridgeCandidate?> SelectAcceptedBridgeCandidateAsync(
        string correlationKey,
        BridgeResolutionResult libResult,
        WikidataResolveRequest? input,
        CancellationToken ct)
    {
        foreach (var attempt in BuildBridgeCandidateAttempts(libResult))
        {
            var finalQid = attempt.UsePrimaryRollup
                ? libResult.ResolvedEntityQid!
                : attempt.Qid;
            var finalWorkQid = attempt.UsePrimaryRollup
                ? libResult.CanonicalWorkQid ?? finalQid
                : finalQid;
            var finalIsEdition = attempt.UsePrimaryRollup
                && libResult.Rollup?.RelationshipPath.Any(step =>
                    string.Equals(step.PropertyId, "P629", StringComparison.OrdinalIgnoreCase)) == true
                && string.Equals(finalQid, libResult.SelectedCandidate?.Qid, StringComparison.OrdinalIgnoreCase);
            var finalEditionQid = finalIsEdition ? finalQid : null;

            var (claims, collectedBridgeIds, instanceOfQids) = await BuildClaimsForResolvedQidAsync(
                finalQid,
                finalIsEdition,
                finalWorkQid,
                finalEditionQid,
                input?.FileLanguage,
                ct).ConfigureAwait(false);

            if (input is not null && input.MediaType != MediaType.Unknown)
            {
                if (input.MediaType == MediaType.Music
                    && !MusicAlbumIdentityEvidence.Corroborates(input.Artist, input.BridgeIds ?? new Dictionary<string, string>(), claims))
                {
                    _logger.LogWarning("{Provider}: rejected album {Qid} for {Key}: conflicting or insufficient artist/release evidence",
                        Name, finalQid, correlationKey);
                    continue;
                }

                if (!ValidateP31ForMediaType(instanceOfQids, finalWorkQid, input.MediaType, input.ResolutionScope))
                {
                    _logger.LogInformation(
                        "{Provider}: Stage2 - rejected {Key} -> {QID}: P31 does not match {MediaType}",
                        Name, correlationKey, finalWorkQid, input.MediaType);
                    continue;
                }

                if (!IsResolvedYearCompatible(input.Year, claims, input.MediaType)
                    // A verified release may be a reissue; its date is not the original album date.
                    && !(input.MediaType == MediaType.Music
                        && input.BridgeIds?.TryGetValue("musicbrainz_release_id", out var releaseId) == true
                        && claims.Any(claim => claim.Key == "musicbrainz_release_id"
                            && string.Equals(claim.Value, releaseId, StringComparison.OrdinalIgnoreCase))))
                {
                    _logger.LogInformation(
                        "{Provider}: Stage2 - rejected {Key} -> {QID}: year mismatch for {MediaType} (hint={HintYear}, resolved={ResolvedYear})",
                        Name,
                        correlationKey,
                        finalWorkQid,
                        input.MediaType,
                        input.Year,
                        GetResolvedClaimsYear(claims));
                    continue;
                }
            }

            if (!attempt.UsePrimaryRollup)
            {
                _logger.LogInformation(
                    "{Provider}: Stage2 - accepted ranked fallback {Key} -> {QID} after rejecting higher candidate(s)",
                    Name, correlationKey, finalWorkQid);
            }

            return new AcceptedBridgeCandidate(
                finalQid,
                finalWorkQid,
                finalIsEdition,
                finalEditionQid,
                claims,
                collectedBridgeIds,
                attempt.Candidate,
                attempt.UsePrimaryRollup ? libResult.Rollup : null,
                attempt.UsePrimaryRollup ? libResult.Series : [],
                attempt.UsePrimaryRollup ? libResult.Relationships : []);
        }

        return null;
    }

    private static IReadOnlyList<(BridgeCandidate? Candidate, string Qid, bool UsePrimaryRollup)> BuildBridgeCandidateAttempts(
        BridgeResolutionResult libResult)
    {
        var attempts = new List<(BridgeCandidate? Candidate, string Qid, bool UsePrimaryRollup)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(libResult.ResolvedEntityQid)
            && seen.Add(libResult.ResolvedEntityQid!))
        {
            attempts.Add((libResult.SelectedCandidate, libResult.ResolvedEntityQid!, true));
        }

        foreach (var candidate in libResult.Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Qid) || !seen.Add(candidate.Qid))
            {
                continue;
            }

            attempts.Add((candidate, candidate.Qid, false));
        }

        return attempts;
    }

    private static IReadOnlyList<ProviderClaim> AddImmediateBridgeSeriesClaims(
        IReadOnlyList<ProviderClaim> claims,
        IReadOnlyList<BridgeSeriesInfo> bridgeSeries)
    {
        var merged = claims.ToList();
        foreach (var series in bridgeSeries.Where(series =>
                     series.IsImmediateSeries
                     && !string.IsNullOrWhiteSpace(series.SeriesQid)))
        {
            var qid = series.SeriesQid!;
            var label = string.IsNullOrWhiteSpace(series.SeriesLabel) ? qid : series.SeriesLabel!;
            AddDistinctClaim(merged, MetadataFieldConstants.Series, label, series.Confidence);
            AddDistinctClaim(merged, "series_qid", $"{qid}::{label}", series.Confidence);

            if (!string.IsNullOrWhiteSpace(series.Position))
            {
                AddDistinctClaim(merged, MetadataFieldConstants.SeriesPosition, series.Position!, series.Confidence);
            }

            if (!string.IsNullOrWhiteSpace(series.SourcePropertyId))
            {
                AddDistinctClaim(merged, MetadataFieldConstants.SeriesMembershipSource, series.SourcePropertyId!, 1.0);
            }
        }

        return merged;
    }

    private static void AddDistinctClaim(
        List<ProviderClaim> claims,
        string key,
        string value,
        double confidence)
    {
        if (!claims.Any(claim => string.Equals(claim.Key, key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.Value, value, StringComparison.OrdinalIgnoreCase)))
        {
            claims.Add(new ProviderClaim(key, value, confidence));
        }
    }

    private sealed record AcceptedBridgeCandidate(
        string FinalQid,
        string FinalWorkQid,
        bool FinalIsEdition,
        string? FinalEditionQid,
        IReadOnlyList<ProviderClaim> Claims,
        IReadOnlyDictionary<string, string> CollectedBridgeIds,
        BridgeCandidate? SelectedCandidate,
        CanonicalRollup? BridgeRollup,
        IReadOnlyList<BridgeSeriesInfo> BridgeSeries,
        IReadOnlyList<BridgeRelationshipEdge> BridgeRelationships);

    internal static string? GetPreferredComicSeriesQid(
        WikidataResolveRequest request,
        string resolvedQid,
        IReadOnlyList<ProviderClaim> claims)
    {
        if (request.MediaType != MediaType.Comics
            || string.IsNullOrWhiteSpace(request.SeriesTitle)
            || string.IsNullOrWhiteSpace(resolvedQid)
            || claims.Count == 0)
        {
            return null;
        }

        var seriesCandidates = claims
            .Where(c => string.Equals(c.Key, "series_qid", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(c.Value))
            .Select(claim => ParseEntityReferenceClaim(claim.Value))
            .Where(c => c is not null)
            .Select(c => c!.Value)
            .Where(c => !string.IsNullOrWhiteSpace(c.Qid)
                        && !string.Equals(c.Qid, resolvedQid, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (seriesCandidates.Count == 0)
        {
            return null;
        }

        var normalizedSeriesTitle = NormalizeComicSeriesLookupText(request.SeriesTitle!);
        if (!string.IsNullOrWhiteSpace(normalizedSeriesTitle))
        {
            var exactMatch = seriesCandidates.FirstOrDefault(candidate =>
                string.Equals(
                    NormalizeComicSeriesLookupText(candidate.Label),
                    normalizedSeriesTitle,
                    StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(exactMatch.Qid))
            {
                return exactMatch.Qid;
            }
        }

        return seriesCandidates.Count == 1 ? seriesCandidates[0].Qid : null;
    }

    private static (string Qid, string Label)? ParseEntityReferenceClaim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split("::", 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return null;
        }

        return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
    }

    private static string NormalizeComicSeriesLookupText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(value, @"[^\p{L}\p{N}]+", " ");
        return normalized.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Checks whether the resolved entity's P31 (instance_of) values are compatible
    /// with the requested media type. Returns <c>true</c> when at least one P31 value
    /// is in the configured <c>instance_of_classes</c> and none are in <c>exclude_classes</c>.
    /// Returns <c>false</c> when P31 data is unavailable. Canonical media identity
    /// matching must fail closed; otherwise short or ambiguous titles like "1984"
    /// can auto-accept unrelated people, games, or organizations.
    /// </summary>
    private bool ValidateP31ForMediaType(
        IReadOnlyList<string> instanceOfQids,
        string qid,
        MediaType mediaType,
        string? resolutionScope = null)
    {
        if (instanceOfQids.Count == 0)
        {
            _logger.LogDebug(
                "{Provider}: P31 validation — {QID} has no P31 data for {MediaType}",
                Name, qid, mediaType);
            return false;
        }

        var mediaTypeKey = string.IsNullOrWhiteSpace(resolutionScope)
            ? mediaType.ToString()
            : resolutionScope;

        // Check exclude list first — if ANY P31 is excluded, reject immediately.
        if (_config.ExcludeClasses.TryGetValue(mediaTypeKey, out var excludedClasses)
            && excludedClasses.Count > 0)
        {
            var excludedSet = new HashSet<string>(excludedClasses, StringComparer.OrdinalIgnoreCase);
            if (instanceOfQids.Any(q => excludedSet.Contains(q)))
            {
                _logger.LogDebug(
                    "{Provider}: P31 validation — {QID} has excluded P31 [{P31}] for {MediaType}",
                    Name, qid, string.Join(", ", instanceOfQids), mediaTypeKey);
                return false;
            }
        }

        // Check include list — at least one P31 must be in instance_of_classes.
        if (_config.InstanceOfClasses.TryGetValue(mediaTypeKey, out var expectedClasses)
            && expectedClasses.Count > 0)
        {
            var expectedSet = new HashSet<string>(expectedClasses, StringComparer.OrdinalIgnoreCase);
            if (!instanceOfQids.Any(q => expectedSet.Contains(q)))
            {
                _logger.LogDebug(
                    "{Provider}: P31 validation — {QID} P31 [{P31}] not in {MediaType} expected classes",
                    Name, qid, string.Join(", ", instanceOfQids), mediaTypeKey);
                return false;
            }
        }

        return true;
    }

    // ── Public Stage 2 facade ────────────────────────────────────────────────

    /// <summary>
    /// Single-request Wikidata identity resolution. Pure pass-through to
    /// <see cref="ResolveAsyncViaLibraryAsync"/>, which delegates to
    /// <c>Tuvima.Wikidata.BridgeResolutionService.ResolveAsync</c> and follows up with
    /// a Data Extension call to populate <c>Claims</c> + <c>CollectedBridgeIds</c>.
    /// <para>
    /// The hand-rolled <c>ResolveBridgeAsync</c> / <c>ResolveMusicAlbumAsync</c>
    /// / <c>ResolveByTextAsync</c> / <c>AutoDetectStrategy</c> / group-key
    /// helpers / sentinel detection were removed in Commit F2 of the adapter
    /// slimdown remediation. The library now owns all of this logic.
    /// </para>
    /// </summary>
    public async Task<WikidataResolveResult> ResolveAsync(
        WikidataResolveRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_reconciler is null)
        {
            return WikidataResolveResult.NotFound;
        }

        return await ResolveAsyncViaLibraryAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Batched Wikidata identity resolution. Pure pass-through to
    /// <see cref="ResolveBatchAsyncViaLibraryAsync"/>, which delegates to
    /// <c>Tuvima.Wikidata.BridgeResolutionService.ResolveBatchAsync</c> (the library
    /// natively groups requests by natural key — music album, bridge ID,
    /// text signature — so N callers asking for the same ISBN share a
    /// single Wikidata round-trip).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, WikidataResolveResult>> ResolveBatchAsync(
        IReadOnlyList<WikidataResolveRequest> requests,
        CancellationToken ct = default)
    {
        if (_reconciler is null || requests is null || requests.Count == 0)
        {
            return new Dictionary<string, WikidataResolveResult>(StringComparer.Ordinal);
        }

        return await ResolveBatchAsyncViaLibraryAsync(requests, ct).ConfigureAwait(false);
    }


    // ── Private: FetchWork / FetchPerson ─────────────────────────────────────

}
