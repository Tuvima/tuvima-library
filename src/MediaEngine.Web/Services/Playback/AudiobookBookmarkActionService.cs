using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Playback;

public sealed record AudiobookBookmarkActionContext(
    Guid DialogId,
    Guid ProfileId,
    Guid WorkId,
    Guid SessionLeaseId,
    long OwnerGeneration,
    Guid? ExpectedAssetId = null);

public sealed record AudiobookBookmarkActionSnapshot(
    IReadOnlyList<AudiobookBookmarkDto> Saved,
    CapturedAudiobookBookmarkDraft? Draft,
    bool IsLoading,
    bool IsSaving,
    Guid? DeleteConfirmationBookmarkId,
    string? Message,
    bool SaveOutcomeUnknown = false);

/// <summary>
/// The shared main-window bookmark owner. Popup hosts will later adapt this interface
/// through the serialized owner command channel and must not call the Engine directly.
/// </summary>
public interface IAudiobookBookmarkActions
{
    event Action<Guid>? Changed;

    Task OpenAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default);

    Task<AudiobookBookmarkActionSnapshot> GetSnapshotAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default);

    Task<AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>> LoadSavedAsync(
        AudiobookBookmarkActionContext context,
        IReadOnlySet<Guid> authorizedAssetIds,
        CancellationToken ct = default);

    Task<AudiobookBookmarkOperationResult<AudiobookBookmarkDto>> SaveAsync(
        AudiobookBookmarkActionContext context,
        long draftGeneration,
        IReadOnlySet<Guid> authorizedAssetIds,
        string? note,
        CancellationToken ct = default);

    Task<AudiobookBookmarkOperationResult<bool>> PreviewCapturedDraftAsync(
        AudiobookBookmarkActionContext context,
        long draftGeneration,
        CancellationToken ct = default);

    Task<AudiobookBookmarkReplayResult> ReplayAsync(
        AudiobookBookmarkActionContext context,
        Guid bookmarkId,
        IReadOnlySet<Guid> authorizedAssetIds,
        CancellationToken ct = default);

    Task<bool> RequestDeleteAsync(AudiobookBookmarkActionContext context, Guid bookmarkId,
        IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default);

    Task<AudiobookBookmarkOperationResult<bool>> ConfirmDeleteAsync(
        AudiobookBookmarkActionContext context,
        IReadOnlySet<Guid> authorizedAssetIds,
        CancellationToken ct = default);

    Task CancelDeleteAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default);

    Task CloseAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default);
}

/// <summary>Owner-only current-position capture; popup dialogs never receive this capability.</summary>
public interface IAudiobookBookmarkCaptureOwner
{
    bool TryCapture(AudiobookBookmarkActionContext context, Guid assetId, IReadOnlySet<Guid> authorizedAssetIds,
        double positionSeconds, double? durationSeconds, PlaybackChapterDto? chapter,
        out AudiobookBookmarkDraftPayloadDto? capture);
}

/// <summary>Invalidates pending dialogs when their profile, book, session, or source access changes.</summary>
public interface IAudiobookBookmarkLeaseInvalidator
{
    void InvalidateDialog(AudiobookBookmarkActionContext context, string reason);

    void InvalidateForLiveContext(Guid? profileId, Guid? workId, Guid? sessionLeaseId,
        IReadOnlySet<Guid> authorizedAssetIds, bool authorityRevoked = false);
}

