using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Records activity runs (spec library-activity WP1, D21). Files that arrive one by one join the library's open run until
/// <see cref="QuietWindow"/> passes with nothing new. A library's first run is flagged (D14) so its initial load never marks anything New.
/// A Vault library records nothing, so no title or cover leaks outside the Vault's own activity list (D19). Photos and documents are
/// never Works in the Work/Edition graph, so they never reach this recorder (D17).
/// One import is one batch (D30): while the Engine is working through files a run never closes, the quiet window only counts from when the
/// Engine went idle, and a run cut off by the Engine stopping is reopened if the same work is picked up again after the restart.
/// </summary>
public sealed class ActivityRunRecorder(
    IActivityRunStore store,
    IVaultLibraryRepository vaultLibraries,
    TimeProvider time,
    ILogger<ActivityRunRecorder> logger,
    IServiceProvider? services = null) : IActivityRunRecorder
{
    /// <summary>How long a run stays open with nothing new before it completes.</summary>
    public static readonly TimeSpan QuietWindow = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);

    // When the Engine was last seen busy (or, once it has gone idle, the first look that saw it idle, so the quiet minutes are never short).
    // The start of a session counts as busy: work picked up again after a restart gets that long to arrive before the batch counts as over.
    private DateTimeOffset _lastBusyAt = time.GetUtcNow();
    private bool _wasBusy;

    // Set at start-up while runs the last session cut off may still be reopened; cleared once they are reopened or the Engine stays idle.
    private bool _interruptedPending;

    public async Task RecordAddedAsync(
        Guid workId, Guid? assetId, string mediaType, string title, string? groupTitle, Guid? groupWorkId, CancellationToken ct = default)
    {
        try
        {
            if (assetId is not { } asset || await store.GetAssetContextAsync(asset, ct).ConfigureAwait(false) is not { } context)
            {
                return;
            }

            await AddAsync(ActivityRunKinds.Added, context.LibraryId, workId, asset, mediaType, title, groupTitle, groupWorkId, context.FilePath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The activity run for added work {WorkId} could not be recorded; ingestion carries on.", workId);
        }
    }

    public async Task RecordRemovedAsync(Guid assetId, Guid? libraryId, string filePath, CancellationToken ct = default)
    {
        try
        {
            var library = libraryId?.ToString("D");
            if (library is null && await store.GetAssetContextAsync(assetId, ct).ConfigureAwait(false) is { } context)
            {
                library = context.LibraryId;
            }

            var name = Path.GetFileNameWithoutExtension(filePath ?? string.Empty);
            await AddAsync(
                ActivityRunKinds.Removed, library, workId: null, assetId, mediaType: string.Empty,
                title: string.IsNullOrWhiteSpace(name) ? "Removed file" : name, groupTitle: null, groupWorkId: null, filePath, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The activity run for removed asset {AssetId} could not be recorded; the removal carries on.", assetId);
        }
    }

    public async Task RecordTouchedAsync(
        string kind, Guid libraryId, Guid workId, string mediaType, string title, string? groupTitle, Guid? groupWorkId, CancellationToken ct = default)
    {
        try
        {
            if (!ActivityRunKinds.IsKnown(kind))
            {
                return;
            }

            await AddAsync(kind, libraryId.ToString("D"), workId, assetId: null, mediaType, title, groupTitle, groupWorkId, filePath: null, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The activity run for {Kind} work {WorkId} could not be recorded.", kind, workId);
        }
    }

    public async Task<int> CloseQuietRunsAsync(TimeSpan quiet, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            if (IsHeldOpen(now, quiet))
            {
                return 0; // the Engine is still busy with the import, or only just went idle: the quiet minutes have not passed
            }

            var closed = await store.CompleteQuietRunsAsync(now - quiet, ct).ConfigureAwait(false);
            if (_interruptedPending)
            {
                // Idle for the whole quiet window after a restart: whatever arrives next is a new drop, not the cut-off import.
                await store.ClearInterruptedAsync(ct).ConfigureAwait(false);
                _interruptedPending = false;
            }

            return closed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CloseLeftOpenRunsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _interruptedPending = true;
            return await store.CompleteInterruptedRunsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Looks at the Engine's queue once and says whether runs must stay open: while it is working through files, and for the quiet window
    /// after the first look that saw it idle (D30). Only "working" counts; files that are merely waiting cannot hold a batch open (D27).
    /// </summary>
    private bool IsHeldOpen(DateTimeOffset now, TimeSpan quiet)
    {
        // Looked up now, not at construction: the queue service sits behind the event publisher, which itself needs this recorder.
        var busy = services?.GetService<IIngestionQueueProgressService>()?.Current is { State: "working" };
        if (busy || _wasBusy)
        {
            _lastBusyAt = now;
            _wasBusy = busy;
        }

        return busy || now - _lastBusyAt < quiet;
    }

    private async Task AddAsync(
        string kind, string? libraryId, Guid? workId, Guid? assetId, string mediaType, string title,
        string? groupTitle, Guid? groupWorkId, string? filePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(libraryId) || !Guid.TryParse(libraryId, out var parsed))
        {
            return; // a file outside every library has no run to join
        }

        var library = parsed.ToString("D");
        if ((await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false)).Contains(library, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var (groupKey, groupName) = Stack(mediaType, workId, assetId, title, groupTitle, filePath);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            var held = IsHeldOpen(now, QuietWindow);
            if (held && _interruptedPending)
            {
                // The same work is being picked up again after a restart: the batch the stop cut off is continued, not replaced.
                await store.ReopenInterruptedRunsAsync(kind, ct).ConfigureAwait(false);
                _interruptedPending = false;
            }

            // While the Engine is busy with the import any open run is still its batch, however long ago its last title finished.
            var notBefore = held ? DateTimeOffset.MinValue : now - QuietWindow;
            var run = await store.FindOpenRunAsync(library, kind, notBefore, ct).ConfigureAwait(false);
            if (run is null)
            {
                // Runs opened while another library's run of the same kind is still going belong to the same scan: one History entry.
                var scanId = await store.FindOpenScanIdAsync(kind, notBefore, ct).ConfigureAwait(false) ?? Guid.NewGuid();
                run = new ActivityRunRecord(
                    Guid.NewGuid(), library, kind, ActivityRunStatuses.Active,
                    IsFirstRun: kind == ActivityRunKinds.Added && await IsInitialImportAsync(library, ct).ConfigureAwait(false),
                    now, now, null, 0, scanId);
                await store.InsertRunAsync(run, ct).ConfigureAwait(false);
            }

            await store.AddItemAsync(
                new ActivityRunItemRecord(Guid.NewGuid(), run.Id, workId, assetId, mediaType, title, groupKey, groupName, groupWorkId, now),
                now, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A run is part of a library's first import (D14) when the library has no runs yet, or when its earlier runs are all first runs and the
    /// Engine has been busy without a break since the first one began: a big library arrives in several waves, and every wave of the initial
    /// load counts as first, so nothing is tagged New until the Library has actually gone quiet once.
    /// </summary>
    private async Task<bool> IsInitialImportAsync(string library, CancellationToken ct)
    {
        if (await store.GetInitialImportStartAsync(library, ct).ConfigureAwait(false) is not { } firstStarted)
        {
            return !await store.LibraryHasRunsAsync(library, ct).ConfigureAwait(false);
        }

        // The queue's active period runs from idle to idle; the import is still the initial one while that period began before the first run did.
        // Looked up now, not at construction: the queue service sits behind the event publisher, which itself needs this recorder.
        return services?.GetService<IIngestionQueueProgressService>()?.Current is { State: "working", StartedAt: { } periodStart } && periodStart <= firstStarted;
    }

    /// <summary>
    /// The stacking rule (D12): episodes into one tile per show, tracks into one per album, other videos into one per root folder;
    /// movies, books, audiobooks and comics are one tile each.
    /// </summary>
    internal static (string Key, string Title) Stack(string mediaType, Guid? workId, Guid? assetId, string title, string? groupTitle, string? filePath)
    {
        if (mediaType is "TV" or "Music" && !string.IsNullOrWhiteSpace(groupTitle))
        {
            return ($"{mediaType}|{groupTitle.Trim().ToLowerInvariant()}", groupTitle.Trim());
        }

        if (mediaType == "Videos" && !string.IsNullOrWhiteSpace(filePath) && Path.GetDirectoryName(filePath) is { Length: > 0 } folder)
        {
            var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return ($"Videos|{folder.ToLowerInvariant()}", string.IsNullOrWhiteSpace(name) ? title : name);
        }

        return ($"item|{workId ?? assetId ?? Guid.NewGuid()}", title);
    }
}
