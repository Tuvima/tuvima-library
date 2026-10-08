using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

public sealed class AudiobookBookmarkCommandBridgeTests
{
    [Fact]
    public async Task VerifiedReplayMayRenewPlaybackLeaseAndReleasesOnlyItsDialog()
    {
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var bookmark = new AudiobookBookmarkDto
        {
            Id = Guid.NewGuid(),
            ProfileId = profileId,
            WorkId = workId,
            AssetId = assetId,
            PositionSeconds = 42,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetAudiobookBookmarksWithOutcomeAsync), _ =>
                Task.FromResult(AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>.Succeeded([bookmark]))));
        var actions = new AudiobookBookmarkActionService(api);
        var native = new NativeOwner(assetId);
        var ownerId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        var context = new AudiobookBookmarkActionContext(Guid.NewGuid(), profileId, workId, Guid.NewGuid(), 4, assetId);
        using var dispatcher = new AudiobookBookmarkCommandDispatcher(ownerId, actions, actions, actions, native,
            new AuthoritySource(assetId));
        native.DuringReplay = async () =>
        {
            var close = await dispatcher.HandleAsync(Command(ownerId, senderId, context,
                ListenPlaybackCommandActions.CloseBookmarkDialog));
            Assert.Equal(AudiobookBookmarkOperationOutcomes.Success, close.Outcome);
        };

        var opened = await dispatcher.HandleAsync(Command(ownerId, senderId, context, ListenPlaybackCommandActions.OpenBookmarkDialog));
        Assert.Equal(AudiobookBookmarkOperationOutcomes.Success, opened.Outcome);
        var loaded = await dispatcher.HandleAsync(Command(ownerId, senderId, context, ListenPlaybackCommandActions.LoadBookmarks));
        Assert.Equal(AudiobookBookmarkOperationOutcomes.Success, loaded.Outcome);

        var replayCommand = Command(ownerId, senderId, context, ListenPlaybackCommandActions.ReplayBookmark)
            with
        { BookmarkId = bookmark.Id };
        var replay = await dispatcher.HandleAsync(replayCommand);

        Assert.Equal(AudiobookBookmarkOperationOutcomes.Success, replay.Outcome);
        Assert.Equal(bookmark.Id, replay.Bookmark?.Id);
        Assert.Equal(1, native.ReplayCalls);
        Assert.False(native.SessionIsCurrent);
        var closed = await actions.GetSnapshotAsync(context);
        Assert.Contains("no longer active", closed.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // A later message from the released sender cannot reuse the old binding.
        var staleLoad = await dispatcher.HandleAsync(Command(ownerId, senderId, context, ListenPlaybackCommandActions.LoadBookmarks));
        Assert.NotEqual(AudiobookBookmarkOperationOutcomes.Success, staleLoad.Outcome);
    }

    private static ListenPlaybackCommandDto Command(Guid ownerId, Guid senderId,
        AudiobookBookmarkActionContext context, string action) => new()
        {
            CommandId = Guid.NewGuid(),
            SenderId = senderId,
            RecipientId = ownerId,
            DialogId = context.DialogId,
            ProfileId = context.ProfileId,
            WorkId = context.WorkId,
            SessionLeaseId = context.SessionLeaseId,
            OwnerGeneration = context.OwnerGeneration,
            ExpectedAssetId = context.ExpectedAssetId,
            Action = action,
        };

    private sealed class AuthoritySource(Guid assetId) : IAudiobookBookmarkAuthoritySource
    {
        public Task<IReadOnlySet<Guid>> GetAuthorizedAssetIdsAsync(AudiobookBookmarkActionContext context,
            CancellationToken ct = default) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid> { assetId });
    }

    private sealed class NativeOwner(Guid assetId) : IAudiobookBookmarkNativeOwner
    {
        public bool SessionIsCurrent { get; private set; } = true;
        public int ReplayCalls { get; private set; }
        public Func<Task>? DuringReplay { get; set; }

        public Task<bool> IsCurrentSessionAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default) =>
            Task.FromResult(SessionIsCurrent);

        public Task<bool> IsCurrentSourceAsync(AudiobookBookmarkActionContext context, Guid expectedAssetId,
            CancellationToken ct = default) => Task.FromResult(SessionIsCurrent && expectedAssetId == assetId);

        public Task<AudiobookBookmarkCaptureObservation?> CaptureCurrentAsync(AudiobookBookmarkActionContext context,
            CancellationToken ct = default) => Task.FromResult<AudiobookBookmarkCaptureObservation?>(
            SessionIsCurrent ? new(assetId, 42, 100, null) : null);

        public async Task<AudiobookBookmarkOperationResult<bool>> ReplayBookmarkAsync(AudiobookBookmarkActionContext context,
            AudiobookBookmarkDto bookmark, CancellationToken ct = default)
        {
            ReplayCalls++;
            if (DuringReplay is not null)
            {
                await DuringReplay();
            }
            SessionIsCurrent = false; // The native owner verified and started the intended new source/session.
            return AudiobookBookmarkOperationResult<bool>.Succeeded(true);
        }

        public Task<AudiobookBookmarkOperationResult<bool>> PreviewCapturedDraftAsync(AudiobookBookmarkActionContext context,
            CapturedAudiobookBookmarkDraft draft, CancellationToken ct = default) =>
            Task.FromResult(AudiobookBookmarkOperationResult<bool>.Failed("Not used by this test."));
    }
}
