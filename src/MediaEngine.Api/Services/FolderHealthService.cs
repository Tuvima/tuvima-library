using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Services;

/// <summary>
/// Background service that periodically checks accessibility of the Watch Folder
/// and Library Root, broadcasting <c>FolderHealthChanged</c> SignalR events only
/// when the status changes — so the Dashboard <c>LibrariesTab</c> can update its
/// green/red status dots in real-time.
///
/// Check interval: every 30 seconds (configurable via <c>MediaEngine:FolderHealthIntervalSeconds</c>).
/// </summary>
public sealed class FolderHealthService : BackgroundService
{
    private readonly IOptionsMonitor<IngestionOptions> _options;
    private readonly IEventPublisher _publisher;
    private readonly ILogger<FolderHealthService> _logger;
    private readonly int _intervalSeconds;

    /// <summary>
    /// In-memory cache of the last-known health state per folder path.
    /// Only paths that change status trigger a SignalR broadcast.
    /// </summary>
    private readonly Dictionary<string, FolderState> _lastState = new(StringComparer.OrdinalIgnoreCase);

    public FolderHealthService(
        IOptionsMonitor<IngestionOptions> options,
        IEventPublisher publisher,
        IConfiguration config,
        ILogger<FolderHealthService> logger)
    {
        _options = options;
        _publisher = publisher;
        _logger = logger;
        _intervalSeconds = config.GetValue("MediaEngine:FolderHealthIntervalSeconds", 30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "FolderHealthService started — checking every {Interval}s", _intervalSeconds);

        // Small initial delay to let the rest of the app start up.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckFoldersAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "FolderHealthService check cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), stoppingToken);
        }
    }

    private async Task CheckFoldersAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;

        foreach (var watchDirectory in opts.EffectiveWatchDirectories)
        {
            var mayWrite = opts.LibraryFolders.SelectMany(folder => folder.Sources).Any(source =>
                source.AllowsFileMutation && string.Equals(Path.GetFullPath(source.Path), Path.GetFullPath(watchDirectory), StringComparison.OrdinalIgnoreCase));
            await CheckAndBroadcastAsync(watchDirectory, mayWrite, ct);
        }

        if (!string.IsNullOrWhiteSpace(opts.LibraryRoot))
        {
            await CheckAndBroadcastAsync(opts.LibraryRoot, false, ct);
        }
    }

    private async Task CheckAndBroadcastAsync(string path, bool allowWriteProbe, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var current = ProbePath(path, allowWriteProbe);

        // Only broadcast if state has actually changed (or first run).
        if (_lastState.TryGetValue(path, out var previous) && previous == current)
        {
            return;
        }

        _lastState[path] = current;

        _logger.LogDebug(
            "FolderHealthChanged: {Path} → Accessible={Accessible} Read={Read} Write={Write}",
            path, current.IsAccessible, current.HasRead, current.HasWrite);

        await _publisher.PublishAsync(
            SignalREvents.FolderHealthChanged,
            new FolderHealthChangedEvent(
                path,
                current.IsAccessible,
                current.HasRead,
                current.HasWrite,
                DateTimeOffset.UtcNow),
            ct);
    }

    /// <summary>
    /// Probes a directory path for existence, read access, and write access.
    /// Matches the same logic used by <c>POST /settings/test-path</c>.
    /// </summary>
    public static FolderState ProbePath(string path, bool allowWriteProbe = false)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return new FolderState(false, false, false);
            }

            // Read probe: can we enumerate the directory?
            bool hasRead;
            try
            {
                _ = Directory.EnumerateFileSystemEntries(path).FirstOrDefault();
                hasRead = true;
            }
            catch
            {
                hasRead = false;
            }

            // Existing-library and unknown paths are never tested by creating files.
            if (!allowWriteProbe)
            {
                return new FolderState(true, hasRead, false);
            }

            // Write probes are limited to explicitly writable managed sources.
            bool hasWrite;
            try
            {
                var testFile = Path.Combine(path, $".tuvima_probe_{Guid.NewGuid():N}");
                File.WriteAllBytes(testFile, []);
                File.Delete(testFile);
                hasWrite = true;
            }
            catch
            {
                hasWrite = false;
            }

            return new FolderState(true, hasRead, hasWrite);
        }
        catch
        {
            return new FolderState(false, false, false);
        }
    }

    public readonly record struct FolderState(bool IsAccessible, bool HasRead, bool HasWrite);
}
