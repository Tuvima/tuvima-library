using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Providers;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Workers;

public sealed record RetailArtworkReplacementResult(
    bool ArtworkChanged,
    bool CoverDownloaded,
    Guid OwnerEntityId,
    Guid? PreferredVariantId,
    int RemovedVariantCount,
    string Message);

/// <summary>
/// Downloads cover art from provider URLs, computes perceptual hashes for dedup,
/// and stamps measured artwork metadata plus display renditions.
///
/// Extracted from <c>HydrationPipelineService.PersistCoverFromUrlAsync</c>.
/// </summary>
public sealed class CoverArtWorker
{
    private readonly IMediaAssetRepository _assetRepo;
    private readonly IEntityAssetRepository? _entityAssetRepo;
    private readonly ICanonicalValueRepository _canonicalRepo;
    private readonly IWorkRepository _workRepo;
    private readonly IImageCacheRepository _imageCache;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AssetPathService _assetPaths;
    private readonly IAssetExportService? _assetExportService;
    private readonly ICoverArtHashService? _coverArtHash;
    private readonly IEventPublisher? _eventPublisher;
    private readonly ImageDownloadCoordinator _imageDownloadCoordinator;
    private readonly ILogger<CoverArtWorker> _logger;

    public CoverArtWorker(
        IMediaAssetRepository assetRepo,
        ICanonicalValueRepository canonicalRepo,
        IWorkRepository workRepo,
        IImageCacheRepository imageCache,
        IHttpClientFactory httpFactory,
        AssetPathService assetPaths,
        ILogger<CoverArtWorker> logger,
        IAssetExportService? assetExportService = null,
        ICoverArtHashService? coverArtHash = null,
        IEntityAssetRepository? entityAssetRepo = null,
        IEventPublisher? eventPublisher = null,
        ImageDownloadCoordinator? imageDownloadCoordinator = null)
    {
        _assetRepo = assetRepo;
        _entityAssetRepo = entityAssetRepo;
        _canonicalRepo = canonicalRepo;
        _workRepo = workRepo;
        _imageCache = imageCache;
        _httpFactory = httpFactory;
        _assetPaths = assetPaths;
        _assetExportService = assetExportService;
        _logger = logger;
        _coverArtHash = coverArtHash;
        _eventPublisher = eventPublisher;
        _imageDownloadCoordinator = imageDownloadCoordinator ?? ImageDownloadCoordinator.Shared;
    }

