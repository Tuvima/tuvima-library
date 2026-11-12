using System.Security.Claims;
using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Reading;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// PDF and comic reader mock, screens 1, 2, 3 and 7: the order of the tools, the phone "..." sheet, the short count, the layout and
/// direction choices, the slider order, the search results and the panel width and colors.
/// </summary>
public sealed class PageReaderChromeTests : AsyncBunitContext
{
    private static readonly Guid ActiveId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Asset = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private readonly UserPlaybackSettingsDto _profile = UserPlaybackSettingsDto.CreateDefaults(ActiveId);
    private readonly List<UserPlaybackSettingsDto> _saved = [];
    private readonly IEngineApiClient _api;
    private readonly DeviceContextService _device;
    private readonly BunitJSModuleInterop _module;

    public PageReaderChromeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(new FakeAccessor(_profile));

        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPageBookmarksAsync), _ => Task.FromResult(new List<PageBookmarkDto>()));
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackSettingsAsync), _ => Task.FromResult<UserPlaybackSettingsDto?>(_profile));
            stub.SetHandler(nameof(IEngineApiClient.UpdatePlaybackSettingsAsync), args =>
            {
                _saved.Add((UserPlaybackSettingsDto)args![1]!);
                return Task.FromResult<UserPlaybackSettingsDto?>((UserPlaybackSettingsDto)args[1]!);
            });
        });
        _api = api;
        Services.AddSingleton(api);
        _device = new DeviceContextService(api);
        Services.AddSingleton(_device);
        var profiles = new ActiveProfileSessionService(null!, api, authenticationStateProvider: new ProfileAuthentication(ActiveId));
        Services.AddSingleton(profiles);
        Services.AddSingleton(new UIOrchestratorService(api, new UniverseStateContainer(), profiles, new ConfigurationManager(), NullLogger<UIOrchestratorService>.Instance));

        _module = JSInterop.SetupModule("./js/page-reader.js");
        _module.Mode = JSRuntimeMode.Loose;
    }

    private Task UsePhoneAsync() => _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));

    private IRenderedComponent<PageReader> Open(string kind, bool rightToLeft = false, string? series = null, bool withOutline = false, Guid? collectionId = null)
    {
        _module.Setup<PageReader.PageReaderOpenResult>("open", _ => true)
            .SetResult(new PageReader.PageReaderOpenResult { Ok = true, PageCount = 28, RightToLeft = rightToLeft });
        if (kind == "pdf")
        {
            _module.Setup<List<PageReader.PdfOutlineItem>>("getOutline")
                .SetResult(withOutline ? [new PageReader.PdfOutlineItem { Title = "Tides and the Moon", Level = 1, Page = 4 }] : []);
        }

        var cut = Render<PageReader>(parameters => parameters
            .Add(page => page.AssetId, Asset)
            .Add(page => page.Kind, kind)
            .Add(page => page.Title, "Saga")
            .Add(page => page.Series, series)
            .Add(page => page.ReturnCollectionId, collectionId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".rf-tools")));
        return cut;
    }

    private static string[] ToolLabels(IRenderedComponent<PageReader> cut) =>
        cut.FindAll(".rf-tools button").Select(button => button.GetAttribute("aria-label") ?? string.Empty).ToArray();

    private static string[] SegmentLabels(IRenderedComponent<PageReader> cut, string groupLabel) =>
        cut.Find($".app-segmented-control[aria-label='{groupLabel}']").QuerySelectorAll("button").Select(button => button.TextContent.Trim()).ToArray();

    private static string SelectedSegment(IRenderedComponent<PageReader> cut, string groupLabel) =>
        cut.Find($".app-segmented-control[aria-label='{groupLabel}'] button.is-selected").TextContent.Trim();

    private static void OpenSettings(IRenderedComponent<PageReader> cut) =>
        cut.Find(".rf-tools button[aria-label='Reading settings']").Click();

    private static object? Option(IRenderedComponent<PageReader> cut, IReadOnlyList<Bunit.JSRuntimeInvocation> invocations, string name)
    {
        var options = invocations.Single(call => call.Identifier == "open").Arguments[2]!;
        return options.GetType().GetProperty(name)!.GetValue(options);
    }

    [Fact]
    public void ThePdfTopLine_OffersSearchContentsPagesBookmarkThisPageBookmarksAndSettingsInThatOrder()
    {
        var cut = Open("pdf", withOutline: true);

        Assert.Equal(["Search in this file", "Contents", "Pages", "Bookmark this page", "Bookmarks", "Reading settings", "Close"], ToolLabels(cut));
    }

    [Fact]
    public void AComicTopLine_HasNoSearchOrContentsAndKeepsTheSameOrderForTheRest()
    {
        var cut = Open("comic");

        Assert.Equal(["Pages", "Bookmark this page", "Bookmarks", "Reading settings", "Close"], ToolLabels(cut));
    }

    [Fact]
    public void OnAComputer_ThereIsNoMoreButtonAndNoSheet()
    {
        var cut = Open("pdf", withOutline: true);

        Assert.DoesNotContain("More actions", ToolLabels(cut));
        Assert.Empty(cut.FindAll("dialog"));
    }

    [Fact]
    public async Task OnAPhone_TheTopLineHoldsOnlyBookmarkThisPageAndOneMoreButton()
    {
        await UsePhoneAsync();

        var cut = Open("pdf", withOutline: true);

        Assert.Equal(["Bookmark this page", "More actions", "Close"], ToolLabels(cut));
        Assert.Empty(cut.FindAll("dialog"));
    }

    [Fact]
    public async Task OnAPhone_MoreOpensASheetWithTheToolsInOrderAndChoosingOneOpensItsPanel()
    {
        await UsePhoneAsync();
        var cut = Open("pdf", withOutline: true);

        cut.Find(".rf-tools button[aria-label='More actions']").Click();

        var sheet = cut.Find("dialog");
        Assert.Contains("app-native-dialog--sheet", sheet.ClassName, StringComparison.Ordinal);
        Assert.Equal(
            ["Search in this file", "Contents", "Pages", "Bookmarks", "Reading settings"],
            sheet.QuerySelectorAll(".app-menu-item").Select(item => item.TextContent.Trim()).ToArray());

        sheet.QuerySelectorAll(".app-menu-item").Single(item => item.TextContent.Trim() == "Pages").Click();

        cut.WaitForAssertion(() => Assert.Equal("Pages", cut.Find(".rf-panel-title").TextContent.Trim()));
        Assert.Empty(cut.FindAll("dialog"));
    }

    [Fact]
    public async Task OnAPhone_TheSheetOfAComicLeavesOutSearchAndContents()
    {
        await UsePhoneAsync();
        var cut = Open("comic");

        cut.Find(".rf-tools button[aria-label='More actions']").Click();

        Assert.Equal(
            ["Pages", "Bookmarks", "Reading settings"],
            cut.Find("dialog").QuerySelectorAll(".app-menu-item").Select(item => item.TextContent.Trim()).ToArray());
    }

    [Fact]
    public void OnAComputer_TheCountReadsPageOfAndTheSliderComesFirst()
    {
        var cut = Open("comic");

        Assert.Equal("Page 1 of 28", cut.Find(".pr-count").TextContent.Trim());
        var footer = cut.Find(".rf-bar--bottom");
        Assert.Equal(["pr-track", "pr-count"], footer.Children.Select(child => child.ClassList.First()).ToArray());
    }

    [Fact]
    public async Task OnAPhone_TheCountIsTheShortForm()
    {
        await UsePhoneAsync();

        var cut = Open("comic");

        Assert.Equal("1 of 28", cut.Find(".pr-count").TextContent.Trim());
    }

    [Fact]
    public void OnAComputer_TheLayoutChoicesIncludeTwoPages()
    {
        var cut = Open("comic");
        OpenSettings(cut);

        Assert.Equal(["Auto", "One page", "Two pages", "Scrolling"], SegmentLabels(cut, "Layout"));
    }

    [Fact]
    public async Task OnAPhone_ThereIsNoTwoPagesChoice_AndASavedTwoShowsOnePageWithoutChangingTheSavedSetting()
    {
        await UsePhoneAsync();
        _profile.Reading.PageLayouts[PageReaderProfileSettings.LayoutKey(Asset)] = "two";

        var cut = Open("comic");
        cut.Find(".rf-tools button[aria-label='More actions']").Click();
        cut.Find("dialog").QuerySelectorAll(".app-menu-item").Single(item => item.TextContent.Trim() == "Reading settings").Click();

        Assert.Equal(["Auto", "One page", "Scrolling"], SegmentLabels(cut, "Layout"));
        Assert.Equal("One page", SelectedSegment(cut, "Layout"));
        Assert.Equal("one", Option(cut, _module.Invocations["open"], "layout"));
        Assert.Equal("two", _profile.Reading.PageLayouts[PageReaderProfileSettings.LayoutKey(Asset)]);
        Assert.Empty(_saved);
    }

    [Fact]
    public void Direction_OffersLeftToRightAndRightToLeftOnly_AndShowsTheFilesDirectionSelectedWhenNothingWasChosen()
    {
        var cut = Open("comic", rightToLeft: true);
        OpenSettings(cut);

        Assert.Equal(["Left to right", "Right to left"], SegmentLabels(cut, "Direction"));
        Assert.Equal("Right to left", SelectedSegment(cut, "Direction"));
        // The file still decides: nothing was chosen, so the script is told to work it out.
        Assert.Equal("auto", Option(cut, _module.Invocations["open"], "direction"));
    }

    [Fact]
    public void Direction_ForAFileThatReadsLeftToRight_ShowsLeftToRightSelected()
    {
        var cut = Open("comic", rightToLeft: false);
        OpenSettings(cut);

        Assert.Equal("Left to right", SelectedSegment(cut, "Direction"));
    }

    [Fact]
    public void Direction_StoredAsAutomatic_CountsAsNotChosenSoTheFileDecides()
    {
        _profile.Reading.ComicDirections["saga"] = "auto";

        var cut = Open("comic", rightToLeft: true, series: "Saga");
        OpenSettings(cut);

        Assert.Equal("auto", Option(cut, _module.Invocations["open"], "direction"));
        Assert.Equal("Right to left", SelectedSegment(cut, "Direction"));
    }

    [Fact]
    public void Direction_ChosenForTheSeries_BeatsTheFile()
    {
        _profile.Reading.ComicDirections["saga"] = "ltr";

        var cut = Open("comic", rightToLeft: true, series: "Saga");
        OpenSettings(cut);

        Assert.Equal("ltr", Option(cut, _module.Invocations["open"], "direction"));
        Assert.Equal("Left to right", SelectedSegment(cut, "Direction"));
    }

    [Fact]
    public void ThePdfSettings_HaveNoDirection()
    {
        var cut = Open("pdf");
        OpenSettings(cut);

        Assert.Empty(cut.FindAll(".app-segmented-control[aria-label='Direction']"));
    }

    [Fact]
    public void TheSettingsPanel_IsTitledReadingSettingsAndCarriesNoExtraHintLine()
    {
        var cut = Open("comic");
        OpenSettings(cut);

        Assert.Equal("Reading settings", cut.Find(".rf-panel-title").TextContent.Trim());
        Assert.Empty(cut.FindAll(".rf-panel-body .rf-hint"));
        Assert.DoesNotContain("saved to your profile", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchResults_ShowTheSnippetWithThePageUnderItAndHaveNoSearchButton()
    {
        // The script answers once its last hit has been handed over, so the call stays open while the hits arrive.
        var search = _module.Setup<PageReader.PdfSearchOutcome>("searchText", _ => true);
        var cut = Open("pdf");
        cut.Find(".rf-tools button[aria-label='Search in this file']").Click();

        Assert.Empty(cut.FindAll(".rf-search button"));

        var input = cut.Find(".rf-search-input");
        input.Input("spring tide");
        input.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        cut.WaitForAssertion(() => Assert.Single(_module.Invocations["searchText"]));
        await cut.InvokeAsync(() => cut.Instance.OnSearchResults(0, [new PageReader.PdfSearchHit { Page = 74, Before = "the ", Match = "spring tide", After = " can lift" }], true));
        search.SetResult(new PageReader.PdfSearchOutcome { HasText = true });

        var row = cut.Find(".rf-list .rf-row");
        Assert.Equal(["rf-row-main", "rf-row-sub"], row.Children.Select(child => child.ClassList.First()).ToArray());
        Assert.Equal("Page 74", row.Children[1].TextContent.Trim());
    }

    [Fact]
    public void ThePanelIsTheFramesPanel_FourHundredPixelsThroughTheSharedPanelToken()
    {
        var cut = Open("comic");
        OpenSettings(cut);
        var frameCss = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/css/reader-frame.css"));

        Assert.Contains("rf-panel", cut.Find(".rf-panel").ClassName, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".br-panel"));
        Assert.Matches(@"\.rf-panel \{[^}]*inline-size: min\(var\(--tl-popout-panel\), 100%\);", frameCss);
    }

    [Fact]
    public void ThePageReaderStylesheet_HoldsNoBarTitleToolsPanelOrScrimRules()
    {
        var css = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/css/page-reader.css"));

        foreach (var retired in new[] { ".pr-shell", ".pr-bar", ".pr-title", ".pr-tools", ".pr-ib", ".pr-live", ".pr-overlay", ".pr-spinner", ".pr-state", ".br-panel", ".br-scrim" })
        {
            Assert.DoesNotContain(retired, css, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheBookmarksSwitch_ListsMineBeforeEveryone_AsInTheBookReader()
    {
        var cut = Open("pdf");

        cut.Find(".rf-tools button[aria-label='Bookmarks']").Click();

        Assert.Equal(["Mine", "Everyone"], SegmentLabels(cut, "Bookmarks"));
        Assert.Equal("Mine", SelectedSegment(cut, "Bookmarks"));
    }

    [Fact]
    public void ThePageReader_LoadsOnlyTheFramesAndItsOwnStylesheet_AndTheSharedPanelRulesLiveInTheFrame()
    {
        var page = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/Components/Pages/PageReader.razor"));
        var frameCss = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/css/reader-frame.css"));
        var pageCss = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/css/page-reader.css"));
        var bookCss = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/css/book-reader.css"));

        Assert.DoesNotContain("book-reader.css", page, StringComparison.Ordinal);
        Assert.DoesNotContain("br-", page, StringComparison.Ordinal);

        foreach (var shared in new[] { ".rf-list", ".rf-item", ".rf-row", ".rf-row-main", ".rf-row-sub", ".rf-search", ".rf-search-input", ".rf-search-match", ".rf-empty", ".rf-hint", ".rf-field", ".rf-field-label", ".rf-subhead" })
        {
            var definition = new Regex($@"^{Regex.Escape(shared)}\s*[,{{]", RegexOptions.Multiline);
            Assert.NotEmpty(definition.Matches(frameCss));
            Assert.Empty(definition.Matches(pageCss));
            Assert.Empty(definition.Matches(bookCss));
        }

        foreach (var retired in new[] { ".br-list", ".br-item", ".br-row {", ".br-row-sub", ".br-search", ".br-empty", ".br-hint", ".br-field", ".br-subhead" })
        {
            Assert.DoesNotContain(retired, bookCss, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePageReaderScript_FindsTheFramesShellBarsAndPanelBody()
    {
        var script = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/js/page-reader.js"));

        Assert.Contains("\".rf-shell\"", script, StringComparison.Ordinal);
        Assert.Contains("\".rf-bar--bottom", script, StringComparison.Ordinal);
        Assert.Contains("\".rf-panel-body\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("pr-shell", script, StringComparison.Ordinal);
        Assert.DoesNotContain("pr-bar", script, StringComparison.Ordinal);
        Assert.DoesNotContain("br-panel", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CloseIsTheLastControlOnTheTopLine_AndThereIsNoBackArrow()
    {
        var cut = Open("comic");

        Assert.Equal("Close", ToolLabels(cut).Last());
        Assert.DoesNotContain("Back to Library", ToolLabels(cut));
        Assert.Contains("rf-close", cut.FindAll(".rf-tools button").Last().ClassList);
    }

    [Fact]
    public void Close_SavesThePageAndReturnsToWhereTheReaderWasOpenedFrom()
    {
        var collection = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var cut = Open("comic", collectionId: collection);

        cut.Find(".rf-tools button[aria-label='Close']").Click();

        cut.WaitForAssertion(() => Assert.EndsWith($"/details/collection/{collection}", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri, StringComparison.Ordinal));
        Assert.Contains(nameof(IEngineApiClient.SaveProgressAsync), ((EngineApiClientStub)(object)_api).Calls);
    }

    [Fact]
    public void ThePageBackgroundChoice_SetsTheFramesColourVariablesThroughTheShell()
    {
        _profile.Reading.PageBackground = "light";

        var cut = Open("comic");

        var shell = cut.Find(".rf-shell");
        Assert.Contains("pr-reader", shell.ClassList);
        Assert.Equal("light", shell.GetAttribute("data-background"));
        var css = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/css/page-reader.css"));
        Assert.Matches(@"\.pr-reader\[data-background=""light""\] \{[^}]*--rf-page: #e9e6df;[^}]*--rf-text:", css);
        Assert.Matches(@"\.pr-reader\[data-background=""dark""\] \{ --rf-page: var\(--tl-bg-root\); \}", css);
    }

    [Fact]
    public void ThePalette_UsesTheSharedTokensAndKeepsOnlyTheLightBackgroundAndTheBookmarkGoldAsOwnValues()
    {
        var css = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/css/page-reader.css"));

        var hexColors = Regex.Matches(css, "#[0-9a-fA-F]{3,6}\\b").Select(match => match.Value.ToLowerInvariant()).Distinct().Order().ToArray();

        Assert.Equal(["#e9e6df", "#f2c14e"], hexColors);
    }

    private sealed class FakeAccessor(UserPlaybackSettingsDto settings) : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId => settings.ProfileId;

        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) =>
            Task.FromResult<UserPlaybackSettingsDto?>(settings);

        public void UpdateCache(UserPlaybackSettingsDto next) { }

        public void Invalidate() { }
    }

    private sealed class ProfileAuthentication(Guid profileId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("tuvima:active_profile_id", profileId.ToString("D"))], "test"))));
    }
}
