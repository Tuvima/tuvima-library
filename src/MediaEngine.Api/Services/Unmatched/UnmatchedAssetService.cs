using MediaEngine.Api.Services.FileWrites;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Contracts;

namespace MediaEngine.Api.Services.Unmatched;

/// <summary>
/// The actions on files the Engine could not match (Library setup spec D34, D40): Retry now, Keep as is, Keep all, Look up again, and the
/// retry itself. A retry runs the normal identification again by queueing a new identity job; if it matches, the job leaving the no-match
/// state takes the file out of Unmatched and it flows on as usual (nothing is filed or written here). A file in a YouTube library with no
/// YouTube ID is re-read instead, since it has no lookup to repeat.
/// </summary>
public sealed class UnmatchedAssetService
{
    /// <summary>The most files one pass looks up again, so a long list is worked through over several passes rather than all at once.</summary>
    public const int BatchSize = 100;

    private readonly IUnmatchedAssetRepository _unmatched;
    private readonly IIdentityJobRepository _jobs;
    private readonly IMediaAssetRepository _assets;
    private readonly ICanonicalValueRepository _canonicals;
    private readonly IExternalLookupGuard _guard;
    private readonly IConfigurationLoader _configuration;
    private readonly IAssetRereader _rereader;
    private readonly ILogger<UnmatchedAssetService> _logger;
    private readonly IIdentityPipelineSignal? _signal;
    private readonly ILibraryKindSource? _kindSource;
    private readonly TimeProvider _clock;
    private readonly UnmatchedRetryWake? _wake;

    public UnmatchedAssetService(
        IUnmatchedAssetRepository unmatched,
        IIdentityJobRepository jobs,
        IMediaAssetRepository assets,
        ICanonicalValueRepository canonicals,
        IExternalLookupGuard guard,
        IConfigurationLoader configuration,
        IAssetRereader rereader,
        ILogger<UnmatchedAssetService> logger,
        IIdentityPipelineSignal? signal = null,
        ILibraryKindSource? kindSource = null,
        TimeProvider? clock = null,
        UnmatchedRetryWake? wake = null)
    {
        _unmatched = unmatched;
        _jobs = jobs;
        _assets = assets;
        _canonicals = canonicals;
        _guard = guard;
        _configuration = configuration;
        _rereader = rereader;
        _logger = logger;
        _signal = signal;
        _kindSource = kindSource;
        _clock = clock ?? TimeProvider.System;
        _wake = wake;
    }

