using Bunit;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Editing Mode drawer is the existing editor shell re-housed beside the page: same sections, a three-tab strip,
/// no History, opened on the tab or artwork slot the button asked for, closing with X, Esc or Done.
/// </summary>
public sealed class EditingDrawerTests : AsyncBunitContext
{
    private static readonly Guid MovieId = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000001");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<(string Tab, string? Slot)> _builds = [];

    public EditingDrawerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(Context()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemHistoryWithStatusAsync), _ =>
                Task.FromResult((new List<MediaEngine.Web.Models.ViewDTOs.LibraryItemHistoryDto>(), (string?)null)));
        });
        Services.AddSingleton(api);
        Services.AddSingleton(serviceProvider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(serviceProvider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api),
            new ConfigurationBuilder().Build(),
            serviceProvider.GetRequiredService<ILogger<UIOrchestratorService>>()));
        Services.AddSingleton(_ => new ProviderCatalogueService(api, new MemoryCache(new MemoryCacheOptions())));
        Render<AppPopoverHost>();
    }

    private async Task<(EditingModeContext Mode, IRenderedComponent<EditingDrawer> Drawer)> OpenAsync(EditingDrawerTab tab, string? slot = null)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var session = new ItemFieldEditSession(Services.GetRequiredService<IEngineApiClient>(), _device);
        var mode = new EditingModeContext(_device, session) { HasPermission = true };
        mode.Loader = () => session.LoadAsync(MovieId, "Movies", "movie", null);
        Assert.True(await mode.EnterAsync());

        var drawer = Render<EditingDrawer>(parameters => parameters
            .Add(component => component.Mode, mode)
            .Add(component => component.ItemTitle, "Dune: Part One")
            .Add(component => component.BuildRequest, (requestedTab, requestedSlot) =>
            {
                _builds.Add((requestedTab, requestedSlot));
                return new MediaEditorLaunchRequest
                {
                    EntityIds = [MovieId],
                    LaunchEntityId = MovieId,
                    LaunchEntityKind = "Work",
                    MediaType = "Movies",
                    InitialScope = "movie",
                    InitialTab = requestedTab,
                    InitialArtworkSlot = requestedSlot,
                    HeaderTitle = "Dune: Part One",
                };
            }));
        Assert.Empty(drawer.FindAll(".tl-editing-drawer"));

        await drawer.InvokeAsync(() => mode.OpenDrawer(tab, slot));
        drawer.WaitForAssertion(() => Assert.NotEmpty(drawer.FindAll(".sme-section-nav--drawer button")));
        return (mode, drawer);
    }

    [Theory]
    [InlineData(EditingDrawerTab.Details, "details", "Details")]
    [InlineData(EditingDrawerTab.Artwork, "artwork", "Artwork")]
    [InlineData(EditingDrawerTab.Match, "links", "Match and Identity")]
    public async Task EachButtonOpensTheExistingEditorOnItsTabWithoutHistory(EditingDrawerTab tab, string builtTab, string activeLabel)
    {
        var (mode, drawer) = await OpenAsync(tab);

        Assert.Equal((builtTab, (string?)null), Assert.Single(_builds));
        Assert.Equal(["Details", "Artwork", "Match and Identity"],
            drawer.FindAll(".sme-section-nav--drawer button").Select(button => button.TextContent.Trim()));
        Assert.DoesNotContain("History", drawer.Find(".sme-section-nav--drawer").TextContent, StringComparison.Ordinal);
        var active = drawer.Find(".sme-section-nav--drawer button[aria-current='page']");
        Assert.Contains(activeLabel, active.TextContent);
        Assert.NotEmpty(drawer.FindAll(".sme-shell--drawer"));
        Assert.Empty(drawer.FindAll(".sme-header"));
        Assert.Equal("Editing Dune: Part One", drawer.Find(".tl-editing-drawer__heading h2").TextContent.Trim());
        Assert.True(mode.DrawerOpen);
    }

    [Fact]
    public async Task DrawerNamesWhatIsBeingEditedAndIsALabelledComplementaryRegion()
    {
        var (_, drawer) = await OpenAsync(EditingDrawerTab.Details);

        var aside = drawer.Find("aside.tl-editing-drawer");
        Assert.Equal("complementary", aside.GetAttribute("role"));
        Assert.Equal("tl-editing-drawer-title", aside.GetAttribute("aria-labelledby"));
        Assert.Equal("tl-editing-drawer-title", drawer.Find("h2").Id);
        Assert.Equal("Details", drawer.Find(".tl-editing-drawer__heading p").TextContent.Trim());
    }

    [Fact]
    public async Task ArtworkButtonsOpenTheArtworkTabOnTheRequestedSlot()
    {
        var (mode, drawer) = await OpenAsync(EditingDrawerTab.Artwork, "Logo");

        Assert.Equal(("artwork", (string?)"Logo"), Assert.Single(_builds));
        Assert.Contains("Artwork", drawer.Find(".sme-section-nav--drawer button[aria-current='page']").TextContent);

        // A second request while the drawer is open reaches the same editor through parameters, not a new session.
        await drawer.InvokeAsync(() => mode.OpenDrawer(EditingDrawerTab.Match));
        drawer.WaitForAssertion(() => Assert.Contains("Match", drawer.Find(".sme-section-nav--drawer button[aria-current='page']").TextContent));
        Assert.Single(_builds);
    }

    [Fact]
    public async Task EscapeClosesTheDrawerAndTheCloseButtonDoes()
    {
        var (mode, drawer) = await OpenAsync(EditingDrawerTab.Details);

        await drawer.Find("aside.tl-editing-drawer").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        drawer.WaitForAssertion(() => Assert.False(mode.DrawerOpen));
        Assert.Empty(drawer.FindAll(".tl-editing-drawer"));

        await drawer.InvokeAsync(() => mode.OpenDrawer(EditingDrawerTab.Details));
        drawer.WaitForAssertion(() => Assert.NotEmpty(drawer.FindAll(".tl-editing-drawer__close")));
        await drawer.Find("button[aria-label='Close editor drawer']").ClickAsync(new());
        drawer.WaitForAssertion(() => Assert.False(mode.DrawerOpen));
        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "tuvimaFocusReturn.remember");
        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "tuvimaFocusReturn.restore");
    }

    [Fact]
    public async Task EscapeClosesAnOpenFieldFirstAndTheDrawerOnlyOnTheNextPress()
    {
        var (mode, drawer) = await OpenAsync(EditingDrawerTab.Details);
        drawer.WaitForAssertion(() => Assert.NotEmpty(drawer.FindAll("button[aria-label^='Edit ']")));

        // Opening a field marks its row as the Escape owner and stops the key before it reaches the drawer.
        drawer.FindAll("button[aria-label^='Edit ']").First().Click();
        drawer.WaitForAssertion(() => Assert.NotEmpty(drawer.FindAll(".sme-details-row[data-app-escape-owner='field'] :is(input, textarea)")));
        await drawer.Find(".sme-details-row[data-app-escape-owner='field'] :is(input, textarea)").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        drawer.WaitForAssertion(() => Assert.Empty(drawer.FindAll(".sme-details-row[data-app-escape-owner='field']")));
        Assert.True(mode.DrawerOpen);

        await drawer.Find("aside.tl-editing-drawer").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        drawer.WaitForAssertion(() => Assert.False(mode.DrawerOpen));
    }

    [Fact]
    public async Task ClosingWithAnOpenFieldAsksBeforeItDiscardsAndDirtyStateReachesTheMode()
    {
        var (mode, drawer) = await OpenAsync(EditingDrawerTab.Details);
        drawer.WaitForAssertion(() => Assert.NotEmpty(drawer.FindAll("button[aria-label^='Edit ']")));
        drawer.FindAll("button[aria-label^='Edit ']").First().Click();
        drawer.WaitForAssertion(() => Assert.True(mode.DrawerDirty));

        await drawer.Find("button[aria-label='Close editor drawer']").ClickAsync(new());

        drawer.WaitForAssertion(() => Assert.Contains("Discard unsaved changes?", drawer.Markup));
        Assert.True(mode.DrawerOpen);
        await drawer.FindAll(".sme-footer button").Single(button => button.TextContent.Contains("Keep Editing")).ClickAsync(new());
        Assert.True(mode.DrawerOpen);

        await drawer.Find("button[aria-label='Close editor drawer']").ClickAsync(new());
        await drawer.FindAll(".sme-footer button").Single(button => button.TextContent.Contains("Discard")).ClickAsync(new());

        drawer.WaitForAssertion(() => Assert.False(mode.DrawerOpen));
        Assert.False(mode.DrawerDirty);
    }

    private static MediaEditorContextDto Context() => new()
    {
        LaunchEntityId = MovieId,
        MediaType = "Movies",
        InitialScope = "movie",
        AvailableTabs = ["details", "artwork", "links", "history"],
        Scopes =
        [
            new MediaEditorScopeDto
            {
                ScopeId = "movie",
                Label = "Movie",
                FieldEntityId = MovieId,
                DisplayTitle = "Dune: Part One",
                CanEditFields = true,
                CanEditArtwork = true,
                ArtworkSlots = ["CoverArt", "Background", "Logo"],
                AvailableTabs = ["details", "artwork", "links", "history"],
                FieldSnapshot = new MediaEditorScopeFieldSnapshotDto
                {
                    CanonicalFields =
                    [
                        new MediaEditorScopedFieldValueDto { Key = "title", Value = "Dune: Part One", ProviderName = "tmdb" },
                        new MediaEditorScopedFieldValueDto { Key = "description", Value = "A noble family.", ProviderName = "tmdb" },
                    ],
                    AllowedOverrideKeys = ["title", "description"],
                },
            },
        ],
    };
}
