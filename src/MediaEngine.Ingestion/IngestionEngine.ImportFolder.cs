using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion;

/// <summary>
/// The optional Import folder: watching it, giving its files a type from their per-type folder, applying the duplicate
/// rule to an import copy, and the periodic sweep that files what was waiting. The decisions themselves live in
/// <see cref="IImportFolderService"/> and <see cref="IImportFolderFiler"/>.
/// </summary>
public sealed partial class IngestionEngine
{
    private IReadOnlyList<IngestionScanTarget> GetConfiguredScanTargets() =>
        _options.LibraryFolders
            .Where(library => string.Equals(library.Kind, LibraryKinds.Catalogued, StringComparison.OrdinalIgnoreCase))
            // A folder that was merged into another one is no longer scanned: its files are catalogued as the copies in the target.
            .SelectMany(library => library.Sources.Where(source => !source.IsMergedAway).Select(source =>
                new IngestionScanTarget(source.Path, source.IncludeSubdirectories)))
            .Concat(GetImportScanTargets())
            .Where(target => !string.IsNullOrWhiteSpace(target.Path))
            .GroupBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => new IngestionScanTarget(
                group.Key,
                group.Any(target => target.IncludeSubdirectories)))
            .ToList();

    /// <summary>The import folder as configured now (so one set after startup is included), or nothing when none is set.</summary>
    private IEnumerable<IngestionScanTarget> GetImportScanTargets()
    {
        var path = _importFolders?.GetImportPath() ?? _options.ImportFolderPath;
        return string.IsNullOrWhiteSpace(path) ? [] : [new IngestionScanTarget(path)];
    }

    /// <inheritdoc/>
    Task IIngestionEngine.RefreshImportFolderAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var importPath = _importFolders?.GetImportPath();
        if (string.IsNullOrWhiteSpace(importPath) || !Directory.Exists(importPath))
        {
            return Task.CompletedTask;
        }

        // The look at the folder can take a while for a big folder, so it runs as owned background work.
        TrackBackgroundTask(RefreshImportFolderCoreAsync(importPath, LifetimeToken), "import-folder refresh");
        return Task.CompletedTask;
    }

    private async Task RefreshImportFolderCoreAsync(string importPath, CancellationToken ct)
    {
        try
        {
            // The folder may have been set after startup: make sure it is watched, then look at what is already in it.
            _importFolders!.EnsureSubfolders();
            EnsureImportFolderWatched(importPath);
            await ScanExistingFilesAsync([new IngestionScanTarget(importPath)], ct).ConfigureAwait(false);

            if (_importFiler is not null)
            {
                await _importFiler.FilePendingAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Checking the import folder failed - it will be checked again on the next pass");
        }
    }

    private void EnsureImportFolderWatched(string importPath)
    {
        try
        {
            var normalized = NormalizeDirectoryPath(importPath);
            if (_watcher.WatchedPaths.Any(path => string.Equals(
                    NormalizeDirectoryPath(path), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _watcher.AddDirectory(normalized, includeSubdirectories: true);
            _logger.LogInformation("Watching: {Path} (recursive=True)", normalized);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not start watching the import folder {Path}", importPath);
        }
    }

    /// <summary>
    /// The folder an audiobook's author and title are read from: the library source it lives in, or for a file in the
    /// Import folder's Audiobooks folder, that folder.
    /// </summary>
    private string? ResolveAudiobookHintRoot(LibraryFolderEntry? library, string path)
    {
        if (library?.MediaTypes.Contains(MediaType.Audiobooks) == true
            && _libraryFolderResolver?.ResolveSourcePath(path) is { } source)
        {
            return source;
        }

        return _importFolders?.ResolveTypePrior(path) == MediaType.Audiobooks
            ? _importFolders.ResolveTypeFolder(path)
            : null;
    }

    /// <summary>
    /// Applies the global duplicate rule to an exact copy that arrived in the Import folder. Under Keep one copy the import
    /// copy is removed (the copy already held is never touched). Under Keep both the import copy is filed beside the held
    /// copy with a unique name. Returns <see langword="false"/> when the copy is kept but couldn't be filed yet.
    /// </summary>
    private async Task<bool> HandleImportDuplicateAsync(
        IngestionCandidate candidate,
        MediaAsset existing,
        string contentHash,
        CancellationToken ct)
    {
        var importFolders = _importFolders!;
        if (MediaEngine.Domain.Configuration.LibraryDuplicatePolicies.DeletesIncomingDuplicate(importFolders.DuplicatePolicy))
        {
            var policy = importFolders.CreateImportPolicy(allowDelete: true);
            var deleteAllowed = policy is not null
                && _sourceMutationPolicyGate?.Evaluate(new SourceMutationRequest
                {
                    Source = policy,
                    Mutation = SourceMutationKind.Delete,
                    Path = candidate.Path,
                }).Allowed == true;

            if (deleteAllowed)
            {
                // A locked file is retried, and if it still can't be removed the operation stays open so the next pass tries again.
                if (!await TryDeleteImportCopyAsync(candidate.Path, ct).ConfigureAwait(false))
                {
                    return false;
                }

                importFolders.RemoveEmptyFolders(Path.GetDirectoryName(candidate.Path) ?? string.Empty);
            }
            else
            {
                _logger.LogInformation(
                    "Exact duplicate was indexed without deleting {Path}; the import folder's policy does not authorize deletion",
                    candidate.Path);
            }

            await DeleteHashCacheEntryAsync(candidate.Path, ct).ConfigureAwait(false);
            return true;
        }

        var filedTo = _importFiler is null
            ? null
            : await _importFiler.FileDuplicateCopyAsync(candidate.Path, existing.Id, contentHash, ct).ConfigureAwait(false);
        if (filedTo is null)
        {
            return false;
        }

        // A file is indexed once, so the kept copy is not a second library item; the history says where it went.
        await SafeActivityLogAsync(new Domain.Entities.SystemActivityEntry
        {
            ActionType = Domain.Constants.SystemActionType.DuplicateSkipped,
            EntityId = existing.Id,
            EntityType = "MediaAsset",
            Detail = $"Duplicate kept under Keep both: {Path.GetFileName(candidate.Path)} filed as {Path.GetFileName(filedTo)}",
        }, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryDeleteImportCopyAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not remove the import copy {Path} on attempt {Attempt}", path, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct).ConfigureAwait(false);
            }
        }

        _logger.LogWarning("The import copy {Path} is locked; it will be removed on a later pass", path);
        return false;
    }

    private async Task RecoverInterruptedImportFilingsAsync(CancellationToken ct)
    {
        if (_importFiler is null)
        {
            return;
        }

        try
        {
            await _importFiler.RecoverInterruptedFilingsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Finishing interrupted import filings failed - they will be retried on the next start");
        }
    }

    /// <summary>
    /// The periodic look at the import folder: starts watching it if it was set after startup, and files anything that was
    /// waiting for a managed folder, a drive to come back or a review to be resolved.
    /// </summary>
    private async Task SweepImportFolderAsync(CancellationToken ct)
    {
        if (_importFolders?.GetImportPath() is not { } importPath || !Directory.Exists(importPath))
        {
            return;
        }

        _importFolders.EnsureSubfolders();
        EnsureImportFolderWatched(importPath);

        if (_importFiler is null)
        {
            return;
        }

        try
        {
            await _importFiler.FilePendingAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Filing the files waiting in the import folder failed - will try again on the next pass");
        }
    }
}
