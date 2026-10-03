using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Playback;

/// <summary>Transient request/reply bridge. Implementations must not persist commands.</summary>
public interface IListenPlaybackCommandChannel
{
    Task<ListenPlaybackCommandReplyDto?> SendAsync(Guid ownerRecipientId, ListenPlaybackCommandDto command,
        CancellationToken ct = default);
}

/// <summary>Popup-only, owner-scoped transient BroadcastChannel request/reply transport.</summary>
public sealed class BroadcastListenPlaybackCommandChannel(IJSRuntime js) : IListenPlaybackCommandChannel
{
    public async Task<ListenPlaybackCommandReplyDto?> SendAsync(Guid ownerRecipientId,
        ListenPlaybackCommandDto command, CancellationToken ct = default)
    {
        if (ownerRecipientId == Guid.Empty || command.RecipientId != ownerRecipientId
            || command.SenderId == Guid.Empty || command.CommandId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return await js.InvokeAsync<ListenPlaybackCommandReplyDto?>(
                "tuvimaBookmarkCommands.send", ct, ownerRecipientId, command, 8000).ConfigureAwait(false);
        }
        catch (JSDisconnectedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>In-process main-window transport using the same envelope and owner dispatcher as popups.</summary>
public sealed class InProcessListenPlaybackCommandChannel(Guid channelOwnerRecipientId,
    AudiobookBookmarkCommandDispatcher dispatcher) : IListenPlaybackCommandChannel
{
    public Task<ListenPlaybackCommandReplyDto?> SendAsync(Guid ownerRecipientId, ListenPlaybackCommandDto command,
        CancellationToken ct = default)
    {
        if (ownerRecipientId != channelOwnerRecipientId)
            return Task.FromResult<ListenPlaybackCommandReplyDto?>(null);
        return DispatchAsync(command, ct);
    }

    private async Task<ListenPlaybackCommandReplyDto?> DispatchAsync(ListenPlaybackCommandDto command, CancellationToken ct) =>
        await dispatcher.HandleAsync(command, ct).ConfigureAwait(false);
}

public interface IAudiobookBookmarkAuthoritySource
{
    Task<IReadOnlySet<Guid>> GetAuthorizedAssetIdsAsync(AudiobookBookmarkActionContext context,
        CancellationToken ct = default);
}

public sealed record AudiobookBookmarkCaptureObservation(
    Guid AssetId,
    double PositionSeconds,
    double? DurationSeconds,
    PlaybackChapterDto? Chapter);

/// <summary>Implemented only by the current native playback owner.</summary>
public interface IAudiobookBookmarkNativeOwner
{
    Task<bool> IsCurrentSessionAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default);
    Task<bool> IsCurrentSourceAsync(AudiobookBookmarkActionContext context, Guid expectedAssetId, CancellationToken ct = default);

    Task<AudiobookBookmarkCaptureObservation?> CaptureCurrentAsync(AudiobookBookmarkActionContext context,
        CancellationToken ct = default);

    Task<AudiobookBookmarkOperationResult<bool>> ReplayBookmarkAsync(AudiobookBookmarkActionContext context,
        AudiobookBookmarkDto bookmark, CancellationToken ct = default);

    Task<AudiobookBookmarkOperationResult<bool>> PreviewCapturedDraftAsync(AudiobookBookmarkActionContext context,
        CapturedAudiobookBookmarkDraft draft, CancellationToken ct = default);
}

/// <summary>
/// Dispatches the one Contracts command envelope to the authoritative bookmark service.
/// Browser hosts supply authorization and native metrics; neither are accepted from a popup.
/// </summary>
public sealed class AudiobookBookmarkCommandDispatcher(
    Guid ownerRecipientId,
    IAudiobookBookmarkActions actions,
    IAudiobookBookmarkCaptureOwner captureOwner,
    IAudiobookBookmarkLeaseInvalidator invalidator,
    IAudiobookBookmarkNativeOwner nativeOwner,
    IAudiobookBookmarkAuthoritySource authority) : IDisposable
{
    private readonly object _bindingSync = new();
    private readonly Dictionary<Guid, (Guid SenderId, AudiobookBookmarkActionContext Context)> _bindings = [];
    private readonly object _commandSync = new();
    private readonly Dictionary<(Guid SenderId, Guid CommandId), CommandDelivery> _deliveries = [];
    private bool _disposed;

    public async Task<ListenPlaybackCommandReplyDto> HandleAsync(ListenPlaybackCommandDto command,
        CancellationToken ct = default)
    {
        var key = (command.SenderId, command.CommandId);
        CommandDelivery delivery;
        var isOwner = false;
        lock (_commandSync)
        {
            if (_disposed)
                return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure, message: "The bookmark command owner has been disposed.");
            if (_deliveries.TryGetValue(key, out delivery!))
            {
                if (delivery.Command != command)
                    return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure, message: "A command identifier was reused with different content.");
            }
            else
            {
                delivery = new CommandDelivery(command);
                _deliveries.Add(key, delivery);
                isOwner = true;
            }
        }

        if (!isOwner)
            return await delivery.Completion.Task.WaitAsync(ct).ConfigureAwait(false);

        ListenPlaybackCommandReplyDto reply;
        try
        {
            reply = await HandleCoreAsync(command, ct).ConfigureAwait(false);
            if (command.Action == ListenPlaybackCommandActions.ReplayBookmark
                && reply.Outcome == AudiobookBookmarkOperationOutcomes.Success
                && TryGetContext(command, out var replayContext))
            {
                var mayReleaseDialog = false;
                lock (_bindingSync)
                {
                    if (!_bindings.TryGetValue(replayContext.DialogId, out var binding))
                    {
                        // The host may already have dismissed this exact dialog while
                        // the native owner was confirming the replay.
                        mayReleaseDialog = true;
                    }
                    else if (binding.SenderId == command.SenderId && binding.Context == replayContext)
                    {
                        mayReleaseDialog = _bindings.Remove(replayContext.DialogId);
                    }
                }

                if (mayReleaseDialog)
                {
                    try
                    {
                        invalidator.InvalidateDialog(replayContext, "The saved bookmark is now playing.");
                    }
                    catch
                    {
                        // The native owner already confirmed the replay.
                    }
                    try
                    {
                        await actions.CloseAsync(replayContext, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Releasing the old dialog lease is best-effort and cannot undo playback.
                    }
                }
            }
            else if (command.Action is not ListenPlaybackCommandActions.OpenBookmarkDialog
                and not ListenPlaybackCommandActions.CloseBookmarkDialog
                and not ListenPlaybackCommandActions.BookmarkDialogState
                && TryGetContext(command, out var context)
                && (!IsBound(command, context) || !await nativeOwner.IsCurrentSessionAsync(context, ct).ConfigureAwait(false)))
            {
                invalidator.InvalidateDialog(context, "Playback changed while the bookmark action was in progress.");
                lock (_bindingSync) _bindings.Remove(context.DialogId);
                reply = Reply(command, command.Action is ListenPlaybackCommandActions.SaveBookmarkDraft or ListenPlaybackCommandActions.ConfirmDeleteBookmark
                    ? AudiobookBookmarkOperationOutcomes.Unknown : AudiobookBookmarkOperationOutcomes.DefiniteFailure,
                    message: "Playback changed before the owner could confirm this bookmark action.");
            }
        }
        catch
        {
            reply = Reply(command, command.Action is ListenPlaybackCommandActions.SaveBookmarkDraft or ListenPlaybackCommandActions.ConfirmDeleteBookmark
                ? AudiobookBookmarkOperationOutcomes.Unknown : AudiobookBookmarkOperationOutcomes.DefiniteFailure,
                message: "The bookmark command could not be completed.");
        }

        delivery.Completion.TrySetResult(reply);
        lock (_commandSync)
        {
            if (_deliveries.Count > 128)
            {
                var excess = _deliveries.Count - 128;
                foreach (var oldKey in _deliveries.Where(pair => pair.Value.Completion.Task.IsCompleted)
                             .Take(excess).Select(pair => pair.Key).ToArray())
                    _deliveries.Remove(oldKey);
            }
        }
        return reply;
    }

    private async Task<ListenPlaybackCommandReplyDto> HandleCoreAsync(ListenPlaybackCommandDto command,
        CancellationToken ct = default)
    {
        if (command.CommandId == Guid.Empty || command.SenderId == Guid.Empty || command.RecipientId != ownerRecipientId
            || !TryGetContext(command, out var context))
        {
            return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure,
                message: "The bookmark command is not addressed to the active owner.");
        }

        try
        {
            switch (command.Action)
            {
                case ListenPlaybackCommandActions.OpenBookmarkDialog:
                    return await OpenAndCaptureAsync(command, context, ct).ConfigureAwait(false);
                case ListenPlaybackCommandActions.BookmarkDialogState:
                    if (!IsBound(command, context) || !await nativeOwner.IsCurrentSessionAsync(context, ct).ConfigureAwait(false))
                        return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure, message: "This popup no longer owns the active audiobook dialog.");
                    return Reply(command, AudiobookBookmarkOperationOutcomes.Success, snapshot: await SnapshotAsync(context, ct));
                case ListenPlaybackCommandActions.PreviewBookmarkDraft:
                {
                    if (!await EnsureBoundCurrentAsync(command, context, ct).ConfigureAwait(false))
                        return StaleReply(command);
                    var result = await actions.PreviewCapturedDraftAsync(context, command.DraftGeneration ?? -1, ct).ConfigureAwait(false);
                    return Reply(command, Outcome(result.Outcome), snapshot: await SnapshotAsync(context, ct), message: result.Message);
                }
                case ListenPlaybackCommandActions.LoadBookmarks:
                {
                    if (!await EnsureBoundCurrentAsync(command, context, ct).ConfigureAwait(false))
                        return StaleReply(command);
                    var assets = await authority.GetAuthorizedAssetIdsAsync(context, ct).ConfigureAwait(false);
                    var result = await actions.LoadSavedAsync(context, assets, ct).ConfigureAwait(false);
                    return Reply(command, Outcome(result.Outcome), snapshot: await SnapshotAsync(context, ct),
                        bookmarks: result.Value, message: result.Message);
                }
                case ListenPlaybackCommandActions.SaveBookmarkDraft:
                {
                    if (!await EnsureBoundCurrentAsync(command, context, ct).ConfigureAwait(false))
                        return StaleReply(command);
                    var assets = await authority.GetAuthorizedAssetIdsAsync(context, ct).ConfigureAwait(false);
                    var draft = command.BookmarkDraft;
                    if (draft is null || draft.ProfileId != context.ProfileId || draft.WorkId != context.WorkId
                        || draft.SessionLeaseId != context.SessionLeaseId || draft.DraftGeneration != command.DraftGeneration
                        || draft.AssetId != command.ExpectedAssetId)
                    {
                        return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure,
                            snapshot: await SnapshotAsync(context, ct), message: "The captured bookmark lease did not match this dialog.");
                    }

                    var result = await actions.SaveAsync(context, draft.DraftGeneration, assets, draft.Note, ct).ConfigureAwait(false);
                    return Reply(command, Outcome(result.Outcome), snapshot: await SnapshotAsync(context, ct),
                        bookmark: result.Value, message: result.Message);
                }
                case ListenPlaybackCommandActions.ReplayBookmark when command.BookmarkId is Guid bookmarkId:
                {
                    if (!await EnsureBoundCurrentAsync(command, context, ct).ConfigureAwait(false))
                        return StaleReply(command);
                    var assets = await authority.GetAuthorizedAssetIdsAsync(context, ct).ConfigureAwait(false);
                    var result = await actions.ReplayAsync(context, bookmarkId, assets, ct).ConfigureAwait(false);
                    if (result.Outcome != AudiobookBookmarkOperationOutcome.Success || result.Bookmark is null)
                    {
                        return Reply(command, Outcome(result.Outcome), snapshot: await SnapshotAsync(context, ct), message: result.Message);
                    }

                    var replay = await nativeOwner.ReplayBookmarkAsync(context, result.Bookmark, ct).ConfigureAwait(false);
                    return Reply(command, Outcome(replay.Outcome), snapshot: await SnapshotAsync(context, ct),
                        bookmark: result.Bookmark, message: replay.Message);
                }
                case ListenPlaybackCommandActions.RequestDeleteBookmark when command.BookmarkId is Guid bookmarkId:
                {
                    if (!await EnsureBoundCurrentAsync(command, context, ct).ConfigureAwait(false))
                        return StaleReply(command);
                    var assets = await authority.GetAuthorizedAssetIdsAsync(context, ct).ConfigureAwait(false);
                    var accepted = await actions.RequestDeleteAsync(context, bookmarkId, assets, ct).ConfigureAwait(false);
                    return Reply(command, accepted ? AudiobookBookmarkOperationOutcomes.Success : AudiobookBookmarkOperationOutcomes.DefiniteFailure,
                        snapshot: await SnapshotAsync(context, ct));
                }
                case ListenPlaybackCommandActions.ConfirmDeleteBookmark:
                {
                    if (!await EnsureBoundCurrentAsync(command, context, ct).ConfigureAwait(false))
                        return StaleReply(command);
                    var assets = await authority.GetAuthorizedAssetIdsAsync(context, ct).ConfigureAwait(false);
                    var result = await actions.ConfirmDeleteAsync(context, assets, ct).ConfigureAwait(false);
                    return Reply(command, Outcome(result.Outcome), snapshot: await SnapshotAsync(context, ct),
                        booleanResult: result.Value, message: result.Message);
                }
                case ListenPlaybackCommandActions.CancelDeleteBookmark:
                    if (!await EnsureBoundCurrentAsync(command, context, ct).ConfigureAwait(false))
                        return StaleReply(command);
                    await actions.CancelDeleteAsync(context, ct).ConfigureAwait(false);
                    return Reply(command, AudiobookBookmarkOperationOutcomes.Success, snapshot: await SnapshotAsync(context, ct));
                case ListenPlaybackCommandActions.CloseBookmarkDialog:
                    // Closing is a lease release, not a playback mutation. Let the exact
                    // recipient release an already-invalidated lease, but never let a
                    // stale close release a newer binding that reused the dialog id.
                    var releaseBinding = false;
                    lock (_bindingSync)
                    {
                        if (!_bindings.TryGetValue(context.DialogId, out var binding))
                            return Reply(command, AudiobookBookmarkOperationOutcomes.Success);
                        if (binding.SenderId != command.SenderId || binding.Context != context)
                            return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure,
                                message: "This popup does not own that bookmark dialog.");
                        releaseBinding = _bindings.Remove(context.DialogId);
                    }
                    if (!releaseBinding)
                        return Reply(command, AudiobookBookmarkOperationOutcomes.Success);
                    await actions.CloseAsync(context, ct).ConfigureAwait(false);
                    return Reply(command, AudiobookBookmarkOperationOutcomes.Success);
                default:
                    return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure, message: "Unknown bookmark command.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Reply(command, command.Action is ListenPlaybackCommandActions.SaveBookmarkDraft or ListenPlaybackCommandActions.ConfirmDeleteBookmark
                ? AudiobookBookmarkOperationOutcomes.Unknown
                : AudiobookBookmarkOperationOutcomes.DefiniteFailure, message: "The bookmark command was cancelled before its result was confirmed.");
        }
        catch
        {
            return Reply(command, command.Action is ListenPlaybackCommandActions.SaveBookmarkDraft or ListenPlaybackCommandActions.ConfirmDeleteBookmark
                ? AudiobookBookmarkOperationOutcomes.Unknown
                : AudiobookBookmarkOperationOutcomes.DefiniteFailure, message: "The bookmark command could not be completed.");
        }
    }

    public void Dispose()
    {
        List<CommandDelivery> pending;
        lock (_commandSync)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _deliveries.Values.Where(delivery => !delivery.Completion.Task.IsCompleted).ToList();
            _deliveries.Clear();
        }

        foreach (var delivery in pending)
        {
            var command = delivery.Command;
            delivery.Completion.TrySetResult(Reply(command,
                command.Action is ListenPlaybackCommandActions.SaveBookmarkDraft or ListenPlaybackCommandActions.ConfirmDeleteBookmark
                    ? AudiobookBookmarkOperationOutcomes.Unknown : AudiobookBookmarkOperationOutcomes.DefiniteFailure,
                message: "The bookmark command owner was disposed before the reply completed."));
        }
        lock (_bindingSync) _bindings.Clear();
    }

    private sealed class CommandDelivery(ListenPlaybackCommandDto command)
    {
        public ListenPlaybackCommandDto Command { get; } = command;
        public TaskCompletionSource<ListenPlaybackCommandReplyDto> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task<ListenPlaybackCommandReplyDto> OpenAndCaptureAsync(ListenPlaybackCommandDto command,
        AudiobookBookmarkActionContext context, CancellationToken ct)
    {
        if (command.ExpectedAssetId is not Guid expectedAssetId || context.ExpectedAssetId != expectedAssetId)
        {
            return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure,
                message: "Opening bookmarks requires the current native audiobook source.");
        }

        lock (_bindingSync)
        {
            if (_bindings.ContainsKey(context.DialogId))
            {
                return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure,
                    message: "This bookmark dialog has already captured its opening position.");
            }

            _bindings.Add(context.DialogId, (command.SenderId, context));
        }

        try
        {
            if (!await nativeOwner.IsCurrentSourceAsync(context, expectedAssetId, ct).ConfigureAwait(false))
                return await FailOpenAsync(command, context, "The playback subject changed before bookmark capture.", ct);

            var observation = await nativeOwner.CaptureCurrentAsync(context, ct).ConfigureAwait(false);
            if (observation is null || observation.AssetId != expectedAssetId
                || !await nativeOwner.IsCurrentSourceAsync(context, expectedAssetId, ct).ConfigureAwait(false))
            {
                return await FailOpenAsync(command, context, "The playback subject changed while bookmark capture was in progress.", ct);
            }

            var assets = await authority.GetAuthorizedAssetIdsAsync(context, ct).ConfigureAwait(false);
            if (!assets.Contains(expectedAssetId))
                return await FailOpenAsync(command, context, "The captured audio source is no longer authorized.", ct);

            await actions.OpenAsync(context, ct).ConfigureAwait(false);
            if (!captureOwner.TryCapture(context, observation.AssetId, assets, observation.PositionSeconds,
                    observation.DurationSeconds, observation.Chapter, out var capture))
            {
                return await FailOpenAsync(command, context, "A valid audiobook position could not be captured.", ct);
            }

            if (!await nativeOwner.IsCurrentSourceAsync(context, expectedAssetId, ct).ConfigureAwait(false))
            {
                invalidator.InvalidateDialog(context, "Playback changed before the captured bookmark could be shown.");
                return await FailOpenAsync(command, context, "Playback changed before the captured bookmark could be shown.", ct);
            }

            return Reply(command, AudiobookBookmarkOperationOutcomes.Success,
                snapshot: await SnapshotAsync(context, ct), draft: capture);
        }
        catch
        {
            return await FailOpenAsync(command, context, "The bookmark dialog could not capture the current audiobook position.", ct);
        }
    }

    private async Task<ListenPlaybackCommandReplyDto> FailOpenAsync(ListenPlaybackCommandDto command,
        AudiobookBookmarkActionContext context, string reason, CancellationToken ct)
    {
        invalidator.InvalidateDialog(context, reason);
        await actions.CloseAsync(context, CancellationToken.None).ConfigureAwait(false);
        lock (_bindingSync) _bindings.Remove(context.DialogId);
        return Reply(command, AudiobookBookmarkOperationOutcomes.DefiniteFailure, message: reason);
    }

    private bool IsBound(ListenPlaybackCommandDto command, AudiobookBookmarkActionContext context)
    {
        lock (_bindingSync)
        {
            return _bindings.TryGetValue(context.DialogId, out var binding)
                && binding.SenderId == command.SenderId
                && binding.Context == context;
        }
    }

    private async Task<bool> EnsureBoundCurrentAsync(ListenPlaybackCommandDto command,
        AudiobookBookmarkActionContext context, CancellationToken ct)
    {
        if (!IsBound(command, context)) return false;
        if (await nativeOwner.IsCurrentSessionAsync(context, ct).ConfigureAwait(false)) return true;
        invalidator.InvalidateDialog(context, "Playback changed. This bookmark dialog is no longer active.");
        lock (_bindingSync) _bindings.Remove(context.DialogId);
        return false;
    }

    private ListenPlaybackCommandReplyDto StaleReply(ListenPlaybackCommandDto command) =>
        Reply(command, command.Action is ListenPlaybackCommandActions.SaveBookmarkDraft or ListenPlaybackCommandActions.ConfirmDeleteBookmark
            ? AudiobookBookmarkOperationOutcomes.Unknown
            : AudiobookBookmarkOperationOutcomes.DefiniteFailure,
            message: "The bookmark action belonged to an earlier playback owner or dialog.");

    private async Task<AudiobookBookmarkDialogSnapshotDto> SnapshotAsync(AudiobookBookmarkActionContext context,
        CancellationToken ct)
    {
        var snapshot = await actions.GetSnapshotAsync(context, ct).ConfigureAwait(false);
        var authorizedAssetIds = await authority.GetAuthorizedAssetIdsAsync(context, ct).ConfigureAwait(false);
        return new AudiobookBookmarkDialogSnapshotDto
        {
            AuthorizedAssetIds = authorizedAssetIds,
            Saved = snapshot.Saved,
            Draft = snapshot.Draft is { } draft ? new AudiobookBookmarkDraftPayloadDto
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
            } : null,
            IsLoading = snapshot.IsLoading,
            IsSaving = snapshot.IsSaving,
            DeleteConfirmationBookmarkId = snapshot.DeleteConfirmationBookmarkId,
            Message = snapshot.Message,
            SaveOutcomeUnknown = snapshot.SaveOutcomeUnknown,
        };
    }

    private static bool TryGetContext(ListenPlaybackCommandDto command, out AudiobookBookmarkActionContext context)
    {
        context = default!;
        if (command.DialogId is not Guid dialogId || dialogId == Guid.Empty
            || command.ProfileId is not Guid profileId || profileId == Guid.Empty
            || command.WorkId is not Guid workId || workId == Guid.Empty
            || command.SessionLeaseId is not Guid sessionLeaseId || sessionLeaseId == Guid.Empty
            || command.OwnerGeneration < 0)
        {
            return false;
        }

        context = new AudiobookBookmarkActionContext(dialogId, profileId, workId, sessionLeaseId, command.OwnerGeneration);
        context = context with { ExpectedAssetId = command.ExpectedAssetId };
        return true;
    }

    private ListenPlaybackCommandReplyDto Reply(ListenPlaybackCommandDto command, string outcome,
        AudiobookBookmarkDialogSnapshotDto? snapshot = null, AudiobookBookmarkDraftPayloadDto? draft = null,
        AudiobookBookmarkDto? bookmark = null, IReadOnlyList<AudiobookBookmarkDto>? bookmarks = null,
        string? message = null, bool? booleanResult = null) => new()
    {
        CommandId = command.CommandId,
        RecipientId = command.SenderId,
        Outcome = outcome,
        BookmarkSnapshot = snapshot,
        BookmarkDraft = draft,
        Bookmark = bookmark,
        Bookmarks = bookmarks,
        BooleanResult = booleanResult,
        Message = message,
    };

    private static string Outcome(AudiobookBookmarkOperationOutcome outcome) => outcome switch
    {
        AudiobookBookmarkOperationOutcome.Success => AudiobookBookmarkOperationOutcomes.Success,
        AudiobookBookmarkOperationOutcome.Unknown => AudiobookBookmarkOperationOutcomes.Unknown,
        _ => AudiobookBookmarkOperationOutcomes.DefiniteFailure,
    };
}

