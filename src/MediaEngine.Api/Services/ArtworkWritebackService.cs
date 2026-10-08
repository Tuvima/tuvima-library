using Dapper;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Synchronizes inherited preferred artwork to eligible audio files. Metadata
/// retagging and sidecar export have separate state and cannot imply embedding.
/// </summary>
public sealed class ArtworkWritebackService(
    IDatabaseConnection database,
    ArtworkAssetService artwork,
    IConfigurationLoader configuration,
    ILibraryFolderResolver libraries,
    ISourceMutationPolicyGate mutationGate,
    IEnumerable<IMetadataTagger> taggers,
    ISystemActivityRepository activity,
    ILogger<ArtworkWritebackService> logger)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Guid? _sweepCursor;

    public async Task<ArtworkWritebackStatusDto?> GetStatusAsync(Guid mediaAssetId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var asset = LoadAsset(mediaAssetId);
        if (asset is null)
        {
            return null;
        }

        var settings = configuration.LoadConfig<WriteBackConfiguration>(string.Empty, "writeback")
            ?? new WriteBackConfiguration();
        if (!settings.Enabled || !settings.ArtworkEnabled)
        {
            return Status(asset, "Disabled", reason: "Artwork file write-back is disabled.");
        }
        if (!ArtworkEmbeddingSupport.CanEmbed(asset.FilePath))
        {
            return Status(asset, "Unsupported", reason: "This file format does not support verified artwork embedding.");
        }
        if (!File.Exists(asset.FilePath))
        {
            return Status(asset, "Missing", reason: "The media file is unavailable.");
        }

        var source = libraries.ResolveSourceForPath(asset.FilePath);
        if (source is null)
        {
            return Status(asset, "Disabled", reason: "This file has no configured library source.");
        }
        var decision = mutationGate.Evaluate(new SourceMutationRequest
        {
            Source = FileSourceMutationPolicyFactory.Create(
                source.Library, source.Source, globalMetadataWritebackEnabled: settings.Enabled),
            Mutation = SourceMutationKind.MetadataWriteback,
            Path = asset.FilePath,
        });
        if (!decision.Allowed)
        {
            return Status(asset, "Disabled", reason: decision.Reason);
        }

        // TV episode stills are not file-cover inheritance. The season-poster
        // request falls back to the show's CoverArt through the shared resolver.
        var sourceType = string.Equals(asset.MediaType, "TV", StringComparison.OrdinalIgnoreCase)
            ? "SeasonPoster" : "CoverArt";
        var selection = await artwork.GetEffectiveWorkArtworkAsync(asset.WorkId, "Primary", sourceType, ct);
        if (selection?.Variant is not { } variant)
        {
            return Status(asset, "NoArtwork", reason: "No preferred parent cover is available.");
        }

        var imagePath = LoadArtworkPath(variant.ArtworkAssetId);
        var image = imagePath is not null && File.Exists(imagePath) ? new FileInfo(imagePath) : null;
        var desiredVersion = $"{variant.ArtworkAssetId:N}:v{ArtworkEmbeddingSupport.FormatVersion}:" +
            $"{Path.GetExtension(asset.FilePath).ToLowerInvariant()}:{source.Source.Id}:" +
            $"{source.Source.WritebackOverride}:{image?.LastWriteTimeUtc.Ticks}:{image?.Length}";
        var saved = LoadState(mediaAssetId);
        var file = new FileInfo(asset.FilePath);
        var isCurrent = saved?.EmbeddedArtworkAssetId == variant.ArtworkAssetId
            && saved.DesiredVersion == desiredVersion
            && string.Equals(saved.FileModifiedUtc, file.LastWriteTimeUtc.ToString("O"), StringComparison.Ordinal)
            && saved.FileSizeBytes == file.Length;
        var state = isCurrent ? "Embedded"
            : saved?.DesiredArtworkAssetId == variant.ArtworkAssetId
              && saved.DesiredVersion == desiredVersion
              && string.Equals(saved.Status, "failed", StringComparison.OrdinalIgnoreCase)
                ? "Failed" : "Pending";
        return new ArtworkWritebackStatusDto(
            mediaAssetId, state, variant.ArtworkAssetId,
            isCurrent ? saved?.EmbeddedArtworkAssetId : null,
            selection.SourceEntityId,
            state == "Failed" ? saved?.LastError : null,
            saved?.Attempts ?? 0,
            saved?.UpdatedAt)
        {
            DesiredVersion = desiredVersion,
        };
    }

    public async Task<ArtworkWritebackStatusDto?> ProcessAsync(Guid mediaAssetId, bool retry, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            var status = await GetStatusAsync(mediaAssetId, ct);
            if (status is null || status.Status is "Disabled" or "Unsupported" or "Missing" or "NoArtwork" or "Embedded")
            {
                return status;
            }
            if (status.Status == "Failed" && !retry)
            {
                return status;
            }

            var asset = LoadAsset(mediaAssetId)!;
            var imagePath = LoadArtworkPath(status.DesiredArtworkAssetId!.Value);
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return await FailAsync(status, "The selected managed artwork file is unavailable.", ct);
            }
            var bytes = await File.ReadAllBytesAsync(imagePath, ct);
            if (bytes.Length == 0 || bytes.Length > 16 * 1024 * 1024)
            {
                return await FailAsync(status, "The selected artwork cannot be embedded at this size.", ct);
            }
            var tagger = taggers.FirstOrDefault(item => item is AudioMetadataTagger && item.CanHandle(asset.FilePath));
            if (tagger is null)
            {
                return status with { Status = "Unsupported", Reason = "No verified audio artwork writer is registered." };
            }

            await SaveStateAsync(status, "writing", null, null, incrementAttempt: true, ct);
            try
            {
                await tagger.WriteCoverArtAsync(asset.FilePath, bytes, ct);
                if (!ArtworkEmbeddingSupport.VerifyFrontCover(asset.FilePath, bytes))
                {
                    throw new InvalidDataException("The saved cover could not be verified in the media file.");
                }
                var file = new FileInfo(asset.FilePath);
                await SaveStateAsync(status, "embedded", file.LastWriteTimeUtc.ToString("O"), file.Length,
                    incrementAttempt: false, ct);
                await RecordActivityAsync(status.MediaAssetId, SystemActionType.ArtworkWrittenToFile,
                    "Preferred artwork embedded and verified in the media file.", ct);
                return await GetStatusAsync(mediaAssetId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Artwork embedding failed for media asset {MediaAssetId}", mediaAssetId);
                return await FailAsync(status, "Artwork could not be verified in the media file.", ct);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<int> SweepOnceAsync(int limit, CancellationToken ct)
    {
        var settings = configuration.LoadConfig<WriteBackConfiguration>(string.Empty, "writeback")
            ?? new WriteBackConfiguration();
        if (!settings.Enabled || !settings.ArtworkEnabled)
        {
            return 0;
        }
        using var connection = database.CreateConnection();
        var ids = (await connection.QueryAsync<Guid>(new CommandDefinition("""
            SELECT ma.id FROM media_assets ma
            INNER JOIN editions e ON e.id = ma.edition_id
            INNER JOIN works w ON w.id = e.work_id
            WHERE w.media_type IN ('Music','Audiobooks')
              AND (@cursor IS NULL OR ma.id > @cursor)
            ORDER BY ma.id LIMIT @limit;
            """, new { cursor = _sweepCursor, limit = Math.Clamp(limit, 1, 100) }, cancellationToken: ct))).ToList();
        if (ids.Count == 0)
        {
            _sweepCursor = null;
            return 0;
        }
        _sweepCursor = ids[^1];
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessAsync(id, retry: false, ct);
        }
        return ids.Count;
    }

    private async Task<ArtworkWritebackStatusDto> FailAsync(ArtworkWritebackStatusDto status, string reason, CancellationToken ct)
    {
        await SaveStateAsync(status, "failed", null, null, incrementAttempt: false, ct, reason);
        await RecordActivityAsync(status.MediaAssetId, SystemActionType.ArtworkWritebackFailed, reason, ct);
        return status with { Status = "Failed", Reason = reason };
    }

    private async Task RecordActivityAsync(Guid mediaAssetId, string actionType, string detail, CancellationToken ct)
    {
        try
        {
            await activity.LogAsync(new SystemActivityEntry
            {
                ActionType = actionType,
                EntityType = "MediaAsset",
                EntityId = mediaAssetId,
                Detail = detail,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Activity delivery cannot turn a physically verified write into a failed write.
            logger.LogWarning(ex, "Artwork write-back activity could not be recorded for {MediaAssetId}", mediaAssetId);
        }
    }

    private async Task SaveStateAsync(ArtworkWritebackStatusDto status, string state,
        string? fileModifiedUtc, long? fileSizeBytes, bool incrementAttempt, CancellationToken ct,
        string? error = null)
    {
        await database.ExecuteWriteAsync((connection, transaction, _) =>
        {
            connection.Execute("""
                INSERT INTO media_artwork_writeback (
                    media_asset_id, desired_artwork_asset_id, desired_version, embedded_artwork_asset_id,
                    status, attempts, last_error, file_modified_utc, file_size_bytes, updated_at)
                VALUES (@mediaAssetId, @desiredArtworkAssetId, @desiredVersion, @embeddedArtworkAssetId,
                    @state, @attempts, @error, @fileModifiedUtc, @fileSizeBytes, @updatedAt)
                ON CONFLICT(media_asset_id) DO UPDATE SET
                    desired_artwork_asset_id = excluded.desired_artwork_asset_id,
                    desired_version = excluded.desired_version,
                    embedded_artwork_asset_id = excluded.embedded_artwork_asset_id,
                    status = excluded.status,
                    attempts = media_artwork_writeback.attempts + @incrementAttempt,
                    last_error = excluded.last_error,
                    file_modified_utc = excluded.file_modified_utc,
                    file_size_bytes = excluded.file_size_bytes,
                    updated_at = excluded.updated_at;
                """, new
            {
                mediaAssetId = status.MediaAssetId,
                desiredArtworkAssetId = status.DesiredArtworkAssetId,
                desiredVersion = status.DesiredVersion,
                embeddedArtworkAssetId = state == "embedded" ? status.DesiredArtworkAssetId : null,
                state,
                attempts = incrementAttempt ? 1 : 0,
                incrementAttempt = incrementAttempt ? 1 : 0,
                error,
                fileModifiedUtc,
                fileSizeBytes,
                updatedAt = DateTimeOffset.UtcNow.ToString("O"),
            }, transaction);
        }, ct);
    }

    private AssetRow? LoadAsset(Guid id)
    {
        using var connection = database.CreateConnection();
        return connection.QueryFirstOrDefault<AssetRow>("""
            SELECT ma.id AS AssetId, ma.file_path_root AS FilePath,
                   e.work_id AS WorkId, w.media_type AS MediaType
            FROM media_assets ma
            INNER JOIN editions e ON e.id = ma.edition_id
            INNER JOIN works w ON w.id = e.work_id
            WHERE ma.id = @id LIMIT 1;
            """, new { id });
    }

    private StateRow? LoadState(Guid id)
    {
        using var connection = database.CreateConnection();
        return connection.QueryFirstOrDefault<StateRow>("""
            SELECT desired_artwork_asset_id AS DesiredArtworkAssetId,
                   desired_version AS DesiredVersion,
                   embedded_artwork_asset_id AS EmbeddedArtworkAssetId,
                   status AS Status, attempts AS Attempts, last_error AS LastError,
                   file_modified_utc AS FileModifiedUtc, file_size_bytes AS FileSizeBytes,
                   updated_at AS UpdatedAt
            FROM media_artwork_writeback WHERE media_asset_id = @id LIMIT 1;
            """, new { id });
    }

    private string? LoadArtworkPath(Guid id)
    {
        using var connection = database.CreateConnection();
        var paths = connection.QueryFirstOrDefault<ArtworkPathRow>("""
            SELECT medium_path AS MediumPath, original_path AS OriginalPath
            FROM artwork_assets WHERE id = @id LIMIT 1;
            """, new { id });
        if (paths is null)
        {
            return null;
        }
        return File.Exists(paths.MediumPath) ? paths.MediumPath : paths.OriginalPath;
    }

    private static ArtworkWritebackStatusDto Status(AssetRow asset, string state, string? reason) =>
        new(asset.AssetId, state, null, null, null, reason, 0, null);

    private sealed class AssetRow
    {
        public Guid AssetId { get; set; }
        public Guid WorkId { get; set; }
        public string MediaType { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
    }

    private sealed class StateRow
    {
        public Guid DesiredArtworkAssetId { get; set; }
        public string? DesiredVersion { get; set; }
        public Guid? EmbeddedArtworkAssetId { get; set; }
        public string Status { get; set; } = string.Empty;
        public int Attempts { get; set; }
        public string? LastError { get; set; }
        public string? FileModifiedUtc { get; set; }
        public long? FileSizeBytes { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    private sealed class ArtworkPathRow
    {
        public string? MediumPath { get; set; }
        public string? OriginalPath { get; set; }
    }
}

public sealed class ArtworkWritebackWorker(ArtworkWritebackService writeback,
    ILogger<ArtworkWritebackWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await writeback.SweepOnceAsync(40, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Artwork file write-back sweep failed");
            }
        }
    }
}
