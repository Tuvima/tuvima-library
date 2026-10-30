using System.Collections.Concurrent;
using System.Threading.Channels;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Api.Services.LocalAssets;

public sealed class FileSystemViewSourceWatcherFactory : IViewSourceWatcherFactory
{
    public IViewSourceWatcher Create(ViewSourceWatch source) => new FileSystemViewSourceWatcher(source);
}

/// <summary>
/// Owns a watcher set dedicated to personal View sources. It never reuses the
/// catalogue ingestion watcher, so personal files cannot enter retail,
/// canonical, provider, or Review Queue workflows.
/// </summary>
public sealed class ViewSourceIndexingHostedService(
    ViewStorageService storage,
    IViewPathIndexer indexer,
    IViewSourceWatcherFactory watcherFactory,
    ViewSourceIndexingOptions options,
    ILogger<ViewSourceIndexingHostedService> logger,
    IViewMissingFileSweeper? sweeper = null) : BackgroundService
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly object _lifecycleLock = new();
    private readonly List<IViewSourceWatcher> _watchers = [];
    private DebounceQueue? _queue;
    private readonly Channel<(Guid LibraryId, bool Scan)> _scanRequests =
        Channel.CreateUnbounded<(Guid LibraryId, bool Scan)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, byte> _pendingScans = new();
    private readonly ConcurrentDictionary<Guid, byte> _pendingSweeps = new();
    private readonly CancellationTokenSource _shutdown = new();

    public void RequestReconcile(Guid libraryId)
    {
        if (_pendingScans.TryAdd(libraryId, 0))
        {
            _scanRequests.Writer.TryWrite((libraryId, true));
        }
    }

    /// <summary>
    /// A file was deleted or renamed in this library: after a short wait (so a move whose new place is still being indexed, or a short
    /// hiccup on a network share, is never mistaken for a deletion) the folders are looked at again and gone files are marked missing.
    /// </summary>
    private void ScheduleSweep(Guid libraryId)
    {
        if (sweeper is null || !_pendingSweeps.TryAdd(libraryId, 0))
        {
            return;
        }

        var token = _shutdown.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(options.MissingRecheckDelay, token).ConfigureAwait(false);
                _pendingSweeps.TryRemove(libraryId, out _);
                _scanRequests.Writer.TryWrite((libraryId, false));
            }
            catch (OperationCanceledException)
            {
                // Engine shutdown; the next start's sweep is the backstop.
            }
        }, CancellationToken.None);
    }
    private IReadOnlyList<ViewSourceWatch> _sources = [];
    private bool _resourcesDisposed;

    public static IReadOnlyList<ViewSourceWatch> SelectConfiguredSources(
        IReadOnlyList<(MediaEngine.Domain.PersonalMedia.ViewPersonalSpace Space,
            MediaEngine.Domain.PersonalMedia.ViewSource Source, string Path)> sources)
        => SelectConfiguredSources(sources.Select(entry =>
            (entry.Space.LibraryId, entry.Path, entry.Source.IncludeSubdirectories)).ToList());

    public static IReadOnlyList<ViewSourceWatch> SelectConfiguredSources(
        IReadOnlyList<(Guid LibraryId, string Path, bool IncludeSubdirectories)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var result = new List<ViewSourceWatch>();
        foreach (var entry in sources)
        {
            try
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Path));
                if (!string.IsNullOrWhiteSpace(root))
                {
                    result.Add(new ViewSourceWatch(entry.LibraryId, root, entry.IncludeSubdirectories));
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Persisted source validation reports malformed paths. The watcher fails closed.
            }
        }

        return result
            .DistinctBy(source => (source.LibraryId, NormalizePath(source.RootPath), source.IncludeSubdirectories))
            .OrderByDescending(source => source.RootPath.Length)
            .ThenBy(source => source.RootPath, PathComparer)
            .ToList();
    }

    public static bool TryResolveLibrary(
        IReadOnlyList<ViewSourceWatch> sources,
        string path,
        out Guid libraryId)
    {
        libraryId = Guid.Empty;
        if (sources is null || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }

        // A more-specific configured root owns its subtree boundary even when
        // it is non-recursive. Never fall back to a broader source and cross a
        // Personal Space boundary merely because the nested source excludes a child.
        var enclosing = sources.Where(source => IsWithinRoot(fullPath, source.RootPath)).ToList();
        if (enclosing.Count == 0)
        {
            return false;
        }

        var longestRoot = enclosing.Max(source => source.RootPath.Length);
        var matches = enclosing.Where(source => source.RootPath.Length == longestRoot
            && IsWithinSource(fullPath, source)).ToList();
        if (matches.Count == 0)
        {
            return false;
        }

        var owners = matches
            .Select(source => source.LibraryId).Distinct().ToList();
        if (owners.Count != 1)
        {
            return false;
        }

        libraryId = owners[0];
        return true;
    }

    public async Task RefreshSourcesAsync(CancellationToken ct = default)
    {
        var next = SelectConfiguredSources(await storage.GetEnabledSourcePathsAsync(ct));
        List<IViewSourceWatcher> previous;
        lock (_lifecycleLock)
        {
            if (_resourcesDisposed)
            {
                return;
            }

            previous = [.. _watchers];
            _watchers.Clear();
            _sources = next;
        }
        foreach (var watcher in previous)
        {
            watcher.FileChanged -= OnFileChanged;
            watcher.WatcherError -= OnWatcherError;
            try { watcher.Stop(); }
            catch (Exception exception) { logger.LogDebug(exception, "View source watcher stop failed during refresh."); }
            try { watcher.Dispose(); }
            catch (Exception exception) { logger.LogDebug(exception, "View source watcher disposal failed during refresh."); }
        }
        StartWatchers();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _sources = SelectConfiguredSources(await storage.GetEnabledSourcePathsAsync(stoppingToken));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "View source indexing could not load libraries configuration.");
            return;
        }

        _queue = new DebounceQueue(new DebounceOptions
        {
            SettleDelay = options.SettleDelay,
            ProbeInterval = options.ProbeInterval,
            MaxProbeDelay = options.MaxProbeDelay,
            MaxProbeAttempts = options.MaxProbeAttempts,
            QueueCapacity = options.QueueCapacity,
        });

        StartWatchers();

        // Directory enumeration can be expensive. Keep it off the hosted-service
        // startup path so Kestrel does not wait for an existing photo archive.
        foreach (var libraryId in _sources.Select(source => source.LibraryId).Distinct())
        {
            RequestReconcile(libraryId);
        }

        var reconciliation = Task.Run(
            () => ReconcileExistingFilesAsync(stoppingToken),
            CancellationToken.None);

        try
        {
            await foreach (var candidate in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (candidate.IsFailed)
                {
                    logger.LogWarning("View source file did not settle for indexing: {Path}. {Reason}",
                        candidate.Path, candidate.FailureReason);
                    continue;
                }

                if (!TryResolveLibrary(_sources, candidate.Path, out var libraryId))
                {
                    logger.LogWarning("Ignored View source event outside configured personal roots: {Path}",
                        candidate.Path);
                    continue;
                }

                await DispatchAsync(libraryId, candidate.Path, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        finally
        {
            DisposeResources();
            try { await reconciliation.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private void StartWatchers()
    {
        foreach (var source in _sources)
        {
            if (!Directory.Exists(source.RootPath))
            {
                logger.LogInformation("View source is not currently available and will be reconciled on the next Engine start: {Root}",
                    source.RootPath);
                continue;
            }

            try
            {
                var watcher = watcherFactory.Create(source);
                watcher.FileChanged += OnFileChanged;
                watcher.WatcherError += OnWatcherError;
                lock (_lifecycleLock)
                {
                    _watchers.Add(watcher);
                }

                watcher.Start();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogWarning(exception, "Could not watch personal View source {Root}", source.RootPath);
            }
        }
    }

    private async Task ReconcileExistingFilesAsync(CancellationToken ct)
    {
        await foreach (var (libraryId, scan) in _scanRequests.Reader.ReadAllAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (scan)
            {
                _pendingScans.TryRemove(libraryId, out _);
                try
                {
                    var result = await indexer.ScanAsync(libraryId, ct).ConfigureAwait(false);
                    if (result is not null)
                    {
                        logger.LogInformation(
                            "View background reconciliation completed for {LibraryId}: {FilesSeen} files, {ItemsAdded} new items, {Errors} errors.",
                            libraryId, result.FilesSeen, result.ItemsAdded, result.Errors);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "View background reconciliation failed for {LibraryId}", libraryId);
                }
            }
            else
            {
                _pendingSweeps.TryRemove(libraryId, out _);
            }

            // The scan has just indexed every file that is there (a moved file's new place included), so what is still gone is gone.
            await SweepMissingAsync(libraryId, ct).ConfigureAwait(false);
        }
    }

    private async Task SweepMissingAsync(Guid libraryId, CancellationToken ct)
    {
        if (sweeper is null)
        {
            return;
        }

        try
        {
            await sweeper.SweepAsync(libraryId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Checking View library {LibraryId} for missing files failed.", libraryId);
        }
    }

    private async Task DispatchAsync(Guid libraryId, string path, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= Math.Max(1, options.DispatchAttempts); attempt++)
        {
            try
            {
                await indexer.IndexPathAsync(libraryId, path, ct).ConfigureAwait(false);
                return;
            }
            catch (FileNotFoundException)
            {
                return; // A later rename/delete superseded this event.
            }
            catch (InvalidDataException exception)
            {
                logger.LogDebug(exception, "Ignored unsupported View source file {Path}", path);
                return;
            }
            catch (Exception exception) when (
                (exception is IOException or UnauthorizedAccessException) && attempt < Math.Max(1, options.DispatchAttempts))
            {
                var delay = TimeSpan.FromMilliseconds(options.DispatchRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not index settled View source file {Path}", path);
                return;
            }
            catch (InvalidOperationException exception)
            {
                // ViewLibraryService rechecks the configured source boundary.
                logger.LogWarning(exception, "Rejected unsafe View source file event {Path}", path);
                return;
            }
        }
    }

    private void OnFileChanged(object? sender, ViewSourceFileEvent change)
    {
        var queue = _queue;
        if (queue is null
            || ViewStorageService.IsInsideStagingFolder(change.Path)
            || !TryResolveLibrary(_sources, change.Path, out var changedLibrary))
        {
            return;
        }

        if (change.Kind == ViewSourceFileEventKind.Deleted)
        {
            ScheduleSweep(changedLibrary);
            return;
        }

        if (change.Kind == ViewSourceFileEventKind.Renamed && change.OldPath is not null)
        {
            // The old place is gone; it is marked once the new place has been indexed, so the item stays.
            ScheduleSweep(changedLibrary);
        }

        var type = change.Kind switch
        {
            ViewSourceFileEventKind.Created => FileEventType.Created,
            ViewSourceFileEventKind.Renamed => FileEventType.Renamed,
            _ => FileEventType.Modified,
        };
        try
        {
            queue.Enqueue(new FileEvent
            {
                Path = change.Path,
                OldPath = change.OldPath,
                EventType = type,
                OccurredAt = DateTimeOffset.UtcNow,
            });
        }
        catch (ObjectDisposedException)
        {
            // A final OS callback raced service shutdown.
        }
    }

    private void OnWatcherError(object? sender, Exception exception) =>
        logger.LogWarning(exception, "A personal View source watcher reported an error; the next startup reconciliation remains authoritative.");

    private void DisposeResources()
    {
        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        List<IViewSourceWatcher> watchers;
        DebounceQueue? queue;
        lock (_lifecycleLock)
        {
            if (_resourcesDisposed)
            {
                return;
            }

            _resourcesDisposed = true;
            watchers = [.. _watchers];
            _watchers.Clear();
            queue = _queue;
            _queue = null;
        }

        foreach (var watcher in watchers)
        {
            watcher.FileChanged -= OnFileChanged;
            watcher.WatcherError -= OnWatcherError;
            try { watcher.Stop(); }
            catch (Exception exception) { logger.LogDebug(exception, "View source watcher stop failed during shutdown."); }
            try { watcher.Dispose(); }
            catch (Exception exception) { logger.LogDebug(exception, "View source watcher disposal failed during shutdown."); }
        }
        queue?.Dispose();
    }

    public override void Dispose()
    {
        DisposeResources();
        base.Dispose();
    }

    private static bool IsWithinSource(string fullPath, ViewSourceWatch source)
    {
        if (!IsWithinRoot(fullPath, source.RootPath))
        {
            return false;
        }

        return source.IncludeSubdirectories
            || string.Equals(Path.GetDirectoryName(fullPath), source.RootPath, PathComparison);
    }

    private static bool IsWithinRoot(string fullPath, string rootPath)
    {
        var relative = Path.GetRelativePath(rootPath, fullPath);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison)
            || Path.IsPathRooted(relative))
        {
            return false;
        }

        return true;
    }

    private static string NormalizePath(string path) =>
        OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

internal sealed class FileSystemViewSourceWatcher : IViewSourceWatcher
{
    private readonly FileSystemWatcher _watcher;
    private bool _disposed;

    public FileSystemViewSourceWatcher(ViewSourceWatch source)
    {
        _watcher = new FileSystemWatcher(source.RootPath)
        {
            IncludeSubdirectories = source.IncludeSubdirectories,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 65_536,
            EnableRaisingEvents = false,
        };
        _watcher.Created += OnCreated;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Deleted += OnDeleted;
        _watcher.Error += OnError;
    }

    public event EventHandler<ViewSourceFileEvent>? FileChanged;
    public event EventHandler<Exception>? WatcherError;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        if (!_disposed)
        {
            _watcher.EnableRaisingEvents = false;
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs args) =>
        Raise(args.FullPath, ViewSourceFileEventKind.Created);

    private void OnChanged(object sender, FileSystemEventArgs args) =>
        Raise(args.FullPath, ViewSourceFileEventKind.Changed);

    private void OnRenamed(object sender, RenamedEventArgs args) =>
        Raise(args.FullPath, ViewSourceFileEventKind.Renamed, args.OldFullPath);

    private void OnDeleted(object sender, FileSystemEventArgs args) =>
        Raise(args.FullPath, ViewSourceFileEventKind.Deleted);

    private void Raise(string path, ViewSourceFileEventKind kind, string? oldPath = null)
    {
        // Files Empty Trash is removing sit in a hidden staging folder; they are never indexed.
        if (_disposed || ViewStorageService.IsInsideStagingFolder(path) || Directory.Exists(path))
        {
            return;
        }

        FileChanged?.Invoke(this, new ViewSourceFileEvent(path, kind, oldPath));
    }

    private void OnError(object sender, ErrorEventArgs args) =>
        WatcherError?.Invoke(this, args.GetException());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnCreated;
        _watcher.Changed -= OnChanged;
        _watcher.Renamed -= OnRenamed;
        _watcher.Deleted -= OnDeleted;
        _watcher.Error -= OnError;
        _watcher.Dispose();
    }
}