    /// <summary>Retry now for one file: looks it up again straight away. False when the file is not Unmatched or was kept as it is (Look up again brings a kept file back).</summary>
    public async Task<bool> RetryNowAsync(Guid assetId, CancellationToken ct)
    {
        if (!await _unmatched.MakeDueAsync(assetId, _clock.GetUtcNow(), ct).ConfigureAwait(false))
        {
            return false;
        }

        var item = await _unmatched.GetAsync(assetId, ct).ConfigureAwait(false);
        return item is not null && await RetryAsync(item, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Retry now for a whole library: makes every file in it that is not kept due now and wakes the retry service, then returns at once with
    /// how many were queued. The lookups run in the background in small batches (a library can hold thousands of files, so none of them
    /// run inside the request).
    /// </summary>
    public async Task<int> RetryLibraryNowAsync(string libraryId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        var queued = await _unmatched.MakeAllDueAsync(libraryId, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        if (queued > 0)
        {
            _wake?.Signal();
        }

        return queued;
    }

    /// <summary>Keep as is: stops the file's retries and takes it out of the Unmatched count and filter.</summary>
    public Task<bool> KeepAsIsAsync(Guid assetId, CancellationToken ct) =>
        _unmatched.KeepAsIsAsync(assetId, _clock.GetUtcNow(), ct);

    /// <summary>Keep all Unmatched as they are, for one library. Returns how many files were kept.</summary>
    public Task<int> KeepAllAsIsAsync(string libraryId, CancellationToken ct) =>
        _unmatched.KeepAllAsIsAsync(libraryId, _clock.GetUtcNow(), ct);

    /// <summary>Look up again: brings a kept file back (clears the kept mark) and looks it up now. False when the file is not Unmatched.</summary>
    public async Task<bool> LookUpAgainAsync(Guid assetId, CancellationToken ct)
    {
        if (!await _unmatched.LookUpAgainAsync(assetId, _clock.GetUtcNow(), ct).ConfigureAwait(false))
        {
            return false;
        }

        var item = await _unmatched.GetAsync(assetId, ct).ConfigureAwait(false);
        return item is not null && await RetryAsync(item, ct).ConfigureAwait(false);
    }

    /// <summary>A provider or plugin was switched on or updated: every file that is not kept becomes due. Returns how many.</summary>
    public Task<int> MakeEveryItemDueAsync(CancellationToken ct) =>
        _unmatched.MakeAllDueAsync(null, _clock.GetUtcNow(), ct);

    /// <summary>
    /// Looks up again the files that are due (not kept), at most <see cref="BatchSize"/> of them, optionally only one library's. Returns how
    /// many were handled; a full batch means more may be due.
    /// </summary>
    public async Task<int> RetryDueAsync(CancellationToken ct, string? libraryId = null)
    {
        var due = await _unmatched.ListDueAsync(_clock.GetUtcNow(), BatchSize, libraryId, ct).ConfigureAwait(false);
        foreach (var item in due)
        {
            ct.ThrowIfCancellationRequested();
            await RetryAsync(item, ct).ConfigureAwait(false);
        }

        return due.Count;
    }

    private async Task<bool> RetryAsync(UnmatchedAsset item, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var next = UnmatchedRetryLimits.NextRetryAt(now, _configuration.LoadMaintenance().UnmatchedRetryDays);

        var asset = await _assets.FindByIdAsync(item.AssetId, ct).ConfigureAwait(false);
        if (asset is null)
        {
            // The file is gone; its row goes with it (the row also cascades when the asset row is deleted).
            await _unmatched.ClearAsync(item.AssetId, ct).ConfigureAwait(false);
            return false;
        }

        var libraryId = asset.LibraryId ?? item.LibraryId;
        var requeued = item.Reason == UnmatchedReasons.NoYoutubeId
            ? await RecheckYoutubeFileAsync(item, libraryId, ct).ConfigureAwait(false)
            : await RequeueIdentificationAsync(item, libraryId, ct).ConfigureAwait(false);

        // The row may have been cleared by the step above (the file matched, or its library stopped looking things up).
        await _unmatched.RecordTriedAsync(item.AssetId, now, next, ct).ConfigureAwait(false);
        return requeued;
    }

    // The normal identification again: a new identity job for the file, the way ingestion makes one. A library that has since become
    // plain (or whose settings cannot be read) never looks anything up, so a file there is no longer Unmatched.
    private async Task<bool> RequeueIdentificationAsync(UnmatchedAsset item, string libraryId, CancellationToken ct)
    {
        if (_guard.BlocksLibrary(libraryId))
        {
            _logger.LogInformation(
                "Unmatched file {AssetId}: library {LibraryId} no longer looks anything up, so the file is no longer Unmatched",
                item.AssetId,
                libraryId);
            await _unmatched.ClearAsync(item.AssetId, ct).ConfigureAwait(false);
            return false;
        }

        var latest = await _jobs.GetByEntityAsync(item.AssetId, ct).ConfigureAwait(false);
        var mediaType = latest?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            mediaType = (await _canonicals.GetByEntityAsync(item.AssetId, ct).ConfigureAwait(false))
                .FirstOrDefault(value => string.Equals(value.Key, MetadataFieldConstants.MediaTypeField, StringComparison.OrdinalIgnoreCase))?.Value;
        }

        if (string.IsNullOrWhiteSpace(mediaType))
        {
            _logger.LogWarning("Unmatched file {AssetId} has no known media type, so it could not be looked up again", item.AssetId);
            return false;
        }

        await _jobs.CreateAsync(new IdentityJob
        {
            EntityId = item.AssetId,
            EntityType = EntityType.MediaAsset.ToString(),
            MediaType = mediaType,
            Pass = "Quick",
        }, ct).ConfigureAwait(false);
        _signal?.Signal(IdentityPipelineSignalKind.Retail);
        _logger.LogInformation("Unmatched file {AssetId} queued to be looked up again", item.AssetId);
        return true;
    }

    // A YouTube library file with no ID has no lookup to repeat. Its tags and file are read again, which picks up a .info.json (or a new
    // file name) that now carries the ID, and the normal YouTube lookup then runs on that ID under the same gates ingestion uses (the YouTube
    // kind, and the Vault and adult-content gate). Once the lookup has succeeded the file is no longer Unmatched; an ID the lookup could not
    // confirm (YouTube unreachable, the video gone, a gate saying no) leaves it Unmatched for the next retry. The file is never moved or renamed.
    private async Task<bool> RecheckYoutubeFileAsync(UnmatchedAsset item, string libraryId, CancellationToken ct)
    {
        if (!IsYoutubeLibrary(libraryId))
        {
            await _unmatched.ClearAsync(item.AssetId, ct).ConfigureAwait(false);
            return false;
        }

        var reread = await _rereader.RereadAsync(item.AssetId, ct).ConfigureAwait(false);
        if (!reread.Refreshed)
        {
            _logger.LogInformation("Unmatched YouTube file {AssetId} could not be read again ({Status})", item.AssetId, reread.Status);
            return false;
        }

        var lookedUp = (await _canonicals.GetByEntityAsync(item.AssetId, ct).ConfigureAwait(false))
            .Any(value => string.Equals(value.Key, MetadataFieldConstants.VideoLookupSource, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(value.Value, "youtube", StringComparison.OrdinalIgnoreCase));
        if (lookedUp)
        {
            await _unmatched.ClearAsync(item.AssetId, ct).ConfigureAwait(false);
        }

        return true;
    }

    private bool IsYoutubeLibrary(string libraryId)
    {
        var library = _configuration.LoadLibraries().Libraries
            .FirstOrDefault(candidate => string.Equals(candidate.Id, libraryId, StringComparison.OrdinalIgnoreCase));
        var kinds = _kindSource?.GetKinds() ?? _configuration.LoadLibraryKinds().Kinds;
        return library is not null && LibraryKindLookupPolicy.For(library, kinds).Mode == LibraryLookupMode.YouTube;
    }
}
