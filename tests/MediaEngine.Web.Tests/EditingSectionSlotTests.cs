using Bunit;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Editing Mode section slot (details overlay spec D3, D7) is the existing editor shell hosted in line below the hero: one
/// section at a time (Artwork, Match and Identity or History), no header and no section rail, History read-only, opened on the
/// artwork slot a chip asked for, and closed by its button, the banner button again, Esc or Done. There is no drawer.
/// </summary>
public sealed class EditingSectionSlotTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<(string Section, string? Slot)> _builds = [];
    private readonly Queue<SaveOrDiscardChoice> _answers = new();
    private readonly List<Dictionary<string, string>> _overrideSaves = [];
    private int _asked;
    private int _committed;
    private bool _buildReturnsNull;

    public EditingSectionSlotTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.SaveItemDisplayOverridesAsync), args =>
            {
                _overrideSaves.Add((Dictionary<string, string>)args![1]!);
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetItemHistoryWithStatusAsync), _ =>
                Task.FromResult((new List<LibraryItemHistoryDto>
                {
                    new()
                    {
                        Id = "h1",
                        EntityId = MovieId,
                        OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                        EventType = "metadata_updated",
                        Label = "Title updated",
                        Detail = "From the provider",
                    },
                }, (string?)null)));
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

    private async Task<(EditingModeContext Mode, IRenderedComponent<EditingSectionSlot> Slot)> StartAsync(bool handlesEscape = false)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var session = new ItemFieldEditSession(Services.GetRequiredService<IEngineApiClient>(), _device);
        var mode = new EditingModeContext(_device, session)
        {
            HasPermission = true,
            AskSaveOrDiscard = () =>
            {
                _asked++;
                return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : SaveOrDiscardChoice.Cancel);
            },
        };
        mode.Loader = () => session.LoadAsync(MovieId, "Movies", "movie", null);
        Assert.True(await mode.EnterAsync());

        var slot = Render<EditingSectionSlot>(parameters => parameters
            .Add(component => component.Mode, mode)
            .Add(component => component.HandlesEscape, handlesEscape)
            .Add(component => component.OnCommitted, EventCallback.Factory.Create(this, () => _committed++))
            .Add(component => component.BuildRequest, (section, slotName) =>
            {
                _builds.Add((section, slotName));
                if (_buildReturnsNull)
                {
                    return null;
                }

                return new MediaEditorLaunchRequest
                {
                    EntityIds = [MovieId],
                    LaunchEntityId = MovieId,
                    LaunchEntityKind = "Work",
                    MediaType = "Movies",
                    InitialScope = "movie",
                    InitialTab = section,
                    InitialArtworkSlot = slotName,
                    HeaderTitle = "Dune: Part One",
                };
            }));
        Assert.Empty(slot.FindAll(".tl-editing-slot"));
        return (mode, slot);
    }

    [Theory]
    [InlineData(EditingSection.Artwork, "artwork", "Manage Artwork", ".sme-artwork")]
    [InlineData(EditingSection.Match, "links", "Match and Identity", ".sme-match-tab")]
    [InlineData(EditingSection.History, "history", "History", ".sme-history-tab")]
    public async Task EachSectionOpensTheExistingEditorWithOnlyThatSectionAndNoRailOrHeader(
        EditingSection section, string builtSection, string heading, string marker)
    {
        var (mode, slot) = await StartAsync();

        await slot.InvokeAsync(() => mode.OpenSection(section));
        slot.WaitForAssertion(() => Assert.NotEmpty(slot.FindAll(marker)));

        Assert.Equal((builtSection, (string?)null), Assert.Single(_builds));
        Assert.Equal(heading, slot.Find(".tl-editing-slot__title").TextContent.Trim());
        Assert.Equal(builtSection, slot.Find(".tl-editing-slot").GetAttribute("data-section"));
        Assert.NotEmpty(slot.FindAll(".sme-shell--section"));
        Assert.Empty(slot.FindAll(".sme-section-nav"));
        Assert.Empty(slot.FindAll(".sme-header"));
        Assert.DoesNotContain("tl-editing-drawer", slot.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSlotIsALabelledRegionNamedByItsHeading()
    {
        var (mode, slot) = await StartAsync();

        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.History));
        slot.WaitForAssertion(() => Assert.NotEmpty(slot.FindAll(".tl-editing-slot")));

        var region = slot.Find("section.tl-editing-slot");
        Assert.Equal("tl-editing-slot-title", region.GetAttribute("aria-labelledby"));
        Assert.Equal("tl-editing-slot-title", slot.Find("h2").Id);
    }

    [Fact]
    public async Task HistoryShowsTheTimelineReadOnlyWithNothingToChangeOrSave()
    {
        var (mode, slot) = await StartAsync();

        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.History));
        slot.WaitForAssertion(() => Assert.NotEmpty(slot.FindAll(".sme-history-tab")));

        slot.WaitForAssertion(() => Assert.Contains("Title updated", slot.Find(".sme-history-tab").TextContent));
        Assert.Empty(slot.FindAll(".sme-history-tab input:not([type='hidden']), .sme-history-tab textarea"));
        Assert.Empty(slot.FindAll(".sme-footer"));
        Assert.False(mode.SectionDirty);
    }

    [Fact]
    public async Task OnlyOneSectionIsInTheSlotAtATime()
    {
        var (mode, slot) = await StartAsync();

        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.Artwork));
        slot.WaitForAssertion(() => Assert.NotEmpty(slot.FindAll(".sme-artwork")));
        await slot.InvokeAsync(() => mode.ShowSectionAsync(EditingSection.History));
        slot.WaitForAssertion(() => Assert.NotEmpty(slot.FindAll(".sme-history-tab")));

        Assert.Single(slot.FindAll(".tl-editing-slot"));
        Assert.Empty(slot.FindAll(".sme-artwork"));
        Assert.Equal([("artwork", (string?)null), ("history", (string?)null)], _builds);
    }

    [Fact]
    public async Task AnArtworkChipOpensTheArtworkSectionOnTheRightSlotAndAskingAgainUsesTheSameEditor()
    {
        var (mode, slot) = await StartAsync();

        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.Artwork, "Logo"));
        slot.WaitForAssertion(() => Assert.NotEmpty(slot.FindAll(".sme-artwork")));
        Assert.Equal(("artwork", (string?)"Logo"), Assert.Single(_builds));

        await slot.InvokeAsync(() => mode.ShowSectionAsync(EditingSection.Artwork, "Background"));
        slot.WaitForAssertion(() => Assert.Equal("Background", mode.OpenSectionArtworkSlot));

        Assert.Single(_builds);
        Assert.Equal(EditingSection.Artwork, mode.OpenSectionKind);
    }

    [Fact]
    public async Task TheCloseButtonClosesTheSectionAndFocusGoesBackToWhatOpenedIt()
    {
        var (mode, slot) = await StartAsync();
        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.History));
        slot.WaitForAssertion(() => Assert.NotEmpty(slot.FindAll("button.tl-editing-slot__close")));

        await slot.Find("button[aria-label='Close History']").ClickAsync(new());

        slot.WaitForAssertion(() => Assert.False(mode.SectionOpen));
        Assert.Empty(slot.FindAll(".tl-editing-slot"));
        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "tuvimaFocusReturn.remember");
        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "tuvimaFocusReturn.restore");
        Assert.Equal(0, _asked);
    }

    [Fact]
    public async Task OpeningASectionScrollsItIntoViewBelowTheStickyBanner()
    {
        var (mode, slot) = await StartAsync();

        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.History));
        slot.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, invocation =>
            invocation.Identifier == "tuvimaEditorScrollTo" && Equals(invocation.Arguments[0], "#tl-editing-slot")));
    }

    [Fact]
    public async Task ClosingASectionWithUnsavedInputAsksFirstAndCancelKeepsItOpen()
    {
        var (mode, slot) = await StartAsync();
        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.History));
        slot.WaitForAssertion(() => Assert.NotEmpty(slot.FindAll(".tl-editing-slot")));
        await slot.InvokeAsync(() => mode.SetSectionDirty(true));
        _answers.Enqueue(SaveOrDiscardChoice.Cancel);

        await slot.Find("button.tl-editing-slot__close").ClickAsync(new());

        Assert.Equal(1, _asked);
        Assert.True(mode.SectionOpen);
        Assert.NotEmpty(slot.FindAll(".tl-editing-slot"));
    }

    [Fact]
    public async Task ClosingASectionWithUnsavedInputAndChoosingDiscardClosesIt()
    {
        var (mode, slot) = await StartAsync();
        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.History));
        slot.WaitForAssertion(() => Assert.NotEmpty(slot.FindAll(".tl-editing-slot")));
        await slot.InvokeAsync(() => mode.SetSectionDirty(true));
        _answers.Enqueue(SaveOrDiscardChoice.Discard);

        await slot.Find("button.tl-editing-slot__close").ClickAsync(new());

        slot.WaitForAssertion(() => Assert.False(mode.SectionOpen));
        Assert.Equal(1, _asked);
        Assert.False(mode.SectionDirty);
    }

    [Fact]
    public async Task ThePagesSaveOrDiscardAnswersReachTheEditorInsideTheSlot()
    {
        var (mode, slot) = await StartAsync();
        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.History));
        slot.WaitForAssertion(() => Assert.NotNull(mode.SectionHost));

        // Nothing is unsaved in a History section, so Save has nothing to do and succeeds; Discard has nothing to drop.
        var saved = false;
        await slot.InvokeAsync(async () => saved = await mode.SectionHost!.SaveAsync());
        Assert.True(saved);
        await slot.InvokeAsync(() => mode.SectionHost!.DiscardAsync());
        Assert.True(mode.SectionOpen);

        mode.CloseSection();
        slot.WaitForAssertion(() => Assert.Null(mode.SectionHost));
    }

    [Fact]
    public async Task OutsideTheOverlayTheSlotListensForEscItselfAndEscFromTheBrowserClosesIt()
    {
        var (mode, slot) = await StartAsync(handlesEscape: true);
        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.History));
        slot.WaitForAssertion(() => Assert.NotEmpty(slot.FindAll(".tl-editing-slot")));
        slot.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, invocation =>
            invocation.Identifier == "import" && Equals(invocation.Arguments[0], "./js/editing-section-escape.js")));

        // The script decides whether the key is the slot's (no open popup, not a control that uses Esc); .NET gets only the yes.
        await slot.InvokeAsync(() => slot.Instance.HandleEscapeFromBrowser());

        slot.WaitForAssertion(() => Assert.False(mode.SectionOpen));
        Assert.True(mode.IsOn);
    }

    [Fact]
    public async Task InsideTheOverlayTheSlotAddsNoEscListenerBecauseTheOverlayOwnsEsc()
    {
        var (mode, slot) = await StartAsync(handlesEscape: false);
        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.History));
        slot.WaitForAssertion(() => Assert.NotEmpty(slot.FindAll(".tl-editing-slot")));

        Assert.DoesNotContain(JSInterop.Invocations, invocation =>
            invocation.Identifier == "import" && Equals(invocation.Arguments[0], "./js/editing-section-escape.js"));
    }

    [Fact]
    public async Task ClosingASectionGivesFocusBackToTheOpenerOrElseTheHeroMenuThenTheOverlayScroller()
    {
        var (mode, slot) = await StartAsync();
        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.History));
        slot.WaitForAssertion(() => Assert.NotEmpty(slot.FindAll(".tl-editing-slot")));

        await slot.InvokeAsync(() => mode.CloseSectionAsync());

        slot.WaitForAssertion(() =>
        {
            var restore = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "tuvimaFocusReturn.restore");
            var fallbacks = Assert.IsType<string[]>(restore.Arguments[0]);
            Assert.Equal(
                [
                    ".tl-detail-secondary-actions button[aria-label='More actions']",
                    ".universe-hero__edit",
                    ".tl-details-overlay__scroller",
                ],
                fallbacks);
        });
    }

    [Fact]
    public async Task ASectionThatHasNothingToShowDoesNotStayOpenWithItsButtonPressed()
    {
        var (mode, slot) = await StartAsync();
        _buildReturnsNull = true;

        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.History));

        slot.WaitForAssertion(() => Assert.False(mode.SectionOpen));
        Assert.Null(mode.OpenSectionKind);
        Assert.Empty(slot.FindAll(".tl-editing-slot"));
    }

    [Fact]
    public async Task SavingFromTheDialogSavesTheEditorsInputReportsTheCommitToThePageOnceAndClosesTheSection()
    {
        var (mode, slot) = await StartAsync();
        _answers.Enqueue(SaveOrDiscardChoice.Save);
        // The Details section is the one the real editor can be made dirty in without provider data; it saves through the same host path.
        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.Details));
        slot.WaitForAssertion(() => Assert.NotEmpty(slot.FindAll("button[aria-label^='Edit ']")));
        slot.FindAll("button[aria-label^='Edit ']").First().Click();
        slot.WaitForAssertion(() => Assert.True(mode.SectionDirty));
        slot.Find(".sme-details-row[data-app-escape-owner='field'] :is(input, textarea)").Input("A new value");

        await slot.Find("button.tl-editing-slot__close").ClickAsync(new());

        slot.WaitForAssertion(() => Assert.False(mode.SectionOpen));
        Assert.Equal(1, _asked);
        Assert.Equal("A new value", Assert.Single(_overrideSaves).Values.Single());
        Assert.Equal(1, _committed);
        Assert.Empty(slot.FindAll(".tl-editing-slot"));
    }

    [Fact]
    public async Task DiscardingFromTheDialogSavesNothingAndTellsThePageNothing()
    {
        var (mode, slot) = await StartAsync();
        _answers.Enqueue(SaveOrDiscardChoice.Discard);
        await slot.InvokeAsync(() => mode.OpenSection(EditingSection.Details));
        slot.WaitForAssertion(() => Assert.NotEmpty(slot.FindAll("button[aria-label^='Edit ']")));
        slot.FindAll("button[aria-label^='Edit ']").First().Click();
        slot.WaitForAssertion(() => Assert.True(mode.SectionDirty));
        slot.Find(".sme-details-row[data-app-escape-owner='field'] :is(input, textarea)").Input("A new value");

        await slot.Find("button.tl-editing-slot__close").ClickAsync(new());

        slot.WaitForAssertion(() => Assert.False(mode.SectionOpen));
        Assert.Empty(_overrideSaves);
        Assert.Equal(0, _committed);
    }

    [Fact]
    public void TheSectionSlotIsCappedAtAboutSeventyPercentOfTheOverlayAndScrollsInside()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src/MediaEngine.Web/Components/Details/EditingSectionSlot.razor.css"));

        Assert.Contains("max-height: calc(70cqh - var(--tl-editing-slot-header-height));", css, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden;", css, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
