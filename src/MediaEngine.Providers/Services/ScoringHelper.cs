using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Shared helper that encapsulates the claim-persist-score-upsert pattern
/// used by both <see cref="MetadataHarvestingService"/> and
/// <see cref="HydrationPipelineService"/>.
///
/// This avoids duplicating the same 40-line scoring block across multiple services.
/// </summary>
public static class ScoringHelper
{
    /// <summary>
    /// Persists new claims for an entity, loads all claims, runs the scoring
    /// engine, and upserts the resulting canonical values.
    /// </summary>
    /// <param name="entityId">The entity being scored.</param>
    /// <param name="claims">New provider claims to persist (may be empty).</param>
    /// <param name="providerId">The provider GUID that produced these claims.</param>
    /// <param name="claimRepo">Claims repository.</param>
    /// <param name="canonicalRepo">Canonical values repository.</param>
    /// <param name="scoringEngine">The intelligence scoring engine.</param>
    /// <param name="configLoader">Configuration loader for scoring settings and provider weights.</param>
    /// <param name="allProviders">All registered providers (for weight map building).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The scoring result after re-scoring the entity.</returns>
    public static async Task<ScoringResult> PersistClaimsAndScoreAsync(
        Guid entityId,
        IReadOnlyList<ProviderClaim> claims,
        Guid providerId,
        IMetadataClaimRepository claimRepo,
        ICanonicalValueRepository canonicalRepo,
        IScoringEngine scoringEngine,
        IConfigurationLoader configLoader,
        IEnumerable<IExternalMetadataProvider> allProviders,
        CancellationToken ct,
        ICanonicalValueArrayRepository? arrayRepo = null,
        ILogger? logger = null,
        ISearchIndexRepository? searchIndex = null,
        MediaType detectedMediaType = MediaType.Unknown,
        Guid? decisionSourceProviderId = null,
        IReadOnlyCollection<string>? snapshotFieldKeys = null)
    {
        // Wrap provider claims as domain MetadataClaim rows. One observation, one timestamp:
        // claims from the same response tie on claimed_at, so a
        // multi-valued field (composers, authors) resolves by the provider's own order instead of
        // by whichever clock ticks happened to separate them.
        var claimedAt = DateTimeOffset.UtcNow;
        var domainClaims = claims
            .Select(pc => new MetadataClaim
            {
                Id = Guid.NewGuid(),
                EntityId = entityId,
                ProviderId = providerId,
                DecisionSourceProviderId = decisionSourceProviderId,
                ClaimKey = pc.Key,
                ClaimValue = pc.Value,
                Confidence = pc.Confidence,
                ClaimedAt = claimedAt,
                IsUserLocked = false,
            })
            .ToList();

        // Provider snapshots replace only the declared field set. Historical
        // observations remain stored but are excluded from current scoring.
        if (snapshotFieldKeys is { Count: > 0 })
        {
            await claimRepo.ReplaceCurrentProviderClaimsAsync(
                entityId, providerId, snapshotFieldKeys, domainClaims, ct).ConfigureAwait(false);
        }
        else if (domainClaims.Count > 0)
        {
            await claimRepo.InsertBatchAsync(domainClaims, ct).ConfigureAwait(false);
        }

        // Load ALL claims for this entity and re-score.
        var allClaims = await claimRepo.GetByEntityAsync(entityId, ct).ConfigureAwait(false);
        detectedMediaType = await ResolveDetectedMediaTypeAsync(
            entityId,
            allClaims,
            canonicalRepo,
            ct,
            detectedMediaType).ConfigureAwait(false);

        var scoringSettings = configLoader.LoadScoring();
        var providerConfigs = configLoader.LoadAllProviders();

        var (providerWeights, providerFieldWeights) = BuildWeightMaps(providerConfigs, allProviders);

        var scoringConfig = new ScoringConfiguration
        {
            AutoLinkThreshold = scoringSettings.AutoLinkThreshold,
            ConflictThreshold = scoringSettings.ConflictThreshold,
            ConflictEpsilon = scoringSettings.ConflictEpsilon,
            StaleClaimDecayDays = scoringSettings.StaleClaimDecayDays,
            StaleClaimDecayFactor = scoringSettings.StaleClaimDecayFactor,
        };

        var scoringContext = new ScoringContext
        {
            EntityId = entityId,
            Claims = allClaims,
            ProviderWeights = providerWeights,
            ProviderFieldWeights = providerFieldWeights,
            Configuration = scoringConfig,
            DetectedMediaType = detectedMediaType,
        };

        var scored = await scoringEngine.ScoreEntityAsync(scoringContext, ct).ConfigureAwait(false);

        // Upsert canonical values (current best answers).
        var canonicals = scored.FieldScores
            .Where(f => !string.IsNullOrEmpty(f.WinningValue))
            .Where(f => !MetadataFieldConstants.IsMultiValued(f.Key))
            .Select(f => new CanonicalValue
            {
                EntityId = entityId,
                Key = f.Key,
                Value = f.WinningValue!,
                LastScoredAt = scored.ScoredAt,
                IsConflicted = f.IsConflicted,
                WinningProviderId = f.WinningProviderId,
                NeedsReview = f.IsConflicted, // Unit 5: conflicted fields immediately flagged for review
            })
            .ToList();

        await canonicalRepo.UpsertBatchAsync(canonicals, ct).ConfigureAwait(false);

        // A successful snapshot may retract facts that existed previously.
        // Remove canonicals for refreshed fields that no longer have a winner.
        if (snapshotFieldKeys is { Count: > 0 })
        {
            var winningKeys = scored.FieldScores
                .Where(score => !string.IsNullOrWhiteSpace(score.WinningValue))
                .Select(score => score.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in snapshotFieldKeys
                .Where(key => !key.EndsWith(MetadataFieldConstants.CompanionQidSuffix, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (winningKeys.Contains(key))
                {
                    continue;
                }

                await canonicalRepo.DeleteByKeyAsync(entityId, key, ct).ConfigureAwait(false);
                if (arrayRepo is not null && MetadataFieldConstants.IsMultiValued(key))
                {
                    await arrayRepo.SetValuesAsync(entityId, key, [], ct).ConfigureAwait(false);
                }
            }
        }

        // Refresh the FTS5 search index for this work. UpsertByEntityIdAsync
        // accepts either an asset id or a Work id and re-reads the canonical
        // state from the database — Self fields from the asset row, Parent
        // fields from the topmost Work row. Both persist passes
        // (asset and parent) call this; the second simply overwrites the row.
        if (searchIndex is not null)
        {
            await searchIndex.UpsertByEntityIdAsync(entityId, ct).ConfigureAwait(false);
        }

        // Decompose multi-valued fields into proper array rows.
        // Collect ALL claims
        // from the winning provider for each multi-valued key.
        if (arrayRepo is not null)
        {
            foreach (var fieldScore in scored.FieldScores)
            {
                if (!MetadataFieldConstants.IsMultiValued(fieldScore.Key))
                {
                    continue;
                }

                // Skip companion _qid keys — they are paired with their parent key below.
                if (fieldScore.Key.EndsWith(MetadataFieldConstants.CompanionQidSuffix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    // Collect ALL claims from the winning provider for this key.
                    // Deduplicate by value (case-insensitive) to prevent "Author & Author" display.
                    var winningClaims = allClaims
                        .Where(c => c.ProviderId == fieldScore.WinningProviderId
                                 && c.ClaimKey.Equals(fieldScore.Key, StringComparison.OrdinalIgnoreCase))
                        .DistinctBy(c => c.ClaimValue, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (winningClaims.Count == 0)
                    {
                        continue;
                    }

                    // Collect companion _qid claims from the same provider.
                    var qidKey = fieldScore.Key + MetadataFieldConstants.CompanionQidSuffix;
                    var qidClaims = allClaims
                        .Where(c => c.ProviderId == fieldScore.WinningProviderId
                                 && c.ClaimKey.Equals(qidKey, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    qidClaims = FilterCollectivePseudonymMemberQids(
                        fieldScore.Key,
                        allClaims,
                        fieldScore.WinningProviderId,
                        qidClaims);

                    var entries = BuildCanonicalArrayEntries(winningClaims, qidClaims);
                    if (entries.Count == 0)
                    {
                        continue;
                    }

                    // Multi-valued metadata is array-only. Remove any scalar row
                    // left from an older field classification before replacing
                    // the idempotent ordered array projection.
                    await canonicalRepo.DeleteByKeyAsync(entityId, fieldScore.Key, ct).ConfigureAwait(false);
                    await arrayRepo.SetValuesAsync(entityId, fieldScore.Key, entries, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogDebug(ex,
                        "Failed to decompose multi-valued field '{Key}' for entity {EntityId}",
                        fieldScore.Key, entityId);
                }
            }

            if (searchIndex is not null)
            {
                await searchIndex.UpsertByEntityIdAsync(entityId, ct).ConfigureAwait(false);
            }
        }

        return scored;
    }

    public static async Task<MediaType> ResolveDetectedMediaTypeAsync(
        Guid entityId,
        IReadOnlyList<MetadataClaim> claims,
        ICanonicalValueRepository canonicalRepo,
        CancellationToken ct,
        MediaType fallback = MediaType.Unknown)
    {
        if (fallback != MediaType.Unknown)
        {
            return fallback;
        }

        var claimMediaType = claims
            .Where(c => c.ClaimKey.Equals(MetadataFieldConstants.MediaTypeField, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.ClaimedAt)
            .Select(c => MediaTypeParser.Parse(c.ClaimValue))
            .FirstOrDefault(mt => mt != MediaType.Unknown);
        if (claimMediaType != MediaType.Unknown)
        {
            return claimMediaType;
        }

        var canonicals = await canonicalRepo.GetByEntityAsync(entityId, ct).ConfigureAwait(false);
        var canonicalMediaType = canonicals
            .Where(c => c.Key.Equals(MetadataFieldConstants.MediaTypeField, StringComparison.OrdinalIgnoreCase))
            .Select(c => MediaTypeParser.Parse(c.Value))
            .FirstOrDefault(mt => mt != MediaType.Unknown);
        if (canonicalMediaType != MediaType.Unknown)
        {
            return canonicalMediaType;
        }

        return InferMediaTypeFromClaimKeys(claims);
    }

    private static MediaType InferMediaTypeFromClaimKeys(IReadOnlyList<MetadataClaim> claims)
    {
        if (claims.Any(c => c.ClaimKey.Equals("tmdb_collection_id", StringComparison.OrdinalIgnoreCase)))
        {
            return MediaType.Movies;
        }

        if (claims.Any(c => c.ClaimKey.Equals(BridgeIdKeys.TmdbId, StringComparison.OrdinalIgnoreCase)
            || c.ClaimKey.Equals(MetadataFieldConstants.ShowName, StringComparison.OrdinalIgnoreCase)
            || c.ClaimKey.Equals(MetadataFieldConstants.EpisodeNumber, StringComparison.OrdinalIgnoreCase)))
        {
            return MediaType.TV;
        }

        if (claims.Any(c => c.ClaimKey.Equals(BridgeIdKeys.ComicVineVolumeId, StringComparison.OrdinalIgnoreCase)
            || c.ClaimKey.Equals(MetadataFieldConstants.IssueNumber, StringComparison.OrdinalIgnoreCase)))
        {
            return MediaType.Comics;
        }

        if (claims.Any(c => c.ClaimKey.Equals(BridgeIdKeys.AppleMusicCollectionId, StringComparison.OrdinalIgnoreCase)
            || c.ClaimKey.Equals(BridgeIdKeys.MusicBrainzReleaseGroupId, StringComparison.OrdinalIgnoreCase)
            || c.ClaimKey.Equals(MetadataFieldConstants.TrackNumber, StringComparison.OrdinalIgnoreCase)
            || c.ClaimKey.Equals(MetadataFieldConstants.Album, StringComparison.OrdinalIgnoreCase)))
        {
            return MediaType.Music;
        }

        return MediaType.Unknown;
    }

    private static List<MetadataClaim> FilterCollectivePseudonymMemberQids(
        string key,
        IReadOnlyList<MetadataClaim> allClaims,
        Guid? winningProviderId,
        IReadOnlyList<MetadataClaim> qidClaims)
    {
        if (!key.Equals(MetadataFieldConstants.Author, StringComparison.OrdinalIgnoreCase)
            || qidClaims.Count <= 1)
        {
            return qidClaims.ToList();
        }

        var isCollectivePseudonym = allClaims.Any(c =>
            c.ProviderId == winningProviderId
            && c.ClaimKey.Equals("author_is_collective_pseudonym", StringComparison.OrdinalIgnoreCase)
            && c.ClaimValue.Equals("true", StringComparison.OrdinalIgnoreCase));
        if (!isCollectivePseudonym)
        {
            return qidClaims.ToList();
        }

        var memberQids = allClaims
            .Where(c => c.ProviderId == winningProviderId
                && c.ClaimKey.Equals("collective_members_qid", StringComparison.OrdinalIgnoreCase))
            .Select(c => ParseQidLabel(c.ClaimValue).Qid)
            .Where(qid => !string.IsNullOrWhiteSpace(qid))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (memberQids.Count == 0)
        {
            return qidClaims.ToList();
        }

        var filtered = qidClaims
            .Where(claim =>
            {
                var parsed = ParseQidLabel(claim.ClaimValue);
                return string.IsNullOrWhiteSpace(parsed.Qid) || !memberQids.Contains(parsed.Qid);
            })
            .ToList();

        return filtered.Count > 0 ? filtered : qidClaims.ToList();
    }

    private static List<CanonicalArrayEntry> BuildCanonicalArrayEntries(
        IReadOnlyList<MetadataClaim> winningClaims,
        IReadOnlyList<MetadataClaim> qidClaims)
    {
        var qidEntries = qidClaims
            .Select(claim => ParseQidLabel(claim.ClaimValue))
            .Where(parsed => !string.IsNullOrWhiteSpace(parsed.Qid) || !string.IsNullOrWhiteSpace(parsed.Label))
            .ToList();

        if (qidEntries.Count > 0)
        {
            var entries = new List<CanonicalArrayEntry>();
            var qidLabels = qidEntries
                .Where(parsed => !string.IsNullOrWhiteSpace(parsed.Label))
                .Select(parsed => parsed.Label!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var parsed in qidEntries)
            {
                var value = StringHelpers.FirstNonBlank(parsed.Label, parsed.Qid);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                entries.Add(new CanonicalArrayEntry
                {
                    Ordinal = entries.Count,
                    Value = value,
                    ValueQid = parsed.Qid,
                });
            }

            foreach (var claim in winningClaims)
            {
                var value = claim.ClaimValue.Trim();
                if (string.IsNullOrWhiteSpace(value)
                    || LooksLikeAggregateContributorName(value)
                    || qidLabels.Contains(value))
                {
                    continue;
                }

                entries.Add(new CanonicalArrayEntry
                {
                    Ordinal = entries.Count,
                    Value = value,
                });
            }

            return DeduplicateArrayEntries(entries);
        }

        return DeduplicateArrayEntries(winningClaims
            .Where(claim => !string.IsNullOrWhiteSpace(claim.ClaimValue))
            .Select((claim, index) => new CanonicalArrayEntry
            {
                Ordinal = index,
                Value = claim.ClaimValue.Trim(),
            })
            .ToList());
    }

    private static List<CanonicalArrayEntry> DeduplicateArrayEntries(IReadOnlyList<CanonicalArrayEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CanonicalArrayEntry>();
        foreach (var entry in entries.OrderBy(entry => entry.Ordinal))
        {
            var key = entry.ValueQid ?? entry.Value;
            if (string.IsNullOrWhiteSpace(entry.Value) || !seen.Add(key))
            {
                continue;
            }

            result.Add(new CanonicalArrayEntry
            {
                Ordinal = result.Count,
                Value = entry.Value,
                ValueQid = entry.ValueQid,
            });
        }

        return result;
    }

    private static (string? Qid, string? Label) ParseQidLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var trimmed = value.Trim();
        var delimiter = trimmed.IndexOf("::", StringComparison.Ordinal);
        if (delimiter > 0)
        {
            return (ExtractQid(trimmed[..delimiter]), StringHelpers.FirstNonBlank(trimmed[(delimiter + 2)..], null));
        }

        return (ExtractQid(trimmed), null);
    }

    private static bool LooksLikeAggregateContributorName(string value)
        => value.Contains(" & ", StringComparison.Ordinal)
            || value.Contains(" and ", StringComparison.OrdinalIgnoreCase)
            || value.Contains(" + ", StringComparison.Ordinal);

    private static string? ExtractQid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var delimiter = trimmed.IndexOf("::", StringComparison.Ordinal);
        if (delimiter > 0)
        {
            trimmed = trimmed[..delimiter].Trim();
        }

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Lineage-aware persist + score (Phase 4 target architecture).
    ///
    /// Splits the inbound provider claims into two disjoint buckets using
    /// <see cref="ClaimScopeCatalog"/>:
    ///   • Self-scope claims  → written to the asset's own entity id.
    ///   • Parent-scope claims → written to <c>lineage.TargetForParentScope</c>,
    ///     which is the topmost Work in the hierarchy (the SHOW for TV, the
    ///     ALBUM for music, the SERIES for comics, the movie's own Work for
    ///     standalone movies).
    ///
    /// This split is unconditional and applies to every media type — including
    /// movies, where <c>TargetForParentScope == TargetForSelfScope</c>. In that
    /// case the parent claims still land on the movie's own Work id (not the
    /// media_assets row), giving every reader a single uniform lookup target:
    /// "parent-scoped fields live on the work; self-scoped fields live on the
    /// asset". No COALESCE, no fallback, no dual-write mirror.
    ///
    /// When <paramref name="lineage"/> is null, the entire write collapses to
    /// the asset id (legacy callers without a resolved hierarchy).
    /// </summary>
    public static async Task<ScoringResult> PersistAndScoreWithLineageAsync(
        Guid entityId,
        IReadOnlyList<ProviderClaim> claims,
        Guid providerId,
        WorkLineage? lineage,
        IMetadataClaimRepository claimRepo,
        ICanonicalValueRepository canonicalRepo,
        IScoringEngine scoringEngine,
        IConfigurationLoader configLoader,
        IEnumerable<IExternalMetadataProvider> allProviders,
        CancellationToken ct,
        ICanonicalValueArrayRepository? arrayRepo = null,
        ILogger? logger = null,
        ISearchIndexRepository? searchIndex = null,
        Guid? decisionSourceProviderId = null)
    {
        // No lineage → fall back to single-target write on the asset id.
        if (lineage is null)
        {
            var snapshotKeys = StructuredSnapshotKeys(providerId, detectedMediaType: MediaType.Unknown, scope: null);
            return await PersistClaimsAndScoreAsync(
                entityId, claims, providerId,
                claimRepo, canonicalRepo, scoringEngine, configLoader, allProviders, ct,
                arrayRepo, logger, searchIndex,
                decisionSourceProviderId: decisionSourceProviderId,
                snapshotFieldKeys: snapshotKeys).ConfigureAwait(false);
        }

        // Partition claims by scope. The split is media-type aware.
        var selfClaims = new List<ProviderClaim>(claims.Count);
        var workClaims = new List<ProviderClaim>(claims.Count);
        var parentClaims = new List<ProviderClaim>(claims.Count);
        var editionClaims = new List<ProviderClaim>(claims.Count);
        foreach (var c in claims)
        {
            if (string.IsNullOrWhiteSpace(c.Key))
            {
                continue;
            }

            switch (ClaimScopeCatalog.GetScope(c.Key, lineage.MediaType))
            {
                case ClaimScope.Parent:
                    parentClaims.Add(c);
                    break;
                case ClaimScope.Edition:
                    editionClaims.Add(c);
                    break;
                case ClaimScope.Work:
                    workClaims.Add(c);
                    break;
                default:
                    selfClaims.Add(c);
                    break;
            }
        }

        // 1. Asset-keyed write — only self-scope claims.
        var assetResult = await PersistClaimsAndScoreAsync(
            entityId, selfClaims, providerId,
            claimRepo, canonicalRepo, scoringEngine, configLoader, allProviders, ct,
            arrayRepo, logger, searchIndex, lineage.MediaType, decisionSourceProviderId,
            StructuredSnapshotKeys(providerId, lineage.MediaType, ClaimScope.Self)).ConfigureAwait(false);

        var workSnapshotKeys = StructuredSnapshotKeys(providerId, lineage.MediaType, ClaimScope.Work);
        if (workClaims.Count > 0 || workSnapshotKeys is { Count: > 0 })
        {
            await PersistClaimsAndScoreAsync(
                lineage.WorkId,
                workClaims,
                providerId,
                claimRepo, canonicalRepo, scoringEngine, configLoader, allProviders, ct,
                arrayRepo, logger, searchIndex, lineage.MediaType, decisionSourceProviderId,
                workSnapshotKeys).ConfigureAwait(false);
        }

        if (editionClaims.Count > 0 || StructuredSnapshotKeys(providerId, lineage.MediaType, ClaimScope.Edition) is { Count: > 0 })
        {
            await PersistClaimsAndScoreAsync(
                lineage.EditionId,
                editionClaims,
                providerId,
                claimRepo, canonicalRepo, scoringEngine, configLoader, allProviders, ct,
                arrayRepo, logger, searchIndex, lineage.MediaType, decisionSourceProviderId,
                StructuredSnapshotKeys(providerId, lineage.MediaType, ClaimScope.Edition)).ConfigureAwait(false);
        }

        // 2. Parent-Work write — only parent-scope claims, always against the
        //    topmost Work id (collapses to the movie's own Work for standalone
        //    media, so the data lives on works.id rather than media_assets.id).
        //
        //    Title synthesis for hierarchical parent Works: when the parent
        //    Work is a distinct entity from the asset (album, TV show, comic
        //    series, book/audiobook series), the inbound claims
        //    never include a top-level "title" because title is Self-scoped
        //    (a track's title, an episode's title — not the container's).
        //    Without intervention, the parent Work has rich canonicals for
        //    album/show_name/series but no title, and the LibraryItem surfaces
        //    it as "Untitled". Here we mint a synthetic title claim from the
        //    appropriate container field so every parent Work has a title.
        //
        //    Movies are skipped — they are standalone, title is Self-scoped,
        //    and the movie's own asset row already holds the title canonical.
        // Always attempt title synthesis — even when parentClaims is empty,
        // because for standalone media (Books, Audiobooks, Movies) the Work
        // row itself receives no claims at all (self-scope → asset, no
        // parent-scope claims), leaving the Work as "Untitled" in library views.
        MaybeSynthesizeParentTitle(claims, parentClaims, lineage, logger);

        var parentSnapshotKeys = StructuredSnapshotKeys(providerId, lineage.MediaType, ClaimScope.Parent);
        if (parentClaims.Count > 0 || parentSnapshotKeys is { Count: > 0 })
        {

            try
            {
                await PersistClaimsAndScoreAsync(
                    lineage.TargetForParentScope,
                    parentClaims,
                    providerId,
                    claimRepo, canonicalRepo, scoringEngine, configLoader, allProviders, ct,
                    arrayRepo, logger, searchIndex, lineage.MediaType, decisionSourceProviderId,
                    parentSnapshotKeys).ConfigureAwait(false);

                logger?.LogDebug(
                    "Lineage write: {ParentCount} parent-scope + {SelfCount} self-scope claim(s) for asset {AssetId} → parent Work {ParentWorkId} ({MediaType})",
                    parentClaims.Count, selfClaims.Count, entityId, lineage.TargetForParentScope, lineage.MediaType);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex,
                    "Parent-scope write failed for parent Work {ParentWorkId} (asset {AssetId}, {MediaType})",
                    lineage.TargetForParentScope, entityId, lineage.MediaType);
            }
        }

        return assetResult;
    }

    private static IReadOnlyCollection<string>? StructuredSnapshotKeys(
        Guid providerId,
        MediaType detectedMediaType,
        ClaimScope? scope)
    {
        if (providerId != WellKnownProviders.Wikidata)
        {
            return null;
        }

        var keys = StructuredDiscoveryFieldCatalog.Fields
            .Where(field => field.Source == DiscoveryFactSource.StructuredProvider)
            .Where(field => detectedMediaType == MediaType.Unknown || field.IsApplicable(detectedMediaType))
            .Where(field => scope is null || ClaimScopeCatalog.GetScope(field.Key, detectedMediaType) == scope)
            .SelectMany(field => new[] { field.Key, field.Key + MetadataFieldConstants.CompanionQidSuffix })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return keys.Length == 0 ? null : keys;
    }

    /// <summary>
    /// Mints a synthetic <c>title</c> claim for a hierarchical parent Work
    /// (album, TV show, comic series, book/audiobook series)
    /// when the inbound parent-scope claims don't already carry one.
    ///
    /// Why this exists: <c>title</c> is Self-scoped by default — a track's
    /// title belongs to the track, not the album. As a result, parent Works
    /// accumulate rich canonicals (album, artist, year, cover_url, genre,
    /// apple IDs…) but no title, and the LibraryItem surfaces them as
    /// "Untitled". Rather than teach every reader to fall back to the
    /// appropriate container field per media type, we write a canonical
    /// <c>title</c> on the parent Work at score-time, sourced from the
    /// appropriate container field:
    ///
    ///   • Music     → album
    ///   • TV        → show_name
    ///   • Comics    → series
    ///   • Books     → series
    ///   • Audiobooks → series
    ///
    /// Skipped when (a) the parent target collapses to the self target
    /// (standalone media — movies, single-volume books) because the asset's
    /// own title already lives on that row; (b) the parent claims already
    /// contain a title; or (c) no suitable container field is present.
    /// Confidence matches the source claim so Tier-D tie-breaks still work.
    /// </summary>
    private static void MaybeSynthesizeParentTitle(
        IReadOnlyList<ProviderClaim> allClaims,
        List<ProviderClaim> parentClaims,
        WorkLineage lineage,
        ILogger? logger)
    {
        // Already has a title claim in the parent batch — nothing to do.
        if (parentClaims.Any(c =>
                string.Equals(c.Key, MetadataFieldConstants.Title,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var isStandalone = lineage.TargetForParentScope == lineage.TargetForSelfScope;

        ProviderClaim? source = null;
        string? sourceKey = null;

        if (isStandalone)
        {
            // Standalone media (Books, Audiobooks, Movies):
            // title is self-scoped and went to the asset row, not the Work
            // row. Copy it from the full claim list so the Work row renders
            // with a real title instead of "Untitled".
            source = allClaims
                .Where(c => string.Equals(c.Key, MetadataFieldConstants.Title,
                                StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(c.Value))
                .OrderByDescending(c => c.Confidence)
                .FirstOrDefault();
            sourceKey = MetadataFieldConstants.Title;
        }
        else
        {
            // Hierarchical parent (album, TV show, comic series, book
            // series): mint a title from the container field.
            sourceKey = lineage.MediaType switch
            {
                MediaType.Music => MetadataFieldConstants.Album,
                MediaType.TV => MetadataFieldConstants.ShowName,
                MediaType.Comics => MetadataFieldConstants.Series,
                MediaType.Books => MetadataFieldConstants.Series,
                MediaType.Audiobooks => MetadataFieldConstants.Series,
                _ => null,
            };
            if (sourceKey is null)
            {
                return;
            }

            source = parentClaims
                .Where(c => string.Equals(c.Key, sourceKey, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(c.Value))
                .OrderByDescending(c => c.Confidence)
                .FirstOrDefault();
        }

        if (source is null)
        {
            return;
        }

        parentClaims.Add(new ProviderClaim(
            Key: MetadataFieldConstants.Title,
            Value: source.Value,
            Confidence: source.Confidence));

        logger?.LogDebug(
            "Synthesized parent-Work title '{Title}' from {SourceKey} for parent {ParentWorkId} ({MediaType}, standalone={Standalone})",
            source.Value, sourceKey, lineage.TargetForParentScope, lineage.MediaType, isStandalone);
    }

    /// <summary>
    /// Builds provider weight maps from provider configs and registered providers.
    /// </summary>
    public static (IReadOnlyDictionary<Guid, double> Weights,
                     IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, double>>? FieldWeights)
        BuildWeightMaps(
            IReadOnlyList<MediaEngine.Domain.Configuration.ProviderConfiguration> providerConfigs,
            IEnumerable<IExternalMetadataProvider> providers)
    {
        var weights = new Dictionary<Guid, double>();
        Dictionary<Guid, IReadOnlyDictionary<string, double>>? fieldWeights = null;

        foreach (var provider in providers)
        {
            var provConfig = providerConfigs
                .FirstOrDefault(p => string.Equals(p.Name, provider.Name,
                    StringComparison.OrdinalIgnoreCase));

            if (provConfig is null)
            {
                continue;
            }

            if (!provConfig.Enabled)
            {
                continue;
            }

            weights[provider.ProviderId] = provConfig.Weight;

            if (provConfig.FieldWeights.Count > 0)
            {
                fieldWeights ??= new Dictionary<Guid, IReadOnlyDictionary<string, double>>();
                fieldWeights[provider.ProviderId] =
                    (IReadOnlyDictionary<string, double>)provConfig.FieldWeights;
            }
        }

        return (weights, fieldWeights);
    }
}
