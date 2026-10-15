using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion;

/// <summary>
/// Keeps the file watchers in step with the library folders as they are saved: a folder added in the Dashboard starts being
/// watched (and gets an initial scan) straight away, a removed folder stops being watched, and a folder whose sub-folder
/// setting changed is restarted. Folders that did not change keep their watcher untouched. The folder list comes from
/// <see cref="IngestionOptions"/>, which is re-read whenever <c>libraries.json</c> is saved
/// (see <see cref="Services.LibrariesOptionsChangeTokenSource"/>); the watchers are only ever started or stopped here, and
/// a watched folder is read, never written to.
/// </summary>
public sealed partial class IngestionEngine
{
    /// <summary>What the engine wants watched for one folder.</summary>
    private readonly record struct WatchFolderSpec(bool IncludeSubdirectories, bool MayCreateIfMissing);

    // One rebuild at a time: the folders the engine currently watches, keyed by full path, with their sub-folder setting.
    // Every start/stop (including the late import-folder watch) happens under this lock, so rapid saves cannot race.
    private readonly object _watchedFoldersLock = new();
    private readonly Dictionary<string, bool> _watchedFolders = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _folderChangeSubscription;

    private bool IsStopping => Volatile.Read(ref _disposeState) != 0 || _shutdownCts.IsCancellationRequested;

    private void SubscribeToFolderChanges()
    {
        if (_optionsMonitor is null)
        {
            return;
        }

        lock (_watchedFoldersLock)
        {
            _folderChangeSubscription ??= _optionsMonitor.OnChange(OnLibraryOptionsChanged);
        }
    }

    private void UnsubscribeFromFolderChanges()
    {
        IDisposable? subscription;
        lock (_watchedFoldersLock)
        {
            subscription = _folderChangeSubscription;
            _folderChangeSubscription = null;
        }

        subscription?.Dispose();
    }

    /// <summary>Runs on the thread that saved the settings: swap in the new options and hand the real work to an owned task.</summary>
    private void OnLibraryOptionsChanged(IngestionOptions updated, string? name)
    {
        if (IsStopping)
        {
            return;
        }

        _options = updated;
        TrackBackgroundTask(ApplyChangedFoldersAsync(), "library folder change");
    }

