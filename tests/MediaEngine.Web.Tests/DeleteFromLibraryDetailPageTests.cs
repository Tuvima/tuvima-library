using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Components.Details;
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
/// Delete from library on the details page: where its red entry shows (Editing Mode, a desktop, an administrator, one title),
/// what the handler refuses even when invoked, and how the page leaves once the title is gone, here or in another window.
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
        Services.AddScoped<MediaEditorLauncherService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
    }

    private Task UseDeviceAsync(string formFactor, bool fine = true, bool hover = true) =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", formFactor, false, FinePointer: fine, Hover: hover));

    private void SetAdministrator(bool administrator)
    {
        var id = Guid.NewGuid();
        _session.Set("test-session-token", id, id, Guid.NewGuid(),
            new DashboardAuthorityResponse(id, id, true, true, 1, 1, administrator, administrator, null, 1, [], [], []));
    }

    private static DetailPageViewModel Model(DetailEntityType type = DetailEntityType.Movie) => new()
    {
        Id = MovieId.ToString("D"),
        EntityType = type,
        Title = "Dune: Part One",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        OverflowActions =
        [
            new DetailAction { Key = "edit", Label = "Edit", Icon = "edit" },
            new DetailAction { Key = "status-history", Label = "Viewing history", Icon = "history" },
        ],
    };

    private IRenderedComponent<DetailPage> RenderPage(DetailEntityType type = DetailEntityType.Movie) =>
        Render<DetailPage>(parameters => parameters.Add(page => page.Model, Model(type)));

    private static void StartEditing(IRenderedComponent<DetailPage> cut)
    {
        cut.Find("button.tl-editing-toggle").Click();
        cut.WaitForAssertion(() => Assert.Equal("Done", cut.Find("button.tl-editing-toggle").TextContent.Trim()));
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> OpenMoreMenu(IRenderedComponent<DetailPage> cut)
    {
        cut.Find("button.tl-editing-bar__more").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        return cut.FindAll("[role='menuitem']");
    }

    private IReadOnlyList<string> Toasts => [.. Services.GetRequiredService<AppToastService>().Toasts.Select(toast => toast.Message)];

    private Task InvokeActionAsync(IRenderedComponent<DetailPage> cut, string key = DetailActionKeys.DeleteFromLibrary) =>
        cut.InvokeAsync(() => cut.FindComponent<DetailHero>().Instance.OnActionSelected.InvokeAsync(new DetailAction { Key = key, Label = Label }));

    // -- Where the entry shows -----------------------------------------------------------------

    [Fact]
    public async Task Entry_IsLastInTheEditingMoreMenuAfterADividerAndInTheDestructiveTone()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var cut = RenderPage();
        StartEditing(cut);

        var items = OpenMoreMenu(cut);

        var last = items[^1];
        Assert.Equal(Label, last.TextContent.Trim());
        Assert.Contains("app-tone--error", last.ClassList);
        Assert.Equal("hr", last.PreviousElementSibling?.TagName.ToLowerInvariant());
        Assert.Contains("app-menu-divider", last.PreviousElementSibling!.ClassList);
        Assert.Equal(1, items.Count(item => item.TextContent.Contains("Delete from library")));
        Assert.Contains(items, item => item.TextContent.Trim() == "Viewing history");
    }

    [Fact]
    public async Task Entry_IsAbsentWhileEditingModeIsOff()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);

        var cut = RenderPage();

        Assert.DoesNotContain(Label, cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("button.tl-editing-bar__more"));
    }

    [Fact]
    public async Task Entry_IsAbsentForAnyoneTheEngineDoesNotTreatAsAnAdministrator()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(false);
        var cut = RenderPage();
        StartEditing(cut);

        var items = OpenMoreMenu(cut);

        Assert.DoesNotContain(items, item => item.TextContent.Contains("Delete from library"));
    }

    [Fact]
    public async Task Entry_IsAbsentWhenNoAuthorityHasBeenLoaded()
    {
        await UseDeviceAsync("desktop");
        var cut = RenderPage();
        StartEditing(cut);

        var items = OpenMoreMenu(cut);

        Assert.DoesNotContain(items, item => item.TextContent.Contains("Delete from library"));
    }

    [Theory]
    [InlineData("phone", true, false)]
    [InlineData("tablet", true, true)]
    [InlineData("tv", false, false)]
    [InlineData("desktop", false, false)]
    [InlineData("desktop", true, false)]
    public async Task Entry_IsAbsentWhereTheDeviceCannotEdit(string formFactor, bool finePointer, bool hover)
    {
        await UseDeviceAsync(formFactor, finePointer, hover);
        SetAdministrator(true);

        var cut = RenderPage();

        Assert.Empty(cut.FindAll("button.tl-editing-toggle"));
        Assert.DoesNotContain(Label, cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DetailEntityType.TvShow)]
    [InlineData(DetailEntityType.TvSeason)]
    [InlineData(DetailEntityType.MusicAlbum)]
    [InlineData(DetailEntityType.BookSeries)]
    public async Task Entry_IsAbsentOnGroupPagesBecauseOnlySingleTitlesAreDeleted(DetailEntityType type)
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var cut = RenderPage(type);
        if (cut.FindAll("button.tl-editing-toggle").Count == 0)
        {
            return;
        }

        StartEditing(cut);
        var items = OpenMoreMenu(cut);

        Assert.DoesNotContain(items, item => item.TextContent.Contains("Delete from library"));
    }

    // -- The handler never trusts the button ---------------------------------------------------

    [Theory]
    [InlineData("phone", true, false)]
    [InlineData("tablet", true, true)]
    [InlineData("desktop", false, false)]
    public async Task Handler_RefusesWhenTheDeviceCannotEditEvenIfInvoked(string formFactor, bool finePointer, bool hover)
    {
        await UseDeviceAsync(formFactor, finePointer, hover);
        SetAdministrator(true);
        var cut = RenderPage();

        await InvokeActionAsync(cut);

        Assert.Equal(EditorDeviceRule.Message, Assert.Single(Toasts));
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

        Assert.Equal("You need administrator access to delete items.", Assert.Single(Toasts));
        Assert.Empty(Services.GetRequiredService<AppDialogService>().Dialogs);
        Assert.Equal(0, _previewCalls);
    }

    [Fact]
    public async Task Handler_DoesNothingOutsideEditingMode()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var cut = RenderPage();

        await InvokeActionAsync(cut);

        Assert.Empty(Services.GetRequiredService<AppDialogService>().Dialogs);
        Assert.Equal(0, _previewCalls);
    }

    // -- Deleting and leaving ------------------------------------------------------------------

    [Fact]
    public async Task Confirming_DeletesThePagesTitleShowsTheToastAndGoesBackToWhereThePersonCameFrom()
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
        Assert.Equal(["Deleted \"Dune: Part One\". 1 GB freed."], Toasts);
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
        Assert.Empty(Toasts);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "detailOrigin.back");
    }

    // -- Deleted somewhere else ----------------------------------------------------------------

    [Fact]
    public async Task WhenTheTitleIsDeletedInAnotherWindow_ThePageSaysSoAndGoesBack()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var cut = RenderPage();

        await cut.InvokeAsync(() => Services.GetRequiredService<UIOrchestratorService>().RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([MovieId], [])));

        cut.WaitForAssertion(() => Assert.Equal(["This item is no longer in your library."], Toasts));
        var back = Assert.Single(JSInterop.Invocations["detailOrigin.back"]);
        Assert.Equal("/watch", back.Arguments[0]);
    }

    [Fact]
    public async Task WhenAnotherTitleIsDeleted_ThePageStaysPut()
    {
        await UseDeviceAsync("desktop");
        var cut = RenderPage();

        await cut.InvokeAsync(() => Services.GetRequiredService<UIOrchestratorService>().RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([Guid.NewGuid()], [])));

        Assert.Empty(Toasts);
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
        Assert.Empty(Toasts);
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
