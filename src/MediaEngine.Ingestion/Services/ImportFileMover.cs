using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Moves a file out of the import folder into its type's managed folder. On the same drive it renames the file. Across
/// drives it copies to a temporary file beside the final name, checks the copy against the fingerprint taken when the file
/// was ingested, renames the copy into place, and only then removes the original, so an interrupted or failed move never
/// loses the file. A name that is already taken always gets a unique suffix; nothing is overwritten.
/// </summary>
public sealed class ImportFileMover
{
    private const int MaxAttempts = 5;
    private const int CopyBufferBytes = 1024 * 1024;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly IAssetHasher _hasher;
    private readonly ILogger<ImportFileMover> _logger;
    private readonly IFileHashCacheRepository? _hashCache;

    /// <summary>Copy, check and then remove even when both ends are on the same drive. Used to exercise the cross-drive path.</summary>
    public bool AlwaysCopyAndVerify { get; init; }

    public ImportFileMover(
        IAssetHasher hasher,
        ILogger<ImportFileMover> logger,
        IFileHashCacheRepository? hashCache = null)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(logger);
        _hasher = hasher;
        _logger = logger;
        _hashCache = hashCache;
    }

    /// <summary>
    /// Moves <paramref name="sourcePath"/> to <paramref name="destinationPath"/>, or to a unique name beside it when that
    /// name is taken. Returns the path the file now has, or <see langword="null"/> when it was not moved (the original is
    /// left exactly where it was).
    /// </summary>
    /// <param name="expectedHash">The SHA-256 recorded when the file was ingested. A copy that does not match it is discarded.</param>
    /// <param name="adoptIdenticalCopy">
    /// True when the file being moved is the one this move is about: if a byte-identical file is already at the destination
    /// (an earlier attempt was interrupted after the copy), that file is adopted and only the original is removed, so a
    /// restart never leaves two copies. False for a genuine duplicate that must be kept with a unique name.
    /// </param>
    public async Task<string?> MoveAsync(
        string sourcePath,
        string destinationPath,
        string? expectedHash,
        bool adoptIdenticalCopy = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (!File.Exists(sourcePath))
        {
            _logger.LogWarning("Import move skipped - the file is no longer there: {Source}", sourcePath);
            return null;
        }

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
        {
            return destinationPath;
        }

        try
        {
            if (Path.GetDirectoryName(destinationPath) is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
            }

            if (adoptIdenticalCopy
                && File.Exists(destinationPath)
                && await IsIdenticalAsync(sourcePath, destinationPath, expectedHash, ct).ConfigureAwait(false))
            {
                await MoveHashCacheAsync(sourcePath, destinationPath, ct).ConfigureAwait(false);
                await TryDeleteWithRetryAsync(sourcePath, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Resumed an interrupted import: {Destination} already holds this file, so only the original was removed",
                    destinationPath);
                return destinationPath;
            }

            return !AlwaysCopyAndVerify && VolumeProbe.SameVolume(sourcePath, destinationPath)
                ? await RenameAsync(sourcePath, destinationPath, ct).ConfigureAwait(false)
                : await CopyVerifyDeleteAsync(sourcePath, destinationPath, expectedHash, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Import move failed; the file stays where it is: {Source}", sourcePath);
            return null;
        }
    }

    private async Task<bool> IsIdenticalAsync(
        string sourcePath, string destinationPath, string? expectedHash, CancellationToken ct)
    {
        if (new FileInfo(sourcePath).Length != new FileInfo(destinationPath).Length)
        {
            return false;
        }

        var original = string.IsNullOrWhiteSpace(expectedHash)
            ? (await _hasher.ComputeAsync(sourcePath, ct).ConfigureAwait(false)).Hex
            : expectedHash;
        var present = (await _hasher.ComputeAsync(destinationPath, ct).ConfigureAwait(false)).Hex;
        return string.Equals(original, present, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> RenameAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var target = UniquePath(destinationPath);
            try
            {
                File.Move(sourcePath, target);
                await MoveHashCacheAsync(sourcePath, target, ct).ConfigureAwait(false);
                _logger.LogInformation("Filed import {Source} -> {Destination}", sourcePath, target);
                return target;
            }
            catch (IOException ex) when (attempt < MaxAttempts)
            {
                // The name was taken a moment ago, or the file is briefly locked: pick a name again and retry.
                _logger.LogDebug(ex, "Import move attempt {Attempt} failed for {Source}", attempt, sourcePath);
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    private async Task<string?> CopyVerifyDeleteAsync(
        string sourcePath,
        string destinationPath,
        string? expectedHash,
        CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(destinationPath)!;
        RemoveStaleParts(folder, Path.GetFileNameWithoutExtension(destinationPath));
        var partPath = Path.Combine(
            folder,
            $".{Path.GetFileNameWithoutExtension(destinationPath)}.{Guid.NewGuid():N}.part");

        try
        {
            await using (var source = new FileStream(
                             sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, useAsync: true))
            await using (var copy = new FileStream(
                             partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes, useAsync: true))
            {
                await source.CopyToAsync(copy, CopyBufferBytes, ct).ConfigureAwait(false);
                await copy.FlushAsync(ct).ConfigureAwait(false);
                copy.Flush(flushToDisk: true);
            }

            File.SetLastWriteTimeUtc(partPath, File.GetLastWriteTimeUtc(sourcePath));

            var sourceLength = new FileInfo(sourcePath).Length;
            if (new FileInfo(partPath).Length != sourceLength)
            {
                _logger.LogWarning("Import copy was a different size from the original; the original stays: {Source}", sourcePath);
                TryDelete(partPath);
                return null;
            }

            var recorded = string.IsNullOrWhiteSpace(expectedHash)
                ? (await _hasher.ComputeAsync(sourcePath, ct).ConfigureAwait(false)).Hex
                : expectedHash;
            var copied = (await _hasher.ComputeAsync(partPath, ct).ConfigureAwait(false)).Hex;
            if (!string.Equals(recorded, copied, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Import copy did not match the original (it may have changed while copying); the original stays: {Source}",
                    sourcePath);
                TryDelete(partPath);
                return null;
            }

            var finalPath = RenameIntoPlace(partPath, destinationPath);
            await MoveHashCacheAsync(sourcePath, finalPath, ct).ConfigureAwait(false);

            // Only now, with a verified copy in place, is the original removed.
            if (!await TryDeleteWithRetryAsync(sourcePath, ct).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "Filed {Destination} but could not remove the original from the import folder: {Source}",
                    finalPath, sourcePath);
            }

            _logger.LogInformation("Filed import across drives {Source} -> {Destination}", sourcePath, finalPath);
            return finalPath;
        }
        catch
        {
            // A failed or cancelled copy leaves the original untouched and removes its own partial file.
            TryDelete(partPath);
            throw;
        }
    }

    /// <summary>Removes temporary copies an earlier, interrupted attempt left beside the destination.</summary>
    private void RemoveStaleParts(string folder, string stem)
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(folder, $".{stem}.*.part")
                         .Where(path => File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddHours(-1)))
            {
                TryDelete(stale);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not look for leftover temporary copies in {Folder}", folder);
        }
    }

    private static string RenameIntoPlace(string partPath, string destinationPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            var target = UniquePath(destinationPath);
            try
            {
                File.Move(partPath, target);
                return target;
            }
            catch (IOException) when (attempt < MaxAttempts && File.Exists(target))
            {
                // Someone took that name between the check and the rename; pick the next free one.
            }
        }
    }

    private async Task MoveHashCacheAsync(string from, string to, CancellationToken ct)
    {
        if (_hashCache is null)
        {
            return;
        }

        await _hashCache.MoveAsync(Path.GetFullPath(from), Path.GetFullPath(to), ct).ConfigureAwait(false);
    }

    private async Task<bool> TryDeleteWithRetryAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not remove {Path} on attempt {Attempt}", path, attempt);
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove the temporary file {Path}", path);
        }
    }

    /// <summary>The path itself when it is free, otherwise the first <c>Name (2).ext</c>, <c>Name (3).ext</c>... that is.</summary>
    internal static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var number = 2; number < 10_000; number++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({number}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }
}
