using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class AudiobookBookmarkDialogTests : AsyncBunitContext
{
    private static readonly Guid ProfileId = Guid.NewGuid();
    private static readonly Guid WorkId = Guid.NewGuid();
    private static readonly Guid AssetId = Guid.NewGuid();
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid BookmarkId = Guid.NewGuid();

    public AudiobookBookmarkDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/audiobook-bookmark-dialog.js");
        JSInterop.SetupModule("./js/playback-tool-sheet.js");
        Services.AddLogging();
        Services.AddNativeUiServices();
    }

    [Fact]
    public async Task Add_usesTheHostCapturedPositionAndSavesTheTypedNote()
    {
        var context = CreateContext();
        var actions = new FakeActions(context, MakeDraft(context));
        var cut = RenderDialog(context, actions);

        await cut.Find("button.audiobook-bookmark-dialog__preview").ClickAsync();
        Assert.Equal(1, actions.PreviewCalls);
        Assert.Contains("00:12:43", cut.Markup);
        cut.Find("textarea").Input("Remember this passage");
        await cut.Find("form").SubmitAsync();

        Assert.Equal(0, actions.OpenCalls);
        Assert.Equal(1, actions.SaveCalls);
        Assert.Equal(19, actions.SavedGeneration);
        Assert.Equal("Remember this passage", actions.SavedNote);
        Assert.Contains("00:12:43", cut.Markup);
        Assert.Contains("Chapter 4", cut.Markup);
        Assert.Equal("Saved", cut.Find("button.audiobook-bookmark-dialog__tab.is-selected").TextContent.Trim());
    }

    [Fact]
    public async Task UnknownSaveOutcomeRequiresReloadBeforeAnotherSave()
    {
        var context = CreateContext();
        var actions = new FakeActions(context, MakeDraft(context)) { SaveResult = AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Unknown("Connection lost.") };
        var cut = RenderDialog(context, actions);

        await cut.Find("form").SubmitAsync();

        Assert.Contains("Reload Saved", cut.Markup);
        Assert.True(cut.Find("button.audiobook-bookmark-dialog__save").HasAttribute("disabled"));
        await cut.Find("button.audiobook-bookmark-dialog__secondary").ClickAsync();
        Assert.Equal(1, actions.LoadCalls);
        Assert.False(cut.Find("button.audiobook-bookmark-dialog__save").HasAttribute("disabled"));
    }

    [Fact]
    public async Task PendingSaveBlocksDoubleSubmitAndStaleCompletionCannotReplaceNewContext()
    {
        var firstContext = CreateContext();
        var firstActions = new FakeActions(firstContext, MakeDraft(firstContext))
        {
            PendingSave = new TaskCompletionSource<AudiobookBookmarkOperationResult<AudiobookBookmarkDto>>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var cut = RenderDialog(firstContext, firstActions);
        var firstSave = cut.Find("form").SubmitAsync();
        await cut.Find("form").SubmitAsync();
        Assert.Equal(1, firstActions.SaveCalls);

        var secondContext = CreateContext();
        var secondActions = new FakeActions(secondContext, MakeDraft(secondContext));
        await cut.InvokeAsync(() => cut.Instance.SetParametersAsync(ParameterView.FromDictionary(
            new Dictionary<string, object?>
            {
                [nameof(AudiobookBookmarkDialog.Context)] = secondContext,
                [nameof(AudiobookBookmarkDialog.Actions)] = secondActions,
                [nameof(AudiobookBookmarkDialog.AuthorizedAssetIds)] = new HashSet<Guid> { AssetId },
                [nameof(AudiobookBookmarkDialog.WorkTitle)] = "A different audiobook",
                [nameof(AudiobookBookmarkDialog.IsMobile)] = true,
                [nameof(AudiobookBookmarkDialog.OnClose)] = EventCallback.Factory.Create(this, () => { }),
            })));
        firstActions.PendingSave.SetResult(AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Succeeded(MakeBookmark()));
        await firstSave;

        Assert.Equal(0, secondActions.SaveCalls);
        Assert.Contains("A different audiobook", cut.Markup);
        Assert.Equal("Add", cut.Find("button.audiobook-bookmark-dialog__tab.is-selected").TextContent.Trim());
        Assert.DoesNotContain("Bookmark saved.", cut.Markup);
    }

    [Fact]
    public async Task SavedRowsUseRealReplayAndInlineDeleteConfirmation()
    {
        var context = CreateContext();
        var bookmark = MakeBookmark();
        var actions = new FakeActions(context, MakeDraft(context), [bookmark]);
        var cut = RenderDialog(context, actions);
        await cut.Find("button.audiobook-bookmark-dialog__tab.is-selected ~ button").ClickAsync();

        Assert.Single(cut.FindAll("[data-bookmark-swipe-row]"));
        Assert.Contains("Read bookmark note", cut.Markup);
        await cut.Find("button.audiobook-bookmark-dialog__delete-request").ClickAsync();
        Assert.Contains("Confirm bookmark deletion", cut.Markup);
        await cut.Find("button.audiobook-bookmark-dialog__tab:not(.is-selected)").ClickAsync();
        Assert.Equal(1, actions.CancelDeleteCalls);
        Assert.DoesNotContain("Confirm bookmark deletion", cut.Markup);
        await cut.Find("button.audiobook-bookmark-dialog__tab:not(.is-selected)").ClickAsync();
        await cut.Find("button.audiobook-bookmark-dialog__delete-request").ClickAsync();
        await cut.Find("button.audiobook-bookmark-dialog__cancel-delete").ClickAsync();
        Assert.Equal(2, actions.CancelDeleteCalls);
        Assert.DoesNotContain("Confirm bookmark deletion", cut.Markup);

        await cut.Find("button.audiobook-bookmark-dialog__delete-request").ClickAsync();
        await cut.Find("button.audiobook-bookmark-dialog__confirm-delete").ClickAsync();
        Assert.Equal(1, actions.ConfirmDeleteCalls);
        Assert.Empty(cut.FindAll("[data-bookmark-swipe-row]"));
    }

    [Fact]
    public async Task SuccessfulReplayClosesTheDialogAndNoteIsPlainPrewrappedText()
    {
        var context = CreateContext();
        var bookmark = MakeBookmark() with { Note = "first line\nsecond line" };
        var actions = new FakeActions(context, MakeDraft(context), [bookmark]);
        var closeCalls = 0;
        var cut = RenderDialog(context, actions, () => closeCalls++);
        await cut.Find("button.audiobook-bookmark-dialog__tab.is-selected ~ button").ClickAsync();
        await cut.Find("button.audiobook-bookmark-dialog__note-toggle").ClickAsync();

        var note = cut.Find(".audiobook-bookmark-dialog__saved-note");
        Assert.Equal("first line\nsecond line", note.TextContent);

        await cut.Find("button.audiobook-bookmark-dialog__replay").ClickAsync();
        Assert.Equal(1, actions.ReplayCalls);
        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(1, closeCalls);
    }

    [Fact]
    public async Task UserCloseReleasesBookmarkLeaseOnceThenInvokesHostDismissalOnce()
    {
        var context = CreateContext();
        var actions = new FakeActions(context, MakeDraft(context));
        var hostDismissCalls = 0;
        var cut = RenderDialog(context, actions, () => hostDismissCalls++);

        await cut.Find("button.playback-tool-sheet__close").ClickAsync();

        Assert.Equal(1, actions.CloseCalls);
        Assert.Equal(1, hostDismissCalls);
    }

    private IRenderedComponent<AudiobookBookmarkDialog> RenderDialog(
        AudiobookBookmarkActionContext context,
        FakeActions actions,
        Action? onClose = null) => Render<AudiobookBookmarkDialog>(parameters => parameters
        .Add(component => component.Context, context)
        .Add(component => component.Actions, actions)
        .Add(component => component.AuthorizedAssetIds, new HashSet<Guid> { AssetId })
        .Add(component => component.WorkTitle, "The Long Way Home")
        .Add(component => component.IsMobile, true)
        .Add(component => component.OnClose, EventCallback.Factory.Create(this, onClose ?? (() => { }))));

    private static AudiobookBookmarkActionContext CreateContext() =>
        new(Guid.NewGuid(), ProfileId, WorkId, SessionId, 7, AssetId);

    private static CapturedAudiobookBookmarkDraft MakeDraft(AudiobookBookmarkActionContext context) =>
        new(19, context.ProfileId, context.WorkId, context.SessionLeaseId, AssetId, 3, "Chapter 4", 763, 3600,
            new DateTimeOffset(2026, 10, 2, 13, 30, 0, TimeSpan.Zero));

    private static AudiobookBookmarkDto MakeBookmark() => new()
    {
        Id = BookmarkId,
        ProfileId = ProfileId,
        WorkId = WorkId,
        AssetId = AssetId,
        ChapterIndex = 3,
        ChapterTitle = "Chapter 4",
        PositionSeconds = 763,
        DurationSeconds = 3600,
        Note = "A saved note",
        CreatedAt = new DateTimeOffset(2026, 10, 2, 13, 30, 0, TimeSpan.Zero),
    };

    private sealed class FakeActions(
        AudiobookBookmarkActionContext context,
        CapturedAudiobookBookmarkDraft draft,
        IReadOnlyList<AudiobookBookmarkDto>? saved = null) : IAudiobookBookmarkActions
    {
        public event Action<Guid>? Changed;
        public int OpenCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public int SavedGeneration { get; private set; }
        public string? SavedNote { get; private set; }
        public int LoadCalls { get; private set; }
        public int ReplayCalls { get; private set; }
        public int PreviewCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int CancelDeleteCalls { get; private set; }
        public int ConfirmDeleteCalls { get; private set; }
        public AudiobookBookmarkOperationResult<AudiobookBookmarkDto> SaveResult { get; set; } =
            AudiobookBookmarkOperationResult<AudiobookBookmarkDto>.Succeeded(MakeBookmark());
        public TaskCompletionSource<AudiobookBookmarkOperationResult<AudiobookBookmarkDto>>? PendingSave { get; set; }
        private List<AudiobookBookmarkDto> _saved = saved?.ToList() ?? [];
        private Guid? _deleteId;
        private bool _unknown;

        public Task OpenAsync(AudiobookBookmarkActionContext value, CancellationToken ct = default)
        {
            OpenCalls++;
            return Task.CompletedTask;
        }

        public Task<AudiobookBookmarkActionSnapshot> GetSnapshotAsync(AudiobookBookmarkActionContext value, CancellationToken ct = default) =>
            Task.FromResult(new AudiobookBookmarkActionSnapshot(_saved, _unknown ? null : draft, false, false, _deleteId,
                null, _unknown));

        public Task<AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>> LoadSavedAsync(
            AudiobookBookmarkActionContext value, IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default)
        {
            LoadCalls++;
            _unknown = false;
            Changed?.Invoke(context.DialogId);
            return Task.FromResult(AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>.Succeeded(_saved));
        }

        public async Task<AudiobookBookmarkOperationResult<AudiobookBookmarkDto>> SaveAsync(AudiobookBookmarkActionContext value,
            long draftGeneration, IReadOnlySet<Guid> authorizedAssetIds, string? note, CancellationToken ct = default)
        {
            SaveCalls++;
            SavedGeneration = (int)draftGeneration;
            SavedNote = note;
            var result = PendingSave is null ? SaveResult : await PendingSave.Task;
            if (result.Outcome == AudiobookBookmarkOperationOutcome.Success && result.Value is { } bookmark)
                _saved = [bookmark with { Note = note }];
            else if (result.Outcome == AudiobookBookmarkOperationOutcome.Unknown)
                _unknown = true;
            Changed?.Invoke(context.DialogId);
            return result;
        }

        public Task<AudiobookBookmarkOperationResult<bool>> PreviewCapturedDraftAsync(AudiobookBookmarkActionContext value,
            long draftGeneration, CancellationToken ct = default)
        {
            PreviewCalls++;
            return Task.FromResult(AudiobookBookmarkOperationResult<bool>.Succeeded(true));
        }

        public Task<AudiobookBookmarkReplayResult> ReplayAsync(AudiobookBookmarkActionContext value, Guid bookmarkId,
            IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default)
        {
            ReplayCalls++;
            return Task.FromResult(new AudiobookBookmarkReplayResult(AudiobookBookmarkOperationOutcome.Success, _saved.FirstOrDefault(item => item.Id == bookmarkId)));
        }

        public Task<bool> RequestDeleteAsync(AudiobookBookmarkActionContext value, Guid bookmarkId,
            IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default)
        {
            _deleteId = bookmarkId;
            Changed?.Invoke(context.DialogId);
            return Task.FromResult(true);
        }

        public Task<AudiobookBookmarkOperationResult<bool>> ConfirmDeleteAsync(AudiobookBookmarkActionContext value,
            IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default)
        {
            ConfirmDeleteCalls++;
            _saved.Clear();
            _deleteId = null;
            Changed?.Invoke(context.DialogId);
            return Task.FromResult(AudiobookBookmarkOperationResult<bool>.Succeeded(true));
        }

        public Task CancelDeleteAsync(AudiobookBookmarkActionContext value, CancellationToken ct = default)
        {
            CancelDeleteCalls++;
            _deleteId = null;
            Changed?.Invoke(context.DialogId);
            return Task.CompletedTask;
        }

        public Task CloseAsync(AudiobookBookmarkActionContext value, CancellationToken ct = default)
        {
            CloseCalls++;
            return Task.CompletedTask;
        }
    }
}
