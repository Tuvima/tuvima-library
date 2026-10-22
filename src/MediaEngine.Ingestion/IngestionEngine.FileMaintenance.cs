using System.Text.Json;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Capabilities;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Detection;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Intelligence.Models;
using MediaEngine.Processors.Contracts;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion;

public sealed partial class IngestionEngine
{
    /// <summary>
    /// The full paths (old and new place) of files with a library move in progress. Empty when nothing is moving. A failure to ask is
    /// logged and treated as nothing moving, so ingestion carries on as it did before the mark existed.
    /// </summary>
    private async Task<IReadOnlySet<string>> LoadMovePathsAsync(CancellationToken ct)
    {
        if (_moveInProgress is null)
        {
            return EmptyPaths;
        }

        try
        {
            return await _moveInProgress.GetPathsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not ask which files are being moved; carrying on as if none are.");
            return EmptyPaths;
        }
    }

    private static readonly IReadOnlySet<string> EmptyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the path, or the place a rename came from, is the old or new place of a file being moved.</summary>
    private async Task<bool> IsMoveInProgressAsync(string path, string? oldPath, CancellationToken ct)
    {
        var moving = await LoadMovePathsAsync(ct).ConfigureAwait(false);
        return moving.Count > 0
            && (moving.Contains(Path.GetFullPath(path))
                || (!string.IsNullOrWhiteSpace(oldPath) && moving.Contains(Path.GetFullPath(oldPath))));
    }

    private async Task HandleDeletedAsync(IngestionCandidate candidate, CancellationToken ct)
    {
        _logger.LogInformation("File deleted: {Path}", candidate.Path);

        await DeleteHashCacheEntryAsync(candidate.Path, ct).ConfigureAwait(false);

        // Look up the asset by its stored file path.
        // The file is gone so we can't hash it, but file_path_root is still in the DB.
        var asset = await _assetRepo.FindByPathRootAsync(candidate.Path, ct)
                                    .ConfigureAwait(false);

        if (asset is null)
        {
            _logger.LogDebug(
                "No asset record found for deleted path {Path} — nothing to orphan.",
                candidate.Path);
            return;
        }

        var lineage = _writeBackStageDependencies.WorkRepository is null
            ? null
            : await _writeBackStageDependencies.WorkRepository.GetLineageByAssetAsync(asset.Id, ct).ConfigureAwait(false);
        var featureId = lineage?.MediaType switch
        {
            MediaType.Books or MediaType.Comics => AccountFeatureId.Read.Value,
            MediaType.Movies or MediaType.TV => AccountFeatureId.Watch.Value,
            MediaType.Audiobooks or MediaType.Music => AccountFeatureId.Listen.Value,
            _ => null,
        };
        var libraryId = Guid.TryParse(asset.LibraryId, out var parsedLibraryId) && parsedLibraryId != Guid.Empty
            ? parsedLibraryId
            : (Guid?)null;

        await _assetRepo.UpdateStatusAsync(asset.Id, Domain.Enums.AssetStatus.Orphaned, ct)
                        .ConfigureAwait(false);

        _logger.LogInformation(
            "Asset {AssetId} marked Orphaned (file no longer exists at {Path}).",
            asset.Id, candidate.Path);

        await SafePublishAsync(
            SignalREvents.MediaRemoved,
            new MediaRemovedEvent(asset.Id, candidate.Path, "Orphaned", libraryId, featureId),
            ct).ConfigureAwait(false);
    }

    private async Task<HashLookupResult> ComputeHashWithCacheAsync(string filePath, CancellationToken ct)
    {
        var info = new FileInfo(filePath);
        var absolutePath = Path.GetFullPath(filePath);
        var mtimeUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

        if (_hashStageDependencies.FileHashCache is not null)
        {
            try
            {
                var cachedHash = await _hashStageDependencies.FileHashCache.TryGetAsync(
                    absolutePath,
                    info.Length,
                    mtimeUtc,
                    ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(cachedHash))
                {
                    return new HashLookupResult(new HashResult
                    {
                        FilePath = absolutePath,
                        Hex = cachedHash,
                        FileSize = info.Length,
                        Elapsed = TimeSpan.Zero,
                    }, CacheHit: true);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Hash cache lookup failed for {Path}; computing hash", absolutePath);
            }
        }

        var hash = await _hasher.ComputeAsync(absolutePath, ct).ConfigureAwait(false);

        if (_hashStageDependencies.FileHashCache is not null)
        {
            try
            {
                await _hashStageDependencies.FileHashCache.UpsertAsync(
                    absolutePath,
                    hash.FileSize,
                    mtimeUtc,
                    hash.Hex,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Hash cache upsert failed for {Path}", absolutePath);
            }
        }

        return new HashLookupResult(hash, CacheHit: false);
    }

    private async Task DeleteHashCacheEntryAsync(string path, CancellationToken ct)
    {
        if (_hashStageDependencies.FileHashCache is null)
        {
            return;
        }

        try
        {
            await _hashStageDependencies.FileHashCache.DeleteAsync(Path.GetFullPath(path), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to remove hash cache entry for {Path}", path);
        }
    }

    /// <summary>
    /// The copy that stays must still be there, must be a different file from the one being removed, and must still hold the
    /// same bytes right now. It is read fresh: the stored fingerprint only says what the file held when it was catalogued, and a
    /// file edited or replaced since must never make its twin look safe to delete.
    /// </summary>
    private async Task<bool> KeptCopyStillMatchesAsync(
        string duplicatePath, string keptPath, string expectedHash, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(keptPath) || MediaEngine.Ingestion.Organise.FileIdentity.AreSameFile(duplicatePath, keptPath))
            {
                return false;
            }

            // A different size can never be the same bytes, so a changed copy is refused without reading it all.
            if (new FileInfo(keptPath).Length != new FileInfo(duplicatePath).Length)
            {
                return false;
            }

            var fresh = await _hasher.ComputeAsync(keptPath, ct).ConfigureAwait(false);
            return string.Equals(fresh.Hex, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A copy that cannot be read is not treated as identical, so nothing is removed on a guess.
            _logger.LogWarning(ex, "Could not confirm the copy at {Kept} before removing {Duplicate}", keptPath, duplicatePath);
            return false;
        }
    }

    // =========================================================================
    // Dry-run simulation
    // =========================================================================

}
