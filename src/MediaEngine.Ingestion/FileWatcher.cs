using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion;

/// <summary>
/// Wraps one or more <see cref="System.IO.FileSystemWatcher"/> instances and
/// normalises their events into <see cref="FileEvent"/> objects.
///
/// Design notes
/// ────────────
/// • <see cref="System.IO.FileSystemWatcher"/> fires its callbacks on the OS
///   thread pool.  Handlers subscribed to <see cref="FileDetected"/> MUST be
///   fast and non-blocking — heavy work belongs in the <see cref="DebounceQueue"/>.
///
/// • <c>InternalBufferSize</c> is set to 64 KB (default is 8 KB) to reduce
///   the risk of buffer-overflow errors under high-I/O conditions.
///   Even so, the <see cref="Error"/> handler logs overflow events.
///
/// • The watcher fires <see cref="System.IO.WatcherChangeTypes.Changed"/> events
///   multiple times during a single write operation.  These raw events flow into
///   the debounce queue which resets its settle timer on each one; only the final
///   settled event proceeds to processing.
///
/// Spec: Phase 7 – Storage Monitoring, IFileWatcher interface.
/// </summary>
public sealed class FileWatcher : IFileWatcher
{
    private readonly List<System.IO.FileSystemWatcher> _watchers = [];

    // Guards _watchers: folders can now be added and removed while OS events, status reads and recovery run.
    private readonly object _gate = new();
    private bool _running;
    private bool _disposed;

    // Diagnostic counters for the watcher-status endpoint.
    private long _eventCount;
    private long _errorCount;
    private DateTimeOffset? _lastEventAt;
    private DateTimeOffset? _lastErrorAt;
    private string? _lastErrorKind;
    private string? _lastErrorMessage;

    /// <summary>Total number of raw OS events received since startup.</summary>
    public long EventCount => Interlocked.Read(ref _eventCount);

    /// <summary>Timestamp of the last raw OS event received.</summary>
    public DateTimeOffset? LastEventAt => _lastEventAt;

    /// <summary>Total watcher errors received since startup.</summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    /// <summary>Timestamp of the last watcher error received.</summary>
    public DateTimeOffset? LastErrorAt => _lastErrorAt;

    /// <summary>Kind of the last watcher error received.</summary>
    public string? LastErrorKind => _lastErrorKind;

    /// <summary>Message from the last watcher error received.</summary>
    public string? LastErrorMessage => _lastErrorMessage;

    /// <summary>Whether the watcher is currently active.</summary>
    public bool IsRunning => _running && !_disposed;