    /// <summary>
    /// Replaces provider-managed artwork after a user selects a different retail identity.
    /// The new cover is downloaded before stale provider variants are removed, so readers
    /// never observe the old identity after this call completes. User-uploaded variants are
    /// retained as alternatives.
    /// </summary>
    public async Task<RetailArtworkReplacementResult> ReplaceProviderArtworkAsync(
        Guid entityId,
        string? coverUrl,
        string providerSource,
        Guid providerId,
        CancellationToken ct)
    {
        if (_entityAssetRepo is null)
        {
            return new RetailArtworkReplacementResult(
                false,
                false,
                entityId,
                null,
                0,
                "Artwork storage is unavailable for immediate replacement.");
        }

        var lineage = await _workRepo.GetLineageByAssetAsync(entityId, ct);
        var ownerEntityId = ResolveCoverOwnerEntityId(entityId, lineage);
        var removedScopedVariantCount = await ClearStaleLowerScopeCoverStateAsync(
            entityId,
            lineage,
            ownerEntityId,
            ct);
        var before = await _entityAssetRepo.GetByEntityAsync(ownerEntityId.ToString(), ct: ct);
        var staleProviderArtwork = before
            .Where(IsReplaceableProviderArtwork)
            .ToList();

        var normalizedCoverUrl = NormalizeRemoteArtworkUrl(coverUrl);
        EntityAsset? replacement = null;
        if (normalizedCoverUrl is not null)
        {
            var now = DateTimeOffset.UtcNow;
            await _canonicalRepo.UpsertBatchAsync(
            [
                new CanonicalValue
                {
                    EntityId = ownerEntityId,
                    Key = MetadataFieldConstants.Cover,
                    Value = normalizedCoverUrl,
                    LastScoredAt = now,
                    WinningProviderId = providerId,
                },
                new CanonicalValue
                {
                    EntityId = ownerEntityId,
                    Key = MetadataFieldConstants.CoverSource,
                    Value = providerSource,
                    LastScoredAt = now,
                    WinningProviderId = providerId,
                },
                .. ArtworkCanonicalHelper.CreateFlags(
                    ownerEntityId,
                    coverState: "pending",
                    coverSource: null,
                    heroState: "pending",
                    lastScoredAt: now,
                    settled: false),
            ], ct);

            try
            {
                await DownloadAndPersistAsync(
                    entityId,
                    wikidataQid: null,
                    ct,
                    explicitCoverUrl: normalizedCoverUrl);
                replacement = (await _entityAssetRepo.GetByEntityAsync(ownerEntityId.ToString(), "CoverArt", ct))
                    .FirstOrDefault(asset =>
                        string.Equals(asset.ImageUrl, normalizedCoverUrl, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(asset.LocalImagePath)
                        && File.Exists(asset.LocalImagePath));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Immediate retail artwork download failed for entity {EntityId} from {CoverUrl}",
                    entityId,
                    normalizedCoverUrl);
                await _canonicalRepo.UpsertBatchAsync(
                    ArtworkCanonicalHelper.CreateFlags(
                        ownerEntityId,
                        coverState: "missing",
                        coverSource: "provider_download_failed",
                        heroState: "missing",
                        lastScoredAt: DateTimeOffset.UtcNow,
                        settled: true),
                    ct);
            }
        }
        else
        {
            await _canonicalRepo.DeleteByKeyAsync(ownerEntityId, MetadataFieldConstants.Cover, ct);
            await _canonicalRepo.DeleteByKeyAsync(ownerEntityId, MetadataFieldConstants.CoverSource, ct);
            await _canonicalRepo.UpsertBatchAsync(
                ArtworkCanonicalHelper.CreateFlags(
                    ownerEntityId,
                    coverState: "missing",
                    coverSource: "selected_match_has_no_cover",
                    heroState: "missing",
                    lastScoredAt: DateTimeOffset.UtcNow,
                    settled: true),
                ct);
        }

        var variantsToRemove = staleProviderArtwork
            .Where(asset => replacement is null || asset.Id != replacement.Id)
            .ToList();
        foreach (var stale in variantsToRemove)
        {
            await ArtworkVariantHelper.DeleteVariantAndUnusedFilesAsync(_entityAssetRepo, stale, _assetPaths, _logger, ct);
        }

        var affectedTypes = variantsToRemove
            .Select(asset => asset.AssetTypeValue)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var assetType in affectedTypes)
        {
            if (string.Equals(assetType, "CoverArt", StringComparison.OrdinalIgnoreCase) && replacement is not null)
            {
                continue;
            }

            var survivors = await _entityAssetRepo.GetByEntityAsync(ownerEntityId.ToString(), assetType, ct);
            var survivor = survivors.FirstOrDefault(asset => asset.IsPreferred)
                           ?? survivors.FirstOrDefault(asset => asset.IsUserOverride)
                           ?? survivors.FirstOrDefault();
            if (survivor is not null)
            {
                await _entityAssetRepo.SetPreferredAsync(survivor.Id, ct);
                await _canonicalRepo.UpsertBatchAsync(
                    ArtworkCanonicalHelper.CreatePreferredAssetCanonicals(ownerEntityId, survivor, DateTimeOffset.UtcNow),
                    ct);
                if (_assetExportService is not null)
                {
                    await _assetExportService.ReconcileArtworkAsync(survivor.EntityId, survivor.EntityType, survivor.AssetTypeValue, ct);
                }

                continue;
            }

            await ClearArtworkDisplayCanonicalsAsync(ownerEntityId, assetType, ct);
            if (_assetExportService is not null)
            {
                await _assetExportService.ClearArtworkExportAsync(ownerEntityId.ToString(), "Work", assetType, ct);
            }
        }

        if (replacement is null && affectedTypes.All(type => !string.Equals(type, "CoverArt", StringComparison.OrdinalIgnoreCase)))
        {
            await ClearArtworkDisplayCanonicalsAsync(ownerEntityId, "CoverArt", ct);
        }

        // The artwork state always changes for a supported retail replacement:
        // it either points at the new managed cover or is explicitly cleared.
        const bool changed = true;
        var message = replacement is not null
            ? "Retail artwork replaced immediately."
            : normalizedCoverUrl is null
                ? "The previous retail artwork was removed; the selected match did not provide a cover."
                : "The previous retail artwork was removed, but the selected cover could not be downloaded.";

