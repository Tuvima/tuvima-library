using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

public sealed class AudiobookBookmarkActionServiceTests
{
    [Fact]
    public async Task CloseIsIdempotentAndStaleLeaseCannotCloseReplacementDialog()
    {
        var actions = new AudiobookBookmarkActionService(EngineApiClientStub.Create(_ => { }));
        var context = new AudiobookBookmarkActionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        var changes = 0;
        actions.Changed += _ => changes++;
        actions.Open(context);
        var openedChanges = changes;

        await actions.CloseAsync(context);
        await actions.CloseAsync(context);
        Assert.Equal(openedChanges + 1, changes);

        var replacement = context with { WorkId = Guid.NewGuid(), OwnerGeneration = 2 };
        actions.Open(replacement);
        var replacementOpenedChanges = changes;
        await actions.CloseAsync(context);

        var snapshot = await actions.GetSnapshotAsync(replacement);
        Assert.NotNull(snapshot);
        Assert.Equal(replacementOpenedChanges, changes);
    }

    [Fact]
    public async Task Save_UsesFrozenCaptureAndRequiresReloadAfterUnknownOutcomeBeforeExplicitRetry()
    {
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var sessionLeaseId = Guid.NewGuid();
        var dialogId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var context = new AudiobookBookmarkActionContext(dialogId, profileId, workId, sessionLeaseId, 1);
        var saveCalls = 0;
        CreateAudiobookBookmarkRequestDto? lastRequest = null;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetAudiobookBookmarksWithOutcomeAsync), _ =>
                Task.FromResult(AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>.Succeeded(
                [new AudiobookBookmarkDto
                {
                    Id = Guid.NewGuid(),
                    ProfileId = profileId,
                    WorkId = workId,
                    AssetId = assetId,
                    PositionSeconds = 75.25,
                    Note = "A note",
                    CreatedAt = DateTimeOffset.Parse("2020-01-01T00:00:00Z"),
                }])));
            stub.SetHandler(nameof(IEngineApiClient.CreateAudiobookBookmarkWithOutcomeAsync), args =>
            {
                saveCalls++;
                lastRequest = (CreateAudiobookBookmarkRequestDto)args![1]!;
                if (saveCalls == 1)
                {
                    return Task.FromResult(AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Unknown(
                        "Check Saved before trying again."));
                }

                return Task.FromResult(AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Succeeded(
                    new AudiobookBookmarkDto
                    {
                        Id = Guid.NewGuid(),
                        ProfileId = profileId,
                        WorkId = workId,
                        AssetId = assetId,
                        PositionSeconds = 75.25,
                        Note = "A note",
                    }));
            });
        });
        var actions = new AudiobookBookmarkActionService(api);
        actions.Open(context);

        Assert.True(actions.TryCapture(context, assetId, new HashSet<Guid> { assetId }, 75.25, 900,
            new PlaybackChapterDto
            {
                Index = 5,
                AssetId = assetId,
                Title = "Original chapter title",
                StartSeconds = 0,
                EndSeconds = 900,
            }, out var capture));
        Assert.NotNull(capture);

        var draftGeneration = actions.GetSnapshot(dialogId).Draft!.Generation;
        Assert.Equal(draftGeneration, capture!.DraftGeneration);
        var unknown = await actions.SaveAsync(context, draftGeneration,
            new HashSet<Guid> { assetId }, "A note");
        Assert.Equal(AudiobookBookmarkOperationOutcome.Unknown, unknown.Outcome);

        var blockedRetry = await actions.SaveAsync(context, draftGeneration, new HashSet<Guid> { assetId }, "A note");
        Assert.Equal(AudiobookBookmarkOperationOutcome.Unknown, blockedRetry.Outcome);
        Assert.Equal(1, saveCalls);

        await actions.LoadSavedAsync(context, new HashSet<Guid> { assetId });
        Assert.Single(actions.GetSnapshot(dialogId).Saved);
        Assert.NotNull(actions.GetSnapshot(dialogId).Draft);
        var saved = await actions.SaveAsync(context, draftGeneration, new HashSet<Guid> { assetId }, "  A note  ");

        Assert.Equal(AudiobookBookmarkOperationOutcome.Success, saved.Outcome);
        Assert.Equal(2, saveCalls);
        Assert.NotNull(lastRequest);
        Assert.Equal(assetId, lastRequest!.AssetId);
        Assert.Equal(75.25, lastRequest.PositionSeconds);
        Assert.Equal("Original chapter title", lastRequest.ChapterTitle);
        Assert.Equal("A note", lastRequest.Note);
        Assert.Null(actions.GetSnapshot(dialogId).Draft);
    }

    [Fact]
    public async Task SuccessfulCreateReplyMustMatchCapturedScopeBeforeItCanBeAdopted()
    {
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.CreateAudiobookBookmarkWithOutcomeAsync), _ =>
                Task.FromResult(AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Succeeded(
                    new AudiobookBookmarkDto
                    {
                        Id = Guid.NewGuid(),
                        ProfileId = profileId,
                        WorkId = Guid.NewGuid(),
                        AssetId = assetId,
                        PositionSeconds = 42,
                    }))));
        var actions = new AudiobookBookmarkActionService(api);
        var context = new AudiobookBookmarkActionContext(Guid.NewGuid(), profileId, workId, Guid.NewGuid(), 1);
        var authorized = new HashSet<Guid> { assetId };
        actions.Open(context);
        Assert.True(actions.TryCapture(context, assetId, authorized, 42, 100, null, out _));

        var outcome = await actions.SaveAsync(context, actions.GetSnapshot(context.DialogId).Draft!.Generation,
            authorized, null);

        Assert.Equal(AudiobookBookmarkOperationOutcome.Unknown, outcome.Outcome);
        Assert.Empty(actions.GetSnapshot(context.DialogId).Saved);
        Assert.True(actions.GetSnapshot(context.DialogId).SaveOutcomeUnknown);
        Assert.NotNull(actions.GetSnapshot(context.DialogId).Draft);
    }

    [Fact]
    public async Task LoadCompletionForReplacedDialogOwnerCannotWriteTheNewOwnerState()
    {
        var profileId = Guid.NewGuid();
        var oldWorkId = Guid.NewGuid();
        var newWorkId = Guid.NewGuid();
        var sessionLeaseId = Guid.NewGuid();
        var dialogId = Guid.NewGuid();
        var pending = new TaskCompletionSource<AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetAudiobookBookmarksWithOutcomeAsync), _ => pending.Task));
        var actions = new AudiobookBookmarkActionService(api);
        var oldContext = new AudiobookBookmarkActionContext(dialogId, profileId, oldWorkId, sessionLeaseId, 1);
        actions.Open(oldContext);

        var oldLoad = actions.LoadSavedAsync(oldContext, new HashSet<Guid>());
        var newContext = oldContext with { WorkId = newWorkId, OwnerGeneration = 2 };
        actions.Open(newContext);
        pending.SetResult(AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>.Succeeded(
        [
            new AudiobookBookmarkDto
            {
                Id = Guid.NewGuid(),
                ProfileId = profileId,
                WorkId = oldWorkId,
                AssetId = Guid.NewGuid(),
            },
        ]));

        await oldLoad;
        Assert.Empty(actions.GetSnapshot(dialogId).Saved);
        Assert.False(actions.GetSnapshot(dialogId).IsLoading);
    }

    [Fact]
    public async Task ReplayRequiresLoadedAuthorizedRowAndDeleteRequiresConfirmation()
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
            PositionSeconds = 20,
        };
        var deleteCalls = 0;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetAudiobookBookmarksWithOutcomeAsync), _ =>
                Task.FromResult(AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>.Succeeded([bookmark])));
            stub.SetHandler(nameof(IEngineApiClient.DeleteAudiobookBookmarkWithOutcomeAsync), _ =>
            {
                deleteCalls++;
                return Task.FromResult(AudiobookBookmarkOperationResult<bool>.Succeeded(true));
            });
        });
        var actions = new AudiobookBookmarkActionService(api);
        var context = new AudiobookBookmarkActionContext(Guid.NewGuid(), profileId, workId, Guid.NewGuid(), 1);
        var allowedAssets = new HashSet<Guid> { assetId };
        actions.Open(context);
        await actions.LoadSavedAsync(context, allowedAssets);

        Assert.Equal(AudiobookBookmarkOperationOutcome.Success, actions.Replay(context, bookmark.Id, allowedAssets).Outcome);
        Assert.False(actions.RequestDelete(context, bookmark.Id, new HashSet<Guid>()));
        Assert.True(actions.RequestDelete(context, bookmark.Id, allowedAssets));
        Assert.Equal(0, deleteCalls);
        actions.CancelDelete(context);
        Assert.Equal(0, deleteCalls);

        Assert.True(actions.RequestDelete(context, bookmark.Id, allowedAssets));
        var deleted = await actions.ConfirmDeleteAsync(context, allowedAssets);
        Assert.Equal(AudiobookBookmarkOperationOutcome.Success, deleted.Outcome);
        Assert.Equal(1, deleteCalls);
        Assert.Empty(actions.GetSnapshot(context.DialogId).Saved);
    }
}