    /// <summary>Paths currently being watched.</summary>
    public IReadOnlyList<string> WatchedPaths
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Select(w => w.Path).ToList();
            }
        }
    }

    // -------------------------------------------------------------------------
    // IFileWatcher
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public event EventHandler<FileEvent>? FileDetected;

    /// <inheritdoc/>
    public event EventHandler<FileWatcherErrorEvent>? WatcherError;

    /// <inheritdoc/>
    public void AddDirectory(string path, bool includeSubdirectories = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Directory.CreateDirectory(path);

        var watcher = new System.IO.FileSystemWatcher
        {
            Path = path,
            IncludeSubdirectories = includeSubdirectories,

            // Capture the changes that matter for ingestion.
            // Omitting CreationTime / Attributes reduces noise from metadata-only writes.
            NotifyFilter = System.IO.NotifyFilters.FileName
                         | System.IO.NotifyFilters.LastWrite
                         | System.IO.NotifyFilters.Size,

            // 64 KB reduces missed events on busy directories.
            // Spec: "MUST detect file system events in real-time."
            InternalBufferSize = 65_536,

            // Don't raise events until Start() is called.
            EnableRaisingEvents = false,
        };

        watcher.Created += OnCreated;
        watcher.Changed += OnChanged;
        watcher.Deleted += OnDeleted;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;

        lock (_gate)
        {
            if (_disposed)
            {
                DetachAndDispose(watcher);
                throw new ObjectDisposedException(nameof(FileWatcher));
            }

            _watchers.Add(watcher);

            // A folder added while the watcher is already running starts raising events straight away.
            if (_running)
            {
                watcher.EnableRaisingEvents = true;
            }
        }
    }

    /// <inheritdoc/>
    public bool RemoveDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var target = NormalizeForComparison(path);
        lock (_gate)
        {
            var index = _watchers.FindIndex(w =>
                string.Equals(NormalizeForComparison(w.Path), target, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            var watcher = _watchers[index];
            _watchers.RemoveAt(index);
            watcher.EnableRaisingEvents = false;
            DetachAndDispose(watcher);
            return true;
        }
    }

    /// <inheritdoc/>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            _running = true;
            foreach (var w in _watchers)
            {
                w.EnableRaisingEvents = true;
            }
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            foreach (var w in _watchers)
            {
                w.EnableRaisingEvents = false;
            }
        }
    }

    /// <inheritdoc/>
    public void UpdateDirectory(string path, bool includeSubdirectories = true)
        => UpdateDirectories([path], includeSubdirectories);

    /// <inheritdoc/>
    public void UpdateDirectories(IEnumerable<string> paths, bool includeSubdirectories = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            // Unsubscribe and fully release every FileSystemWatcher so no new events arrive while we swap.
            foreach (var w in _watchers)
            {
                w.EnableRaisingEvents = false;
                DetachAndDispose(w);
            }
            _watchers.Clear();

            foreach (var path in paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // AddDirectory() subscribes the same handlers, appends the new watcher to _watchers
                // and, because _running is honoured there, resumes it immediately if we were running.
                AddDirectory(path, includeSubdirectories);
            }
        }
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;

            foreach (var w in _watchers)
            {
                w.EnableRaisingEvents = false;
                DetachAndDispose(w);
            }

            _watchers.Clear();
        }
    }

    private void DetachAndDispose(System.IO.FileSystemWatcher watcher)
    {
        watcher.Created -= OnCreated;
        watcher.Changed -= OnChanged;
        watcher.Deleted -= OnDeleted;
        watcher.Renamed -= OnRenamed;
        watcher.Error -= OnError;
        watcher.Dispose();
    }

    private static string NormalizeForComparison(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // -------------------------------------------------------------------------
    // OS event handlers (called on the OS thread pool)
    // -------------------------------------------------------------------------

    private void OnCreated(object _, System.IO.FileSystemEventArgs e)
    {
        // Directories are not media files — skip immediately.
        if (Directory.Exists(e.FullPath))
        {
            return;
        }

        RecordOsEvent();
        Raise(new FileEvent
        {
            Path = e.FullPath,
            EventType = FileEventType.Created,
            OccurredAt = DateTimeOffset.UtcNow,
        });
    }

    private void OnChanged(object _, System.IO.FileSystemEventArgs e)
    {
        // Directory writes (e.g. file added under a folder) fire Changed on the
        // folder itself — we only ingest files, so skip them here. Without this
        // guard the lock probe later opens the directory path as a file, fails
        // 8 retries, and quarantines the folder.
        if (Directory.Exists(e.FullPath))
        {
            return;
        }

        RecordOsEvent();
        Raise(new FileEvent
        {
            Path = e.FullPath,
            EventType = FileEventType.Modified,
            OccurredAt = DateTimeOffset.UtcNow,
        });
    }

    private void OnDeleted(object _, System.IO.FileSystemEventArgs e)
    {
        RecordOsEvent();
        Raise(new FileEvent
        {
            Path = e.FullPath,
            EventType = FileEventType.Deleted,
            OccurredAt = DateTimeOffset.UtcNow,
        });
    }

    private void OnRenamed(object _, System.IO.RenamedEventArgs e)
    {
        // Directory renames are not ingestion events. Skip them so the lock
        // probe doesn't try to open the directory as a file.
        if (Directory.Exists(e.FullPath))
        {
            return;
        }

        RecordOsEvent();
        Raise(new FileEvent
        {
            Path = e.FullPath,
            OldPath = e.OldFullPath,
            EventType = FileEventType.Renamed,
            OccurredAt = DateTimeOffset.UtcNow,
        });
    }

    private void OnError(object _, System.IO.ErrorEventArgs e)
    {
        var ex = e.GetException();
        var isOverflow = ex is System.IO.InternalBufferOverflowException;
        var kind = isOverflow ? "buffer_overflow" : ex.GetType().Name;
        var message = string.IsNullOrWhiteSpace(ex.Message)
            ? "File watcher reported an error."
            : ex.Message;

        Interlocked.Increment(ref _errorCount);
        _lastErrorAt = DateTimeOffset.UtcNow;
        _lastErrorKind = kind;
        _lastErrorMessage = message;

        WatcherError?.Invoke(this, new FileWatcherErrorEvent(
            kind,
            message,
            _lastErrorAt.Value,
            isOverflow));
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private void RecordOsEvent()
    {
        Interlocked.Increment(ref _eventCount);
        _lastEventAt = DateTimeOffset.UtcNow;
    }

    private void Raise(FileEvent evt)
    {
        // Guard: don't raise after Stop() or Dispose().
        if (!_running || _disposed)
        {
            return;
        }

        // Ignore internal probe files written by FolderHealthService to verify
        // write access.  These are zero-byte temp files that are created and
        // deleted in the same synchronous call, so they should never be ingested.
        var fileName = Path.GetFileName(evt.Path.AsSpan());
        if (MemoryExtensions.StartsWith(fileName, ProbeFilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        FileDetected?.Invoke(this, evt);
    }

    /// <summary>
    /// Prefix shared with <see cref="MediaEngine.Api.Services.FolderHealthService"/>.
    /// Must stay in sync with the probe file naming used there.
    /// </summary>
    private const string ProbeFilePrefix = ".tuvima_probe_";
}