/// <summary>Popup-side adapter. It has no API, playback, authorization, or capture capability.</summary>
public sealed class ListenPlaybackCommandActionsClient(Guid ownerRecipientId, Guid recipientId,
    IListenPlaybackCommandChannel channel) : IAudiobookBookmarkActions
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ProxyDialogState> _dialogs = [];
    public event Action<Guid>? Changed;

    public async Task OpenAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default)
    {
        lock (_sync) _dialogs[context.DialogId] = new ProxyDialogState(context);
        var result = await SendAsync(context, ListenPlaybackCommandActions.OpenBookmarkDialog, ct).ConfigureAwait(false);
        EnsureSuccess(result, "The bookmark dialog could not be opened by its playback owner.");
        UpdateFromReply(context, result);
    }

    public IReadOnlySet<Guid> GetAuthorizedAssetIds(AudiobookBookmarkActionContext context)
    {
        lock (_sync)
        {
            return _dialogs.TryGetValue(context.DialogId, out var state) && state.Context == context
                ? state.AuthorizedAssetIds
                : new HashSet<Guid>();
        }
    }

    public async Task<AudiobookBookmarkActionSnapshot> GetSnapshotAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default)
    {
        var reply = await SendAsync(context, ListenPlaybackCommandActions.BookmarkDialogState, ct).ConfigureAwait(false);
        // Reads are deliberately not applied to the cached presentation. A state read can
        // race a pending Save and return an older snapshot; the command reply is still
        // useful to this caller, but must not overwrite a newer mutation result.
        if (reply?.BookmarkSnapshot is { } snapshot) return FromDto(snapshot);
        lock (_sync)
        {
            if (_dialogs.TryGetValue(context.DialogId, out var state) && state.Context == context && state.Snapshot is { } cached)
                return cached;
        }
        return new AudiobookBookmarkActionSnapshot([], null, false, false, null,
            reply?.Message ?? "The playback owner did not return bookmark state.");
    }

    public async Task<AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>> LoadSavedAsync(
        AudiobookBookmarkActionContext context, IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default)
    {
        _ = authorizedAssetIds;
        var reply = await SendAsync(context, ListenPlaybackCommandActions.LoadBookmarks, ct).ConfigureAwait(false);
        UpdateFromReply(context, reply);
        return ParseResult(reply, reply?.Bookmarks ?? (IReadOnlyList<AudiobookBookmarkDto>)[], "Saved bookmarks could not be loaded.");
    }

    public async Task<AudiobookBookmarkOperationResult<AudiobookBookmarkDto>> SaveAsync(
        AudiobookBookmarkActionContext context, long draftGeneration, IReadOnlySet<Guid> authorizedAssetIds,
        string? note, CancellationToken ct = default)
    {
        _ = authorizedAssetIds;
        var current = await GetSnapshotAsync(context, ct).ConfigureAwait(false);
        if (current.Draft?.Generation != draftGeneration)
            return AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Failed("The captured draft changed before it could be saved.");
        var reply = await SendAsync(context, ListenPlaybackCommandActions.SaveBookmarkDraft, ct,
            draft: ToPayload(current.Draft, note));
        UpdateFromReply(context, reply);
        return ParseResult<AudiobookBookmarkDto>(reply, reply?.Bookmark, "The save result was not returned by the playback owner.");
    }

    public async Task<AudiobookBookmarkOperationResult<bool>> PreviewCapturedDraftAsync(
        AudiobookBookmarkActionContext context, long draftGeneration, CancellationToken ct = default)
    {
        var reply = await SendAsync(context, ListenPlaybackCommandActions.PreviewBookmarkDraft, ct, draftGeneration: draftGeneration);
        UpdateFromReply(context, reply);
        return ParseResult(reply, reply?.Outcome == AudiobookBookmarkOperationOutcomes.Success, "The captured position could not be previewed.");
    }

    public async Task<AudiobookBookmarkReplayResult> ReplayAsync(AudiobookBookmarkActionContext context, Guid bookmarkId,
        IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default)
    {
        _ = authorizedAssetIds;
        var reply = await SendAsync(context, ListenPlaybackCommandActions.ReplayBookmark, ct, bookmarkId: bookmarkId);
        UpdateFromReply(context, reply);
        return new AudiobookBookmarkReplayResult(ParseOutcome(reply), reply?.Bookmark, reply?.Message);
    }

    public async Task<bool> RequestDeleteAsync(AudiobookBookmarkActionContext context, Guid bookmarkId,
        IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default)
    {
        _ = authorizedAssetIds;
        var reply = await SendAsync(context, ListenPlaybackCommandActions.RequestDeleteBookmark, ct, bookmarkId: bookmarkId);
        UpdateFromReply(context, reply);
        return ParseOutcome(reply) == AudiobookBookmarkOperationOutcome.Success;
    }

    public async Task<AudiobookBookmarkOperationResult<bool>> ConfirmDeleteAsync(
        AudiobookBookmarkActionContext context, IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default)
    {
        _ = authorizedAssetIds;
        var reply = await SendAsync(context, ListenPlaybackCommandActions.ConfirmDeleteBookmark, ct);
        UpdateFromReply(context, reply);
        if (reply?.BooleanResult is not bool deleted)
        {
            return AudiobookBookmarkOperationResult<bool>.Unknown(
                reply?.Message ?? "The owner did not confirm whether the bookmark was deleted.");
        }

        return ParseResult<bool>(reply, deleted, "The delete result was not returned by the playback owner.");
    }

    public async Task CancelDeleteAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default)
    {
        var reply = await SendAsync(context, ListenPlaybackCommandActions.CancelDeleteBookmark, ct);
        UpdateFromReply(context, reply);
        EnsureSuccess(reply, "The delete confirmation could not be cancelled.");
    }

    public async Task CloseAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default)
    {
        ProxyDialogState? state;
        lock (_sync)
        {
            if (!_dialogs.TryGetValue(context.DialogId, out state) || state.Context != context)
                return;
        }

        try
        {
            var reply = await SendAsync(context, ListenPlaybackCommandActions.CloseBookmarkDialog, ct).ConfigureAwait(false);
            UpdateFromReply(context, reply, publishChanged: false);
        }
        catch
        {
            // A close only releases this local dialog lease. Transport loss or owner
            // disposal must not keep a dismissed dialog alive or surface an error.
        }
        finally
        {
            lock (_sync)
            {
                if (_dialogs.TryGetValue(context.DialogId, out var current) && ReferenceEquals(current, state))
                    _dialogs.Remove(context.DialogId);
            }
        }
    }

    private async Task<ListenPlaybackCommandReplyDto?> SendAsync(AudiobookBookmarkActionContext context, string action,
        CancellationToken ct, long? draftGeneration = null, Guid? bookmarkId = null, AudiobookBookmarkDraftPayloadDto? draft = null)
    {
        ProxyDialogState? requestedState;
        lock (_sync)
        {
            if (!_dialogs.TryGetValue(context.DialogId, out requestedState)
                || requestedState.Context != context)
            {
                return FailedReply(action, "This popup bookmark dialog is no longer active.");
            }

        }

        var command = new ListenPlaybackCommandDto
        {
            CommandId = Guid.NewGuid(),
            SenderId = recipientId,
            RecipientId = ownerRecipientId,
            DialogId = context.DialogId,
            OwnerGeneration = context.OwnerGeneration,
            Action = action,
            ProfileId = context.ProfileId,
            WorkId = context.WorkId,
            SessionLeaseId = context.SessionLeaseId,
            ExpectedAssetId = draft?.AssetId ?? context.ExpectedAssetId,
            BookmarkId = bookmarkId,
            DraftGeneration = draftGeneration ?? draft?.DraftGeneration,
            BookmarkDraft = draft,
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        ListenPlaybackCommandReplyDto? reply;
        try
        {
            reply = await channel.SendAsync(ownerRecipientId, command, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return action is ListenPlaybackCommandActions.SaveBookmarkDraft or ListenPlaybackCommandActions.ConfirmDeleteBookmark
                ? new ListenPlaybackCommandReplyDto { CommandId = command.CommandId, RecipientId = recipientId, Outcome = AudiobookBookmarkOperationOutcomes.Unknown, Message = "The owner did not confirm the mutation result." }
                : new ListenPlaybackCommandReplyDto { CommandId = command.CommandId, RecipientId = recipientId, Outcome = AudiobookBookmarkOperationOutcomes.DefiniteFailure, Message = "The owner did not respond to the bookmark command." };
        }
        catch
        {
            return FailedReply(action, "The bookmark command transport failed before a reliable reply was received.", command.CommandId);
        }

        if (reply is null || reply.CommandId != command.CommandId || reply.RecipientId != command.SenderId)
            return FailedReply(action, "The bookmark command reply did not match this request.", command.CommandId);

        lock (_sync)
        {
            if (!_dialogs.TryGetValue(context.DialogId, out var current)
                || !ReferenceEquals(current, requestedState))
            {
                return FailedReply(action, "The reply belonged to an earlier popup dialog instance.", command.CommandId);
            }
        }

        return reply;
    }

    private ListenPlaybackCommandReplyDto FailedReply(string action, string message, Guid? commandId = null) => new()
    {
        CommandId = commandId ?? Guid.NewGuid(),
        RecipientId = recipientId,
        Outcome = action is ListenPlaybackCommandActions.SaveBookmarkDraft or ListenPlaybackCommandActions.ConfirmDeleteBookmark
            ? AudiobookBookmarkOperationOutcomes.Unknown
            : AudiobookBookmarkOperationOutcomes.DefiniteFailure,
        Message = message,
    };

    private ProxyDialogState? UpdateFromReply(AudiobookBookmarkActionContext context, ListenPlaybackCommandReplyDto? reply,
        bool publishChanged = true)
    {
        if (reply?.BookmarkSnapshot is not { } snapshot)
            return null;
        ProxyDialogState? state;
        lock (_sync)
        {
            if (!_dialogs.TryGetValue(context.DialogId, out state) || state.Context != context)
                return null;
            state.Snapshot = FromDto(snapshot);
            state.AuthorizedAssetIds = snapshot.AuthorizedAssetIds.ToHashSet();
        }
        if (publishChanged) Changed?.Invoke(context.DialogId);
        return state;
    }

    private static AudiobookBookmarkActionSnapshot FromDto(AudiobookBookmarkDialogSnapshotDto source)
    {
        var draft = source.Draft is { } item ? new CapturedAudiobookBookmarkDraft(item.DraftGeneration, item.ProfileId,
            item.WorkId, item.SessionLeaseId, item.AssetId, item.ChapterIndex, item.ChapterTitle,
            item.PositionSeconds, item.DurationSeconds, item.CapturedAt) : null;
        return new AudiobookBookmarkActionSnapshot(source.Saved, draft, source.IsLoading, source.IsSaving,
            source.DeleteConfirmationBookmarkId, source.Message, source.SaveOutcomeUnknown);
    }

    private static AudiobookBookmarkDraftPayloadDto ToPayload(CapturedAudiobookBookmarkDraft draft, string? note) => new()
    {
        DraftGeneration = draft.Generation, ProfileId = draft.ProfileId, WorkId = draft.WorkId,
        SessionLeaseId = draft.SessionLeaseId, AssetId = draft.AssetId, ChapterIndex = draft.ChapterIndex,
        ChapterTitle = draft.ChapterTitle, PositionSeconds = draft.PositionSeconds, DurationSeconds = draft.DurationSeconds,
        CapturedAt = draft.CapturedAt, Note = note,
    };

    private static AudiobookBookmarkOperationResult<T> ParseResult<T>(ListenPlaybackCommandReplyDto? reply, T? value, string fallback) =>
        ParseOutcome(reply) switch
        {
            AudiobookBookmarkOperationOutcome.Success when value is not null => AudiobookBookmarkOperationResult<T>.Succeeded(value),
            AudiobookBookmarkOperationOutcome.Unknown => AudiobookBookmarkOperationResult<T>.Unknown(reply?.Message ?? fallback),
            _ => AudiobookBookmarkOperationResult<T>.Failed(reply?.Message ?? fallback),
        };

    private static AudiobookBookmarkOperationOutcome ParseOutcome(ListenPlaybackCommandReplyDto? reply) => reply?.Outcome switch
    {
        AudiobookBookmarkOperationOutcomes.Success => AudiobookBookmarkOperationOutcome.Success,
        AudiobookBookmarkOperationOutcomes.Unknown => AudiobookBookmarkOperationOutcome.Unknown,
        _ => AudiobookBookmarkOperationOutcome.DefiniteFailure,
    };

    private static void EnsureSuccess(ListenPlaybackCommandReplyDto? reply, string fallback)
    {
        if (ParseOutcome(reply) != AudiobookBookmarkOperationOutcome.Success)
            throw new InvalidOperationException(reply?.Message ?? fallback);
    }

    private sealed class ProxyDialogState(AudiobookBookmarkActionContext context)
    {
        public AudiobookBookmarkActionContext Context { get; } = context;
        public AudiobookBookmarkActionSnapshot? Snapshot { get; set; }
        public IReadOnlySet<Guid> AuthorizedAssetIds { get; set; } = new HashSet<Guid>();
    }
}

