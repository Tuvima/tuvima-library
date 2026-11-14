using System.Security.Cryptography;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Mirrors preferred artwork into optional local sidecars when policy allows it.
/// Central storage remains canonical; local files are compatibility exports only.
/// </summary>
public sealed class AssetExportService : IAssetExportService
{
    private readonly IDatabaseConnection _db;
    private readonly IEntityAssetRepository _entityAssetRepo;
    private readonly AssetPathService _assetPaths;
    private readonly ILogger<AssetExportService> _logger;

    public AssetExportService(
        IDatabaseConnection db,
        IEntityAssetRepository entityAssetRepo,
        AssetPathService assetPaths,
        ILogger<AssetExportService> logger)
    {
        _db = db;
        _entityAssetRepo = entityAssetRepo;
        _assetPaths = assetPaths;
        _logger = logger;
    }

    public async Task ReconcileAllArtworkAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<(string EntityId, string EntityType, string AssetType)>($"""
            SELECT DISTINCT {GuidSql.EntityIdProjection} AS EntityId,
                            entity_type AS EntityType,
                            asset_type AS AssetType
            FROM entity_assets
            WHERE asset_class = 'Artwork'
            ORDER BY entity_type, entity_id, asset_type;
            """).ToList();

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            await ReconcileArtworkAsync(row.EntityId, row.EntityType, row.AssetType, ct);
        }
    }

    public async Task ReconcileArtworkAsync(
        string entityId,
        string entityType,
        string assetType,
        CancellationToken ct = default,
        bool replaceExistingFile = false)
    {
        ct.ThrowIfCancellationRequested();

        var assets = (await _entityAssetRepo.GetByEntityAsync(entityId, assetType, ct))
            .OrderByDescending(asset => asset.IsPreferred)
            .ThenByDescending(asset => asset.CreatedAt)
            .ToList();

        var exportPath = ResolveArtworkExportPath(entityId, entityType, assetType);
        var preferred = assets.FirstOrDefault(asset => asset.IsPreferred) ?? assets.FirstOrDefault();

        if (!_assetPaths.ShouldExportArtwork || !ShouldExportArtworkType(assetType))
        {
            await ClearManagedExportAsync(exportPath, assets, preferred, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(exportPath))
        {
            await UpdateExportFlagsAsync(assets, preferredAssetId: null, exported: false, ct);
            return;
        }

        if (preferred is null
            || string.IsNullOrWhiteSpace(preferred.LocalImagePath)
            || !File.Exists(preferred.LocalImagePath))
        {
            await ClearManagedExportAsync(exportPath, assets, preferred, ct);
            return;
        }

        if (!replaceExistingFile
            && File.Exists(exportPath)
            && !assets.Any(asset => asset.IsLocallyExported || asset.IsPreferredExported))
        {
            _logger.LogInformation(
                "Skipped artwork export for {EntityType}:{EntityId} {AssetType} because a local sidecar already exists at {Path}",
                entityType,
                entityId,
                assetType,
                exportPath);
            return;
        }

        AssetPathService.EnsureDirectory(exportPath);
        File.Copy(preferred.LocalImagePath, exportPath, overwrite: true);
        await UpdateExportFlagsAsync(assets, preferred.Id, exported: true, ct);
    }

    public async Task ClearArtworkExportAsync(
        string entityId,
        string entityType,
        string assetType,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var assets = (await _entityAssetRepo.GetByEntityAsync(entityId, assetType, ct)).ToList();
        var exportPath = ResolveArtworkExportPath(entityId, entityType, assetType);
        var preferred = assets.OrderByDescending(asset => asset.IsPreferred)
            .ThenByDescending(asset => asset.CreatedAt)
            .FirstOrDefault();
        await ClearManagedExportAsync(exportPath, assets, preferred, ct);
    }

    private async Task ClearManagedExportAsync(
        string? exportPath,
        IReadOnlyList<EntityAsset> assets,
        EntityAsset? preferredAsset,
        CancellationToken ct)
    {
        var hadManagedExport = assets.Any(asset => asset.IsLocallyExported || asset.IsPreferredExported);
        var shouldDeleteLegacyDuplicate = _assetPaths.Policy.CleanupManagedLocalArtwork
            && IsSafeLegacyDuplicate(exportPath, preferredAsset?.LocalImagePath);

        if ((hadManagedExport || shouldDeleteLegacyDuplicate) && !string.IsNullOrWhiteSpace(exportPath))
        {
            try
            {
                if (File.Exists(exportPath))
                {
                    File.Delete(exportPath);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to remove managed artwork export at {Path}", exportPath);
            }
        }

        await UpdateExportFlagsAsync(assets, preferredAssetId: null, exported: false, ct);
    }

    private static bool IsSafeLegacyDuplicate(string? exportPath, string? preferredPath)
    {
        if (string.IsNullOrWhiteSpace(exportPath)
            || string.IsNullOrWhiteSpace(preferredPath)
            || !File.Exists(exportPath)
            || !File.Exists(preferredPath))
        {
            return false;
        }

        var normalizedExport = Path.GetFullPath(exportPath);
        var normalizedPreferred = Path.GetFullPath(preferredPath);
        if (string.Equals(normalizedExport, normalizedPreferred, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var exportInfo = new FileInfo(normalizedExport);
        var preferredInfo = new FileInfo(normalizedPreferred);
        if (exportInfo.Length != preferredInfo.Length)
        {
            return false;
        }

        using var exportStream = File.OpenRead(normalizedExport);
        using var preferredStream = File.OpenRead(normalizedPreferred);
        var exportHash = SHA256.HashData(exportStream);
        var preferredHash = SHA256.HashData(preferredStream);
        return exportHash.AsSpan().SequenceEqual(preferredHash);
    }

    private async Task UpdateExportFlagsAsync(
        IReadOnlyList<EntityAsset> assets,
        Guid? preferredAssetId,
        bool exported,
        CancellationToken ct)
    {
        foreach (var asset in assets)
        {
            ct.ThrowIfCancellationRequested();

            var shouldMarkPreferred = exported && preferredAssetId.HasValue && asset.Id == preferredAssetId.Value;
            if (asset.IsLocallyExported == exported && asset.IsPreferredExported == shouldMarkPreferred)
            {
                continue;
            }

            asset.IsLocallyExported = exported;
            asset.IsPreferredExported = shouldMarkPreferred;
            await _entityAssetRepo.UpsertAsync(asset, ct);
        }
    }

    private string? ResolveArtworkExportPath(string entityId, string entityType, string assetType)
    {
        if (!string.Equals(entityType, "Work", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Guid.TryParse(entityId, out var workId))
        {
            return null;
        }

        var context = ResolveWorkExportContext(workId);
        if (context is null || string.IsNullOrWhiteSpace(context.RepresentativeMediaFilePath))
        {
            return null;
        }

        var extension = ResolvePreferredExtension(assetType);
        var mediaFilePath = context.RepresentativeMediaFilePath;
        var containerFolder = Path.GetDirectoryName(mediaFilePath);

        return assetType switch
        {
            "EpisodeStill" => _assetPaths.GetExportedSidecarPath(mediaFilePath, assetType, extension),
            "SeasonPoster" or "SeasonThumb" when !string.IsNullOrWhiteSpace(containerFolder)
                => _assetPaths.GetExportedSidecarPath(containerFolder!, assetType, extension),
            _ when string.Equals(context.MediaType, "TV", StringComparison.OrdinalIgnoreCase)
                 && !string.IsNullOrWhiteSpace(containerFolder)
                => _assetPaths.GetExportedSidecarPath(ResolveSeriesFolder(containerFolder!), assetType, extension),
            _ when !string.IsNullOrWhiteSpace(containerFolder)
                => _assetPaths.GetExportedSidecarPath(containerFolder!, assetType, extension),
            _ => null,
        };
    }

    private WorkExportContext? ResolveWorkExportContext(Guid workId)
    {
        using var conn = _db.CreateConnection();

        return conn.QueryFirstOrDefault<WorkExportContext>("""
            WITH RECURSIVE descendants(id, depth) AS (
                SELECT w.id, 0
                FROM works w
                WHERE w.id = @workId

                UNION ALL

                SELECT child.id, descendants.depth + 1
                FROM works child
                INNER JOIN descendants ON child.parent_work_id = descendants.id
            )
            SELECT w.id AS WorkId,
                   w.media_type AS MediaType,
                   (
                       SELECT ma.file_path_root
                       FROM descendants d
                       INNER JOIN editions e ON e.work_id = d.id
                       INNER JOIN media_assets ma ON ma.edition_id = e.id
                       ORDER BY d.depth, ma.id
                       LIMIT 1
                   ) AS RepresentativeMediaFilePath
            FROM works w
            WHERE w.id = @workId
            LIMIT 1;
            """, new { workId });
    }

    private bool ShouldExportArtworkType(string assetType)
    {
        if (_assetPaths.Policy.Mode == StorageMode.CoLocated)
        {
            return assetType is
                "CoverArt" or "Background" or "Logo" or "SeasonPoster" or "SeasonThumb" or "EpisodeStill";
        }

        return assetType is
            "CoverArt" or "Background" or "Logo" or "SeasonPoster" or "SeasonThumb";
    }

    private static string ResolvePreferredExtension(string assetType) =>
        assetType switch
        {
            "Logo" => ".png",
            _ => ".jpg",
        };

    private static string ResolveSeriesFolder(string containerFolder)
    {
        var folderName = Path.GetFileName(containerFolder);
        if (folderName.StartsWith("Season ", StringComparison.OrdinalIgnoreCase)
            || string.Equals(folderName, "Specials", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(containerFolder) ?? containerFolder;
        }

        return containerFolder;
    }

    private sealed class WorkExportContext
    {
        public Guid WorkId { get; init; }

        public string MediaType { get; init; } = string.Empty;

        public string? RepresentativeMediaFilePath { get; init; }
    }
}
