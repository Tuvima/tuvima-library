using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

/// <summary>Commands from a rendered snapshot always reach the existing playback owner.
/// Presentation changes and session exit are deliberately outside this contract.</summary>
public interface IPlaybackCommandSink
{
    bool Supports(string action, ListenPlaybackSnapshot snapshot);
    Task<ListenPlaybackCommandReplyDto?> SendAsync(ListenPlaybackSnapshot snapshot,
        ListenPlaybackCommandDto command, CancellationToken ct = default);
}

public static class PlaybackCommandCapabilities
{
    public static bool IsBookmark(string action) => action is
        ListenPlaybackCommandActions.OpenBookmarkDialog or ListenPlaybackCommandActions.BookmarkDialogState
        or ListenPlaybackCommandActions.PreviewBookmarkDraft or ListenPlaybackCommandActions.LoadBookmarks
        or ListenPlaybackCommandActions.SaveBookmarkDraft or ListenPlaybackCommandActions.ReplayBookmark
        or ListenPlaybackCommandActions.RequestDeleteBookmark or ListenPlaybackCommandActions.ConfirmDeleteBookmark
        or ListenPlaybackCommandActions.CancelDeleteBookmark or ListenPlaybackCommandActions.CloseBookmarkDialog;

    public static bool Supports(string action, ListenPlaybackSnapshot snapshot)
    {
        if (action is ListenPlaybackCommandActions.CloseBookmarkDialog or ListenPlaybackCommandActions.CancelDeleteBookmark) return true;
        if (snapshot.IsDismissed || snapshot.CurrentIndex < 0 || snapshot.CurrentIndex >= snapshot.Queue.Count) return false;
        var experience = snapshot.Queue[snapshot.CurrentIndex].PlaybackExperience;
        if (IsBookmark(action)) return experience == PlaybackExperience.Audiobook;
        return action switch
        {
            ListenPlaybackCommandActions.TogglePlay or ListenPlaybackCommandActions.Play or ListenPlaybackCommandActions.Pause
                or ListenPlaybackCommandActions.Seek
                or ListenPlaybackCommandActions.ToggleMute => true,
            ListenPlaybackCommandActions.SetVolume => snapshot.SoftwareVolumeSupported,
            ListenPlaybackCommandActions.ListOutputs or ListenPlaybackCommandActions.SetOutputDevice => snapshot.OutputSupported,
            ListenPlaybackCommandActions.SetSpeed or ListenPlaybackCommandActions.SkipBack
                or ListenPlaybackCommandActions.SkipForward => experience != PlaybackExperience.Music,
            ListenPlaybackCommandActions.PlayChapter or ListenPlaybackCommandActions.PlayNextChapter
                or ListenPlaybackCommandActions.PlayPreviousChapter or ListenPlaybackCommandActions.SetSleepTimer
                or ListenPlaybackCommandActions.PlayAudiobookHistory => experience == PlaybackExperience.Audiobook,
            ListenPlaybackCommandActions.ToggleShuffle or ListenPlaybackCommandActions.CycleRepeat
                or ListenPlaybackCommandActions.PlayHistory or ListenPlaybackCommandActions.ClearHistory
                or ListenPlaybackCommandActions.ReorderUpcoming => experience == PlaybackExperience.Music,
            ListenPlaybackCommandActions.PlayNext or ListenPlaybackCommandActions.PlayPrevious
                or ListenPlaybackCommandActions.PlayIndex or ListenPlaybackCommandActions.RemoveUpcoming
                or ListenPlaybackCommandActions.ClearUpcoming => experience != PlaybackExperience.Audiobook,
            _ => false,
        };
    }
}

