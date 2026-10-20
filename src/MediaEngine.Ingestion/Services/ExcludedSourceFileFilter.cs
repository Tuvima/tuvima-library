using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Default <see cref="IExcludedSourceFileFilter"/>. The exclusions are kept in memory, keyed by source folder and the file's place
/// inside it, and loaded the first time they are needed. An excluded file whose size and last-write time still match its row is
/// skipped without being read; one that changed is read once to tell a touched copy from a different file.
/// </summary>
public sealed class ExcludedSourceFileFilter : IExcludedSourceFileFilter
{
    private readonly ILibraryFolderResolver _folders;
    private readonly IExcludedSourceFileRepository _excluded;
    private readonly IAssetHasher _hasher;
    private readonly ILogger<ExcludedSourceFileFilter> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly object _indexLock = new();
    private Dictionary<string, ExcludedSourceFile>? _index;
    private int _version;

    public ExcludedSourceFileFilter(
        ILibraryFolderResolver folders,
        IExcludedSourceFileRepository excluded,
        IAssetHasher hasher,
        ILogger<ExcludedSourceFileFilter> logger)
    {
        _folders = folders;
        _excluded = excluded;
        _hasher = hasher;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        lock (_indexLock)
        {
            _version++;
            _index = null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ShouldSkipAsync(string fullPath, CancellationToken ct = default)
    {
        var index = await GetIndexAsync(ct).ConfigureAwait(false);
        lock (_indexLock)
        {
            // Nothing is excluded anywhere: no need to work out which source folder the file is in.
            if (index.Count == 0)
            {
                return false;
            }
        }

        var source = _folders.ResolveSourceForPath(fullPath);
        if (source is null)
        {
            return false;
        }

        var relative = ExcludedSourceFilePaths.RelativePathOf(Path.GetFullPath(source.Source.Path), Path.GetFullPath(fullPath));
        if (relative is null)
        {
            return false;
        }

        var key = KeyOf(source.Source.Id, relative);
        ExcludedSourceFile? row;
        lock (_indexLock)
        {
            if (!index.TryGetValue(key, out row))
            {
                return false;
            }
        }

        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read {FileName} to check it against the excluded files; leaving it for the next scan", Path.GetFileName(fullPath));
            return true;
        }

        var size = info.Length;
        var lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (row.FileSize == size && row.LastWriteUtc == lastWrite)
        {
            return true;
        }

        string hash;
        try
        {
            hash = (await _hasher.ComputeAsync(fullPath, ct).ConfigureAwait(false)).Hex;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still being written or locked: it is not ingested this time and is looked at again by the next scan.
            _logger.LogDebug(ex, "Could not read {FileName} to check it against the excluded files; leaving it for the next scan", Path.GetFileName(fullPath));
            return true;
        }

        var sameContent = string.IsNullOrWhiteSpace(row.ContentHash)
            ? row.FileSize == size
            : string.Equals(row.ContentHash, hash, StringComparison.OrdinalIgnoreCase);
        if (sameContent)
        {
            // The same file, touched. Remember its new facts so the next scan does not read it again.
            var refreshed = row with { ContentHash = hash, FileSize = size, LastWriteUtc = lastWrite };
            try
            {
                await _excluded.AddOrReplaceAsync(refreshed, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The file is still the excluded one, so it stays skipped; only the remembered facts could not be saved.
                _logger.LogWarning(ex, "Could not save the refreshed facts of the excluded file {RelativePath}; it stays excluded", relative);
                return true;
            }

            lock (_indexLock)
            {
                if (ReferenceEquals(_index, index))
                {
                    index[key] = refreshed;
                }
            }

            return true;
        }

        // A different file now sits at this place: it is a new file, not the one that was deleted.
        await _excluded.RemoveAsync(row.Id, ct).ConfigureAwait(false);
        lock (_indexLock)
        {
            // Only the lifted exclusion leaves the remembered rows; the others stay as they are.
            if (ReferenceEquals(_index, index))
            {
                index.Remove(key);
            }
        }

        _logger.LogInformation(
            "A different file now sits at the excluded place {RelativePath} in source {SourceId}; the exclusion was lifted and the file will be ingested",
            relative, source.Source.Id);
        return false;
    }

    private async Task<Dictionary<string, ExcludedSourceFile>> GetIndexAsync(CancellationToken ct)
    {
        lock (_indexLock)
        {
            if (_index is not null)
            {
                return _index;
            }
        }

        await _loadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int version;
            lock (_indexLock)
            {
                if (_index is not null)
                {
                    return _index;
                }

                version = _version;
            }

            var rows = await _excluded.ListAllAsync(ct).ConfigureAwait(false);
            var loaded = new Dictionary<string, ExcludedSourceFile>(PathSafety.Comparer);
            foreach (var row in rows)
            {
                loaded[KeyOf(row.SourceId, row.RelativePath)] = row;
            }

            lock (_indexLock)
            {
                // An Invalidate during the read means these rows may already be stale: answer from them once, but do not keep them.
                if (version == _version)
                {
                    _index = loaded;
                }
            }

            return loaded;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static string KeyOf(string sourceId, string relativePath) => sourceId + "\n" + relativePath;
}
