using Bunit;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Artwork tool on a real detail page (universal editor spec D8, D9): the bar's Artwork tab opens the floating picker instead of
/// a panel, a picked picture shows on the page before anything is saved, and a season picture opens the labelled temporary view that
/// "Return to series view" and closing the tool take away again. The Engine is a hand-written stub; nothing is written anywhere.
/// </summary>
public sealed class DetailArtworkPickerPageTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;
    private static readonly Guid Stored = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Other = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<string> _writes = [];
    private MediaEditorContextDto _context = EditingModeTestData.MovieContext();

    public DetailArtworkPickerPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(_context));
            stub.SetHandler(nameof(IEngineApiClient.GetEntityArtworkAsync), _ => Task.FromResult<ArtworkEntityWorkspaceDto?>(Workspace()));
            foreach (var write in new[]
            {
                nameof(IEngineApiClient.LinkArtworkAssetAsync),
                nameof(IEngineApiClient.AddArtworkFromUrlAsync),
                nameof(IEngineApiClient.UploadCanonicalArtworkAsync),
                nameof(IEngineApiClient.RunEditSessionAsync),
            })
            {
                var name = write;
                stub.SetHandler(name, _ =>
                {
                    _writes.Add(name);
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
    }

    private static ArtworkEntityWorkspaceDto Workspace() => new(MovieId, "Work",
    [
        Variant(Stored, "Background", "Background", true, "stored-backdrop"),
        Variant(Other, "Background", "Background", false, "preview-backdrop"),
        Variant(Stored, "SeasonPoster", "Primary", true, "stored-season"),
        Variant(Other, "SeasonPoster", "Primary", false, "preview-season"),
    ]);

    private static ArtworkEntityVariantDto Variant(Guid asset, string slot, string role, bool current, string name) =>
        new(Guid.NewGuid(), asset, role, null, slot, current, false, $"/art/{name}/large", $"/art/{name}/small", 1920, 1080, "Wide", "tmdb", null);

    private static DetailPageViewModel Model() => new()
    {
        Id = MovieId.ToString("D"),
        EntityType = DetailEntityType.Movie,
        Title = "Dune: Part One",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        OverflowActions = [new DetailAction { Key = "edit", Label = "Edit", Icon = "edit" }],
        Artwork = new ArtworkSet
        {
            BackdropUrl = "/art/stored-backdrop/large",
            HeroArtwork = new HeroArtworkViewModel
            {
                Url = "/art/stored-backdrop/large",
                HasImage = true,
                Mode = HeroArtworkMode.BackdropWithRenderedTitle,
            },
        },
    };

    private async Task<IRenderedComponent<DetailPage>> StartEditingAsync()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var cut = Render<DetailPage>(parameters => parameters.Add(page => page.Model, Model()));
        cut.Find("button[aria-label='More actions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-bar__tool")));
        return cut;
    }

    private static void OpenArtworkTool(IRenderedComponent<DetailPage> cut)
    {
        cut.FindAll(".tl-editing-bar__tool").Single(tool => tool.TextContent.Trim() == "Artwork").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__body")));
    }

    [Fact]
    public async Task TheArtworkTab_OpensThePickerDockedInTheEditorWindow()
    {
        var cut = await StartEditingAsync();

        OpenArtworkTool(cut);

        Assert.Single(cut.FindAll(".tl-editor-window .tl-art-picker.tl-art-picker--docked"));
        Assert.Empty(cut.FindAll(".tl-editing-slot"));
        Assert.Equal("true", cut.Find(".tl-editing-bar__tool.is-open").GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task APickedBackground_ShowsOnThePageBeforeSaving_AndClosingThePickerPutsItBack()
    {
        var cut = await StartEditingAsync();
        OpenArtworkTool(cut);
        Assert.Contains("stored-backdrop", cut.Markup);

        cut.FindAll(".tl-art-picker__tabs .app-segmented-control__item").Single(tab => tab.TextContent.Trim() == "Background").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__tile")));
        cut.FindAll(".tl-art-picker__tile").Last().Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("preview-backdrop", cut.Find(".tl-detail-hero").InnerHtml);
            Assert.DoesNotContain("stored-backdrop", cut.Find(".tl-detail-hero").InnerHtml);
        });
        Assert.Empty(_writes);

        cut.Find("button.tl-art-picker__close").Click();

        cut.WaitForAssertion(() => Assert.Contains("stored-backdrop", cut.Find(".tl-detail-hero").InnerHtml));
        Assert.Empty(cut.FindAll(".tl-art-picker"));
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task AUsedBackground_StaysOnThePageAfterTheToolClosesAndCountsAsAChange()
    {
        var cut = await StartEditingAsync();
        OpenArtworkTool(cut);
        cut.FindAll(".tl-art-picker__tabs .app-segmented-control__item").Single(tab => tab.TextContent.Trim() == "Background").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__tile")));
        cut.FindAll(".tl-art-picker__tile").Last().Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find("button.tl-art-picker__use").HasAttribute("disabled")));
        cut.Find("button.tl-art-picker__use").Click();
        cut.WaitForAssertion(() => Assert.Equal("Save 1 change", cut.Find("button.tl-editing-bar__save").TextContent.Trim()));

        cut.Find("button.tl-art-picker__close").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-art-picker")));
        Assert.Contains("preview-backdrop", cut.Find(".tl-detail-hero").InnerHtml);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task ASeasonPoster_OpensTheTemporaryView_AndReturnToSeriesViewRestoresThePage()
    {
        _context = SeasonContext();
        var cut = await StartEditingAsync();

        OpenArtworkTool(cut);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-temporary-view")));
        Assert.Contains("Temporary editing view", cut.Find(".tl-temporary-view__label").TextContent);
        Assert.Contains("stored-season", cut.Find(".tl-temporary-view__poster img").GetAttribute("src"));
        Assert.Contains("tl-detail-stage--temporary", cut.Find(".tl-detail-stage").ClassName);

        cut.Find("button.tl-temporary-view__return").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-temporary-view")));
        Assert.DoesNotContain("tl-detail-stage--temporary", cut.Find(".tl-detail-stage").ClassName);
        Assert.Single(cut.FindAll(".tl-art-picker"));
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task ClosingThePicker_TakesTheTemporaryViewAway()
    {
        _context = SeasonContext();
        var cut = await StartEditingAsync();
        OpenArtworkTool(cut);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-temporary-view")));

        cut.Find("button.tl-art-picker__close").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-temporary-view")));
        Assert.Empty(cut.FindAll(".tl-art-picker"));
    }

    private static MediaEditorContextDto SeasonContext()
    {
        var context = EditingModeTestData.MovieContext();
        context.Scopes[0].ArtworkSlots = ["SeasonPoster", "SeasonThumb"];
        return context;
    }

    private sealed class NoHistory : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }
}
