using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Delete from library on the details page: where its red entry shows (the page's "..." menu with or without Editing Mode, and the
/// editing banner's "..."; a desktop, an administrator, one title), what the handler refuses even when invoked, and how the page
/// leaves once the title is gone, here or in another window (a page inside the overlay shows a notice instead: see
/// <c>DetailsOverlayRemovalTests</c> and <c>DetailsOverlayRemovedNoticeTests</c>).
/// </summary>
public sealed class DeleteFromLibraryDetailPageTests : AsyncBunitContext
{
    private const string Label = "Delete from library…";

    private static readonly Guid MovieId = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000001");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly DashboardSessionAccessor _session = new();
    private readonly List<(IReadOnlyList<Guid> Ids, string Fingerprint)> _deleteCalls = [];
    private int _previewCalls;
    private LibraryItemDeletionResultDto _result = new() { DeletedWorkIds = [MovieId], Titles = ["Dune: Part One"], BytesFreed = 1_073_741_824 };

    private readonly IRenderedComponent<EditorPopover> _editor;

    public DeleteFromLibraryDetailPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryItemDeletionPreviewAsync), _ =>
            {
                _previewCalls++;
                return Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(new LibraryItemDeletionPreview
                {
                    Items = [new() { WorkId = MovieId, Title = "Dune: Part One", MediaType = "Movies", FileCount = 1 }],
                    Files = [new() { WorkId = MovieId, AssetId = Guid.NewGuid(), FolderLabel = "Movies", FileName = "Dune.mkv", SizeBytes = 1_073_741_824, Outcome = LibraryItemDeletionOutcomes.Delete }],
                    BytesFreed = 1_073_741_824,
                    Fingerprint = "fp-1",
                }));
            });
            stub.SetHandler(nameof(IEngineApiClient.DeleteLibraryItemsAsync), args =>
            {
                _deleteCalls.Add(((IReadOnlyList<Guid>)args![0]!, (string)args[1]!));
                return Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Ok(_result));
            });
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(_device);
        Services.AddSingleton(_session);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
        _editor = Render<EditorPopover>();
    }

    private Task UseDeviceAsync(string formFactor, bool fine = true, bool hover = true) =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", formFactor, false, FinePointer: fine, Hover: hover));

    private void SetAdministrator(bool administrator)
    {
        var id = Guid.NewGuid();
        _session.Set("test-session-token", id, id, Guid.NewGuid(),
            new DashboardAuthorityResponse(id, id, true, true, 1, 1, administrator, administrator, null, 1, [], [], []));
    }

    // The Engine offers "edit" only to a person it allows to edit; engineOffersEdit false is such a profile.
    private static DetailPageViewModel Model(DetailEntityType type = DetailEntityType.Movie, bool engineOffersEdit = true) => new()
    {
        Id = MovieId.ToString("D"),
        EntityType = type,
        Title = "Dune: Part One",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        OverflowActions =
        [
            .. (engineOffersEdit ? new[] { new DetailAction { Key = "edit", Label = "Edit", Icon = "edit" } } : Array.Empty<DetailAction>()),
            new DetailAction { Key = "status-history", Label = "Viewing history", Icon = "history" },
            .. (engineOffersEdit ? Array.Empty<DetailAction>() : [new DetailAction { Key = "add-queue", Label = "Add to queue", Icon = "queue" }]),
        ],
    };

    private IRenderedComponent<DetailPage> RenderPage(DetailEntityType type = DetailEntityType.Movie, bool engineOffersEdit = true) =>
        Render<DetailPage>(parameters => parameters.Add(page => page.Model, Model(type, engineOffersEdit)));

    private void StartEditing(IRenderedComponent<DetailPage> cut)
    {
        // Edit is an entry in the page's "..." menu; there is no standing Edit button. It opens the pop-over the layout draws.
        cut.Find("button[aria-label='More actions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();
        _editor.WaitForAssertion(() => Assert.Single(_editor.FindAll(".tl-editor-window")));
    }


    private Task InvokeActionAsync(IRenderedComponent<DetailPage> cut, string key = DetailActionKeys.DeleteFromLibrary) =>
        cut.InvokeAsync(() => cut.FindComponent<DetailHero>().Instance.OnActionSelected.InvokeAsync(new DetailAction { Key = key, Label = Label }));

    // -- Where the entry shows -----------------------------------------------------------------

    private static IReadOnlyList<AngleSharp.Dom.IElement> OpenPageMoreMenu(IRenderedComponent<DetailPage> cut)
    {
        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        return cut.FindAll("[role='menuitem']");
    }

    private static void AssertDeleteIsLastRedAndAfterADivider(IReadOnlyList<AngleSharp.Dom.IElement> items)
    {
        Assert.True(items.Count > 1, "The page menu holds its other entries as well as Delete.");
        var last = items[^1];
        Assert.Equal(Label, last.TextContent.Trim());
        Assert.Contains("app-tone--error", last.ClassList);
        Assert.Contains("app-menu-divider", last.PreviousElementSibling?.ClassName);
        Assert.Equal(1, items.Count(item => item.TextContent.Contains("Delete from library")));
    }

    [Fact]
    public async Task Entry_IsTheLastRedEntryOfThePagesOwnMoreMenuAfterADivider_WhileEditing()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var cut = RenderPage();
        StartEditing(cut);

        AssertDeleteIsLastRedAndAfterADivider(OpenPageMoreMenu(cut));
    }

    [Fact]
    public async Task Entry_IsTheLastRedEntryOfThePagesOwnMoreMenuAfterADivider_WithoutEditing()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var cut = RenderPage();

        AssertDeleteIsLastRedAndAfterADivider(OpenPageMoreMenu(cut));
        Assert.Empty(cut.FindAll("button.tl-editing-bar__more"));
    }

    [Fact]
    public async Task ChoosingTheEntryInThePagesMoreMenu_OpensTheDeleteDialogOnceWithoutEditing()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var host = Render<AppDialogHost>();
        var cut = RenderPage();
        OpenPageMoreMenu(cut);

        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == Label).Click();

        host.WaitForElement(".delete-library__section");
        Assert.Equal("Delete \"Dune: Part One\" from your library?", host.Find(".app-dialog-shell__title").TextContent.Trim());
        Assert.Single(Services.GetRequiredService<AppDialogService>().Dialogs);
        Assert.Equal(1, _previewCalls);
        Assert.Empty(cut.FindAll(".tl-editing-bar"));
    }

    [Fact]
    public async Task Entry_IsNotInThePagesMoreMenuForAnyoneTheEngineDoesNotTreatAsAnAdministrator()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(false);
        var cut = RenderPage();

        var items = OpenPageMoreMenu(cut);

        Assert.DoesNotContain(items, item => item.TextContent.Contains("Delete from library"));
    }

    [Theory]
    [InlineData("phone", true, false)]
    [InlineData("tablet", true, true)]
    public async Task Entry_IsInThePagesMoreMenuOnAPhoneOrTablet_AsTheLastRedEntry(string formFactor, bool finePointer, bool hover)
    {
        await UseDeviceAsync(formFactor, finePointer, hover);
        SetAdministrator(true);
        var cut = RenderPage();

        // A phone lists the same actions in a bottom sheet instead of a popover; either way they render inside the page.
        AssertDeleteIsLastRedAndAfterADivider(OpenPageMoreMenu(cut));
    }

    [Theory]
    [InlineData("phone", true, false)]
    [InlineData("tablet", true, true)]
    public async Task ChoosingTheEntryOnAPhoneOrTablet_OpensTheDeleteDialogOnce(string formFactor, bool finePointer, bool hover)
    {
        await UseDeviceAsync(formFactor, finePointer, hover);
        SetAdministrator(true);
        var host = Render<AppDialogHost>();
        var cut = RenderPage();
        OpenPageMoreMenu(cut);

        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == Label).Click();

        host.WaitForElement(".delete-library__section");
        Assert.Single(Services.GetRequiredService<AppDialogService>().Dialogs);
        Assert.Equal(1, _previewCalls);
    }

    [Fact]
    public async Task Entry_IsAbsentFromTheBannerWhileEditingModeIsOff()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);

        var cut = RenderPage();

        Assert.Empty(cut.FindAll("button.tl-editing-bar__more"));
    }

    [Fact]
    public async Task Entry_IsAbsentForAnyoneTheEngineDoesNotTreatAsAnAdministrator()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(false);
        var cut = RenderPage();
        StartEditing(cut);

        // With no Delete entry the banner draws no "..." at all.
        Assert.Empty(cut.FindAll("button.tl-editing-bar__more"));
        Assert.DoesNotContain(Label, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entry_IsAbsentWhenNoAuthorityHasBeenLoaded()
    {
        await UseDeviceAsync("desktop");
        var cut = RenderPage();
        StartEditing(cut);

        Assert.Empty(cut.FindAll("button.tl-editing-bar__more"));
        Assert.DoesNotContain(Label, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entry_IsAbsentWhereTheDeviceCannotEdit()
    {
        await UseDeviceAsync("tv", false, false);
        SetAdministrator(true);

        var cut = RenderPage();

        Assert.Empty(cut.FindAll(".tl-editing-bar"));
        Assert.DoesNotContain(Label, cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DetailEntityType.TvShow)]
    [InlineData(DetailEntityType.MusicAlbum)]
    [InlineData(DetailEntityType.BookSeries)]
    public async Task Entry_OnGroupPagesIsTheGroupsOwnDeleteNotTheSingleTitleOne(DetailEntityType type)
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var cut = RenderPage(type);

        // The page's "..." offers the group's own Delete entry, never the single-title wording.
        var items = OpenPageMoreMenu(cut);
        Assert.DoesNotContain(items, item => item.TextContent.Trim() == Label);
        Assert.Contains(items, item => item.TextContent.Trim().StartsWith("Delete ", StringComparison.Ordinal));
        Assert.DoesNotContain(Label, cut.Markup, StringComparison.Ordinal);
    }

    // -- The handler never trusts the button ---------------------------------------------------

    [Fact]
    public async Task Handler_RefusesWhenTheDeviceCannotEditEvenIfInvoked()
    {
        await UseDeviceAsync("tv", true, true);
        SetAdministrator(true);
        var cut = RenderPage();

        await InvokeActionAsync(cut);

        // The refusal is an alert on the page itself, never a toast.
        Assert.Equal(EditorDeviceRule.ItemMessage, cut.Find("[data-detail-notice]").TextContent.Trim());
        Assert.Empty(Services.GetRequiredService<AppDialogService>().Dialogs);
        Assert.Equal(0, _previewCalls);
    }

    [Fact]
    public async Task Handler_RefusesANonAdministratorEvenIfInvoked()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(false);
        var cut = RenderPage();
        StartEditing(cut);

        await InvokeActionAsync(cut);

        Assert.Equal("You need administrator access to delete items.", cut.Find("[data-detail-notice]").TextContent.Trim());
        Assert.Empty(Services.GetRequiredService<AppDialogService>().Dialogs);
        Assert.Equal(0, _previewCalls);
    }

    [Theory]
    [InlineData(DetailEntityType.TvShow)]
    [InlineData(DetailEntityType.MusicAlbum)]
    [InlineData(DetailEntityType.BookSeries)]
    public async Task Handler_OnAGroupPageNeverUsesTheSingleTitlePreview(DetailEntityType type)
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var cut = RenderPage(type);

        // The group dialog stays open until someone answers it, so the call is left running.
        _ = InvokeActionAsync(cut);

        Assert.Equal(0, _previewCalls);
    }

    // -- Deleting and leaving ------------------------------------------------------------------

    [Fact]
    public async Task Confirming_DeletesThePagesTitleSaysNothingAndGoesBackToWhereThePersonCameFrom()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var host = Render<AppDialogHost>();
        var cut = RenderPage();
        StartEditing(cut);

        var run = InvokeActionAsync(cut);
        host.WaitForElement(".delete-library__section");
        Assert.Equal("Delete \"Dune: Part One\" from your library?", host.Find(".app-dialog-shell__title").TextContent.Trim());
        host.FindAll("footer button").Single(button => button.TextContent.Trim() == "Delete from library and disk").Click();
        await run;

        var call = Assert.Single(_deleteCalls);
        Assert.Equal([MovieId], call.Ids);
        Assert.Equal("fp-1", call.Fingerprint);
        // Going back to the list is the confirmation: no toast, no notice.
        Assert.Empty(cut.FindAll("[data-detail-notice]"));
        var back = Assert.Single(JSInterop.Invocations["detailOrigin.back"]);
        Assert.Equal("/watch", back.Arguments[0]);
    }

    [Fact]
    public async Task Cancelling_LeavesThePageWhereItIsAndSaysNothing()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var host = Render<AppDialogHost>();
        var cut = RenderPage();
        StartEditing(cut);

        var run = InvokeActionAsync(cut);
        host.WaitForElement(".delete-library__section");
        host.FindAll("footer button").Single(button => button.TextContent.Trim() == "Cancel").Click();
        await run;

        Assert.Empty(_deleteCalls);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "detailOrigin.back");
    }

    // -- Deleted somewhere else ----------------------------------------------------------------

    [Fact]
    public async Task WhenAPageOutsideTheOverlayHasItsTitleDeletedInAnotherWindow_ItSaysSoAndGoesBack()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var cut = RenderPage();

        await cut.InvokeAsync(() => Services.GetRequiredService<UIOrchestratorService>().RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([MovieId], [])));

        // The page stays with the notice in its place (someone may have been typing); Close goes back.
        cut.WaitForAssertion(() => Assert.Contains("no longer in your library", cut.Find(".tl-removed-notice").TextContent, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "detailOrigin.back");

        cut.Find(".tl-removed-notice__close").Click();

        var back = Assert.Single(JSInterop.Invocations["detailOrigin.back"]);
        Assert.Equal("/watch", back.Arguments[0]);
    }

    [Fact]
    public async Task WhenAnotherTitleIsDeleted_ThePageStaysPut()
    {
        await UseDeviceAsync("desktop");
        var cut = RenderPage();

        await cut.InvokeAsync(() => Services.GetRequiredService<UIOrchestratorService>().RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([Guid.NewGuid()], [])));

        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "detailOrigin.back");
    }

    [Fact]
    public async Task AfterThePageIsLeft_ItNoLongerListensForTheLiveEvent()
    {
        await UseDeviceAsync("desktop");
        var cut = RenderPage();
        var orchestrator = Services.GetRequiredService<UIOrchestratorService>();
        var backingField = typeof(UIOrchestratorService).GetField(nameof(UIOrchestratorService.OnLibraryItemsRemoved), BindingFlags.Instance | BindingFlags.NonPublic)!;
        IEnumerable<Delegate> Listeners() => (backingField.GetValue(orchestrator) as Delegate)?.GetInvocationList() ?? [];

        Assert.Contains(Listeners(), listener => listener.Target is DetailPage);

        await DisposeComponentsAsync();

        Assert.DoesNotContain(Listeners(), listener => listener.Target is DetailPage);
        orchestrator.RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([MovieId], []));
        await Task.Yield();
    }

    // -- Editing works on phones and tablets (editor v6 D19); the Engine's answer still decides who may ------------

    [Theory]
    [InlineData("phone")]
    [InlineData("tablet")]
    public async Task Edit_FromThePagesMenu_OpensTheEditorOnAPhoneAndTablet(string formFactor)
    {
        await UseDeviceAsync(formFactor, false, false);
        SetAdministrator(true);
        var cut = RenderPage();

        StartEditing(cut);

        Assert.Single(_editor.FindAll(".tl-editor-window"));
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("tablet")]
    public async Task Edit_OnAPhoneOrTablet_IsAbsentAndRefusedForAProfileTheEngineDoesNotAllowToEdit(string formFactor)
    {
        await UseDeviceAsync(formFactor, false, false);
        SetAdministrator(false);
        var cut = RenderPage(engineOffersEdit: false);

        var items = OpenPageMoreMenu(cut);
        await InvokeActionAsync(cut, DetailActionKeys.Edit);

        Assert.DoesNotContain(items, item => item.TextContent.Trim() == "Edit");
        Assert.Empty(_editor.FindAll(".tl-editor-window"));
    }

    [Fact]
    public async Task Edit_IsNotInThePagesMenuOnATv()
    {
        await UseDeviceAsync("tv");
        SetAdministrator(true);
        var cut = RenderPage();

        var items = cut.FindAll(".tl-detail-secondary-actions button[aria-label='More actions']").Count > 0 ? OpenPageMoreMenu(cut) : [];

        Assert.DoesNotContain(items, item => item.TextContent.Trim() == "Edit");
        Assert.Empty(_editor.FindAll(".tl-editor-window"));
    }

    [Theory]
    [InlineData("phone", true)]
    [InlineData("tablet", true)]
    [InlineData("desktop", true)]
    [InlineData("tv", false)]
    public async Task AddToCollection_IsOfferedByTheHeroWhereTheDeviceMayEdit(string formFactor, bool expected)
    {
        await UseDeviceAsync(formFactor, false, false);
        // An adult profile (not a Kids one): everyone makes their own collections.
        var adult = TestSessions.For(kids: false, administrator: false);
        _session.Set("test-session-token", TestSessions.Account, TestSessions.Profile, Guid.NewGuid(), adult.Authority!);
        var cut = RenderPage();

        Assert.Equal(expected, cut.FindComponent<DetailHero>().Instance.CanAddToCollection);
    }

    [Fact]
    public async Task AddToCollection_IsNotOfferedToAKidsProfileOnAPhone()
    {
        await UseDeviceAsync("phone", false, false);
        var kids = TestSessions.For(kids: true, administrator: false);
        _session.Set("test-session-token", TestSessions.Account, TestSessions.Profile, Guid.NewGuid(), kids.Authority!);
        var cut = RenderPage();

        Assert.False(cut.FindComponent<DetailHero>().Instance.CanAddToCollection);
    }

    [Fact]
    public async Task Delete_OnAPhone_IsStillRefusedForSomeoneTheEngineDoesNotTreatAsAnAdministrator()
    {
        await UseDeviceAsync("phone", false, false);
        SetAdministrator(false);
        var cut = RenderPage();

        var items = OpenPageMoreMenu(cut);
        await InvokeActionAsync(cut);

        Assert.DoesNotContain(items, item => item.TextContent.Contains("Delete from library"));
        Assert.Empty(Services.GetRequiredService<AppDialogService>().Dialogs);
        Assert.Equal(0, _previewCalls);
    }

    // -- Navigation helper ---------------------------------------------------------------------

    [Theory]
    [InlineData(DetailEntityType.Movie, null, "/watch")]
    [InlineData(DetailEntityType.TvEpisode, null, "/watch")]
    [InlineData(DetailEntityType.Book, null, "/read")]
    [InlineData(DetailEntityType.ComicIssue, null, "/read")]
    [InlineData(DetailEntityType.Audiobook, null, "/listen/audiobooks")]
    [InlineData(DetailEntityType.Work, "Audiobooks", "/listen/audiobooks")]
    [InlineData(DetailEntityType.Work, "Movies", "/watch")]
    [InlineData(DetailEntityType.Work, "Books", "/read")]
    [InlineData(DetailEntityType.Work, null, "/")]
    public void TheFallbackRoute_IsTheBrowsePageForThatKindOfTitle(DetailEntityType type, string? mediaKind, string expected)
    {
        Assert.Equal(expected, MediaEngine.Web.Services.Navigation.DetailOriginInterop.FallbackRoute(type, mediaKind));
    }
}
