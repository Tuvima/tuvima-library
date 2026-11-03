using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
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

namespace MediaEngine.Web.Tests;

/// <summary>
/// Switching items on a real detail page (universal editor spec D7): a show page's bar lists its seasons and episodes, choosing one keeps the
/// page where it is, loads that item's fields into the panel, keeps every item's staged changes, leaves the page showing its own item, and
/// saves nothing. An album page does the same for its artist and tracks. The Engine is a hand-written stub; nothing is written anywhere.
/// </summary>
public sealed class DetailEditItemSwitchPageTests : AsyncBunitContext
{
    private static readonly Guid ShowId = EditTrailTestData.ShowId;
    private static readonly Guid SeasonOne = EditTrailTestData.SeasonOneId;
    private static readonly Guid EpisodeOne = EditTrailTestData.EpisodeOneId;
    private static readonly Guid EpisodeTwo = EditTrailTestData.EpisodeTwoId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<string> _writes = [];
    private readonly List<Guid> _contextAsks = [];
    private readonly List<string> _familyReads = [];
    private readonly IRenderedComponent<AppDialogHost> _dialogs;

    public DetailEditItemSwitchPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub =>
        {
            EditTrailTestData.RegisterFamily(stub, _familyReads);
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), args =>
            {
                var id = (Guid)args![0]!;
                _contextAsks.Add(id);
                return Task.FromResult<MediaEditorContextDto?>(ContextFor(id));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetItemHistoryWithStatusAsync), _ =>
                Task.FromResult((new List<LibraryItemHistoryDto>(), (string?)null)));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            foreach (var write in new[] { nameof(IEngineApiClient.SaveItemDisplayOverridesAsync), nameof(IEngineApiClient.RunEditSessionAsync) })
            {
                var name = write;
                stub.SetHandler(name, args =>
                {
                    _writes.Add(name + (args is [EditSessionRequest { PreviewOnly: true }, ..] ? ":check" : string.Empty));
                    return Task.FromResult<object?>(null);
                });
            }
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton<IDetailsOverlayHistory>(new NoHistory());
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        Services.AddScoped<DetailsOverlayState>();
        Services.AddSingleton(_device);
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
        Services.AddSingleton(_ => new ProviderCatalogueService(api, new MemoryCache(new MemoryCacheOptions())));
        Render<AppPopoverHost>();
        _dialogs = Render<AppDialogHost>();
        _editor = Render<EditorPopover>();
    }

    private readonly IRenderedComponent<EditorPopover> _editor;

    private sealed class NoHistory : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }

    private static MediaEditorContextDto? ContextFor(Guid id)
    {
        if (id == ShowId)
        {
            return EditTrailTestData.TvContext(id, "series");
        }

        if (id == SeasonOne || id == EditTrailTestData.SeasonTwoId)
        {
            return EditTrailTestData.TvContext(id, "season");
        }

        if (id == EpisodeOne || id == EpisodeTwo || id == EditTrailTestData.EpisodeThreeId)
        {
            return EditTrailTestData.TvContext(id, "episode");
        }

        if (id == EditTrailTestData.AlbumId)
        {
            return EditTrailTestData.AlbumContext(id, "album");
        }

        if (id == EditingModeTestData.MovieId)
        {
            return EditingModeTestData.MovieContext();
        }

        return id == EditTrailTestData.TrackOneId || id == EditTrailTestData.TrackTwoId ? EditTrailTestData.AlbumContext(id, "track") : null;
    }

    private async Task<IRenderedComponent<DetailPage>> StartEditingAsync(DetailPageViewModel model)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var cut = Render<DetailPage>(parameters => parameters.Add(page => page.Model, model));
        cut.Find("button[aria-label='More actions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();
        _editor.WaitForAssertion(() => Assert.NotEmpty(_editor.FindAll(".tl-editor-window__tabs [role='tab']")));
        return cut;
    }

    private EditSessionFlow FlowOf(IRenderedComponent<DetailPage> cut) => Services.GetRequiredService<EditorHost>().Flow;

    // Picks a row of the family tree (the left column); a season opens as it is picked, so its episodes can be picked next.
    private void Choose(IRenderedComponent<DetailPage> cut, string name)
    {
        _editor.WaitForAssertion(() => Assert.Contains(_editor.FindAll(".tl-family__row"), row => RowName(row) == name));
        _editor.FindAll(".tl-family__row").First(row => RowName(row) == name).Click();
    }

    private static string RowName(AngleSharp.Dom.IElement row) => row.QuerySelector(".tl-family__title")!.TextContent.Trim();

    // The pop-over opens on Details by itself.
    private void OpenDetails(IRenderedComponent<DetailPage> cut) => _editor.WaitForElement("input[aria-label='Title']");

    [Fact]
    public async Task TheTrailListsTheShowsSeasonsAndEpisodes_AndChoosingOneSwitchesWithoutLeavingThePage()
    {
        var cut = await StartEditingAsync(EditTrailTestData.ShowPage());
        var flow = FlowOf(cut);
        var address = Services.GetRequiredService<NavigationManager>().Uri;

        Choose(cut, "Season 1");
        _editor.WaitForAssertion(() => Assert.Equal(SeasonOne, flow.Session.ActiveItem!.EntityId));
        Choose(cut, "Episode 2");
        _editor.WaitForAssertion(() => Assert.Equal(EpisodeTwo, flow.Session.ActiveItem!.EntityId));

        Assert.Equal([ShowId, SeasonOne, EpisodeTwo], flow.Session.Trail.Select(step => step.EntityId));
        Assert.Equal(address, Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal("Le Continental", cut.Find(".tl-detail-page h1").TextContent.Trim());
        Assert.DoesNotContain(_writes, write => !write.EndsWith(":check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePanelReloadsForTheNewItem_AndItsFieldsStageIntoTheSameList()
    {
        var cut = await StartEditingAsync(EditTrailTestData.ShowPage());
        var flow = FlowOf(cut);
        OpenDetails(cut);
        Choose(cut, "Season 1");
        _editor.WaitForAssertion(() => Assert.Equal(SeasonOne, flow.Session.ActiveItem!.EntityId));

        _editor.WaitForElement("[data-field-key='title'] input[aria-label='Season']");
        Assert.Equal("season", flow.Mode().Fields.ScopeId);
        _editor.Find("[data-field-key='title'] input").Input("Premiere");

        _editor.WaitForAssertion(() => Assert.True(flow.Session.Pending.TryGetField(SeasonOne, "title", out _)));
        Assert.False(flow.Session.Pending.TryGetField(ShowId, "title", out _));
    }

    [Fact]
    public async Task EveryItemsChangesStayInThePendingList_AndTheSaveCountCoversAllOfThem()
    {
        var cut = await StartEditingAsync(EditTrailTestData.ShowPage());
        var flow = FlowOf(cut);
        flow.Session.Pending.SetField(ShowId, "tagline", "A hotel");
        Choose(cut, "Season 1");
        _editor.WaitForAssertion(() => Assert.Equal(SeasonOne, flow.Session.ActiveItem!.EntityId));
        Choose(cut, "Episode 1");
        _editor.WaitForAssertion(() => Assert.Equal(EpisodeOne, flow.Session.ActiveItem!.EntityId));
        flow.Session.Pending.SetField(EpisodeOne, "description", "The first night.");

        _editor.WaitForAssertion(() => Assert.Equal("Save 2 changes", _editor.Find("button.tl-editor-window__save").TextContent.Trim()));
        var items = flow.ReviewRows().Select(row => row.Item).Distinct().ToList();
        Assert.Equal(["Le Continental", "Episode 1"], items);
    }

    [Fact]
    public async Task FromAnEpisode_TheShowAnotherSeasonAndAThirdEpisodeAreReachable_AndTheSaveCountCoversAllThree()
    {
        var cut = await StartEditingAsync(EditTrailTestData.EpisodePage());
        var flow = FlowOf(cut);
        Assert.Equal(EpisodeTwo, flow.Session.ActiveItem!.EntityId);
        flow.Session.Pending.SetField(EpisodeTwo, "description", "A change on the episode I opened.");

        // The show above it.
        Choose(cut, EditTrailTestData.ShowTitle);
        _editor.WaitForAssertion(() => Assert.Equal(ShowId, flow.Session.ActiveItem!.EntityId));
        flow.Session.Pending.SetField(ShowId, "tagline", "A hotel");

        // Another season, then a third episode inside it.
        Choose(cut, "Season 2");
        _editor.WaitForAssertion(() => Assert.Equal(EditTrailTestData.SeasonTwoId, flow.Session.ActiveItem!.EntityId));
        Choose(cut, "Episode 3");
        _editor.WaitForAssertion(() => Assert.Equal(EditTrailTestData.EpisodeThreeId, flow.Session.ActiveItem!.EntityId));
        flow.Session.Pending.SetField(EditTrailTestData.EpisodeThreeId, "description", "The third one.");

        _editor.WaitForAssertion(() => Assert.Equal("Save 3 changes", _editor.Find("button.tl-editor-window__save").TextContent.Trim()));
        // Every row that has a change waiting carries the purple dot, and nothing was written on the way.
        var dotted = _editor.FindAll(".tl-family__row").Where(row => row.QuerySelector(".tl-family__dot") is not null).Select(RowName).ToList();
        Assert.Contains(EditTrailTestData.ShowTitle, dotted);
        Assert.Contains("Episode 2", dotted);
        Assert.Contains("Episode 3", dotted);
        Assert.DoesNotContain(_writes, write => !write.EndsWith(":check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePageBehindTheWindow_KeepsItsStoredTitle_WhileAnEpisodeIsEdited()
    {
        var cut = await StartEditingAsync(EditTrailTestData.ShowPage());
        var flow = FlowOf(cut);
        var storedTitle = cut.Find(".tl-detail-page h1").TextContent.Trim();
        flow.Session.Pending.SetField(ShowId, "title", "Renamed show");
        Choose(cut, "Season 1");
        _editor.WaitForAssertion(() => Assert.Equal(SeasonOne, flow.Session.ActiveItem!.EntityId));
        Choose(cut, "Episode 1");
        _editor.WaitForAssertion(() => Assert.Equal(EpisodeOne, flow.Session.ActiveItem!.EntityId));

        flow.Session.Pending.SetField(EpisodeOne, "title", "Pilot");

        Assert.Equal(storedTitle, cut.Find(".tl-detail-page h1").TextContent.Trim());
    }

    [Fact]
    public async Task ASeasonThePageHasNotOpened_ListsItsEpisodesOnceTheItemIsSwitchedTo()
    {
        var cut = await StartEditingAsync(EditTrailTestData.ShowPage());
        var flow = FlowOf(cut);

        await cut.InvokeAsync(() => flow.SwitchToAsync(EditTrailTestData.SeasonTwoId));

        // The stub answers no detail page for the season, so the season has no episodes to list; switching itself still works.
        Assert.Equal(EditTrailTestData.SeasonTwoId, flow.Session.ActiveItem!.EntityId);
        Assert.Empty(flow.Catalog.ChildrenOf(EditTrailTestData.SeasonTwoId));
    }

    [Fact]
    public async Task AnAlbumPage_SwitchesBetweenItsArtistItselfAndItsTracks()
    {
        var cut = await StartEditingAsync(EditTrailTestData.AlbumPage());
        var flow = FlowOf(cut);
        Assert.Equal([EditTrailTestData.ArtistId, EditTrailTestData.AlbumId], flow.Session.Trail.Select(step => step.EntityId));
        _editor.WaitForAssertion(() => Assert.Equal("This album", _editor.Find(".tl-family__heading").TextContent.Trim()));

        Choose(cut, "Paranoid Android");

        _editor.WaitForAssertion(() => Assert.Equal(EditTrailTestData.TrackTwoId, flow.Session.ActiveItem!.EntityId));
        Assert.Equal("track", flow.Mode().Fields.ScopeId);
        // The tree is the album's own family: the artist is not part of it.
        Assert.Equal([EditTrailTestData.AlbumId, EditTrailTestData.TrackTwoId], flow.Session.Trail.Select(step => step.EntityId));
    }

    [Fact]
    public async Task AMoviePage_HasNoFamilyColumn_AndTheToolsUseTheFullWidth()
    {
        var movie = new DetailPageViewModel
        {
            Id = EditingModeTestData.MovieId.ToString("D"),
            EntityType = DetailEntityType.Movie,
            Title = "Dune: Part One",
            Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
            OverflowActions = [new DetailAction { Key = "edit", Label = "Edit", Icon = "edit" }],
        };

        var cut = await StartEditingAsync(movie);

        Assert.Empty(_editor.FindAll(".tl-family"));
        Assert.Contains("Dune: Part One", _editor.Find(".tl-editor-popover__editing").TextContent);
    }

}

internal static class FlowTestExtensions
{
    public static EditingModeContext Mode(this EditSessionFlow flow) =>
        (EditingModeContext)typeof(EditSessionFlow).GetField("_mode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(flow)!;
}