        _logger.LogInformation(
            "Retail artwork replacement for entity {EntityId}: owner={OwnerEntityId}, downloaded={Downloaded}, removed={RemovedCount}, provider={Provider}",
            entityId,
            ownerEntityId,
            replacement is not null,
            variantsToRemove.Count + removedScopedVariantCount,
            providerSource);

        return new RetailArtworkReplacementResult(
            changed,
            replacement is not null,
            ownerEntityId,
            replacement?.Id,
            variantsToRemove.Count + removedScopedVariantCount,
            message);
    }

    /// <summary>
    /// Downloads cover art from the provider URL stored in canonicals
    /// and persists measured artwork metadata plus renditions.
    /// </summary>
    public async Task DownloadAndPersistAsync(
        Guid entityId,
        string? wikidataQid,
        CancellationToken ct,
        string? explicitCoverUrl = null)
    {
        var canonicals = await _canonicalRepo.GetByEntityAsync(entityId, ct);
        var lineage = await _workRepo.GetLineageByAssetAsync(entityId, ct);
        var ownerEntityId = ResolveCoverOwnerEntityId(entityId, lineage);

        // Manual retail replacement passes the exact selected URL. This must
        // bypass normal lineage lookup because an older asset-scope canonical
        // can otherwise win before the newly selected work-scope cover.
        var normalizedExplicitCoverUrl = NormalizeRemoteArtworkUrl(explicitCoverUrl);
        var coverLookup = normalizedExplicitCoverUrl is not null
            ? new CoverLookupResult(
                normalizedExplicitCoverUrl,
                await _canonicalRepo.GetByEntityAsync(ownerEntityId, ct).ConfigureAwait(false))
            : await FindCoverUrlWithLineageAsync(entityId, canonicals, lineage, ct)
                .ConfigureAwait(false);
        var coverUrl = coverLookup.Url;
        var coverSourceCanonicals = coverLookup.Canonicals;

        // Parent-scope fallback. For TV / Movies / Music,
        // ClaimScopeCatalog routes `cover` and `cover_url` to ClaimScope.Parent,
        // so the retail provider's cover URL lands on the parent Work id
        // (album / show / movie Work) — not the media asset id. Without this
        // fallback, every one of those media types silently returns "No cover
        // URL found" and the Dashboard shows a placeholder.
        if (string.IsNullOrEmpty(coverUrl))
        {
            try
            {
                if (lineage is not null && lineage.RootParentWorkId != entityId)
                {
                    var parentCanonicals = await _canonicalRepo.GetByEntityAsync(
                        lineage.RootParentWorkId, ct);
                    coverUrl = FindCoverUrl(parentCanonicals);
                    if (!string.IsNullOrEmpty(coverUrl))
                    {
                        _logger.LogDebug(
                            "Cover art: asset {EntityId} had no cover canonical; using parent Work {ParentId} cover URL",
                            entityId, lineage.RootParentWorkId);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex,
                    "Cover art: parent lineage lookup failed for entity {EntityId} — proceeding with self-scope only",
                    entityId);
            }
        }

        if (string.IsNullOrEmpty(coverUrl))
        {
            // A claim-less owner can still hold managed artwork: TheTVDB show posters arrive
            // through ImageEnrichmentService as entity assets, never as a `cover` claim.
            // Marking such an owner missing would overwrite valid artwork state.
            if (await TryRestoreStoredCoverStateAsync(ownerEntityId, ct).ConfigureAwait(false))
            {
                await PublishCoverHarvestedAsync(ownerEntityId, ct).ConfigureAwait(false);
                return;
            }

            await MarkCoverMissingAsync(ownerEntityId, "none", ct);
            _logger.LogDebug("No cover URL found for entity {EntityId}", entityId);
            return;
        }

        // Manual rematch and background enrichment can target the same URL at
        // the same time. Hold the URL lease through the durable recheck,
        // download/cache copy, and asset persistence so only one request wins.
        await using var downloadLease = await _imageDownloadCoordinator
            .AcquireAsync(coverUrl, ct)
            .ConfigureAwait(false);

        // Resolve output path.
        //
        // Side-by-side-with-Plex plan §D — prefer the per-file location next
        // to the media file (Plex/Jellyfin convention) so the same disk path
        // serves Tuvima, Plex, and Jellyfin without duplication. Fall back to
        // the current managed asset location under .data/assets/ when
        // the asset has no resolvable file path (e.g. pre-organize states or
        // entities that aren't backed by a file).
        string coverPath;

        // Look up the asset's current file path. The lookup is cheap (single
        // row by id) and is the gate that decides "per-file" vs "legacy".
        var existingCoverVariant = await FindExistingCoverVariantAsync(ownerEntityId, coverUrl, ct);
        var coverVariantId = existingCoverVariant?.Id ?? Guid.NewGuid();
        var coverExtension = InferImageExtension(coverUrl, "CoverArt");
        coverPath = _assetPaths.GetCentralAssetPath("Work", ownerEntityId, "CoverArt", coverVariantId, coverExtension);
        AssetPathService.EnsureDirectory(coverPath);

        // Skip if cover already exists
        if (File.Exists(coverPath))
        {
            var existingVariant = await EnsureCoverVariantAsync(
                ownerEntityId,
                coverVariantId,
                coverUrl,
                coverPath,
                InferCoverSource(coverSourceCanonicals, coverUrl),
                keepRankedPreferred: normalizedExplicitCoverUrl is null,
                canonicals,
                ct);
            await _canonicalRepo.UpsertBatchAsync(
                BuildCoverCanonicals(
                    ownerEntityId,
                    existingVariant,
                    InferCoverSource(coverSourceCanonicals, coverUrl)),
                ct);
            if (existingVariant is not null && _assetExportService is not null)
            {
                await _assetExportService.ReconcileArtworkAsync(existingVariant.EntityId, existingVariant.EntityType, existingVariant.AssetTypeValue, ct);
            }

            await PublishCoverHarvestedAsync(ownerEntityId, ct).ConfigureAwait(false);

            var titleForSkip = canonicals
                .FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase))?.Value
                ?? $"entity {entityId}";
            _logger.LogInformation(
                "Cover art: skipped '{Title}' — already cached on disk ({Path})",
                titleForSkip, coverPath);
            return;
        }

        {
            var titleForDownload = canonicals
                .FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase))?.Value
                ?? $"entity {entityId}";
            _logger.LogInformation(
                "Cover art: downloading '{Title}' from {Url} → {LocalPath}",
                titleForDownload, coverUrl, coverPath);
        }

        // Capture embedded cover pHash before downloading new one
        ulong? embeddedPhash = null;
        if (_coverArtHash is not null && File.Exists(coverPath))
        {
            var existingBytes = await File.ReadAllBytesAsync(coverPath, ct);
            var existingHash = Hashing.Sha256Hex(existingBytes);
            embeddedPhash = await _imageCache.GetPerceptualHashAsync(existingHash, ct);
        }

        // Reuse a previously downloaded source URL before issuing any network
        // request. The URL mapping is durable, so this also works after restart
        // and when another entity owns the original cached file.
        byte[] bytes;
        var sourceCachedPath = await _imageCache.FindBySourceUrlAsync(coverUrl, ct);
        if (!string.IsNullOrWhiteSpace(sourceCachedPath) && File.Exists(sourceCachedPath))
        {
            bytes = await File.ReadAllBytesAsync(sourceCachedPath, ct);
            _logger.LogDebug(
                "Cover art: reusing source URL cache for {Url} from {CachedPath}",
                coverUrl,
                sourceCachedPath);
        }
        else
        {
            try
            {
                using var client = _httpFactory.CreateClient("cover_download");
                using var response = await client.GetAsync(coverUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                bytes = await BoundedHttpContent.ReadImageAsync(response.Content, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to download cover from {Url} for entity {EntityId}", coverUrl, entityId);
                await MarkCoverMissingAsync(ownerEntityId, "provider_unavailable", ct);
                return;
            }
        }

        if (bytes.Length == 0)
        {
            await MarkCoverMissingAsync(ownerEntityId, "provider_empty", ct);
            return;
        }

        // Content-hash dedup
        var hash = Hashing.Sha256Hex(bytes);
        var cached = await _imageCache.FindByHashAsync(hash, ct);
        if (cached is not null && File.Exists(cached))
        {
            File.Copy(cached, coverPath, overwrite: true);
        }
        else
        {
            await BoundedHttpContent.WriteFileAtomicallyAsync(coverPath, bytes, ct);
        }

        // Always register the source URL. InsertAsync preserves the canonical
        // hash row and records this URL as an alias when the bytes already
        // existed under another path or URL.
        await _imageCache.InsertAsync(hash, coverPath, coverUrl, ct);

        // Compute perceptual hash
        if (_coverArtHash is not null && bytes.Length > 100)
        {
            var phash = await _coverArtHash.ComputeHashAsync(bytes, ct);
            if (phash.HasValue)
            {
                await _imageCache.SetPerceptualHashAsync(hash, phash.Value, ct);

                if (embeddedPhash.HasValue)
                {
                    var similarity = _coverArtHash.ComputeSimilarity(embeddedPhash.Value, phash.Value);
                    _logger.LogDebug(
                        "Cover pHash similarity for entity {EntityId}: {Similarity:F2}",
                        entityId, similarity);
                }
            }
        }

        var coverSource = InferCoverSource(coverSourceCanonicals, coverUrl);
        var coverVariant = await EnsureCoverVariantAsync(
            ownerEntityId,
            coverVariantId,
            coverUrl,
            coverPath,
            coverSource,
            keepRankedPreferred: normalizedExplicitCoverUrl is null,
            canonicals,
            ct);
        await _canonicalRepo.UpsertBatchAsync(
            BuildCoverCanonicals(
                ownerEntityId,
                coverVariant,
                coverSource),
            ct);
        if (coverVariant is not null && _assetExportService is not null)
        {
            await _assetExportService.ReconcileArtworkAsync(coverVariant.EntityId, coverVariant.EntityType, coverVariant.AssetTypeValue, ct);
        }

        await PublishCoverHarvestedAsync(ownerEntityId, ct).ConfigureAwait(false);

        {
            var titleForDone = canonicals
                .FirstOrDefault(c => string.Equals(c.Key, MetadataFieldConstants.Title, StringComparison.OrdinalIgnoreCase))?.Value
                ?? $"entity {entityId}";
            // NOTE: We deliberately do NOT write a cover_url canonical pointing at
            // /stream/{entityId}/cover here. The /stream URL is a display hint that the
            // Dashboard layer can build on the fly from the entity id; it is not
            // persisted state. The authoritative source URL (the provider URL) lives in
            // the original claims, and the disk location is owned by AssetPathService.
            _logger.LogInformation(
                "Cover art: downloaded poster for '{Title}' ({SizeKB:F1} KB) → {LocalPath}",
                titleForDone, bytes.Length / 1024.0, coverPath);
        }
    }

    private Task PublishCoverHarvestedAsync(Guid entityId, CancellationToken ct)
    {
        if (_eventPublisher is null)
        {
            return Task.CompletedTask;
        }

        return _eventPublisher.PublishAsync(
            SignalREvents.MetadataHarvested,
            new MetadataHarvestedEvent(
                entityId,
                "cover_art",
                [
                    MetadataFieldConstants.Cover,
                    MetadataFieldConstants.CoverUrl,
                    MetadataFieldConstants.CoverState,
                ]),
            ct);
    }

    /// <summary>
    /// Finds the first HTTP(S) cover URL in a list of canonical values,
    /// checking <c>cover</c> (provider poster) first and falling back to <c>cover_url</c>.
    /// </summary>
    private async Task<CoverLookupResult> FindCoverUrlWithLineageAsync(
        Guid entityId,
        IReadOnlyList<CanonicalValue> entityCanonicals,
        WorkLineage? lineage,
        CancellationToken ct)
    {
        var candidates = new List<(Guid EntityId, IReadOnlyList<CanonicalValue> Canonicals)>
        {
            (entityId, entityCanonicals)
        };

        if (lineage is not null)
        {
            foreach (var candidateId in new[]
                     {
                         lineage.TargetForSelfScope,
                         lineage.TargetForParentScope,
                         lineage.RootParentWorkId
                     }
                         .Where(id => id != entityId)
                         .Distinct())
            {
                try
                {
                    candidates.Add((candidateId, await _canonicalRepo.GetByEntityAsync(candidateId, ct)
                        .ConfigureAwait(false)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(
                        ex,
                        "Cover art: scoped canonical lookup failed for entity {EntityId}; continuing with other scopes",
                        candidateId);
                }
            }
        }

        foreach (var candidate in candidates)
        {
            var url = FindCoverUrl(candidate.Canonicals);
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (candidate.EntityId != entityId)
            {
                _logger.LogDebug(
                    "Cover art: asset {EntityId} had no downloadable cover canonical; using scoped entity {SourceEntityId} cover URL",
                    entityId,
                    candidate.EntityId);
            }

            return new CoverLookupResult(url, candidate.Canonicals);
        }

        return new CoverLookupResult(null, entityCanonicals);
    }

    private static string? FindCoverUrl(IReadOnlyList<CanonicalValue> canonicals)
    {
        // Prefer the provider poster URL (cover) over local streaming URLs (cover_url).
        // The "cover" claim comes from retail providers (TMDB, Apple, ComicVine) and
        // contains the actual poster artwork. The "cover_url" claim may contain a
        // /stream/ URL (not downloadable) or a stale supplementary image.
        return canonicals
            .Where(c => string.Equals(c.Key, MetadataFieldConstants.Cover, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Value)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v) && v.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            ?? canonicals
            .Where(c => string.Equals(c.Key, MetadataFieldConstants.CoverUrl, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Value)
            .FirstOrDefault(v => !string.IsNullOrEmpty(v) && v.StartsWith("http", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// When the owner already has a preferred managed cover on disk, re-asserts its
    /// present state and display canonicals instead of reporting the cover as missing.
    /// </summary>
    private async Task<bool> TryRestoreStoredCoverStateAsync(Guid ownerEntityId, CancellationToken ct)
    {
        if (_entityAssetRepo is null)
        {
            return false;
        }

        var stored = (await _entityAssetRepo.GetByEntityAsync(ownerEntityId.ToString(), "CoverArt", ct)
                .ConfigureAwait(false))
            .FirstOrDefault(asset => asset.IsPreferred
                && !string.IsNullOrWhiteSpace(asset.LocalImagePath)
                && File.Exists(asset.LocalImagePath));
        if (stored is null)
        {
            return false;
        }

        var coverSource = string.Equals(stored.SourceProvider, "user_upload", StringComparison.OrdinalIgnoreCase)
            ? "manual"
            : !string.IsNullOrWhiteSpace(stored.SourceProvider)
                ? "provider"
                : "stored";
        await _canonicalRepo.UpsertBatchAsync(BuildCoverCanonicals(ownerEntityId, stored, coverSource), ct)
            .ConfigureAwait(false);
        _logger.LogDebug(
            "Cover art: owner {OwnerEntityId} has no cover claim but keeps stored {Provider} artwork {AssetId}",
            ownerEntityId, stored.SourceProvider, stored.Id);
        return true;
    }

    private Task MarkCoverMissingAsync(Guid entityId, string coverSource, CancellationToken ct)
        => _canonicalRepo.UpsertBatchAsync(
            ArtworkCanonicalHelper.CreateFlags(
                entityId,
                coverState: "missing",
                coverSource: coverSource,
                heroState: "missing",
                lastScoredAt: DateTimeOffset.UtcNow,
                settled: true),
            ct);

    // Written only while an owner has no cover. A later successful download is the cover's source, so
    // whether an earlier attempt failed (a sibling's pass, a sweep that ran first) must not stay visible.
    private static readonly HashSet<string> MissingCoverSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "provider_unavailable", "provider_empty", "provider_download_failed", "selected_match_has_no_cover",
    };

    internal static string InferCoverSource(IReadOnlyList<CanonicalValue> canonicals, string? coverUrl)
    {
        var existing = canonicals.FirstOrDefault(c =>
            string.Equals(c.Key, MetadataFieldConstants.CoverSource, StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(existing) && !MissingCoverSources.Contains(existing))
        {
            return existing;
        }

        if (!string.IsNullOrWhiteSpace(coverUrl) && coverUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return "provider";
        }

        return "existing";
    }

    private sealed record CoverLookupResult(string? Url, IReadOnlyList<CanonicalValue> Canonicals);

    private static Guid ResolveCoverOwnerEntityId(Guid entityId, WorkLineage? lineage)
    {
        if (lineage is null)
        {
            return entityId;
        }

        return lineage.MediaType switch
        {
            MediaType.Books or MediaType.Audiobooks or MediaType.Comics => lineage.TargetForSelfScope,
            _ => lineage.TargetForParentScope,
        };
    }

    /// <summary>
    /// Provider artwork a retail rematch replaces. That includes a provider variant the user picked, since
    /// it belongs to the earlier identity; uploads and artwork-library choices (local addresses) stay.
    /// </summary>
    private static bool IsReplaceableProviderArtwork(EntityAsset asset) =>
        string.Equals(asset.AssetClassValue, "Artwork", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(asset.SourceProvider, "user_upload", StringComparison.OrdinalIgnoreCase)
        && (!asset.IsUserOverride || NormalizeRemoteArtworkUrl(asset.ImageUrl) is not null);

    private async Task<int> ClearStaleLowerScopeCoverStateAsync(
        Guid entityId,
        WorkLineage? lineage,
        Guid ownerEntityId,
        CancellationToken ct)
    {
        var staleEntityIds = new HashSet<Guid>();
        if (entityId != ownerEntityId)
        {
            staleEntityIds.Add(entityId);
        }

        // Parent-owned artwork (albums, shows, and movies) must also clear a
        // stale child-work cover. Self-owned artwork (books, audiobooks, and
        // comic issues) deliberately leaves the parent shelf artwork intact.
        if (lineage is not null
            && ownerEntityId == lineage.TargetForParentScope
            && lineage.TargetForSelfScope != ownerEntityId)
        {
            staleEntityIds.Add(lineage.TargetForSelfScope);
        }

        var removed = 0;
        foreach (var staleEntityId in staleEntityIds)
        {
            var staleAssets = (await _entityAssetRepo!.GetByEntityAsync(
                    staleEntityId.ToString(),
                    "CoverArt",
                    ct))
                .Where(IsReplaceableProviderArtwork)
                .ToList();
            foreach (var staleAsset in staleAssets)
            {
                await ArtworkVariantHelper.DeleteVariantAndUnusedFilesAsync(_entityAssetRepo, staleAsset, _assetPaths, _logger, ct);
                removed++;
            }

            await _canonicalRepo.DeleteByKeyAsync(staleEntityId, MetadataFieldConstants.Cover, ct);
            await _canonicalRepo.DeleteByKeyAsync(staleEntityId, MetadataFieldConstants.CoverSource, ct);
            await ClearArtworkDisplayCanonicalsAsync(staleEntityId, "CoverArt", ct);
        }

        return removed;
    }

    private static string? NormalizeRemoteArtworkUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri.AbsoluteUri;
    }

    private async Task ClearArtworkDisplayCanonicalsAsync(Guid entityId, string assetType, CancellationToken ct)
    {
        foreach (var key in ArtworkCanonicalHelper.DisplayCanonicalKeys(assetType))
        {
            await _canonicalRepo.DeleteByKeyAsync(entityId, key, ct);
        }
    }

    /// <summary>
    /// Stores the cover pass's variant and returns the owner's preferred cover afterwards. With
    /// <paramref name="keepRankedPreferred"/>, a preferred poster chosen by the user or by the ranked
    /// images pass stays preferred: the cover pass only fills the poster slot, so running it after the
    /// images pass (a maintenance sweep that ran first, a later re-hydration) leaves the same poster as
    /// running it before. An explicit retail rematch passes false so the selected cover takes over.
    /// <paramref name="itemCanonicals"/> are the item's own values, read for its current bridge id.
    /// </summary>
    private async Task<EntityAsset?> EnsureCoverVariantAsync(
        Guid entityId,
        Guid variantId,
        string? providerUrl,
        string coverPath,
        string sourceProvider,
        bool keepRankedPreferred,
        IReadOnlyList<CanonicalValue> itemCanonicals,
        CancellationToken ct)
    {
        if (_entityAssetRepo is null)
        {
            return null;
        }

        var variants = await _entityAssetRepo.GetByEntityAsync(entityId.ToString(), "CoverArt", ct);
        var existing = variants
            .FirstOrDefault(asset =>
                (!string.IsNullOrWhiteSpace(providerUrl)
                 && string.Equals(asset.ImageUrl, providerUrl, StringComparison.OrdinalIgnoreCase))
                || string.Equals(asset.LocalImagePath, coverPath, StringComparison.OrdinalIgnoreCase));

        var variant = existing ?? new EntityAsset
        {
            Id = variantId,
            EntityId = entityId.ToString(),
            EntityType = "Work",
            AssetTypeValue = "CoverArt",
            CreatedAt = DateTimeOffset.UtcNow,
            AssetClassValue = "Artwork",
            StorageLocationValue = "Central",
            OwnerScope = "Work",
        };

        var currentPreferred = keepRankedPreferred
            ? variants.FirstOrDefault(asset => asset.IsPreferred && asset.Id != variant.Id)
            : null;
        var rankedPreferred = currentPreferred is not null
            && await OutranksCoverPassAsync(currentPreferred, entityId, itemCanonicals, ct).ConfigureAwait(false)
                ? currentPreferred
                : null;

        variant.ImageUrl = providerUrl;
        variant.LocalImagePath = coverPath;
        variant.SourceProvider = sourceProvider;
        variant.IsPreferred = rankedPreferred is null;
        // An ordinary pass keeps the user's pick of this same image; an explicit rematch replaces it.
        variant.IsUserOverride = keepRankedPreferred && existing?.IsUserOverride == true;
        variant.IsLocallyExported = false;
        variant.IsPreferredExported = false;
        ArtworkVariantHelper.StampMetadataAndRenditions(variant, _assetPaths);

        await _entityAssetRepo.UpsertAsync(variant, ct);
        if (rankedPreferred is not null)
        {
            _logger.LogDebug(
                "Cover art: owner {OwnerEntityId} keeps preferred {Provider} poster {PreferredId}; stored {Url} as an alternative",
                entityId, rankedPreferred.SourceProvider, rankedPreferred.Id, providerUrl);
            return rankedPreferred;
        }

        await _entityAssetRepo.SetPreferredAsync(variant.Id, ct);
        return variant;
    }

    // Providers whose posters ImageEnrichmentService chooses across every candidate, with the bridge id
    // the choice belongs to and the canonical in which that pass records the id it chose the poster for.
    private static readonly Dictionary<string, (string BridgeKey, string ChosenForKey)> RankedArtworkProviders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["tmdb"] = (BridgeIdKeys.TmdbId, ArtworkCanonicalHelper.PosterChoiceKey("tmdb")),
            ["tvdb"] = (BridgeIdKeys.TvdbId, ArtworkCanonicalHelper.PosterChoiceKey("tvdb")),
        };

    /// <summary>
    /// A held user choice always outranks the cover pass. A held images-pass poster does while that pass ran
    /// for the item's current identity; after a rematch or re-identification the cover pass takes over again.
    /// </summary>
    private async Task<bool> OutranksCoverPassAsync(
        EntityAsset preferred,
        Guid ownerEntityId,
        IReadOnlyList<CanonicalValue> itemCanonicals,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(preferred.LocalImagePath) || !File.Exists(preferred.LocalImagePath))
        {
            return false;
        }

        if (preferred.IsUserOverride
            || string.Equals(preferred.SourceProvider, "user_upload", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (preferred.SourceProvider is not { } provider
            || !RankedArtworkProviders.TryGetValue(provider, out var keys))
        {
            return false;
        }

        var ownerCanonicals = await _canonicalRepo.GetByEntityAsync(ownerEntityId, ct).ConfigureAwait(false);
        var chosenFor = CanonicalValueOf(ownerCanonicals, keys.ChosenForKey);
        var currentId = CanonicalValueOf(ownerCanonicals, keys.BridgeKey) ?? CanonicalValueOf(itemCanonicals, keys.BridgeKey);
        return !string.IsNullOrWhiteSpace(chosenFor)
            && string.Equals(chosenFor, currentId, StringComparison.OrdinalIgnoreCase);
    }

    private static string? CanonicalValueOf(IReadOnlyList<CanonicalValue> canonicals, string key) =>
        canonicals.FirstOrDefault(value =>
            string.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(value.Value))?.Value;

    private async Task<EntityAsset?> FindExistingCoverVariantAsync(Guid entityId, string? providerUrl, CancellationToken ct)
    {
        if (_entityAssetRepo is null || string.IsNullOrWhiteSpace(providerUrl))
        {
            return null;
        }

        return (await _entityAssetRepo.GetByEntityAsync(entityId.ToString(), "CoverArt", ct))
            .FirstOrDefault(asset =>
                string.Equals(asset.ImageUrl, providerUrl, StringComparison.OrdinalIgnoreCase));
    }

    private static string InferImageExtension(string? sourceUrl, string assetType)
    {
        if (string.Equals(assetType, "Logo", StringComparison.OrdinalIgnoreCase))
        {
            return ".png";
        }

        return MediaMimeTypes.InferImageExtension(sourceUrl) ?? ".jpg";
    }

    private static IReadOnlyList<CanonicalValue> BuildCoverCanonicals(
        Guid entityId,
        EntityAsset? preferredVariant,
        string? coverSource)
    {
        var values = new List<CanonicalValue>();

        if (preferredVariant is not null)
        {
            values.AddRange(ArtworkCanonicalHelper.CreatePreferredAssetCanonicals(
                entityId,
                preferredVariant,
                DateTimeOffset.UtcNow));
        }

        values.AddRange(ArtworkCanonicalHelper.CreateFlags(
            entityId,
            coverState: "present",
            coverSource: coverSource,
            heroState: "missing",
            lastScoredAt: DateTimeOffset.UtcNow,
            settled: true));

        return values;
    }
}
