using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A title in the details overlay that someone else deletes (details overlay spec section 4): the overlay stays open with a
/// notice, Editing Mode ends without asking "Save or discard?", and unsaved text from a pencil field is offered back with a Copy
/// button. When this page started the delete itself, the dialog's own toast and the dismiss stand, with no notice.
/// </summary>
public sealed class DetailsOverlayRemovedNoticeTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly DashboardSessionAccessor _session = new();
    private readonly List<int> _steps = [];
    private readonly List<(IReadOnlyList<Guid> Ids, string Fingerprint)> _deleteCalls = [];
    private readonly NavigationManager _nav;

    public DetailsOverlayRemovedNoticeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetItemHistoryWithStatusAsync), _ =>
                Task.FromResult((new List<MediaEngine.Web.Models.ViewDTOs.LibraryItemHistoryDto>(), (string?)null)));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryItemDeletionPreviewAsync), _ =>
                Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(new LibraryItemDeletionPreview
                {
                    Items = [new() { WorkId = MovieId, Title = "Dune: Part One", MediaType = "Movies", FileCount = 1 }],
                    Files = [new() { WorkId = MovieId, AssetId = Guid.NewGuid(), FolderLabel = "Movies", FileName = "Dune.mkv", SizeBytes = 1_073_741_824, Outcome = LibraryItemDeletionOutcomes.Delete }],
                    BytesFreed = 1_073_741_824,
                    Fingerprint = "fp-1",
                })));
            stub.SetHandler(nameof(IEngineApiClient.DeleteLibraryItemsAsync), args =>
            {
                _deleteCalls.Add(((IReadOnlyList<Guid>)args![0]!, (string)args[1]!));
                return Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Ok(
                    new LibraryItemDeletionResultDto { DeletedWorkIds = [MovieId], Titles = ["Dune: Part One"], BytesFreed = 1_073_741_824 }));
            });
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(_device);
        Services.AddSingleton(_session);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEditorLauncherService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
        Services.AddSingleton(_ => new ProviderCatalogueService(api, new MemoryCache(new MemoryCacheOptions())));
        Services.AddSingleton<IDetailsOverlayHistory>(new StepRecorder(_steps));
        Services.AddScoped<DetailsOverlayState>();
        _nav = Services.GetRequiredService<NavigationManager>();
        Render<AppPopoverHost>();
    }

    private DetailsOverlayState State => Services.GetRequiredService<DetailsOverlayState>();

    private Task UseDesktopAsync() =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

    private void SetAdministrator()
    {
        var id = Guid.NewGuid();
        _session.Set("test-session-token", id, id, Guid.NewGuid(),
            new DashboardAuthorityResponse(id, id, true, true, 1, 1, true, true, null, 1, [], [], []));
    }

    private static DetailPageViewModel Model() => new()
    {
        Id = MovieId.ToString("D"),
        EntityType = DetailEntityType.Movie,
        Title = "Dune: Part One",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        OverflowActions =
        [
            new DetailAction { Key = "edit", Label = "Edit", Icon = "edit" },
            new DetailAction { Key = "status-history", Label = "Viewing history", Icon = "history" },
        ],
    };

    private IRenderedComponent<DetailPage> RenderInOpenOverlay()
    {
        _nav.NavigateTo("/watch");
        State.RememberPage("/watch", new RouteData(typeof(BlankPage), new Dictionary<string, object?>()));
        State.Attach();
        _nav.NavigateTo($"/details/movie/{MovieId}");
        Assert.True(State.IsOpen);
        return Render<DetailPage>(parameters => parameters
            .Add(page => page.Model, Model())
            .AddCascadingValue(State));
    }

    private async Task<IRenderedComponent<DetailPage>> StartEditingAsync()
    {
        await UseDesktopAsync();
        var cut = RenderInOpenOverlay();
        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();
        cut.WaitForAssertion(() => Assert.Equal("Done", cut.Find("button.tl-editing-banner__done").TextContent.Trim()));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Edit title']")));
        return cut;
    }

    private Task RemoveFromElsewhereAsync(IRenderedComponent<DetailPage> cut) =>
        cut.InvokeAsync(() => Services.GetRequiredService<UIOrchestratorService>().RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([MovieId], [])));

    private IReadOnlyList<string> Toasts => [.. Services.GetRequiredService<AppToastService>().Toasts.Select(toast => toast.Message)];

    [Fact]
    public async Task WhenTheTitleIsRemovedWhileEditing_EditModeEndsAndNoSaveOrDiscardQuestionIsAsked()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();

        await RemoveFromElsewhereAsync(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-removed-notice")));
        Assert.Empty(cut.FindAll(".tl-editing-banner"));
        Assert.Empty(host.FindAll(".app-confirm"));
        Assert.Empty(_steps);
    }

    [Fact]
    public async Task WhenTheTitleIsRemovedWhileAFieldHoldsTypedText_TheNoticeShowsThatTextReadOnlyWithACopyButton()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        cut.Find("button[aria-label='Edit title']").Click();
        cut.Find("input[aria-label='Edit Title']").Input("Dune Messiah");

        await RemoveFromElsewhereAsync(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-removed-notice__input")));
        Assert.Equal("Dune Messiah", cut.Find(".tl-removed-notice__input-text").TextContent);
        Assert.Contains("Title", cut.Find(".tl-removed-notice__input-label").TextContent);
        Assert.Empty(cut.FindAll("input"));
        Assert.Empty(host.FindAll(".app-confirm"));
        Assert.Empty(_steps);
    }

    [Fact]
    public async Task CopyOnTheNotice_PutsTheTypedTextOnTheClipboardAndSaysSo()
    {
        var cut = await StartEditingAsync();
        cut.Find("button[aria-label='Edit title']").Click();
        cut.Find("input[aria-label='Edit Title']").Input("Dune Messiah");
        await RemoveFromElsewhereAsync(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-removed-notice__copy")));

        cut.Find(".tl-removed-notice__copy").Click();

        cut.WaitForAssertion(() => Assert.Equal(["Copied."], Toasts));
        var copy = Assert.Single(JSInterop.Invocations["navigator.clipboard.writeText"]);
        Assert.Equal("Dune Messiah", copy.Arguments[0]);
    }

    [Fact]
    public async Task WhenCopyIsRefusedByTheBrowser_TheNoticeTellsThePersonToCopyItThemselves()
    {
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetException(new Microsoft.JSInterop.JSException("not allowed"));
        var cut = await StartEditingAsync();
        cut.Find("button[aria-label='Edit title']").Click();
        cut.Find("input[aria-label='Edit Title']").Input("Dune Messiah");
        await RemoveFromElsewhereAsync(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-removed-notice__copy")));

        cut.Find(".tl-removed-notice__copy").Click();

        cut.WaitForAssertion(() => Assert.Equal([ClipboardInterop.FailedMessage("the text")], Toasts));
    }

    [Fact]
    public async Task WhenAFieldWasOpenButUntouched_TheNoticeOffersNothingToCopy()
    {
        var cut = await StartEditingAsync();
        cut.Find("button[aria-label='Edit title']").Click();

        await RemoveFromElsewhereAsync(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-removed-notice")));
        Assert.Empty(cut.FindAll(".tl-removed-notice__input"));
    }

    [Fact]
    public async Task ClosingTheNoticeAfterEditing_DismissesTheOverlayWithoutAskingAnything()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        cut.Find("button[aria-label='Edit title']").Click();
        cut.Find("input[aria-label='Edit Title']").Input("Dune Messiah");
        await RemoveFromElsewhereAsync(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-removed-notice__close")));

        cut.Find(".tl-removed-notice__close").Click();

        cut.WaitForAssertion(() => Assert.Equal([1], _steps));
        Assert.Empty(host.FindAll(".app-confirm"));
    }

    [Fact]
    public async Task WhenThisPageStartedTheDelete_TheDialogsToastAndTheDismissStandWithNoNotice()
    {
        await UseDesktopAsync();
        SetAdministrator();
        var host = Render<AppDialogHost>();
        var cut = RenderInOpenOverlay();
        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Delete from library…").Click();
        host.WaitForElement(".delete-library__section");

        // The Engine's live "removed" event for the title arrives while the dialog is still open: it is this page's own delete.
        await RemoveFromElsewhereAsync(cut);
        host.FindAll("footer button").Single(button => button.TextContent.Trim() == "Delete from library and disk").Click();

        cut.WaitForAssertion(() => Assert.Equal([1], _steps));
        Assert.Equal([MovieId], Assert.Single(_deleteCalls).Ids);
        Assert.Equal(["Deleted \"Dune: Part One\". 1 GB freed."], Toasts);
        Assert.Empty(cut.FindAll(".tl-removed-notice"));
    }

    [Fact]
    public async Task WhenSomeoneElseDeletesTheTitleWhileThisPagesDeleteDialogIsCancelled_TheNoticeShows()
    {
        await UseDesktopAsync();
        SetAdministrator();
        var host = Render<AppDialogHost>();
        var cut = RenderInOpenOverlay();
        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Delete from library…").Click();
        host.WaitForElement(".delete-library__section");

        await RemoveFromElsewhereAsync(cut);
        host.FindAll("footer button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-removed-notice")));
        Assert.Empty(_deleteCalls);
        Assert.Empty(_steps);
    }

    private sealed class BlankPage : ComponentBase
    {
    }

    private sealed class StepRecorder(List<int> steps) : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int count)
        {
            steps.Add(count);
            return ValueTask.FromResult(true);
        }
    }
}
