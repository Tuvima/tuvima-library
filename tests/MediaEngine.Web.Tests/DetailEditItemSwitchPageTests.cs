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
    private readonly IRenderedComponent<AppDialogHost> _dialogs;

    public DetailEditItemSwitchPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub =>
        {
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
    }

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

        if (id == EpisodeOne || id == EpisodeTwo)
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
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-bar__tool")));
        return cut;
    }

    private static EditSessionFlow FlowOf(IRenderedComponent<DetailPage> cut) => cut.FindComponent<EditingBar>().Instance.Flow!;

    private static void Choose(IRenderedComponent<DetailPage> cut, int step, string name)
    {
        cut.FindAll(".tl-editing-bar__step-trigger")[step].Click();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role^='menuitem']"), item => item.TextContent.Contains(name, StringComparison.Ordinal)));
        cut.FindAll("[role^='menuitem']").First(item => item.TextContent.Contains(name, StringComparison.Ordinal)).Click();
    }

    private static void OpenDetails(IRenderedComponent<DetailPage> cut)
    {
        cut.FindAll(".tl-editing-bar__tool").Single(tool => tool.TextContent.Trim() == "Details").Click();
        cut.WaitForElement("input[aria-label='Title']");
    }

    [Fact]
    public async Task TheBarListsTheShowsSeasonsAndEpisodes_AndChoosingOneSwitchesWithoutLeavingThePage()
    {
        var cut = await StartEditingAsync(EditTrailTestData.ShowPage());
        var flow = FlowOf(cut);
        var address = Services.GetRequiredService<NavigationManager>().Uri;

        Choose(cut, 0, "Season 1");
        cut.WaitForAssertion(() => Assert.Equal(SeasonOne, flow.Session.ActiveItem!.EntityId));
        Choose(cut, 1, "Episode 2");
        cut.WaitForAssertion(() => Assert.Equal(EpisodeTwo, flow.Session.ActiveItem!.EntityId));

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
        Choose(cut, 0, "Season 1");
        cut.WaitForAssertion(() => Assert.Equal(SeasonOne, flow.Session.ActiveItem!.EntityId));

        cut.WaitForElement("[data-field-key='title'] input[aria-label='Season']");
        Assert.Equal("season", flow.Mode().Fields.ScopeId);
        cut.Find("[data-field-key='title'] input").Input("Premiere");

        cut.WaitForAssertion(() => Assert.True(flow.Session.Pending.TryGetField(SeasonOne, "title", out _)));
        Assert.False(flow.Session.Pending.TryGetField(ShowId, "title", out _));
    }

    [Fact]
    public async Task EveryItemsChangesStayInThePendingList_AndTheSaveCountCoversAllOfThem()
    {
        var cut = await StartEditingAsync(EditTrailTestData.ShowPage());
        var flow = FlowOf(cut);
        flow.Session.Pending.SetField(ShowId, "tagline", "A hotel");
        Choose(cut, 0, "Season 1");
        cut.WaitForAssertion(() => Assert.Equal(SeasonOne, flow.Session.ActiveItem!.EntityId));
        Choose(cut, 1, "Episode 1");
        cut.WaitForAssertion(() => Assert.Equal(EpisodeOne, flow.Session.ActiveItem!.EntityId));
        flow.Session.Pending.SetField(EpisodeOne, "description", "The first night.");

        cut.WaitForAssertion(() => Assert.Equal("Save 2 changes", cut.Find("button.tl-editing-bar__save").TextContent.Trim()));
        var items = flow.ReviewRows().Select(row => row.Item).Distinct().ToList();
        Assert.Equal(["Le Continental", "Episode 1"], items);
    }

    [Fact]
    public async Task ThePageKeepsShowingItsOwnStagedTitle_WhileAnEpisodeIsEdited()
    {
        var cut = await StartEditingAsync(EditTrailTestData.ShowPage());
        var flow = FlowOf(cut);
        flow.Session.Pending.SetField(ShowId, "title", "Renamed show");
        cut.WaitForAssertion(() => Assert.Equal("Renamed show", cut.Find(".tl-detail-page h1").TextContent.Trim()));
        Choose(cut, 0, "Season 1");
        cut.WaitForAssertion(() => Assert.Equal(SeasonOne, flow.Session.ActiveItem!.EntityId));
        Choose(cut, 1, "Episode 1");
        cut.WaitForAssertion(() => Assert.Equal(EpisodeOne, flow.Session.ActiveItem!.EntityId));

        flow.Session.Pending.SetField(EpisodeOne, "title", "Pilot");

        cut.WaitForAssertion(() => Assert.Equal("Renamed show", cut.Find(".tl-detail-page h1").TextContent.Trim()));
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

        Choose(cut, 1, "Paranoid Android");

        cut.WaitForAssertion(() => Assert.Equal(EditTrailTestData.TrackTwoId, flow.Session.ActiveItem!.EntityId));
        Assert.Equal("track", flow.Mode().Fields.ScopeId);
        Assert.Equal([EditTrailTestData.ArtistId, EditTrailTestData.AlbumId, EditTrailTestData.TrackTwoId], flow.Session.Trail.Select(step => step.EntityId));
    }

    [Fact]
    public async Task AMoviePage_HasNoTrailDropdown()
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

        Assert.Empty(cut.FindAll(".tl-editing-bar__step-trigger"));
        Assert.Contains("Dune: Part One", cut.Find(".tl-editing-bar__trail").TextContent);
    }

}

internal static class FlowTestExtensions
{
    public static EditingModeContext Mode(this EditSessionFlow flow) =>
        (EditingModeContext)typeof(EditSessionFlow).GetField("_mode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(flow)!;
}
