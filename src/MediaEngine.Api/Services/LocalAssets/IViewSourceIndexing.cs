namespace MediaEngine.Api.Services.LocalAssets;

public interface IViewPathIndexer
{
    Task<MediaEngine.Contracts.LocalAssets.LocalAssetScanResultDto?> ScanAsync(
        Guid libraryId,
        CancellationToken ct = default);

    Task<MediaEngine.Storage.Contracts.LocalAssetUpsertResult?> IndexPathAsync(
        Guid libraryId,
        string path,
        CancellationToken ct = default);
}

public sealed class ViewSourceIndexingOptions
{
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxProbeDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxProbeAttempts { get; init; } = 8;
    public int QueueCapacity { get; init; } = 512;
    public int DispatchAttempts { get; init; } = 3;
    public TimeSpan DispatchRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long after a file was deleted or renamed the folder is looked at again before anything is marked missing, so a move or a short hiccup is never mistaken for a deletion.</summary>
    public TimeSpan MissingRecheckDelay { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>Marks files that disappeared outside Library (Safer deleting in View). Implemented by <see cref="ViewMissingFileService"/>.</summary>
public interface IViewMissingFileSweeper
{
    /// <summary>Checks the folders of one library (or all libraries when null). Returns how many items changed state.</summary>
    Task<int> SweepAsync(Guid? libraryId, CancellationToken ct = default);
}

public sealed record ViewSourceWatch(
    Guid LibraryId,
    string RootPath,
    bool IncludeSubdirectories);

public enum ViewSourceFileEventKind
{
    Created,
    Changed,
    Renamed,
    Deleted,
}

public sealed record ViewSourceFileEvent(
    string Path,
    ViewSourceFileEventKind Kind,
    string? OldPath = null);

public interface IViewSourceWatcher : IDisposable
{
    event EventHandler<ViewSourceFileEvent>? FileChanged;
    event EventHandler<Exception>? WatcherError;
    void Start();
    void Stop();
}

public interface IViewSourceWatcherFactory
{
    IViewSourceWatcher Create(ViewSourceWatch source);
}
