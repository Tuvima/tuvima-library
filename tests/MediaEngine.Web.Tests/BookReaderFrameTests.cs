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
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Music table and reader frame spec D12-D19 for the book reader (mockups B1-B3): it sits in the one reader frame, so the top line is
/// Search, Contents, Bookmark this page, Bookmarks and Reading settings with Close at the top right, the bookmark icon toggles, the
/// Bookmarks panel keeps the highlights beneath the bookmarks, a phone gets the "..." sheet, and the themes set the frame's colours.
/// </summary>
public sealed class BookReaderFrameTests : AsyncBunitContext
{
    private const string Cfi = "epubcfi(/6/4!/4/2)";
    private static readonly Guid ActiveId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Asset = Guid.Parse("33333333-2222-3333-4444-555555555555");
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private readonly UserPlaybackSettingsDto _profile = UserPlaybackSettingsDto.CreateDefaults(ActiveId);
    private readonly List<ReaderBookmarkDto> _stored = [];
    private readonly List<Guid> _deleted = [];
    private readonly List<ReaderHighlightDto> _highlights = [];
    private readonly IEngineApiClient _api;
    private readonly DeviceContextService _device;
    private readonly BunitJSModuleInterop _module;

    public BookReaderFrameTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddScoped<DashboardSessionAccessor>();
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(new FakeAccessor(_profile));

