using System.Security.Cryptography;
using MediaEngine.Ingestion.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Organise;

/// <summary>How a file transfer ended. <see cref="FinalPath"/> is set only when a verified copy is in place.</summary>
/// <param name="FinalPath">Where the verified copy now is (a numbered name when the wanted name was taken).</param>
/// <param name="UniqueName">True when the wanted name was taken by a different file, so a numbered name was used.</param>
/// <param name="Locked">True when the file could not be read yet (it is in use); the job tries it again later.</param>
/// <param name="Failure">A plain reason when no copy was made.</param>
public sealed record MergeCopyResult(string? FinalPath, bool UniqueName = false, bool Locked = false, string? Failure = null);

/// <summary>
/// Puts a verified copy of a file beside its new name, and (for a move) removes the original only after the copy has been
/// checked byte for byte. The copy is written to a temporary file in the target folder, its size and SHA-256 are compared
/// with what was read from the original, and only then is it renamed into place; a name that is taken is never overwritten.
/// A copy that does not match is discarded and the original is not touched.
/// </summary>
public sealed class MergeFileTransfer
{
    private const int CopyBufferBytes = 1024 * 1024;
    private const int NameAttempts = 5;

    /// <summary>Temporary copies carry this ending, so a restart can tell and clear the ones a stopped job left behind.</summary>
    public const string PartSuffix = ".merge-part";

    private readonly IAssetHasher _hasher;
    private readonly ILogger<MergeFileTransfer> _logger;

    public MergeFileTransfer(IAssetHasher hasher, ILogger<MergeFileTransfer> logger)
    {
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(logger);
        _hasher = hasher;
        _logger = logger;
    }

    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <paramref name="targetPath"/>, or to a numbered name beside it when that name
    /// is taken. The original is never changed.
    /// </summary>
    public async Task<MergeCopyResult> CopyVerifiedAsync(string sourcePath, string targetPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var folder = Path.GetDirectoryName(targetPath)!;
        var part = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(targetPath)}.{Guid.NewGuid():N}{PartSuffix}");
        try
        {
            Directory.CreateDirectory(folder);
            var before = Snapshot(sourcePath);

            string readHash;
            await using (var input = new FileStream(
                             sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, useAsync: true))
            await using (var output = new FileStream(
                             part, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes, useAsync: true))
            {
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[CopyBufferBytes];
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    digest.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                readHash = Convert.ToHexStringLower(digest.GetHashAndReset());
            }

            File.SetLastWriteTimeUtc(part, File.GetLastWriteTimeUtc(sourcePath));

            // The original must not have changed while it was being copied, and the copy must hold exactly what was read.
            if (!Snapshot(sourcePath).Equals(before))
            {
                TryDelete(part);
                return new MergeCopyResult(null, Failure: "The file changed while it was being copied, so nothing was kept.");
            }

            if (new FileInfo(part).Length != before.Length
                || !string.Equals((await _hasher.ComputeAsync(part, ct).ConfigureAwait(false)).Hex, readHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("The copy of {Source} did not match the original; the copy was discarded", sourcePath);
                TryDelete(part);
                return new MergeCopyResult(null, Failure: "The copy did not match the original, so it was discarded and the original was not touched.");
            }

            var unique = false;
            for (var attempt = 1; ; attempt++)
            {
                var candidate = attempt == 1 && !File.Exists(targetPath) ? targetPath : UniqueName(targetPath);
                try
                {
                    File.Move(part, candidate, overwrite: false);
                    return new MergeCopyResult(candidate, UniqueName: unique || !string.Equals(candidate, targetPath, StringComparison.Ordinal));
                }
                catch (IOException) when (File.Exists(candidate) && attempt < NameAttempts)
                {
                    // Someone took that name between the check and the rename: use the next free numbered one.
                    unique = true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not copy {Source} to {Target} yet", sourcePath, targetPath);
            TryDelete(part);
            return new MergeCopyResult(null, Locked: true, Failure: ex.Message);
        }
        catch
        {
            // A stopped or failed copy removes its own partial file and leaves the original as it was.
            TryDelete(part);
            throw;
        }
    }

    /// <summary>True when the two files have the same size and the same SHA-256, both read fresh.</summary>
    public async Task<bool> SameContentAsync(string first, string second, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(first) || !File.Exists(second) || new FileInfo(first).Length != new FileInfo(second).Length)
            {
                return false;
            }

            var a = (await _hasher.ComputeAsync(first, ct).ConfigureAwait(false)).Hex;
            var b = (await _hasher.ComputeAsync(second, ct).ConfigureAwait(false)).Hex;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read is never treated as identical, so nothing is ever removed on a guess.
            _ = ex;
            return false;
        }
    }

    /// <summary>
    /// Removes the original of a move, but only when the copy is still there with the same bytes. Returns false (and leaves the
    /// original) when the copy is missing or different, or the original cannot be removed yet.
    /// </summary>
    public async Task<bool> DeleteOriginalIfCopyMatchesAsync(string sourcePath, string copyPath, CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath))
        {
            return true;
        }

        // Two names for one file must never count as "a copy": removing the original would remove the only file.
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(copyPath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!await SameContentAsync(sourcePath, copyPath, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Not removing {Source}: the copy at {Copy} could not be confirmed byte for byte", sourcePath, copyPath);
            return false;
        }

        for (var attempt = 1; attempt <= NameAttempts; attempt++)
        {
            try
            {
                File.Delete(sourcePath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not remove {Source} on attempt {Attempt}", sourcePath, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>Removes the temporary copies a stopped merge left in a folder. Only this job writes them, one at a time.</summary>
    public void RemoveLeftoverParts(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (var stale in Directory.EnumerateFiles(folder, "*" + PartSuffix).Where(path => Path.GetFileName(path).StartsWith('.')))
            {
                TryDelete(stale);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not look for leftover temporary copies in {Folder}", folder);
        }
    }

    /// <summary>The first <c>Name (2).ext</c>, <c>Name (3).ext</c>... that does not exist.</summary>
    public static string UniqueName(string target)
    {
        var folder = Path.GetDirectoryName(target) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);
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

    private readonly record struct FileState(long Length, DateTime LastWriteUtc);

    private static FileState Snapshot(string path)
    {
        var info = new FileInfo(path);
        return new FileState(info.Length, info.LastWriteTimeUtc);
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
}
