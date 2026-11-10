using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Records activity runs (spec library-activity WP1, D21). Files that arrive one by one join the library's open run until
/// <see cref="QuietWindow"/> passes with nothing new. A library's first run is flagged (D14) so its initial load never marks anything New.
/// A Vault library records nothing, so no title or cover leaks outside the Vault's own activity list (D19). Photos and documents are
/// never Works in the Work/Edition graph, so they never reach this recorder (D17).
/// </summary>
public sealed class ActivityRunRecorder(
    IActivityRunStore store,
    IVaultLibraryRepository vaultLibraries,
    TimeProvider time,
    ILogger<ActivityRunRecorder> logger,
    IIngestionQueueProgressService? queue = null) : IActivityRunRecorder
{
    /// <summary>How long a run stays open with nothing new before it completes.</summary>
    public static readonly TimeSpan QuietWindow = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);

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
            return await store.CompleteQuietRunsAsync(time.GetUtcNow() - quiet, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
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
            var run = await store.FindOpenRunAsync(library, kind, now - QuietWindow, ct).ConfigureAwait(false);
            if (run is null)
            {
                // Runs opened while another library's run of the same kind is still going belong to the same scan: one History entry.
                var scanId = await store.FindOpenScanIdAsync(kind, now - QuietWindow, ct).ConfigureAwait(false) ?? Guid.NewGuid();
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
        return queue?.Current is { State: "working", StartedAt: { } periodStart } && periodStart <= firstStarted;
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
