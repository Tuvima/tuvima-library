using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Tests.Helpers;

/// <summary>
/// A file watcher that watches nothing but records exactly which folders were started and stopped, so a test can prove
/// which folders the engine (re)started and which it left alone. It never touches the file system.
/// </summary>
internal sealed class RecordingFileWatcher : IFileWatcher
{
    private readonly object _gate = new();
    private readonly List<(string Path, bool Recursive)> _watched = [];
    private readonly List<string> _log = [];

    public event EventHandler<FileEvent>? FileDetected;
    public event EventHandler<FileWatcherErrorEvent>? WatcherError;

    public long EventCount => 0;
    public DateTimeOffset? LastEventAt => null;
    public long ErrorCount => 0;
    public DateTimeOffset? LastErrorAt => null;
    public string? LastErrorKind => null;
    public string? LastErrorMessage => null;
    public bool IsRunning { get; private set; }

    public IReadOnlyList<string> WatchedPaths
    {
        get
        {
            lock (_gate)
            {
                return _watched.Select(w => w.Path).ToList();
            }
        }
    }

    /// <summary>Every start and stop in order, as <c>add:path:recursive</c> or <c>remove:path</c>.</summary>
    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_gate)
            {
                return [.. _log];
            }
        }
    }

    public bool IsWatching(string path) => WatchedPaths.Any(w => SamePath(w, path));

    public bool? IsRecursive(string path)
    {
        lock (_gate)
        {
            var entry = _watched.FirstOrDefault(w => SamePath(w.Path, path));
            return entry.Path is null ? null : entry.Recursive;
        }
    }

    public int CountEntries(string path, string kind)
        => Log.Count(entry => entry.StartsWith(kind + ":" + path, StringComparison.OrdinalIgnoreCase));

    public void AddDirectory(string path, bool includeSubdirectories = true)
    {
        lock (_gate)
        {
            _watched.Add((path, includeSubdirectories));
            _log.Add($"add:{path}:{includeSubdirectories}");
        }
    }

    public bool RemoveDirectory(string path)
    {
        lock (_gate)
        {
            var removed = _watched.RemoveAll(w => SamePath(w.Path, path)) > 0;
            if (removed)
            {
                _log.Add($"remove:{path}");
            }

            return removed;
        }
    }

    public void Start() => IsRunning = true;
    public void Stop() => IsRunning = false;
    public void UpdateDirectory(string path, bool includeSubdirectories = true) { }
    public void Dispose() { }

    internal void RaiseForTest(FileEvent evt) => FileDetected?.Invoke(this, evt);
    internal void RaiseErrorForTest(FileWatcherErrorEvent evt) => WatcherError?.Invoke(this, evt);

    private static bool SamePath(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
