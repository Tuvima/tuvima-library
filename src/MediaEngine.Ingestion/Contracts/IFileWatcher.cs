using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Contracts;

/// <summary>
/// Interface for the OS-specific file system observation service.
/// Spec: Phase 7 – Interfaces § IFileWatcher.
///
/// Raises a <see cref="FileDetected"/> event for every raw OS notification.
/// Consumers (typically <see cref="DebounceQueue"/>) are responsible for
/// coalescing and gating the raw events before processing.
/// </summary>
public interface IFileWatcher : IDisposable
{
    /// <summary>
    /// Fired on the watcher's internal thread whenever the OS reports a
    /// file system change.  Handlers MUST be fast and non-blocking.
    /// </summary>
    event EventHandler<FileEvent> FileDetected;

    /// <summary>
    /// Fired when the underlying watcher reports an error. Consumers should use
    /// this to trigger a rescan/restart path because OS events may have been lost.
    /// </summary>
    event EventHandler<FileWatcherErrorEvent> WatcherError;

    /// <summary>Total raw OS events received since startup.</summary>
    long EventCount { get; }

    /// <summary>Timestamp of the last raw OS event, or null if none.</summary>
    DateTimeOffset? LastEventAt { get; }

    /// <summary>Total watcher errors received since startup.</summary>
    long ErrorCount { get; }

    /// <summary>Timestamp of the last watcher error, or null if none.</summary>
    DateTimeOffset? LastErrorAt { get; }

    /// <summary>Kind of the last watcher error, or null if no error has occurred.</summary>
    string? LastErrorKind { get; }

    /// <summary>Message from the last watcher error, or null if no error has occurred.</summary>
    string? LastErrorMessage { get; }

    /// <summary>Whether the watcher is currently active.</summary>
    bool IsRunning { get; }

    /// <summary>Paths currently being watched.</summary>
    IReadOnlyList<string> WatchedPaths { get; }

    /// <summary>
    /// Registers a directory to be observed.
    /// May be called multiple times to watch several root paths.
    /// </summary>
    void AddDirectory(string path, bool includeSubdirectories = true);

    /// <summary>
    /// Stops observing one directory and releases its OS resources, leaving every other watched directory running
    /// untouched. Returns <see langword="false"/> when the directory was not being watched. Never touches the directory.
    /// </summary>
    bool RemoveDirectory(string path);

    /// <summary>
    /// Whether the watched directory <paramref name="path"/> includes its subdirectories, or <see langword="null"/> when the
    /// directory is not being watched.
    /// </summary>
    bool? GetIncludeSubdirectories(string path);

    /// <summary>Begins raising <see cref="FileDetected"/> events.</summary>
    void Start();

    /// <summary>Pauses event raising without releasing OS resources.</summary>
    void Stop();

    /// <summary>
    /// Atomically replaces the currently watched directory with <paramref name="path"/>.
    /// Stops all existing <see cref="System.IO.FileSystemWatcher"/> instances, disposes
    /// them, and starts a fresh watcher on the new path.  If the watcher was running
    /// before the call it resumes immediately on the new path.
    /// </summary>
    /// <param name="path">New directory to observe.</param>
    /// <param name="includeSubdirectories">
    /// Whether subdirectories under <paramref name="path"/> should also be monitored.
    /// </param>
    /// <exception cref="ObjectDisposedException">Thrown if the watcher has been disposed.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when <paramref name="path"/> does not exist.</exception>
    void UpdateDirectory(string path, bool includeSubdirectories = true);

    /// <summary>
    /// Atomically replaces the currently watched directories with
    /// <paramref name="paths"/>.
    /// </summary>
    void UpdateDirectories(IEnumerable<string> paths, bool includeSubdirectories = true)
    {
        var firstPath = paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (!string.IsNullOrWhiteSpace(firstPath))
        {
            UpdateDirectory(firstPath, includeSubdirectories);
        }
    }
}
