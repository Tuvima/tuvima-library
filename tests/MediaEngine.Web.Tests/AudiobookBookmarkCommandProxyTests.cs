using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class ListenPlaybackCommandActionsClientTests
{
    [Fact]
    public async Task StateReadUsesCorrelatedOwnerReplyWithoutPublishingChangedAgain()
    {
        var ownerId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var channel = new ReplyChannel((command, _) => Task.FromResult<ListenPlaybackCommandReplyDto?>(Reply(command,
            snapshot: new AudiobookBookmarkDialogSnapshotDto { Message = "Owner state" })));
        var proxy = new ListenPlaybackCommandActionsClient(ownerId, recipientId, channel);
        var context = Context();
        await proxy.OpenAsync(context);
        var changed = 0;
        proxy.Changed += _ => changed++;

        var snapshot = await proxy.GetSnapshotAsync(context);

        Assert.Equal("Owner state", snapshot.Message);
        Assert.Equal(0, changed);
        Assert.Equal(ListenPlaybackCommandActions.BookmarkDialogState, channel.LastCommand!.Action);
        Assert.Equal(recipientId, channel.LastCommand.SenderId);
        Assert.Equal(ownerId, channel.LastCommand.RecipientId);
    }

    [Fact]
    public async Task MismatchedReplyCorrelationIsRejected()
    {
        var channel = new ReplyChannel((command, _) => Task.FromResult<ListenPlaybackCommandReplyDto?>(
            command.Action == ListenPlaybackCommandActions.LoadBookmarks
                ? Reply(command) with { CommandId = Guid.NewGuid() }
                : Reply(command)));
        var proxy = new ListenPlaybackCommandActionsClient(Guid.NewGuid(), Guid.NewGuid(), channel);
        var context = Context();
        await proxy.OpenAsync(context);

        var result = await proxy.LoadSavedAsync(context, new HashSet<Guid>());

        Assert.Equal(AudiobookBookmarkOperationOutcome.DefiniteFailure, result.Outcome);
        Assert.Contains("reply did not match", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplyForReplacedDialogGenerationCannotReplaceNewOwnerSnapshot()
    {
        var pending = new TaskCompletionSource<ListenPlaybackCommandReplyDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ListenPlaybackCommandDto? pendingCommand = null;
        var channel = new ReplyChannel((command, _) =>
        {
            if (command.Action == ListenPlaybackCommandActions.LoadBookmarks)
            {
                pendingCommand = command;
                return pending.Task;
            }

            return Task.FromResult<ListenPlaybackCommandReplyDto?>(Reply(command,
                snapshot: new AudiobookBookmarkDialogSnapshotDto { Message = "Current owner" }));
        });
        var proxy = new ListenPlaybackCommandActionsClient(Guid.NewGuid(), Guid.NewGuid(), channel);
        var oldContext = Context();
        await proxy.OpenAsync(oldContext);
        var oldLoad = proxy.LoadSavedAsync(oldContext, new HashSet<Guid>());
        var newContext = oldContext with { WorkId = Guid.NewGuid(), SessionLeaseId = Guid.NewGuid(), OwnerGeneration = 2 };
        await proxy.OpenAsync(newContext);
        pending.SetResult(Reply(pendingCommand!, bookmarks:
        [new AudiobookBookmarkDto { Id = Guid.NewGuid(), WorkId = oldContext.WorkId, ProfileId = oldContext.ProfileId }]));

        var oldResult = await oldLoad;
        Assert.Equal(AudiobookBookmarkOperationOutcome.DefiniteFailure, oldResult.Outcome);

        channel.SetHandler((command, _) => Task.FromResult<ListenPlaybackCommandReplyDto?>(Reply(command)));
        var snapshot = await proxy.GetSnapshotAsync(newContext);
        Assert.Equal("Current owner", snapshot.Message);
        Assert.Empty(snapshot.Saved);
    }

    [Fact]
    public async Task ConcurrentStateReadDoesNotInvalidateDelayedSaveReply()
    {
        var saveReply = new TaskCompletionSource<ListenPlaybackCommandReplyDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ListenPlaybackCommandDto? saveCommand = null;
        var context = Context();
        var draft = new AudiobookBookmarkDraftPayloadDto
        {
            DraftGeneration = 7,
            ProfileId = context.ProfileId,
            WorkId = context.WorkId,
            SessionLeaseId = context.SessionLeaseId,
            AssetId = context.ExpectedAssetId!.Value,
            PositionSeconds = 42,
            CapturedAt = DateTimeOffset.UtcNow,
        };
        var channel = new ReplyChannel((command, _) =>
        {
            var snapshot = new AudiobookBookmarkDialogSnapshotDto
            {
                Draft = draft,
                Saved = [],
            };
            if (command.Action == ListenPlaybackCommandActions.SaveBookmarkDraft)
            {
                saveCommand = command;
                return saveReply.Task;
            }
            return Task.FromResult<ListenPlaybackCommandReplyDto?>(Reply(command, snapshot));
        });
        var proxy = new ListenPlaybackCommandActionsClient(Guid.NewGuid(), Guid.NewGuid(), channel);
        await proxy.OpenAsync(context);

        var save = proxy.SaveAsync(context, draft.DraftGeneration, new HashSet<Guid> { draft.AssetId }, "note");
        await proxy.GetSnapshotAsync(context);
        var saved = new AudiobookBookmarkDto { Id = Guid.NewGuid(), WorkId = context.WorkId, ProfileId = context.ProfileId };
        saveReply.SetResult(Reply(saveCommand!, new AudiobookBookmarkDialogSnapshotDto { Saved = [saved] }, [saved]) with { Bookmark = saved });
        var result = await save;

        Assert.Equal(AudiobookBookmarkOperationOutcome.Success, result.Outcome);
        Assert.Equal(saved.Id, result.Value?.Id);
    }

    [Fact]
    public async Task CloseIsIdempotentWhenOwnerIsGoneAndLateCloseCannotRemoveNewDialog()
    {
        var context = Context();
        var pendingClose = new TaskCompletionSource<ListenPlaybackCommandReplyDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ListenPlaybackCommandDto? closingCommand = null;
        var channel = new ReplyChannel((command, _) =>
        {
            if (command.Action == ListenPlaybackCommandActions.CloseBookmarkDialog)
            {
                closingCommand = command;
                return pendingClose.Task;
            }
            return Task.FromResult<ListenPlaybackCommandReplyDto?>(Reply(command,
                new AudiobookBookmarkDialogSnapshotDto { Message = "Replacement is active" }));
        });
        var proxy = new ListenPlaybackCommandActionsClient(Guid.NewGuid(), Guid.NewGuid(), channel);
        await proxy.OpenAsync(context);

        var closing = proxy.CloseAsync(context);
        var replacement = context with { WorkId = Guid.NewGuid(), OwnerGeneration = context.OwnerGeneration + 1 };
        await proxy.OpenAsync(replacement);
        pendingClose.SetResult(Reply(closingCommand!, new AudiobookBookmarkDialogSnapshotDto
        {
            Message = "Late old close",
        }));
        await closing;

        var current = await proxy.GetSnapshotAsync(replacement);
        Assert.Equal("Replacement is active", current.Message);

        channel.SetHandler((command, _) => Task.FromResult<ListenPlaybackCommandReplyDto?>(null));
        await proxy.CloseAsync(replacement);
        await proxy.CloseAsync(replacement);
        Assert.Contains("no longer active", (await proxy.GetSnapshotAsync(replacement)).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static AudiobookBookmarkActionContext Context() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid());

    private static ListenPlaybackCommandReplyDto Reply(ListenPlaybackCommandDto command,
        AudiobookBookmarkDialogSnapshotDto? snapshot = null,
        IReadOnlyList<AudiobookBookmarkDto>? bookmarks = null) => new()
        {
            CommandId = command.CommandId,
            RecipientId = command.SenderId,
            Outcome = AudiobookBookmarkOperationOutcomes.Success,
            BookmarkSnapshot = snapshot,
            Bookmarks = bookmarks,
        };

    private sealed class ReplyChannel(Func<ListenPlaybackCommandDto, CancellationToken, Task<ListenPlaybackCommandReplyDto?>> handler)
        : IListenPlaybackCommandChannel
    {
        private Func<ListenPlaybackCommandDto, CancellationToken, Task<ListenPlaybackCommandReplyDto?>> _handler = handler;
        public ListenPlaybackCommandDto? LastCommand { get; private set; }

        public void SetHandler(Func<ListenPlaybackCommandDto, CancellationToken, Task<ListenPlaybackCommandReplyDto?>> next) => _handler = next;

        public Task<ListenPlaybackCommandReplyDto?> SendAsync(Guid ownerRecipientId, ListenPlaybackCommandDto command,
            CancellationToken ct = default)
        {
            LastCommand = command;
            return _handler(command, ct);
        }
    }
}

