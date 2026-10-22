using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;

namespace MediaEngine.Api.Services.FileWrites;

/// <summary>
/// Tells whether a media file has changed since Library last saw it (batched write-back D11): the fingerprint of the file now is
/// compared with the one recorded for the asset. Library's own writes record their new fingerprint as they finish, so only a change
/// made by something else (another program, the person) counts. The fingerprint is read from the file hash cache when the file's size
/// and time still match, so most files are not read at all.
/// </summary>
public sealed class OutsideChangeGuard(
    IMediaAssetRepository assets,
    IAssetHasher hasher,
    ILogger<OutsideChangeGuard> logger,
    IFileHashCacheRepository? cache = null)
{
    /// <summary>True when the asset's file now differs from the fingerprint recorded for it. A file that is missing is not a change.</summary>
    public async Task<bool> ChangedSinceLibraryLastSawAsync(Guid assetId, CancellationToken ct)
    {
        var asset = await assets.FindByIdAsync(assetId, ct);
        if (asset is null || string.IsNullOrWhiteSpace(asset.ContentHash)
            || string.IsNullOrWhiteSpace(asset.FilePathRoot) || !File.Exists(asset.FilePathRoot))
        {
            return false;
        }

        var current = await CurrentFingerprintAsync(asset.FilePathRoot, ct);
        return !string.Equals(current, asset.ContentHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Records the file as it is now as the one Library knows, after Library itself wrote into it.</summary>
    public async Task RecordOwnWriteAsync(Guid assetId, CancellationToken ct)
    {
        var asset = await assets.FindByIdAsync(assetId, ct);
        if (asset is null || string.IsNullOrWhiteSpace(asset.FilePathRoot) || !File.Exists(asset.FilePathRoot))
        {
            return;
        }

        // Cheap when nothing changed: the cached fingerprint still matches the file's size and time.
        var current = await CurrentFingerprintAsync(asset.FilePathRoot, ct);
        if (string.Equals(current, asset.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!await assets.UpdateContentHashAsync(assetId, current, ct))
        {
            logger.LogWarning("The new fingerprint of asset {AssetId} belongs to another asset; it was not recorded.", assetId);
        }
    }

    private async Task<string> CurrentFingerprintAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var absolute = Path.GetFullPath(path);
        var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (cache is not null)
        {
            var cached = await cache.TryGetAsync(absolute, info.Length, modified, ct);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }
        }

        var fingerprint = await hasher.ComputeAsync(absolute, ct);
        await RememberAsync(absolute, fingerprint.FileSize, fingerprint.Hex, ct);
        return fingerprint.Hex;
    }

    private async Task RememberAsync(string path, long size, string hash, CancellationToken ct)
    {
        if (cache is null)
        {
            return;
        }

        var info = new FileInfo(path);
        await cache.UpsertAsync(Path.GetFullPath(path), size, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), hash, ct);
    }
}

/// <summary>Reads a changed file's own tags back into Library (the same re-read the folder watcher does for a file changed in place).</summary>
public interface IAssetRereader
{
    Task<FileMetadataRereadResult> RereadAsync(Guid assetId, CancellationToken ct);
}

/// <summary>The re-read of the ingestion engine: local tags and technical facts, with no rematching and no moving.</summary>
public sealed class IngestionAssetRereader(IIngestionEngine engine) : IAssetRereader
{
    public Task<FileMetadataRereadResult> RereadAsync(Guid assetId, CancellationToken ct) =>
        engine.RereadAssetMetadataAsync(assetId, ct);
}