public abstract class PlaybackCommandSink(Guid ownerRecipientId, Guid senderId, IListenPlaybackCommandChannel channel)
    : IPlaybackCommandSink, IPlaybackLyricsSelectionSink, IPlaybackIdentityNavigationSink
{
    public bool Supports(string action, ListenPlaybackSnapshot snapshot) => PlaybackCommandCapabilities.Supports(action, snapshot);

    public Task<ListenPlaybackCommandReplyDto?> SelectLyricsAsync(ListenPlaybackSnapshot snapshot, Guid trackId,
        CancellationToken ct = default) => SendCoreAsync(snapshot, new ListenPlaybackCommandDto
        { Action = ListenPlaybackPresentationActions.SelectLyrics, LyricTrackId = trackId }, ct);

    public Task<ListenPlaybackCommandReplyDto?> NavigateIdentityAsync(ListenPlaybackSnapshot snapshot, string kind, Guid id,
        CancellationToken ct = default) => SendCoreAsync(snapshot, new ListenPlaybackCommandDto
        { Action = ListenPlaybackPresentationActions.NavigateIdentity, IdentityKind = kind, IdentityId = id }, ct);

    public Task<ListenPlaybackCommandReplyDto?> SendAsync(ListenPlaybackSnapshot snapshot,
        ListenPlaybackCommandDto command, CancellationToken ct = default) => Supports(command.Action, snapshot)
        ? SendCoreAsync(snapshot, command, ct) : Task.FromResult<ListenPlaybackCommandReplyDto?>(null);

    private async Task<ListenPlaybackCommandReplyDto?> SendCoreAsync(ListenPlaybackSnapshot snapshot,
        ListenPlaybackCommandDto command, CancellationToken ct = default)
    {
        if (ownerRecipientId == Guid.Empty || senderId == Guid.Empty) return null;
        var current = snapshot.CurrentIndex >= 0 && snapshot.CurrentIndex < snapshot.Queue.Count ? snapshot.Queue[snapshot.CurrentIndex] : null;
        var bookmark = PlaybackCommandCapabilities.IsBookmark(command.Action);
        var targetId = command.QueueEntryId;
        if (command.Action is ListenPlaybackCommandActions.PlayIndex or ListenPlaybackCommandActions.RemoveUpcoming
            && targetId is null && command.Index is int index && index >= 0 && index < snapshot.Queue.Count)
            targetId = snapshot.Queue[index].QueueEntryId;

        // Bookmark drafts keep the captured lease and generation supplied by their existing bridge.
        var request = command with
        {
            CommandId = command.CommandId == Guid.Empty ? Guid.NewGuid() : command.CommandId,
            SenderId = senderId,
            RecipientId = ownerRecipientId,
            ProfileId = bookmark ? command.ProfileId : snapshot.ProfileId,
            WorkId = bookmark ? command.WorkId : command.Action == ListenPlaybackCommandActions.SetSleepTimer
                ? current?.AudiobookWorkId ?? current?.WorkId : current?.WorkId,
            ExpectedAssetId = bookmark ? command.ExpectedAssetId : current?.AssetId,
            ExpectedPlaybackRequestVersion = bookmark ? command.ExpectedPlaybackRequestVersion : snapshot.PlaybackRequestVersion,
            QueueEntryId = targetId,
        };
        var reply = await channel.SendAsync(ownerRecipientId, request, ct).ConfigureAwait(false);
        return reply is not null && reply.CommandId == request.CommandId && reply.RecipientId == senderId ? reply : null;
    }
}

public sealed class DirectPlaybackCommandSink(ListenPlaybackCommandOwner owner)
    : PlaybackCommandSink(owner.RecipientId, owner.RecipientId, new OwnerPlaybackCommandChannel(owner)) { }

public sealed class BroadcastPlaybackCommandSink(Guid ownerRecipientId, Guid senderId, IListenPlaybackCommandChannel channel)
    : PlaybackCommandSink(ownerRecipientId, senderId, channel) { }

/// <summary>Direct transport dispatches exactly the same envelope as a remote host.</summary>
public sealed class OwnerPlaybackCommandChannel(ListenPlaybackCommandOwner owner) : IListenPlaybackCommandChannel
{
    public Task<ListenPlaybackCommandReplyDto?> SendAsync(Guid ownerRecipientId, ListenPlaybackCommandDto command,
        CancellationToken ct = default) => ownerRecipientId == owner.RecipientId
        ? owner.HandleAsync(command, ct) : Task.FromResult<ListenPlaybackCommandReplyDto?>(null);
}

/// <summary>Shared panels/full players consume this projection, never a media element or a controller.</summary>
public sealed record PlaybackPresentationInput(ListenPlaybackSnapshot Snapshot, IPlaybackCommandSink Commands);

/// <summary>Host-local presentation callbacks. Only a phone host supplies Collapse; popups omit it.</summary>
public sealed record PlaybackPresentationCallbacks(
    Func<string, Task>? SelectTab = null,
    Func<string?, Task>? SelectTool = null,
    Func<Task>? Collapse = null);
