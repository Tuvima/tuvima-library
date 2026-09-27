using System.Globalization;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using ArtworkEditorEnvelope = MediaEngine.Contracts.Metadata.ArtworkEditorDto;
using ArtworkSlotEnvelope = MediaEngine.Contracts.Metadata.ArtworkSlotDto;
using ArtworkVariantEnvelope = MediaEngine.Contracts.Metadata.ArtworkVariantDto;
using EditorScopeResolution = MediaEngine.Api.Endpoints.MetadataEndpoints.EditorScopeResolution;
using ProviderArtworkRefreshEnvelope = MediaEngine.Contracts.Metadata.ProviderArtworkRefreshDto;
using ProviderArtworkRefreshTarget = MediaEngine.Api.Endpoints.MetadataEndpoints.ProviderArtworkRefreshTarget;

namespace MediaEngine.Api.Services.Metadata;

/// <summary>
/// The artwork-scope cluster extracted from <c>MetadataEndpoints.cs</c> (Stage 5A wave 2,
/// packet f2, Job 3): artwork slot policy, container/series/season/artist folder-path
/// resolution, canonical artwork sync, and provider artwork bridge/refresh resolution.
///
/// Methods that need repository access are constructor-injected instance methods (all
/// dependencies were previously threaded through as per-call parameters on free-standing
/// statics in <c>MetadataEndpoints.cs</c> — this only changes how they are supplied, not
/// the logic). Pure formatting/policy helpers with no repository dependency remain
/// <c>static</c> so callers that don't otherwise need an injected instance (e.g.
/// <c>MetadataEndpoints.BuildEditorScopes</c>) can call them directly.
///
/// <see cref="MetadataEndpoints.NormalizeEditorMediaType"/>, <see cref="MetadataEndpoints.GetCanonicalValue"/>,
/// and <see cref="MetadataEndpoints.BuildArtworkVariantStreamUrl"/> remain defined in
/// <c>MetadataEndpoints.cs</c> (shared with editor-scope resolution / kept as the endpoint's
/// URL builder per the packet brief) and were bumped from <c>private</c> to <c>internal</c>
/// so this service can call them.
/// </summary>
internal sealed class ArtworkScopeService(
    ICanonicalValueRepository canonicalRepo,
    IEntityAssetRepository entityAssetRepo,
    ILibraryItemRepository libraryItemRepo,
    IWorkRepository workRepo,
    IMediaAssetRepository mediaAssetRepo,
    IMetadataEditorRepository metadataData,
    AssetPathService assetPathService,
    IConfigurationLoader configurationLoader)
{
    public async Task<ArtworkEditorEnvelope> BuildScopedArtworkEnvelopeAsync(
        EditorScopeResolution scope,
        CancellationToken ct)
    {
        var slotTypes = GetScopedArtworkSlots(scope.MediaType, scope.ScopeId);
        if (scope.ArtworkOwnerEntityId is null || slotTypes.Count == 0)
        {
            return new ArtworkEditorEnvelope(scope.FieldEntityId, []);
        }

        var assets = await entityAssetRepo.GetByEntityAsync(scope.ArtworkOwnerEntityId.Value.ToString(), null, ct);
        var canonicals = await canonicalRepo.GetByEntityAsync(scope.ArtworkOwnerEntityId.Value, ct);
        var detail = string.Equals(scope.ArtworkOwnerEntityKind, "Work", StringComparison.OrdinalIgnoreCase)
            ? await libraryItemRepo.GetDetailAsync(scope.ArtworkOwnerEntityId.Value, ct)
            : null;

        var payload = slotTypes.Select(assetType =>
        {
            var variants = assets
                .Where(asset => string.Equals(asset.AssetTypeValue, assetType, StringComparison.OrdinalIgnoreCase))
                .GroupBy(BuildArtworkVariantIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(asset => asset.IsPreferred)
                    .ThenByDescending(asset => asset.CreatedAt)
                    .First())
                .OrderByDescending(asset => asset.IsPreferred)
                .ThenByDescending(asset => asset.CreatedAt)
                .Select(MapArtworkVariant)
                .ToList();

            // Structural shelves render their owned stack unless they have a managed cover.
            // A representative child's detail cover must never become a synthetic shelf override.
            var automaticGroup = scope.ScopeId == "series" && scope.MediaType != "TV";
            var preferredUrl = automaticGroup ? null : GetArtworkCanonicalValue(canonicals, assetType)
                               ?? GetArtworkDetailUrl(detail, assetType);

            if (!string.IsNullOrWhiteSpace(preferredUrl)
                && !variants.Any(variant => string.Equals(variant.ImageUrl, preferredUrl, StringComparison.OrdinalIgnoreCase)))
            {
                variants.Insert(0, new ArtworkVariantEnvelope(
                    Guid.Empty,
                    assetType,
                    preferredUrl,
                    true,
                    InferSyntheticArtworkOrigin(canonicals, assetType, detail?.ArtworkSource),
                    ProviderName: null,
                    CanDelete: false,
                    CreatedAt: null));
            }

            return new ArtworkSlotEnvelope(assetType, variants);
        }).ToList();

        return new ArtworkEditorEnvelope(scope.ArtworkOwnerEntityId.Value, payload);
    }

    public static IReadOnlyList<string> GetScopedArtworkSlots(string mediaType, string scopeId) =>
        (MetadataEndpoints.NormalizeEditorMediaType(mediaType), scopeId) switch
        {
            ("TV", "series") =>
            [
                "CoverArt",
                "Background",
                "Logo",
            ],
            ("TV", "season") =>
            [
                "SeasonPoster",
                "SeasonThumb",
            ],
            ("Movies", "series") or ("Books", "series") or ("Audiobooks", "series") or ("Comics", "series") => ["CoverArt"],
            ("Movies", "item") =>
            [
                "CoverArt",
                "Background",
                "Logo",
            ],
            ("TV", "episode") =>
            [
                "EpisodeStill",
            ],
            ("Music", "album") =>
            [
                "CoverArt",
                "Background",
                "Logo",
            ],
            ("Books", "book" or "item") or ("Audiobooks", "audiobook" or "item") or ("Comics", "issue" or "item") =>
            [
                "CoverArt",
                "Background",
                "Logo",
            ],
            _ => [],
        };

    public static bool IsProviderArtworkRefreshSupported(EditorScopeResolution scope) =>
        (MetadataEndpoints.NormalizeEditorMediaType(scope.MediaType), scope.ScopeId) is
            ("Movies", "item")
            or ("TV", "series")
            or ("TV", "season")
            or ("TV", "episode")
            or ("Music", "album")
            or ("Books", "book" or "item")
            or ("Audiobooks", "audiobook" or "item")
            or ("Comics", "issue" or "item");

    public async Task<ProviderArtworkRefreshTarget> ResolveProviderArtworkRefreshTargetAsync(
        EditorScopeResolution scope,
        CancellationToken ct, bool discoveryOnly = false)
    {
        var representativeAssetId = await metadataData.ResolveRepresentativeAssetAsync(
            [scope.FieldEntityId, scope.ArtworkOwnerEntityId ?? Guid.Empty],
            ct);
        if (representativeAssetId is null)
        {
            return ProviderArtworkRefreshTarget.Skip(CreateProviderArtworkRefreshEnvelope(
                status: "Skipped",
                skippedReason: "missing_representative_asset",
                message: "No owned media file was found for this artwork scope.",
                mediaType: scope.MediaType));
        }

        var representativeAsset = await mediaAssetRepo.FindByIdAsync(representativeAssetId.Value, ct);
        if (representativeAsset is not null
            && !string.IsNullOrWhiteSpace(representativeAsset.LibraryId))
        {
            LibraryFolderConfig? library;
            try
            {
                library = configurationLoader.LoadLibraries().Libraries.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, representativeAsset.LibraryId, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return ProviderArtworkRefreshTarget.Skip(CreateProviderArtworkRefreshEnvelope(
                    status: "Skipped",
                    skippedReason: "library_policy_unavailable",
                    message: "The library metadata policy could not be read, so no external artwork provider was contacted.",
                    mediaType: scope.MediaType));
            }
            if (library is not null && LibraryMetadataPolicies.BypassesExternalIdentity(library.MetadataPolicy))
            {
                return ProviderArtworkRefreshTarget.Skip(CreateProviderArtworkRefreshEnvelope(
                    status: "Skipped",
                    skippedReason: "external_lookup_blocked",
                    message: "This library uses local-only or manual metadata, so no external artwork provider was contacted.",
                    mediaType: scope.MediaType));
            }
        }

        if (discoveryOnly && MetadataEndpoints.NormalizeEditorMediaType(scope.MediaType) == "Comics")
            return new ProviderArtworkRefreshTarget(representativeAssetId, null, null, null);

        var lineage = await workRepo.GetLineageByAssetAsync(representativeAssetId.Value, ct);
        var qidCandidateIds = new List<Guid>();
        if (lineage is not null && MetadataEndpoints.NormalizeEditorMediaType(scope.MediaType) == "TV")
        {
            AddCanonicalSource(qidCandidateIds, lineage.TargetForParentScope);
        }

        AddCanonicalSource(qidCandidateIds, scope.ArtworkOwnerEntityId);
        AddCanonicalSource(qidCandidateIds, scope.FieldEntityId);
        if (lineage is not null)
        {
            AddCanonicalSource(qidCandidateIds, lineage.TargetForSelfScope);
        }

        AddCanonicalSource(qidCandidateIds, representativeAssetId.Value);

        var canonicalLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? qid = null;
        foreach (var candidateId in qidCandidateIds.Distinct())
        {
            foreach (var canonical in await canonicalRepo.GetByEntityAsync(candidateId, ct))
            {
                if (!string.IsNullOrWhiteSpace(canonical.Key)
                    && !string.IsNullOrWhiteSpace(canonical.Value)
                    && !canonicalLookup.ContainsKey(canonical.Key))
                {
                    canonicalLookup[canonical.Key] = canonical.Value;
                }

                if (qid is null
                    && string.Equals(canonical.Key, "wikidata_qid", StringComparison.OrdinalIgnoreCase))
                {
                    qid = NormalizeWikidataQid(canonical.Value);
                }
            }
        }

        var normalizedMediaType = MetadataEndpoints.NormalizeEditorMediaType(scope.MediaType);
        if (normalizedMediaType is "Books" or "Audiobooks" or "Comics" or "Music")
        {
            var coverUrl = StringHelpers.FirstNonBlankOr(
                string.Empty,
                MetadataEndpoints.GetCanonicalValue(canonicalLookup, MetadataFieldConstants.CoverUrl),
                MetadataEndpoints.GetCanonicalValue(canonicalLookup, MetadataFieldConstants.Cover));
            if (string.IsNullOrWhiteSpace(coverUrl))
            {
                return ProviderArtworkRefreshTarget.Skip(CreateProviderArtworkRefreshEnvelope(
                    status: "Skipped",
                    skippedReason: "missing_cover_url",
                    message: "This item needs a cover URL from its metadata provider before artwork can be refreshed.",
                    mediaType: scope.MediaType,
                    provider: "retail_provider",
                    providerName: "Metadata provider"));
            }

            return new ProviderArtworkRefreshTarget(representativeAssetId, qid, coverUrl, null);
        }

        var bridge = ResolveProviderArtworkBridge(canonicalLookup, scope.MediaType);
        if (bridge is null)
        {
            return ProviderArtworkRefreshTarget.Skip(CreateProviderArtworkRefreshEnvelope(
                status: "Skipped",
                skippedReason: "missing_bridge_id",
                message: "This item needs a TMDB ID before TMDB artwork can be refreshed.",
                mediaType: scope.MediaType));
        }

        return new ProviderArtworkRefreshTarget(representativeAssetId, qid, null, null);
    }

    public static (string Key, string Value)? ResolveProviderArtworkBridge(
        IReadOnlyDictionary<string, string> canonicals,
        string mediaType)
    {
        var normalized = MetadataEndpoints.NormalizeEditorMediaType(mediaType);
        if (normalized == "Movies")
        {
            var tmdb = MetadataEndpoints.GetCanonicalValue(canonicals, BridgeIdKeys.TmdbId);
            return string.IsNullOrWhiteSpace(tmdb) ? null : (BridgeIdKeys.TmdbId, tmdb);
        }

        if (normalized == "TV")
        {
            var tmdb = MetadataEndpoints.GetCanonicalValue(canonicals, BridgeIdKeys.TmdbId);
            return string.IsNullOrWhiteSpace(tmdb) ? null : (BridgeIdKeys.TmdbId, tmdb);
        }

        if (normalized == "Music")
        {
            var releaseGroup = MetadataEndpoints.GetCanonicalValue(
                canonicals,
                BridgeIdKeys.MusicBrainzReleaseGroupId);
            return string.IsNullOrWhiteSpace(releaseGroup)
                ? null
                : (BridgeIdKeys.MusicBrainzReleaseGroupId, releaseGroup);
        }

        return null;
    }

    public static string? NormalizeWikidataQid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var qid = value.Trim();
        if (qid.Contains('/'))
        {
            qid = qid.Split('/')[^1];
        }

        if (qid.Contains("::", StringComparison.Ordinal))
        {
            qid = qid.Split("::", 2, StringSplitOptions.None)[0].Trim();
        }

        return qid.Length > 1 && qid[0] is 'Q' && qid.Skip(1).All(char.IsDigit)
            ? qid
            : null;
    }

    public static ProviderArtworkRefreshEnvelope MapProviderArtworkRefreshResult(ImageEnrichmentResult result) =>
        CreateProviderArtworkRefreshEnvelope(
            result.Status,
            result.SkippedReason,
            result.Message,
            result.MediaType,
            result.BridgeKey,
            result.BridgeId,
            result.Endpoint,
            result.HttpStatusCode,
            result.DownloadedCount,
            result.UpdatedPreferredCount,
            result.StoredVariantCounts,
            result.Diagnostics,
            result.LastCheckedAt,
            result.Provider,
            result.ProviderName);

    public static ProviderArtworkRefreshEnvelope CreateProviderArtworkRefreshEnvelope(
        string status,
        string? skippedReason,
        string? message,
        string? mediaType,
        string? bridgeKey = null,
        string? bridgeId = null,
        string? endpoint = null,
        int? httpStatusCode = null,
        int downloadedCount = 0,
        int updatedPreferredCount = 0,
        IReadOnlyDictionary<string, int>? storedCounts = null,
        IReadOnlyList<string>? diagnostics = null,
        DateTimeOffset? lastCheckedAt = null,
        string provider = "tmdb",
        string providerName = "TMDB") =>
        new(
            Provider: provider,
            ProviderName: providerName,
            Status: status,
            Success: string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(status, "NoImages", StringComparison.OrdinalIgnoreCase),
            Skipped: !string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase),
            SkippedReason: skippedReason,
            Message: message,
            MediaType: mediaType,
            BridgeKey: bridgeKey,
            BridgeId: bridgeId,
            Endpoint: endpoint,
            HttpStatusCode: httpStatusCode,
            DownloadedCount: downloadedCount,
            UpdatedPreferredCount: updatedPreferredCount,
            StoredVariantCounts: storedCounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            Diagnostics: diagnostics ?? [],
            LastCheckedAt: lastCheckedAt ?? DateTimeOffset.UtcNow);

    public string? BuildScopedArtworkUploadPath(
        EditorScopeResolution scope,
        string normalizedAssetType,
        Guid variantId,
        string? contentType)
    {
        if (scope.ArtworkOwnerEntityId is null || string.IsNullOrWhiteSpace(scope.ArtworkOwnerEntityKind))
        {
            return null;
        }

        return assetPathService.GetCentralAssetPath(
            scope.ArtworkOwnerEntityKind!,
            scope.ArtworkOwnerEntityId.Value,
            normalizedAssetType,
            variantId,
            BuildArtworkExtension(normalizedAssetType, contentType));
    }

    public static string? GetContainerFolderPath(string? mediaFilePath) =>
        string.IsNullOrWhiteSpace(mediaFilePath)
            ? null
            : Path.GetDirectoryName(mediaFilePath);

    public static string? GetSeriesFolderPath(string? mediaFilePath)
    {
        var seasonFolder = GetSeasonFolderPath(mediaFilePath);
        return string.IsNullOrWhiteSpace(seasonFolder)
            ? null
            : Path.GetDirectoryName(seasonFolder);
    }

    public static string? GetSeasonFolderPath(string? mediaFilePath) =>
        string.IsNullOrWhiteSpace(mediaFilePath)
            ? null
            : Path.GetDirectoryName(mediaFilePath);

    public static string? GetArtistFolderPath(string? mediaFilePath)
    {
        var albumFolder = GetContainerFolderPath(mediaFilePath);
        return string.IsNullOrWhiteSpace(albumFolder)
            ? null
            : Path.GetDirectoryName(albumFolder);
    }

    public static string? NormalizeUploadedArtworkType(string assetType) =>
        assetType.Trim() switch
        {
            "cover" or "Cover" or "Poster" or "poster" or "CoverArt" => "CoverArt",
            "background" or "Background" => "Background",
            "logo" or "Logo" => "Logo",
            "seasonposter" or "SeasonPoster" => "SeasonPoster",
            "seasonthumb" or "SeasonThumb" => "SeasonThumb",
            "episodestill" or "EpisodeStill" or "still" or "Still" => "EpisodeStill",
            _ => null,
        };

    public static bool IsArtworkUploadAllowed(string? contentType, string normalizedAssetType)
    {
        if (string.Equals(normalizedAssetType, "Logo", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase);
        }

        return contentType is not null && (string.Equals(contentType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentType, "image/jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase));
    }

    public static void AddCanonicalSource(List<Guid> sources, Guid? sourceId)
    {
        if (!sourceId.HasValue || sourceId == Guid.Empty || sources.Contains(sourceId.Value))
        {
            return;
        }

        sources.Add(sourceId.Value);
    }

    public static string? GetArtworkDetailUrl(LibraryItemDetail? detail, string assetType) =>
        assetType switch
        {
            "CoverArt" => detail?.CoverUrl,
            "Background" => detail?.BackgroundUrl,
            _ => null,
        };

    public static string BuildArtworkVariantIdentity(EntityAsset asset)
    {
        var stableSource = !string.IsNullOrWhiteSpace(asset.ImageUrl)
            ? asset.ImageUrl
            : !string.IsNullOrWhiteSpace(asset.LocalImagePath)
                ? asset.LocalImagePath
                : asset.Id.ToString("D");

        return $"{asset.AssetTypeValue}|{stableSource}";
    }

    public string BuildArtworkUploadPath(
        string ownerEntityKind,
        Guid ownerEntityId,
        string normalizedAssetType,
        Guid variantId,
        string? contentType) =>
        assetPathService.GetCentralAssetPath(
            ownerEntityKind,
            ownerEntityId,
            normalizedAssetType,
            variantId,
            BuildArtworkExtension(normalizedAssetType, contentType));

    public static string BuildArtworkExtension(string normalizedAssetType, string? contentType) =>
        string.Equals(normalizedAssetType, "Logo", StringComparison.OrdinalIgnoreCase)
            ? ".png"
            : string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase)
                ? ".png"
                : ".jpg";

    public static string GetArtworkCanonicalKey(string normalizedAssetType) =>
        normalizedAssetType switch
        {
            "CoverArt" => MetadataFieldConstants.CoverUrl,
            "Background" => "background",
            "Logo" => "logo",
            "SeasonPoster" => "season_poster",
            "SeasonThumb" => "season_thumb",
            "EpisodeStill" => "episode_still",
            _ => throw new ArgumentOutOfRangeException(nameof(normalizedAssetType), normalizedAssetType, "Unsupported artwork type."),
        };

    public async Task SyncArtworkCanonicalAsync(
        Guid entityId,
        string assetType,
        EntityAsset? preferredAsset,
        CancellationToken ct)
    {
        var canonicalKey = GetArtworkCanonicalKey(assetType);

        if (preferredAsset is null)
        {
            await canonicalRepo.DeleteByKeyAsync(entityId, canonicalKey, ct);

            if (string.Equals(assetType, "CoverArt", StringComparison.OrdinalIgnoreCase))
            {
                await canonicalRepo.UpsertBatchAsync(
                    ArtworkCanonicalHelper.CreateFlags(
                        entityId,
                        coverState: "missing",
                        coverSource: null,
                        heroState: "missing",
                        lastScoredAt: DateTimeOffset.UtcNow,
                        settled: true),
                    ct);
            }

            return;
        }

        var canonicals = ArtworkCanonicalHelper.CreatePreferredAssetCanonicals(
            entityId,
            preferredAsset,
            DateTimeOffset.UtcNow);

        if (string.Equals(assetType, "CoverArt", StringComparison.OrdinalIgnoreCase))
        {
            var coverSource = string.Equals(preferredAsset.SourceProvider, "user_upload", StringComparison.OrdinalIgnoreCase)
                ? "manual"
                : !string.IsNullOrWhiteSpace(preferredAsset.SourceProvider)
                    ? "provider"
                    : "stored";

            canonicals.AddRange(ArtworkCanonicalHelper.CreateFlags(
                entityId,
                coverState: "present",
                coverSource: coverSource,
                heroState: "missing",
                lastScoredAt: DateTimeOffset.UtcNow,
                settled: true));
        }

        await canonicalRepo.UpsertBatchAsync(canonicals, ct);
    }

    public static string? GetArtworkCanonicalValue(IReadOnlyList<CanonicalValue> canonicals, string assetType)
    {
        var canonicalKey = GetArtworkCanonicalKey(assetType);
        return canonicals.FirstOrDefault(c =>
            string.Equals(c.Key, canonicalKey, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    public static string InferSyntheticArtworkOrigin(
        IReadOnlyList<CanonicalValue> canonicals,
        string assetType,
        string? detailArtworkSource)
    {
        if (string.Equals(assetType, "CoverArt", StringComparison.OrdinalIgnoreCase))
        {
            var coverSource = canonicals.FirstOrDefault(c =>
                string.Equals(c.Key, MetadataFieldConstants.CoverSource, StringComparison.OrdinalIgnoreCase))?.Value
                ?? detailArtworkSource;

            return coverSource switch
            {
                "manual" => "Uploaded",
                "provider" => "Provider",
                "embedded" => "Stored",
                _ => "Stored",
            };
        }

        return "Stored";
    }

    public static ArtworkVariantEnvelope MapArtworkVariant(EntityAsset asset) =>
        new(
            asset.Id,
            asset.AssetTypeValue,
            MetadataEndpoints.BuildArtworkVariantStreamUrl(asset.Id),
            asset.IsPreferred,
            string.Equals(asset.SourceProvider, "user_upload", StringComparison.OrdinalIgnoreCase)
                ? "Uploaded"
                : !string.IsNullOrWhiteSpace(asset.SourceProvider)
                    ? "Provider"
                    : "Stored",
            FormatArtworkProviderName(asset.SourceProvider),
            CanDelete: true,
            CreatedAt: asset.CreatedAt,
            WidthPx: asset.WidthPx,
            HeightPx: asset.HeightPx);

    public static string? FormatArtworkProviderName(string? sourceProvider) =>
        string.IsNullOrWhiteSpace(sourceProvider)
            ? null
            : sourceProvider switch
            {
                "tmdb" => "TMDB",
                "fanart_tv" => "Fanart.tv",
                "user_upload" => "Library Upload",
                _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(sourceProvider.Replace('_', ' ')),
            };
}