/// <summary>
/// Holds independent transient Add/Saved state for each open player dialog, with
/// context and owner-generation guards around every asynchronous completion.
/// </summary>
public sealed class AudiobookBookmarkActionService : IAudiobookBookmarkActions, IAudiobookBookmarkCaptureOwner, IAudiobookBookmarkLeaseInvalidator
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, DialogState> _dialogs = [];
    private readonly IEngineApiClient _api;
    private readonly Func<AudiobookBookmarkActionContext, CapturedAudiobookBookmarkDraft, CancellationToken,
        Task<AudiobookBookmarkOperationResult<bool>>>? _previewCapturedDraft;

    public AudiobookBookmarkActionService(IEngineApiClient api,
        Func<AudiobookBookmarkActionContext, CapturedAudiobookBookmarkDraft, CancellationToken,
            Task<AudiobookBookmarkOperationResult<bool>>>? previewCapturedDraft = null)
    {
        _api = api;
        _previewCapturedDraft = previewCapturedDraft;
    }

    public event Action<Guid>? Changed;

    public void Open(AudiobookBookmarkActionContext context)
    {
        ValidateContext(context);
        lock (_sync)
        {
            if (_dialogs.Remove(context.DialogId, out var prior))
            {
                prior.DraftLease.Invalidate();
            }

            _dialogs[context.DialogId] = new DialogState(context);
        }

        Changed?.Invoke(context.DialogId);
    }

    public Task OpenAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Open(context);
        return Task.CompletedTask;
    }

    public bool TryCapture(
        AudiobookBookmarkActionContext context,
        Guid assetId,
        IReadOnlySet<Guid> authorizedAssetIds,
        double positionSeconds,
        double? durationSeconds,
        PlaybackChapterDto? chapter,
        out AudiobookBookmarkDraftPayloadDto? capture)
    {
        capture = null;
        var captured = false;
        lock (_sync)
        {
            if (!TryGetCurrent(context, out var state))
            {
                return false;
            }

            if (!authorizedAssetIds.Contains(assetId))
            {
                state.DraftLease.Invalidate();
                state.Message = "The captured audio source is no longer authorized.";
            }
            else if (!state.DraftLease.TryCapture(context.ProfileId, context.WorkId, context.SessionLeaseId,
                         assetId, positionSeconds, durationSeconds, chapter, out var draft))
            {
                state.Message = "The listening position could not be captured for this source.";
            }
            else
            {
                state.Message = null;
                capture = ToPayload(draft!);
                captured = true;
            }
        }

        Changed?.Invoke(context.DialogId);
        return captured;
    }

    public AudiobookBookmarkActionSnapshot GetSnapshot(Guid dialogId)
    {
        lock (_sync)
        {
            return _dialogs.TryGetValue(dialogId, out var state)
                ? state.Snapshot()
                : new AudiobookBookmarkActionSnapshot([], null, false, false, null, null);
        }
    }

    public Task<AudiobookBookmarkActionSnapshot> GetSnapshotAsync(AudiobookBookmarkActionContext context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(TryGetCurrent(context, out var state)
                ? state.Snapshot()
                : new AudiobookBookmarkActionSnapshot([], null, false, false, null, "This bookmark dialog is no longer active."));
        }
    }

    public async Task<AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>> LoadSavedAsync(
        AudiobookBookmarkActionContext context,
        IReadOnlySet<Guid> authorizedAssetIds,
        CancellationToken ct = default)
    {
        DialogState state;
        long operationGeneration;
        long savedRevision;
        lock (_sync)
        {
            if (!TryGetCurrent(context, out state))
            {
                return AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>.Failed("This bookmark dialog is no longer active.");
            }

            state.IsLoading = true;
            state.Message = null;
            operationGeneration = ++state.LoadGeneration;
            savedRevision = state.SavedRevision;
        }

        Changed?.Invoke(context.DialogId);
        var result = await _api.GetAudiobookBookmarksWithOutcomeAsync(context.WorkId, context.ProfileId, ct).ConfigureAwait(false);
        lock (_sync)
        {
            if (!IsCurrentOperation(context, state!, operationGeneration, DialogOperation.Load))
            {
                return result;
            }

            state!.IsLoading = false;
            if (result.Outcome == AudiobookBookmarkOperationOutcome.Success && result.Value is not null)
            {
                if (state.SavedRevision != savedRevision)
                {
                    state.Message = "Bookmarks changed while the list was loading. Reload Saved to refresh.";
                }
                else
                {
                    state.Saved = FilterAuthorized(result.Value, context, authorizedAssetIds);
                    state.Message = null;
                    if (state.SaveOutcomeUnknownGeneration is long unknownGeneration
                        && state.DraftLease.Current is { } unknownDraft
                        && unknownDraft.Generation == unknownGeneration)
                    {
                        var matchingSavedBookmark = state.SaveOutcomeUnknownAfterUtc is DateTimeOffset attemptStartedAt
                            && state.Saved.Any(bookmark => bookmark.AssetId == unknownDraft.AssetId
                                && bookmark.PositionSeconds == unknownDraft.PositionSeconds
                                && bookmark.CreatedAt >= attemptStartedAt
                                && string.Equals(bookmark.Note, state.SaveOutcomeUnknownNote, StringComparison.Ordinal));
                        if (matchingSavedBookmark)
                        {
                            state.DraftLease.Invalidate();
                            state.Message = "The bookmark appears in Saved.";
                        }
                        else
                        {
                            state.Message = "No matching bookmark was found. You can choose whether to save again.";
                        }

                        state.SaveOutcomeUnknownGeneration = null;
                        state.SaveOutcomeUnknownNote = null;
                        state.SaveOutcomeUnknownAfterUtc = null;
                    }

                    state.DeleteConfirmationBookmarkId = state.Saved.Any(bookmark => bookmark.Id == state.DeleteConfirmationBookmarkId)
                        ? state.DeleteConfirmationBookmarkId
                        : null;
                }
            }
            else
            {
                state.Message = result.Message ?? "Saved bookmarks could not be loaded.";
            }
        }

        Changed?.Invoke(context.DialogId);
        return result;
    }

    public async Task<AudiobookBookmarkOperationResult<AudiobookBookmarkDto>> SaveAsync(
        AudiobookBookmarkActionContext context,
        long draftGeneration,
        IReadOnlySet<Guid> authorizedAssetIds,
        string? note,
        CancellationToken ct = default)
    {
        DialogState state;
        AudiobookBookmarkSaveAttempt? attempt;
        long operationGeneration;
        lock (_sync)
        {
            if (!TryGetCurrent(context, out state))
            {
                return AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Failed("This bookmark dialog is no longer active.");
            }

            if (state.SaveOutcomeUnknownGeneration == draftGeneration)
            {
                return AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Unknown(
                    "The prior save may have succeeded. Reload Saved before deciding whether to try again.");
            }

            if (!state.DraftLease.TryBeginSave(draftGeneration, context.ProfileId, context.WorkId,
                    context.SessionLeaseId, authorizedAssetIds, note, out attempt))
            {
                return AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Failed(
                    "The captured bookmark is no longer valid or is already being saved.");
            }

            state.IsSaving = true;
            state.Message = null;
            operationGeneration = ++state.SaveGeneration;
        }

        Changed?.Invoke(context.DialogId);
        var draft = attempt!.Draft;
        var attemptStartedAt = DateTimeOffset.UtcNow;
        var request = new CreateAudiobookBookmarkRequestDto
        {
            ProfileId = context.ProfileId,
            AssetId = draft.AssetId,
            ChapterIndex = draft.ChapterIndex,
            ChapterTitle = draft.ChapterTitle,
            PositionSeconds = draft.PositionSeconds,
            DurationSeconds = draft.DurationSeconds,
            Note = attempt.Note,
        };
        var result = await _api.CreateAudiobookBookmarkWithOutcomeAsync(context.WorkId, request, context.ProfileId, ct)
            .ConfigureAwait(false);

        lock (_sync)
        {
            if (!IsCurrentOperation(context, state!, operationGeneration, DialogOperation.Save))
            {
                return result;
            }

            state!.DraftLease.CompleteSave(draftGeneration);
            state.IsSaving = false;
            state.Message = result.Message;
            var responseMatchesCapture = result.Value is { } responseBookmark
                && responseBookmark.ProfileId == context.ProfileId
                && responseBookmark.WorkId == context.WorkId
                && responseBookmark.AssetId == draft.AssetId
                && responseBookmark.PositionSeconds == draft.PositionSeconds;
            if (result.Outcome == AudiobookBookmarkOperationOutcome.Success && responseMatchesCapture)
            {
                var saved = result.Value!;
                state.Saved = [saved, .. state.Saved.Where(bookmark => bookmark.Id != saved.Id)];
                state.SavedRevision++;
                state.DraftLease.Invalidate();
                state.Message = null;
                state.SaveOutcomeUnknownGeneration = null;
                state.SaveOutcomeUnknownNote = null;
                state.SaveOutcomeUnknownAfterUtc = null;
            }
            else if (result.Outcome == AudiobookBookmarkOperationOutcome.Unknown
                || result.Outcome == AudiobookBookmarkOperationOutcome.Success)
            {
                state.SaveOutcomeUnknownGeneration = draftGeneration;
                state.SaveOutcomeUnknownNote = attempt.Note;
                state.SaveOutcomeUnknownAfterUtc = attemptStartedAt;
                if (result.Outcome == AudiobookBookmarkOperationOutcome.Success)
                {
                    state.Message = "The save response did not match the captured bookmark. Reload Saved before deciding whether to try again.";
                    result = AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Unknown(state.Message);
                }
            }
        }

        Changed?.Invoke(context.DialogId);
        return result;
    }

    public async Task<AudiobookBookmarkOperationResult<bool>> PreviewCapturedDraftAsync(
        AudiobookBookmarkActionContext context,
        long draftGeneration,
        CancellationToken ct = default)
    {
        CapturedAudiobookBookmarkDraft? draft;
        DialogState state;
        lock (_sync)
        {
            if (!TryGetCurrent(context, out state)
                || state.DraftLease.Current is not { } captured
                || captured.Generation != draftGeneration)
            {
                return AudiobookBookmarkOperationResult<bool>.Failed("The captured bookmark is no longer available to preview.");
            }

            draft = captured;
        }

        if (_previewCapturedDraft is null)
        {
            return AudiobookBookmarkOperationResult<bool>.Failed("Preview is unavailable from the current playback owner.");
        }

        var result = await _previewCapturedDraft(context, draft, ct).ConfigureAwait(false);
        lock (_sync)
        {
            if (!TryGetCurrent(context, out var current)
                || !ReferenceEquals(current, state)
                || current.DraftLease.Current?.Generation != draftGeneration)
            {
                return AudiobookBookmarkOperationResult<bool>.Failed("The bookmark preview belonged to an earlier dialog state.");
            }
        }

        return result;
    }

    public AudiobookBookmarkReplayResult Replay(
        AudiobookBookmarkActionContext context,
        Guid bookmarkId,
        IReadOnlySet<Guid> authorizedAssetIds)
    {
        lock (_sync)
        {
            if (!TryGetCurrent(context, out var state))
            {
                return new AudiobookBookmarkReplayResult(AudiobookBookmarkOperationOutcome.DefiniteFailure,
                    Message: "This bookmark dialog is no longer active.");
            }

            var bookmark = state.Saved.FirstOrDefault(item => item.Id == bookmarkId
                && item.ProfileId == context.ProfileId
                && item.WorkId == context.WorkId
                && authorizedAssetIds.Contains(item.AssetId));
            return bookmark is null
                ? new AudiobookBookmarkReplayResult(AudiobookBookmarkOperationOutcome.DefiniteFailure,
                    Message: "That bookmark is no longer available for this audiobook.")
                : new AudiobookBookmarkReplayResult(AudiobookBookmarkOperationOutcome.Success, bookmark);
        }
    }

    public Task<AudiobookBookmarkReplayResult> ReplayAsync(AudiobookBookmarkActionContext context, Guid bookmarkId,
        IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Replay(context, bookmarkId, authorizedAssetIds));
    }

    public bool RequestDelete(AudiobookBookmarkActionContext context, Guid bookmarkId, IReadOnlySet<Guid> authorizedAssetIds)
    {
        lock (_sync)
        {
            if (!TryGetCurrent(context, out var state)
                || state.IsDeleting
                || !state.Saved.Any(item => item.Id == bookmarkId && item.ProfileId == context.ProfileId
                    && item.WorkId == context.WorkId && authorizedAssetIds.Contains(item.AssetId)))
            {
                return false;
            }

            state.DeleteConfirmationBookmarkId = bookmarkId;
            state.Message = null;
        }

        Changed?.Invoke(context.DialogId);
        return true;
    }

    public Task<bool> RequestDeleteAsync(AudiobookBookmarkActionContext context, Guid bookmarkId,
        IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(RequestDelete(context, bookmarkId, authorizedAssetIds));
    }

    public async Task<AudiobookBookmarkOperationResult<bool>> ConfirmDeleteAsync(
        AudiobookBookmarkActionContext context,
        IReadOnlySet<Guid> authorizedAssetIds,
        CancellationToken ct = default)
    {
        DialogState state;
        Guid bookmarkId;
        long operationGeneration;
        lock (_sync)
        {
            if (!TryGetCurrent(context, out state) || state.IsDeleting || state.DeleteConfirmationBookmarkId is not Guid selectedId
                || !state.Saved.Any(item => item.Id == selectedId && item.ProfileId == context.ProfileId
                    && item.WorkId == context.WorkId && authorizedAssetIds.Contains(item.AssetId)))
            {
                return AudiobookBookmarkOperationResult<bool>.Failed("The selected bookmark is no longer available.");
            }

            bookmarkId = selectedId;
            state.IsDeleting = true;
            state.Message = null;
            operationGeneration = ++state.DeleteGeneration;
        }

        Changed?.Invoke(context.DialogId);
        var result = await _api.DeleteAudiobookBookmarkWithOutcomeAsync(bookmarkId, context.ProfileId, ct)
            .ConfigureAwait(false);
        var reload = result.Outcome == AudiobookBookmarkOperationOutcome.Success && result.Value == false
            || result.FailureKind == AudiobookBookmarkFailureKind.NotFound
            ? await _api.GetAudiobookBookmarksWithOutcomeAsync(context.WorkId, context.ProfileId, ct).ConfigureAwait(false)
            : null;
        if (result.Outcome == AudiobookBookmarkOperationOutcome.Success && result.Value == false)
        {
            result = AudiobookBookmarkOperationResult<bool>.Failed("The bookmark was not deleted; Saved was refreshed.",
                failureKind: AudiobookBookmarkFailureKind.NotFound);
        }
        lock (_sync)
        {
            if (!IsCurrentOperation(context, state!, operationGeneration, DialogOperation.Delete))
            {
                return result;
            }

            state!.IsDeleting = false;
            if (result.Outcome == AudiobookBookmarkOperationOutcome.Success && result.Value == true)
            {
                state.Saved = state.Saved.Where(item => item.Id != bookmarkId).ToArray();
                state.SavedRevision++;
                state.DeleteConfirmationBookmarkId = null;
                state.Message = null;
            }
            else if (reload?.Outcome == AudiobookBookmarkOperationOutcome.Success && reload.Value is not null)
            {
                state.Saved = FilterAuthorized(reload.Value, context, authorizedAssetIds);
                state.SavedRevision++;
                state.DeleteConfirmationBookmarkId = state.Saved.Any(item => item.Id == bookmarkId) ? bookmarkId : null;
                state.Message = state.Saved.Any(item => item.Id == bookmarkId)
                    ? "The bookmark was not deleted. Saved has been refreshed."
                    : "The bookmark was already unavailable. Saved has been refreshed.";
                if (state.Saved.All(item => item.Id != bookmarkId))
                {
                    result = AudiobookBookmarkOperationResult<bool>.Failed("The bookmark was not found. Saved has been refreshed.");
                }
            }
            else
            {
                state.Message = result.Message;
            }
        }

        Changed?.Invoke(context.DialogId);
        return result;
    }

    public void CancelDelete(AudiobookBookmarkActionContext context)
    {
        lock (_sync)
        {
            if (!TryGetCurrent(context, out var state))
            {
                return;
            }

            state.DeleteConfirmationBookmarkId = null;
        }

        Changed?.Invoke(context.DialogId);
    }

    public Task CancelDeleteAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        CancelDelete(context);
        return Task.CompletedTask;
    }

    public void Close(AudiobookBookmarkActionContext context)
    {
        var closed = false;
        lock (_sync)
        {
            if (_dialogs.TryGetValue(context.DialogId, out var state)
                && Matches(state, context)
                && _dialogs.Remove(context.DialogId))
            {
                state.DraftLease.Invalidate();
                state.LoadGeneration++;
                state.SaveGeneration++;
                state.DeleteGeneration++;
                closed = true;
            }
        }

        if (closed)
        {
            Changed?.Invoke(context.DialogId);
        }
    }

    public Task CloseAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default)
    {
        // Dialog close is an idempotent lifecycle release. Cancellation or an
        // already-invalidated lease must not prevent the host from dismissing it.
        Close(context);
        return Task.CompletedTask;
    }

    public void InvalidateForLiveContext(Guid? profileId, Guid? workId, Guid? sessionLeaseId,
        IReadOnlySet<Guid> authorizedAssetIds, bool authorityRevoked = false)
    {
        _ = authorizedAssetIds;
        List<Guid> changed = [];
        lock (_sync)
        {
            foreach (var state in _dialogs.Values)
            {
                var contextStillValid = !authorityRevoked
                    && state.Context.ProfileId == profileId
                    && state.Context.WorkId == workId
                    && state.Context.SessionLeaseId == sessionLeaseId;
                if (contextStillValid)
                {
                    continue;
                }

                state.DraftLease.Invalidate();
                state.LoadGeneration++;
                state.SaveGeneration++;
                state.DeleteGeneration++;
                state.IsLoading = false;
                state.IsSaving = false;
                state.IsDeleting = false;
                state.DeleteConfirmationBookmarkId = null;
                state.SaveOutcomeUnknownGeneration = null;
                state.SaveOutcomeUnknownNote = null;
                state.SaveOutcomeUnknownAfterUtc = null;
                state.Message = authorityRevoked
                    ? "Bookmark access changed. Reopen Saved to continue."
                    : "Playback changed. This captured bookmark is no longer active.";
                changed.Add(state.Context.DialogId);
            }
        }

        foreach (var dialogId in changed)
        {
            Changed?.Invoke(dialogId);
        }
    }

    public void InvalidateDialog(AudiobookBookmarkActionContext context, string reason)
    {
        var changed = false;
        lock (_sync)
        {
            if (TryGetCurrent(context, out var state))
            {
                state.DraftLease.Invalidate();
                state.LoadGeneration++;
                state.SaveGeneration++;
                state.DeleteGeneration++;
                state.IsLoading = false;
                state.IsSaving = false;
                state.IsDeleting = false;
                state.DeleteConfirmationBookmarkId = null;
                state.SaveOutcomeUnknownGeneration = null;
                state.SaveOutcomeUnknownNote = null;
                state.SaveOutcomeUnknownAfterUtc = null;
                state.Message = reason;
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke(context.DialogId);
        }
    }

    private bool TryGetCurrent(AudiobookBookmarkActionContext context, out DialogState state)
    {
        if (_dialogs.TryGetValue(context.DialogId, out var found) && Matches(found, context))
        {
            state = found;
            return true;
        }

        state = null!;
        return false;
    }

    private bool IsCurrentOperation(AudiobookBookmarkActionContext context, DialogState state,
        long operationGeneration, DialogOperation operation) =>
        _dialogs.TryGetValue(context.DialogId, out var current)
        && ReferenceEquals(current, state)
        && Matches(state, context)
        && (operation switch
        {
            DialogOperation.Load => state.LoadGeneration == operationGeneration,
            DialogOperation.Save => state.SaveGeneration == operationGeneration,
            DialogOperation.Delete => state.DeleteGeneration == operationGeneration,
            _ => false,
        });

    private static bool Matches(DialogState state, AudiobookBookmarkActionContext context) =>
        state.Context == context;

    private static IReadOnlyList<AudiobookBookmarkDto> FilterAuthorized(
        IEnumerable<AudiobookBookmarkDto> bookmarks,
        AudiobookBookmarkActionContext context,
        IReadOnlySet<Guid> authorizedAssetIds) => bookmarks
        .Where(bookmark => bookmark.ProfileId == context.ProfileId
            && bookmark.WorkId == context.WorkId
            && authorizedAssetIds.Contains(bookmark.AssetId))
        .OrderByDescending(bookmark => bookmark.CreatedAt)
        .ToArray();

    private static AudiobookBookmarkDraftPayloadDto ToPayload(CapturedAudiobookBookmarkDraft draft) => new()
    {
        DraftGeneration = draft.Generation,
        ProfileId = draft.ProfileId,
        WorkId = draft.WorkId,
        SessionLeaseId = draft.SessionLeaseId,
        AssetId = draft.AssetId,
        ChapterIndex = draft.ChapterIndex,
        ChapterTitle = draft.ChapterTitle,
        PositionSeconds = draft.PositionSeconds,
        DurationSeconds = draft.DurationSeconds,
        CapturedAt = draft.CapturedAt,
    };

    private static void ValidateContext(AudiobookBookmarkActionContext context)
    {
        if (context.DialogId == Guid.Empty || context.ProfileId == Guid.Empty || context.WorkId == Guid.Empty
            || context.SessionLeaseId == Guid.Empty || context.OwnerGeneration < 0
            || context.ExpectedAssetId == Guid.Empty)
        {
            throw new ArgumentException("A valid dialog, profile, work, session lease, and owner generation are required.", nameof(context));
        }
    }

    private enum DialogOperation
    {
        Load,
        Save,
        Delete,
    }

    private sealed class DialogState(AudiobookBookmarkActionContext context)
    {
        public AudiobookBookmarkActionContext Context { get; } = context;
        public long LoadGeneration { get; set; }
        public long SaveGeneration { get; set; }
        public long DeleteGeneration { get; set; }
        public long SavedRevision { get; set; }
        public CapturedAudiobookBookmarkDraftLease DraftLease { get; } = new();
        public IReadOnlyList<AudiobookBookmarkDto> Saved { get; set; } = [];
        public bool IsLoading { get; set; }
        public bool IsSaving { get; set; }
        public bool IsDeleting { get; set; }
        public Guid? DeleteConfirmationBookmarkId { get; set; }
        public long? SaveOutcomeUnknownGeneration { get; set; }
        public string? SaveOutcomeUnknownNote { get; set; }
        public DateTimeOffset? SaveOutcomeUnknownAfterUtc { get; set; }
        public string? Message { get; set; }

        public AudiobookBookmarkActionSnapshot Snapshot() =>
            new(Saved, DraftLease.Current, IsLoading, IsSaving || IsDeleting, DeleteConfirmationBookmarkId, Message,
                SaveOutcomeUnknownGeneration is not null);
    }
}