        _api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetBookmarksAsync), _ => Task.FromResult(_stored.ToList()));
            stub.SetHandler(nameof(IEngineApiClient.GetHighlightsAsync), _ => Task.FromResult(_highlights.ToList()));
            stub.SetHandler(nameof(IEngineApiClient.CreateBookmarkAsync), args =>
            {
                var created = new ReaderBookmarkDto { Id = Guid.NewGuid(), AssetId = Asset, CfiPosition = (string?)args![2], Label = (string?)args[3], CreatedAt = DateTime.UtcNow };
                _stored.Insert(0, created);
                return Task.FromResult<ReaderBookmarkDto?>(created);
            });
            stub.SetHandler(nameof(IEngineApiClient.DeleteBookmarkAsync), args =>
            {
                _deleted.Add((Guid)args![0]!);
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackSettingsAsync), _ => Task.FromResult<UserPlaybackSettingsDto?>(_profile));
        });
        Services.AddSingleton(_api);
        _device = new DeviceContextService(_api);
        Services.AddSingleton(_device);
        var profiles = new ActiveProfileSessionService(null!, _api, authenticationStateProvider: new ProfileAuthentication(ActiveId));
        Services.AddSingleton(profiles);
        Services.AddSingleton(new UIOrchestratorService(_api, new UniverseStateContainer(), profiles, new ConfigurationManager(), NullLogger<UIOrchestratorService>.Instance));

        _module = JSInterop.SetupModule("./js/book-reader.js");
        _module.Mode = JSRuntimeMode.Loose;
        _module.Setup<BookReaderOpenResult>("open", _ => true)
            .SetResult(new BookReaderOpenResult { Ok = true, Title = "Dune", Author = "Frank Herbert", Layout = "reflowable" });
        _module.Setup<List<BookReaderTocItem>>("getToc").SetResult([]);
    }

    private Task UsePhoneAsync() => _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));

    private IRenderedComponent<BookReader> Open(Guid? collectionId = null, string theme = "light")
    {
        // What the script answers: the profile base with this device's choice (the theme) on top.
        _module.Setup<BookReaderOpenOptions>("loadSettings", _ => true).SetResult(new BookReaderOpenOptions { Theme = theme });
        var cut = Render<BookReader>(parameters => parameters
            .Add(page => page.AssetId, Asset)
            .Add(page => page.ReturnCollectionId, collectionId));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".rf-tools button")));
        return cut;
    }

    private static Task Relocate(IRenderedComponent<BookReader> cut, string cfi = Cfi) =>
        cut.InvokeAsync(() => cut.Instance.OnRelocated(cfi, 0.4, "Chapter 7"));

    private static string[] ToolLabels(IRenderedComponent<BookReader> cut) =>
        cut.FindAll(".rf-tools button").Select(button => button.GetAttribute("aria-label") ?? string.Empty).ToArray();

    private static string[] SegmentLabels(IRenderedComponent<BookReader> cut, string groupLabel) =>
        cut.Find($".app-segmented-control[aria-label='{groupLabel}']").QuerySelectorAll("button").Select(button => button.TextContent.Trim()).ToArray();

    [Fact]
    public void TheBookTopLine_HasNoPagesGridAndOffersSearchContentsBookmarkThisPageBookmarksAndReadingSettingsInThatOrder()
    {
        var cut = Open();

        Assert.Equal(["Search in this book", "Contents", "Bookmark this page", "Bookmarks", "Reading settings", "Close"], ToolLabels(cut));
    }

    [Fact]
    public void TheTitleAndAuthorSitAtTheLeftOfTheFramesTopLine()
    {
        var cut = Open();

        Assert.Equal("Dune", cut.Find(".rf-title-text").TextContent.Trim());
        Assert.Equal("Frank Herbert", cut.Find(".rf-title-sub").TextContent.Trim());
    }

    [Fact]
    public void CloseIsTheLastControlOnTheTopLine_AndThereIsNoBackArrow()
    {
        var cut = Open();

        Assert.Equal("Close", ToolLabels(cut).Last());
        Assert.Contains("rf-close", cut.FindAll(".rf-tools button").Last().ClassList);
        Assert.DoesNotContain(cut.FindAll(".rf-bar--top button"), button => (button.GetAttribute("aria-label") ?? string.Empty).Contains("Back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Close_SavesThePlaceAndReturnsToWhereTheReaderWasOpenedFrom()
    {
        var collection = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var cut = Open(collection);
        await Relocate(cut);

        cut.Find(".rf-tools button[aria-label='Close']").Click();

        cut.WaitForAssertion(() => Assert.EndsWith($"/details/collection/{collection}", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal));
        Assert.Contains(nameof(IEngineApiClient.SaveProgressAsync), ((EngineApiClientStub)(object)_api).Calls);
    }

    [Fact]
    public async Task TheBookmarkButton_IsAnOutlineUntilThisPageIsBookmarked_ThenFilledAndPressingAgainRemovesIt()
    {
        var cut = Open();
        await Relocate(cut);

        var button = cut.Find(".rf-tools button[aria-label='Bookmark this page']");
        Assert.Equal("false", button.GetAttribute("aria-pressed"));
        Assert.DoesNotContain("rf-ib--on", button.ClassList);

        button.Click();

        cut.WaitForAssertion(() =>
        {
            var pressed = cut.Find(".rf-tools button[aria-label='Remove bookmark']");
            Assert.Equal("true", pressed.GetAttribute("aria-pressed"));
            Assert.Contains("rf-ib--on", pressed.ClassList);
        });
        var mine = Assert.Single(_stored);
        Assert.Equal(Cfi, mine.CfiPosition);

        cut.Find(".rf-tools button[aria-label='Remove bookmark']").Click();

        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find(".rf-tools button[aria-label='Bookmark this page']").GetAttribute("aria-pressed")));
        Assert.Equal([mine.Id], _deleted);
    }

    [Fact]
    public async Task AnEveryonesBookmarkOnThisPage_DoesNotFillTheButtonOrStopMeBookmarkingIt()
    {
        _stored.Add(new ReaderBookmarkDto { Id = Guid.NewGuid(), AssetId = Asset, CfiPosition = Cfi, Label = "Shared", IsGlobal = true, CreatedAt = DateTime.UtcNow });
        var cut = Open();
        await Relocate(cut);

        cut.Find(".rf-tools button[aria-label='Bookmark this page']").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".rf-tools button[aria-label='Remove bookmark']")));
        Assert.Equal(2, _stored.Count);
    }

    [Fact]
    public void TheBookmarksPanel_IsTitledBookmarksAndKeepsHighlightsBeneathMyBookmarks()
    {
        _stored.Add(new ReaderBookmarkDto { Id = Guid.NewGuid(), AssetId = Asset, CfiPosition = Cfi, Label = "Chapter 7 · 40%", CreatedAt = DateTime.UtcNow });
        _highlights.Add(new ReaderHighlightDto { Id = Guid.NewGuid(), AssetId = Asset, CfiRange = "epubcfi(/6/4!/4/2,/1:0,/1:9)", SelectedText = "A beginning is a very delicate time", CreatedAt = DateTime.UtcNow });
        var cut = Open();

        cut.Find(".rf-tools button[aria-label='Bookmarks']").Click();

        Assert.Equal("Bookmarks", cut.Find(".rf-panel-title").TextContent.Trim());
        Assert.Equal(["Mine", "Everyone"], SegmentLabels(cut, "Bookmarks"));
        Assert.Contains(cut.FindAll(".rf-panel-body button"), button => button.TextContent.Trim() == "Bookmark this page");
        var body = cut.Find(".rf-panel-body").InnerHtml;
        Assert.True(body.IndexOf("Chapter 7 · 40%", StringComparison.Ordinal) < body.IndexOf("Highlights", StringComparison.Ordinal));
        Assert.True(body.IndexOf("Highlights", StringComparison.Ordinal) < body.IndexOf("A beginning is a very delicate time", StringComparison.Ordinal));
    }

    [Fact]
    public void Everyone_ListsOnlyTheSharedBookmarks_AndHasNoHighlights()
    {
        _stored.Add(new ReaderBookmarkDto { Id = Guid.NewGuid(), AssetId = Asset, CfiPosition = Cfi, Label = "Mine one", CreatedAt = DateTime.UtcNow });
        _stored.Add(new ReaderBookmarkDto { Id = Guid.NewGuid(), AssetId = Asset, CfiPosition = "epubcfi(/6/8!/4/2)", Label = "Shared one", IsGlobal = true, CreatedAt = DateTime.UtcNow });
        var cut = Open();
        cut.Find(".rf-tools button[aria-label='Bookmarks']").Click();
        Assert.DoesNotContain("Shared one", cut.Find(".rf-panel-body").TextContent, StringComparison.Ordinal);

        cut.FindAll(".app-segmented-control[aria-label='Bookmarks'] button").Single(button => button.TextContent.Trim() == "Everyone").Click();

        var text = cut.Find(".rf-panel-body").TextContent;
        Assert.Contains("Shared one", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Mine one", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Highlights", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePanelTitles_AreTheFramesNames_AndTheSearchPanelIsNamedForTheBook()
    {
        var cut = Open();

        cut.Find(".rf-tools button[aria-label='Contents']").Click();
        Assert.Equal("Contents", cut.Find(".rf-panel-title").TextContent.Trim());

        cut.Find(".rf-tools button[aria-label='Reading settings']").Click();
        Assert.Equal("Reading settings", cut.Find(".rf-panel-title").TextContent.Trim());

        cut.Find(".rf-tools button[aria-label='Search in this book']").Click();
        Assert.Equal("Search in this book", cut.Find(".rf-panel-title").TextContent.Trim());
    }

    [Fact]
    public async Task OnAPhone_TheTopLineHoldsOnlyBookmarkThisPageAndOneMoreButtonBesideClose()
    {
        await UsePhoneAsync();

        var cut = Open();

        Assert.Equal(["Bookmark this page", "More actions", "Close"], ToolLabels(cut));
        Assert.Empty(cut.FindAll("dialog"));
    }

    [Fact]
    public async Task OnAPhone_MoreOpensASheetWithTheOtherToolsInOrderAndChoosingOneOpensItsPanel()
    {
        await UsePhoneAsync();
        var cut = Open();

        cut.Find(".rf-tools button[aria-label='More actions']").Click();

        var sheet = cut.Find("dialog");
        Assert.Contains("app-native-dialog--sheet", sheet.ClassName, StringComparison.Ordinal);
        Assert.Equal(
            ["Search in this book", "Contents", "Bookmarks", "Reading settings"],
            sheet.QuerySelectorAll(".app-menu-item").Select(item => item.TextContent.Trim()).ToArray());

        sheet.QuerySelectorAll(".app-menu-item").Single(item => item.TextContent.Trim() == "Bookmarks").Click();

        cut.WaitForAssertion(() => Assert.Equal("Bookmarks", cut.Find(".rf-panel-title").TextContent.Trim()));
        Assert.Empty(cut.FindAll("dialog"));
    }

    [Fact]
    public async Task TappingThePage_HidesTheFramesBars_AndTappingAgainShowsThem()
    {
        var cut = Open();
        Assert.Equal("shown", cut.Find(".rf-shell").GetAttribute("data-chrome"));

        await cut.InvokeAsync(() => cut.Instance.OnToggleChrome());
        Assert.Equal("hidden", cut.Find(".rf-shell").GetAttribute("data-chrome"));

        await cut.InvokeAsync(() => cut.Instance.OnToggleChrome());
        Assert.Equal("shown", cut.Find(".rf-shell").GetAttribute("data-chrome"));
    }

    [Theory]
    [InlineData("light")]
    [InlineData("sepia")]
    [InlineData("dark")]
    [InlineData("system")]
    public void EachBookTheme_IsAClassOnTheFramesShell_AndSetsTheThreeFrameColours(string theme)
    {
        var cut = Open(theme: theme);

        var shell = cut.Find(".rf-shell");
        Assert.Contains("br-reader", shell.ClassList);
        Assert.Contains($"br-theme-{theme}", shell.ClassList);
        var css = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/css/book-reader.css"));
        var rule = theme == "light" ? @"\.rf-shell\.br-reader \{" : $@"\.rf-shell\.br-reader\.br-theme-{theme} \{{";
        Assert.Matches(rule + @"[^}]*--rf-page:[^}]*--rf-text:[^}]*--rf-accent:", css);
    }

    [Fact]
    public void TheBookStylesheet_HoldsOnlyTheStageThemesHighlightsAndPanelContents()
    {
        var css = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/css/book-reader.css"));

        foreach (var retired in new[] { ".br-shell", ".br-bar", ".br-title", ".br-actions", ".br-icon-btn", ".br-panel", ".br-overlay", "--br-page", "--br-chrome", "--br-focus" })
        {
            Assert.DoesNotContain(retired, css, StringComparison.Ordinal);
        }

        var script = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/wwwroot/js/book-reader.js"));
        Assert.Contains("'.rf-bar--top'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("br-bar", script, StringComparison.Ordinal);
        Assert.DoesNotContain("br-panel", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBookStringsTheFrameReplaced_AreGoneFromTheStringTable()
    {
        var strings = File.ReadAllText(Path.Combine(Root, "src/MediaEngine.Web/Resources/SharedStrings.resx"));

        foreach (var key in new[] { "BookReader_BackToLibrary", "BookReader_ReaderTools", "BookReader_Contents", "BookReader_BookmarksAndHighlights", "BookReader_ReadingSettings", "BookReader_ReturnToLibrary" })
        {
            Assert.DoesNotMatch(new Regex($"name=\"{key}\""), strings);
        }

        foreach (var language in new[] { "de", "es", "fr" })
        {
            var translated = File.ReadAllText(Path.Combine(Root, $"src/MediaEngine.Web/Resources/SharedStrings.{language}.resx"));
            Assert.Contains("name=\"BookReader_SearchInThisBook\"", translated, StringComparison.Ordinal);
        }
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