    private async Task ApplyChangedFoldersAsync()
    {
        try
        {
            // Leave the thread that saved the settings straight away; the rebuild runs on the thread pool.
            await Task.Yield();

            var newlyWatched = ReconcileWatchedFolders(startup: false);
            if (newlyWatched.Count > 0 && !IsStopping)
            {
                // Same known-path pre-filter as the startup scan, so files already ingested are not queued again.
                await ScanExistingFilesAsync(newlyWatched, LifetimeToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (IsStopping)
        {
            // The engine is stopping; the rest of the change is not needed.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Applying the changed library folders failed - the watched folders stay as they were until the next change");
        }
    }

    /// <summary>
    /// Brings the watchers in line with the folders the current options ask for: stops removed folders, restarts folders
    /// whose sub-folder setting changed, starts new ones. Returns the folders that were just started, which need an
    /// initial scan. Safe to call repeatedly and from several threads; a call that finds nothing to change does nothing.
    /// </summary>
    private IReadOnlyList<IngestionScanTarget> ReconcileWatchedFolders(bool startup)
    {
        var started = new List<IngestionScanTarget>();

        lock (_watchedFoldersLock)
        {
            if (IsStopping)
            {
                return started;
            }

            var desired = BuildDesiredWatchFolders();

            foreach (var (path, includeSubdirectories) in _watchedFolders.ToList())
            {
                if (desired.TryGetValue(path, out var spec) && spec.IncludeSubdirectories == includeSubdirectories)
                {
                    continue;
                }

                StopWatching(path, forgetPollState: !desired.ContainsKey(path));
            }

            foreach (var (path, spec) in desired)
            {
                if (_watchedFolders.ContainsKey(path))
                {
                    continue;
                }

                if (TryStartWatching(path, spec.IncludeSubdirectories, spec.MayCreateIfMissing)
                    && Directory.Exists(path))
                {
                    started.Add(new IngestionScanTarget(path, spec.IncludeSubdirectories));
                }
            }

            if (startup)
            {
                if (desired.Count == 0)
                {
                    _logger.LogInformation(
                        "IngestionEngine: No WatchDirectory configured. " +
                        "Set a Watch Folder in Settings to begin file ingestion.");
                }

                // Mark the watcher as "running" so that a folder added later is watched immediately.
                // Safe to call with zero directories — Start() is a no-op on an empty list.
                _watcher.Start();
            }
        }

        return started;
    }

    private Dictionary<string, WatchFolderSpec> BuildDesiredWatchFolders()
    {
        var desired = new Dictionary<string, WatchFolderSpec>(StringComparer.OrdinalIgnoreCase);
        var importPath = _importFolders?.GetImportPath() ?? _options.ImportFolderPath;

        foreach (var target in GetConfiguredScanTargets())
        {
            string path;
            try
            {
                path = NormalizeDirectoryPath(target.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _logger.LogWarning(ex, "Ignoring a library folder whose path is not valid: {Path}", target.Path);
                continue;
            }

            // Only a Tuvima-managed writable folder (or the Import folder) may be created when it is missing; a read-only
            // or as-is folder that is not there (an offline drive, a mistyped path) is left alone and not watched.
            var mayCreate = IsSamePath(path, importPath)
                || _options.LibraryFolders.Any(library => library.Sources.Any(source =>
                    source.AllowsFileMutation && IsSamePath(path, source.Path)));

            desired[path] = new WatchFolderSpec(target.IncludeSubdirectories, mayCreate);
        }

        return desired;
    }

    private static bool IsSamePath(string normalizedPath, string? other)
    {
        if (string.IsNullOrWhiteSpace(other))
        {
            return false;
        }

        try
        {
            return string.Equals(normalizedPath, NormalizeDirectoryPath(other), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path that cannot be normalised cannot be the same folder; the caller already skips invalid targets.
            _ = ex;
            return false;
        }
    }

    /// <summary>Starts watching one folder. Must be called under <see cref="_watchedFoldersLock"/>.</summary>
    private bool TryStartWatching(string path, bool includeSubdirectories, bool mayCreateIfMissing)
    {
        try
        {
            // Another path (the late Import-folder watch) may already have it: adopt that watcher rather than doubling it up.
            if (_watcher.WatchedPaths.Any(watched => IsSamePath(path, watched)))
            {
                _watchedFolders[path] = includeSubdirectories;
                return false;
            }

            if (!mayCreateIfMissing && !Directory.Exists(path))
            {
                _logger.LogWarning(
                    "IngestionEngine: Watch directory does not exist: {Path}. " +
                    "Check the drive is connected or update the path in Settings.",
                    path);
                return false;
            }

            _watcher.AddDirectory(path, includeSubdirectories);
            _watchedFolders[path] = includeSubdirectories;
            _logger.LogInformation(
                "Watching: {Path} (recursive={Recursive})",
                path,
                includeSubdirectories);
            return true;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.LogWarning(
                ex,
                "IngestionEngine: Watch directory does not exist or cannot be watched: {Path}. " +
                "Create the directory or update the path in Settings.",
                path);
            return false;
        }
        catch (ObjectDisposedException ex)
        {
            // The watcher was disposed while the engine is shutting down; there is nothing left to start.
            _logger.LogDebug(ex, "File watcher already disposed; not watching {Path}", path);
            return false;
        }
    }

    /// <summary>Stops watching one folder. Must be called under <see cref="_watchedFoldersLock"/>.</summary>
    private void StopWatching(string path, bool forgetPollState)
    {
        try
        {
            _watcher.RemoveDirectory(path);
        }
        catch (ObjectDisposedException ex)
        {
            // The watcher was disposed while the engine is shutting down; the folder is already unwatched.
            _logger.LogDebug(ex, "File watcher already disposed; {Path} is already unwatched", path);
        }

        _watchedFolders.Remove(path);
        _logger.LogInformation("Stopped watching: {Path}", path);

        if (forgetPollState)
        {
            ForgetPollFingerprintsUnder(path);
        }
    }

    /// <summary>Drops the polling fallback's remembered file states for a folder that is no longer watched.</summary>
    private void ForgetPollFingerprintsUnder(string folder)
    {
        var prefix = folder + Path.DirectorySeparatorChar;
        lock (_fswBufferLock)
        {
            foreach (var path in _pollFingerprints.Keys.ToList())
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    _pollFingerprints.Remove(path);
                }
            }
        }
    }
}
