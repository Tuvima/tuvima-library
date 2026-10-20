using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Editing Mode banner (details overlay spec D4, D18) and the hero art chips: what the banner shows while the mode is on,
/// the three section buttons and their pressed state, the "..." menu that holds only Delete, and the one Done button. It is
/// drawn only for a person who may edit on a desktop with a mouse.
/// </summary>
public sealed class EditingBannerTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly AskCounter _ask = new();

    public EditingBannerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
        Render<AppPopoverHost>();
    }

    private Task UseDesktopAsync() =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

    private static IEngineApiClient Api(MediaEditorContextDto? context = null) => EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(context ?? EditingModeTestData.MovieContext()));
        stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
    });

    private async Task<EditingModeContext> StartedAsync(bool permission = true, bool turnOn = true, MediaEditorContextDto? context = null)
    {
        var session = new ItemFieldEditSession(Api(context), _device);
        var mode = new EditingModeContext(_device, session)
        {
            HasPermission = permission,
            AskSaveOrDiscard = _ask.AskAsync,
        };
        mode.Loader = () => session.LoadAsync(MovieId, context?.MediaType ?? "Movies", context?.InitialScope ?? "movie", null);
        if (turnOn)
        {
            Assert.True(await mode.EnterAsync());
        }

        return mode;
    }

    private IRenderedComponent<EditingBanner> RenderBanner(EditingModeContext mode, bool canDelete = false, EventCallback<DetailAction>? onAction = null) =>
        Render<EditingBanner>(parameters =>
        {
            parameters.Add(component => component.Mode, mode).Add(component => component.CanDelete, canDelete);
            if (onAction is { } callback)
            {
                parameters.Add(component => component.OnActionSelected, callback);
            }
        });

    private IRenderedComponent<CascadingValue<EditingModeContext>> RenderChips(EditingModeContext mode) =>
        Render<CascadingValue<EditingModeContext>>(parameters => parameters
            .Add(component => component.Value, mode)
            .Add(component => component.ChildContent, builder =>
            {
                builder.OpenComponent<EditingHeroArtworkButtons>(0);
                builder.CloseComponent();
            }));

    private static MediaEditorContextDto TvContext(string scopeId) => new()
    {
        LaunchEntityId = MovieId,
        MediaType = "TV",
        InitialScope = scopeId,
        AvailableTabs = ["details", "artwork", "links", "history"],
        Scopes =
        [
            new MediaEditorScopeDto
            {
                ScopeId = scopeId,
                Label = scopeId,
                FieldEntityId = MovieId,
                DisplayTitle = "A show",
                CanEditFields = true,
                CanEditArtwork = true,
                ArtworkSlots = ["CoverArt"],
                FieldSnapshot = new MediaEditorScopeFieldSnapshotDto(),
            },
        ],
    };

    // -- When it shows -------------------------------------------------------------------------

    [Fact]
    public async Task TheBannerRendersNothingWhileEditingModeIsOff()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync(turnOn: false);

        var cut = RenderBanner(mode);

        Assert.Empty(cut.FindAll("button"));
        Assert.Empty(cut.FindAll(".tl-editing-banner"));
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("tablet")]
    [InlineData("tv")]
    [InlineData("desktop-touch")]
    [InlineData("desktop-no-pointer")]
    public async Task PhonesTabletsAndOtherDevicesThatCannotEditNeverSeeTheBanner(string device)
    {
        var snapshot = device switch
        {
            "phone" => new DeviceContextSnapshot("mobile", "phone", true),
            "tablet" => new DeviceContextSnapshot("web", "tablet", true),
            "tv" => new DeviceContextSnapshot("television", "tv", false, FinePointer: true, Hover: true),
            "desktop-touch" => new DeviceContextSnapshot("web", "desktop", true),
            _ => new DeviceContextSnapshot("web", "desktop", false),
        };
        await _device.InitialiseAsync(snapshot);
        var mode = await StartedAsync(turnOn: false);

        var cut = RenderBanner(mode);

        Assert.Empty(cut.FindAll(".tl-editing-banner"));
        Assert.False(await mode.EnterAsync());
        Assert.False(mode.IsOn);
    }

    [Fact]
    public async Task APersonWhoMayNotEditSeesNoBannerEvenOnADesktop()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync(permission: false, turnOn: false);

        var cut = RenderBanner(mode);

        Assert.False(await mode.EnterAsync());
        Assert.Empty(cut.FindAll(".tl-editing-banner"));
    }

    [Fact]
    public async Task TheBannerAppearsWhenTheModeTurnsOnAndGoesWhenItEnds()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync(turnOn: false);
        var cut = RenderBanner(mode);
        Assert.Empty(cut.FindAll(".tl-editing-banner"));

        await cut.InvokeAsync(() => mode.EnterAsync());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-editing-banner")));

        await cut.InvokeAsync(() => mode.DoneAsync());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editing-banner")));
    }

    // -- What it shows -------------------------------------------------------------------------

    [Fact]
    public async Task TheBannerShowsTheDotEditingTheQuietNoteTheThreeSectionButtonsAndDone()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();

        var cut = RenderBanner(mode);

        var banner = cut.Find(".tl-editing-banner");
        Assert.Equal("region", banner.GetAttribute("role"));
        Assert.Equal("Editing", banner.GetAttribute("aria-label"));
        Assert.NotNull(cut.Find(".tl-editing-banner__dot"));
        Assert.Equal("Editing", cut.Find(".tl-editing-banner__title").TextContent.Trim());
        Assert.Equal("Changes stay in your library", cut.Find(".tl-editing-banner__note").TextContent.Trim());
        Assert.Equal(["Manage Artwork", "Match and Identity", "History"],
            cut.FindAll(".tl-editing-banner__section").Select(button => button.TextContent.Trim()));
        Assert.Equal("Done", cut.Find("button.tl-editing-banner__done").TextContent.Trim());
    }

    [Fact]
    public async Task TheBannerHasNoEditingModeLabelNoExitEditModeAndNoSplitDoneMenu()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();

        var cut = RenderBanner(mode);

        Assert.DoesNotContain("Editing Mode", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Exit Edit Mode", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Mark as watched", cut.Markup, StringComparison.OrdinalIgnoreCase);
        // Done is one plain button: nothing menu-like sits beside it when there is no Delete.
        Assert.Empty(cut.FindAll("button[aria-haspopup]"));
        Assert.Empty(cut.FindAll("button.tl-editing-banner__more"));
    }

    [Fact]
    public async Task MatchAndIdentityIsOfferedOnlyWhereTheEditorCanMatchTheTarget()
    {
        await UseDesktopAsync();
        var show = await StartedAsync(context: TvContext("series"));
        var showBanner = RenderBanner(show);
        Assert.Contains("Match and Identity", showBanner.FindAll(".tl-editing-banner__section").Select(button => button.TextContent.Trim()));

        var other = await StartedAsync(context: TvContext("file"));
        var otherBanner = RenderBanner(other);

        Assert.Equal(["Manage Artwork", "History"], otherBanner.FindAll(".tl-editing-banner__section").Select(button => button.TextContent.Trim()));
    }

    [Fact]
    public async Task TheSectionButtonsAreDisabledWhileAFieldDraftIsOpen()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = RenderBanner(mode);
        Assert.All(cut.FindAll(".tl-editing-banner__section"), button => Assert.False(button.HasAttribute("disabled")));

        await cut.InvokeAsync(() => Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero")));

        cut.WaitForAssertion(() => Assert.All(cut.FindAll(".tl-editing-banner__section"), button => Assert.True(button.HasAttribute("disabled"))));
        // Done stays reachable whatever else is going on.
        Assert.False(cut.Find("button.tl-editing-banner__done").HasAttribute("disabled"));
    }

    // -- The sections --------------------------------------------------------------------------

    [Fact]
    public async Task EachSectionButtonTogglesItsSectionAndAriaPressedFollowsTheOpenOne()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = RenderBanner(mode);
        IReadOnlyList<string> Pressed() => cut.FindAll(".tl-editing-banner__section")
            .Where(button => button.GetAttribute("aria-pressed") == "true")
            .Select(button => button.TextContent.Trim())
            .ToList();
        Assert.Empty(Pressed());

        cut.FindAll(".tl-editing-banner__section").Single(button => button.TextContent.Contains("Manage Artwork")).Click();
        cut.WaitForAssertion(() => Assert.Equal(["Manage Artwork"], Pressed()));
        Assert.Equal(EditingSection.Artwork, mode.OpenSectionKind);

        // Only one is open at a time: pressing another swaps to it.
        cut.FindAll(".tl-editing-banner__section").Single(button => button.TextContent.Contains("History")).Click();
        cut.WaitForAssertion(() => Assert.Equal(["History"], Pressed()));
        Assert.Equal(EditingSection.History, mode.OpenSectionKind);

        // Pressing the open one again closes it.
        cut.FindAll(".tl-editing-banner__section").Single(button => button.TextContent.Contains("History")).Click();
        cut.WaitForAssertion(() => Assert.Empty(Pressed()));
        Assert.False(mode.SectionOpen);
    }

    [Fact]
    public async Task MatchAndIdentityOpensTheMatchSection()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = RenderBanner(mode);

        cut.FindAll(".tl-editing-banner__section").Single(button => button.TextContent.Contains("Match and Identity")).Click();

        cut.WaitForAssertion(() => Assert.Equal(EditingSection.Match, mode.OpenSectionKind));
    }

    [Fact]
    public async Task DoneClosesAnOpenSectionAndEndsTheMode()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        mode.OpenSection(EditingSection.History);
        var cut = RenderBanner(mode);

        cut.Find("button.tl-editing-banner__done").Click();

        cut.WaitForAssertion(() => Assert.False(mode.IsOn));
        Assert.False(mode.SectionOpen);
        Assert.Equal(0, _ask.Asked);
    }

    [Fact]
    public async Task DoneWithUnsavedInputAsksAndCancelKeepsTheBannerAndTheInput()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        _ask.Next = SaveOrDiscardChoice.Cancel;
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));
        mode.Fields.UpdateDraft("Typed but not saved");
        var cut = RenderBanner(mode);

        cut.Find("button.tl-editing-banner__done").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, _ask.Asked));
        Assert.True(mode.IsOn);
        Assert.Single(cut.FindAll(".tl-editing-banner"));
        Assert.Equal("Typed but not saved", mode.Fields.Draft);
    }

    [Fact]
    public async Task DoneIsDisabledWhileSaveOrDiscardIsBeingAnswered()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var gate = new TaskCompletionSource<SaveOrDiscardChoice>();
        mode.AskSaveOrDiscard = () => gate.Task;
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));
        mode.Fields.UpdateDraft("Typed but not saved");
        var cut = RenderBanner(mode);

        cut.Find("button.tl-editing-banner__done").Click();

        cut.WaitForAssertion(() => Assert.True(cut.Find("button.tl-editing-banner__done").HasAttribute("disabled")));
        gate.SetResult(SaveOrDiscardChoice.Cancel);
        cut.WaitForAssertion(() => Assert.False(cut.Find("button.tl-editing-banner__done").HasAttribute("disabled")));
        Assert.True(mode.IsOn);
    }

    // -- Leaving a page that has no overlay guard (a Universe) ---------------------------------

    [Fact]
    public async Task OnAPageOutsideTheOverlayLeavingWithUnsavedInputAsksAndCancelKeepsThePersonThere()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        _ask.Next = SaveOrDiscardChoice.Cancel;
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));
        mode.Fields.UpdateDraft("Typed but not saved");
        Render<EditingBanner>(parameters => parameters.Add(component => component.Mode, mode).Add(component => component.InPage, true));
        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;

        navigation.NavigateTo("/collections");

        Assert.Equal(before, navigation.Uri);
        Assert.Equal(1, _ask.Asked);
        Assert.True(mode.IsOn);
        Assert.Equal("Typed but not saved", mode.Fields.Draft);
    }

    [Fact]
    public async Task OnAPageOutsideTheOverlayDiscardLetsThePersonLeaveAndEndsTheMode()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        _ask.Next = SaveOrDiscardChoice.Discard;
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));
        mode.Fields.UpdateDraft("Typed but not saved");
        Render<EditingBanner>(parameters => parameters.Add(component => component.Mode, mode).Add(component => component.InPage, true));
        var navigation = Services.GetRequiredService<NavigationManager>();

        navigation.NavigateTo("/collections");

        Assert.EndsWith("/collections", navigation.Uri);
        Assert.False(mode.IsOn);
    }

    [Fact]
    public async Task InsideTheOverlayTheBannerNeverAsksBecauseTheOverlaysDismissGuardDoes()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));
        mode.Fields.UpdateDraft("Typed but not saved");
        Render<EditingBanner>(parameters => parameters.Add(component => component.Mode, mode));
        var navigation = Services.GetRequiredService<NavigationManager>();

        navigation.NavigateTo("/collections");

        Assert.Equal(0, _ask.Asked);
    }

    // -- The "..." menu ------------------------------------------------------------------------

    [Fact]
    public async Task TheMoreMenuHoldsOnlyDeleteFromLibraryAndAppearsOnlyWhenDeleteIsAllowed()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var without = RenderBanner(mode, canDelete: false);
        Assert.Empty(without.FindAll("button.tl-editing-banner__more"));

        var cut = RenderBanner(mode, canDelete: true);
        cut.Find("button.tl-editing-banner__more").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        var item = Assert.Single(cut.FindAll("[role='menuitem']"));
        Assert.Equal("Delete from library…", item.TextContent.Trim());
        Assert.Contains("app-tone--error", item.ClassList);
    }

    [Fact]
    public async Task ChoosingDeleteHandsTheDeleteActionToThePageHandler()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var chosen = new List<string>();
        var cut = RenderBanner(mode, canDelete: true, onAction: EventCallback.Factory.Create<DetailAction>(this, action => chosen.Add(action.Key)));

        cut.Find("button.tl-editing-banner__more").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        cut.Find("[role='menuitem']").Click();

        cut.WaitForAssertion(() => Assert.Equal([DetailActionKeys.DeleteFromLibrary], chosen));
    }

    // -- The hero art chips --------------------------------------------------------------------

    [Fact]
    public async Task EachHeroArtChipOpensTheArtworkSectionOnThatSlot()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync();
        var cut = RenderChips(mode);

        Assert.Equal(["Edit Background", "Edit Logo", "Edit Cover"],
            cut.FindAll(".tl-editing-art__button").Select(button => button.TextContent.Trim()));

        cut.FindAll(".tl-editing-art__button").Single(button => button.TextContent.Contains("Edit Logo")).Click();
        cut.WaitForAssertion(() => Assert.Equal("Logo", mode.OpenSectionArtworkSlot));
        Assert.Equal(EditingSection.Artwork, mode.OpenSectionKind);

        cut.FindAll(".tl-editing-art__button").Single(button => button.TextContent.Contains("Edit Cover")).Click();
        cut.WaitForAssertion(() => Assert.Equal("CoverArt", mode.OpenSectionArtworkSlot));
        Assert.Equal(EditingSection.Artwork, mode.OpenSectionKind);
        Assert.Equal(2, mode.SectionRequestVersion);
    }

    [Fact]
    public async Task HeroArtChipsAreHiddenWhileTheModeIsOffAndDisabledWhileAFieldIsOpen()
    {
        await UseDesktopAsync();
        var mode = await StartedAsync(turnOn: false);
        var cut = RenderChips(mode);
        Assert.Empty(cut.FindAll("button"));
        Assert.False(mode.OpenSection(EditingSection.Artwork, "Logo"));

        await cut.InvokeAsync(() => mode.EnterAsync());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-art__button")));
        await cut.InvokeAsync(() => Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero")));

        cut.WaitForAssertion(() => Assert.All(cut.FindAll(".tl-editing-art__button"), button => Assert.True(button.HasAttribute("disabled"))));
        Assert.False(mode.OpenSection(EditingSection.Artwork));
    }

    [Fact]
    public async Task AnItemWhoseFieldsCannotBeLoadedStillEntersTheModeAndSaysSoInlineInTheBanner()
    {
        await UseDesktopAsync();
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(null)));
        var session = new ItemFieldEditSession(api, _device);
        var mode = new EditingModeContext(_device, session) { HasPermission = true };
        mode.Loader = () => session.LoadAsync(MovieId, "Movies", "movie", null);
        var cut = RenderBanner(mode);

        await cut.InvokeAsync(() => mode.EnterAsync());

        cut.WaitForAssertion(() => Assert.Contains("could not be loaded", cut.Find(".tl-editing-banner__notice[role='alert']").TextContent));
        Assert.True(mode.IsOn);
        Assert.Equal(3, cut.FindAll(".tl-editing-banner__section").Count);
    }

    private sealed class AskCounter
    {
        public int Asked { get; private set; }

        public SaveOrDiscardChoice Next { get; set; } = SaveOrDiscardChoice.Discard;

        public Task<SaveOrDiscardChoice> AskAsync()
        {
            Asked++;
            return Task.FromResult(Next);
        }
    }
}
