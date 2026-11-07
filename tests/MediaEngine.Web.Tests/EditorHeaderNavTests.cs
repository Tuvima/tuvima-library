using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The nav row of the editor's header (universal editor v5, acceptance A1, 3a and the supporting-file rows of D2): which controls show for
/// which kind, what the item list says, how many supporting files show inline, where "All supporting files" and "Browse files and folders"
/// lead, and how a long list pages in. The Engine's header is test data; the component decides nothing from the kind of item.
/// </summary>
public sealed class EditorHeaderNavTests : AsyncBunitContext
{
    private readonly List<(Guid Entity, Guid? Level, string? Query, int Offset)> _pageReads = [];
    private Func<int, string?, EditorHeaderLevelDto?> _page = (_, _) => null;

    public EditorHeaderNavTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetEditorNavLevelAsync), args =>
            {
                var entity = (Guid)args![0]!;
                var level = (Guid?)args[1];
                var query = (string?)args[2];
                var offset = (int)args[3]!;
                _pageReads.Add((entity, level, query, offset));
                return Task.FromResult(_page(offset, query));
            })));
        Render<AppPopoverHost>();
    }

    private IRenderedComponent<EditorHeaderNav> Show(EditorHeaderDto header, EditorHeaderHooks? hooks = null) =>
        Render<EditorHeaderNav>(parameters => parameters.Add(nav => nav.Header, header).Add(nav => nav.Hooks, hooks));

    private static string OptionTitle(AngleSharp.Dom.IElement option) => option.QuerySelector(".tl-hnav__text > b")!.TextContent.Trim();

    // -- A1: which controls show ----------------------------------------------------------------------------------------------------

    [Fact]
    public void FromAnEpisode_TheHeaderShowsHomeASeasonDropdownAndAnEpisodeDropdown()
    {
        var cut = Show(EditorHeaderTestData.TvEpisode());

        Assert.Single(cut.FindAll(".tl-hnav__home"));
        var second = cut.Find("#tl-hnav-second");
        Assert.Equal("Season", second.GetAttribute("data-level"));
        Assert.Contains("Season 2", second.TextContent, StringComparison.Ordinal);
        var item = cut.Find("#tl-hnav-item");
        Assert.Contains("S02E03", item.TextContent, StringComparison.Ordinal);
        Assert.Contains("Echoes in the Dark", item.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHomeButton_NamesTheShowItGoesBackTo()
    {
        var cut = Show(EditorHeaderTestData.TvEpisode());

        Assert.Equal($"Go to {EditorHeaderTestData.ShowTitle}", cut.Find(".tl-hnav__home").GetAttribute("aria-label"));
    }

    [Fact]
    public void OnTheRoot_TheHomeButtonIsHighlightedAndMarkedCurrent()
    {
        var header = new EditorHeaderDto
        {
            Kind = EditorKinds.TvShow,
            Root = new EditorHeaderRootDto { Id = EditorHeaderTestData.ShowId, Title = EditorHeaderTestData.ShowTitle },
            CurrentId = EditorHeaderTestData.ShowId,
            ShowHome = true,
            HomeId = EditorHeaderTestData.ShowId,
            HomeIsCurrent = true,
            Level = new EditorHeaderLevelDto { SupportingFiles = new EditorHeaderSupportingFilesDto() },
            Config = EditorHeaderTestData.Config(EditorKinds.TvShow),
        };

        var home = Show(header).Find(".tl-hnav__home");

        Assert.Contains("is-current", home.ClassList);
        Assert.Equal("true", home.GetAttribute("aria-current"));
    }

    [Fact]
    public void AMovieOnItsOwn_ShowsNoSeasonOrEpisodeControlAtAll_AndNoNavRow()
    {
        var cut = Show(EditorHeaderTestData.Movie());

        Assert.Empty(cut.FindAll(".tl-hnav"));
        Assert.Empty(cut.FindAll(".tl-hnav__home"));
        Assert.Empty(cut.FindAll("#tl-hnav-second"));
        Assert.Empty(cut.FindAll("#tl-hnav-item"));
    }

    [Fact]
    public void AMovieWithSupportingFiles_ShowsTheItemDropdownAloneAndNothingElse()
    {
        var cut = Show(EditorHeaderTestData.Movie(withSupportingFiles: true));

        Assert.Single(cut.FindAll("#tl-hnav-item"));
        Assert.Empty(cut.FindAll("#tl-hnav-second"));
        Assert.Empty(cut.FindAll(".tl-hnav__home"));
    }

    [Fact]
    public void ACoursesLesson_ShowsSectionAndNumbers_NeverSeasonAndEpisodeCodes()
    {
        var cut = Show(EditorHeaderTestData.CourseLesson());

        Assert.Equal("Section", cut.Find("#tl-hnav-second").GetAttribute("data-level"));
        cut.Find("#tl-hnav-item").Click();
        var codes = cut.FindAll(".tl-hnav__option .tl-hnav__text > small").Select(code => code.TextContent.Trim()).ToList();
        Assert.Equal(["01 · 12 min", "02 · 14 min"], codes);
        Assert.DoesNotContain("Season", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Episode", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AWatchedLesson_ShowsTheWatchedTick_AndAnUnwatchedOneNone()
    {
        var cut = Show(EditorHeaderTestData.CourseLesson());

        cut.Find("#tl-hnav-item").Click();

        var rows = cut.FindAll(".tl-hnav__option[data-state]");
        Assert.NotEmpty(rows[0].QuerySelectorAll(".tl-hnav__mark--ok"));
        Assert.Contains("Watched", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Empty(rows[1].QuerySelectorAll(".tl-hnav__mark"));
    }

    // -- The item list: codes, stills and states -----------------------------------------------------------------------------------

    [Fact]
    public void TheEpisodeList_ShowsCodesTitlesAndStills_AndMarksTheCurrentRow()
    {
        var cut = Show(EditorHeaderTestData.TvEpisode());

        cut.Find("#tl-hnav-item").Click();

        var rows = cut.FindAll(".tl-hnav__option[data-state]");
        Assert.Equal(["Static", "The Relay", "Echoes in the Dark"], rows.Select(OptionTitle));
        Assert.Equal(["S02E01", "S02E02", "S02E03"], rows.Select(row => row.QuerySelector(".tl-hnav__text > small")!.TextContent.Trim()));
        Assert.All(rows, row => Assert.NotNull(row.QuerySelector(".tl-hnav__still img")));
        Assert.Equal(["false", "false", "true"], rows.Select(row => row.GetAttribute("aria-selected")));
    }

    [Fact]
    public void AMatchedEpisodeShowsATick_AndAnUnmatchedOneShowsAWarning()
    {
        var cut = Show(EditorHeaderTestData.TvEpisode());

        cut.Find("#tl-hnav-item").Click();

        var rows = cut.FindAll(".tl-hnav__option[data-state]");
        Assert.Equal(EditorNavItemStates.Matched, rows[0].GetAttribute("data-state"));
        Assert.NotEmpty(rows[0].QuerySelectorAll(".tl-hnav__mark--ok"));
        Assert.Equal(EditorNavItemStates.NotMatched, rows[1].GetAttribute("data-state"));
        Assert.NotEmpty(rows[1].QuerySelectorAll(".tl-hnav__mark--warn"));
    }

    [Fact]
    public void TheSeasonDropdown_ListsEachSeasonWithItsEpisodeAndSupportingFileCounts()
    {
        var cut = Show(EditorHeaderTestData.TvEpisode());

        cut.Find("#tl-hnav-second").Click();

        var rows = cut.FindAll(".tl-hnav__option");
        Assert.Equal(["Season 1", "Season 2"], rows.Select(OptionTitle));
        Assert.Contains("10 episodes · 4 supporting files", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("8 episodes · 12 supporting files", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Equal("true", rows[1].GetAttribute("aria-selected"));
    }

    [Fact]
    public void OpeningOneDropdown_ClosesTheOther()
    {
        var cut = Show(EditorHeaderTestData.TvEpisode());

        cut.Find("#tl-hnav-second").Click();
        cut.Find("#tl-hnav-item").Click();

        Assert.Equal("false", cut.Find("#tl-hnav-second").GetAttribute("aria-expanded"));
        Assert.Equal("true", cut.Find("#tl-hnav-item").GetAttribute("aria-expanded"));
    }

    // -- Supporting files ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WithMoreSupportingFilesThanTheInlineCount_OnlyTheInlineOnesShow_AndAllSupportingFilesCarriesTheTotal()
    {
        var cut = Show(EditorHeaderTestData.TvEpisode(supportingTotal: 238));

        cut.Find("#tl-hnav-item").Click();

        Assert.Equal(2, cut.FindAll(".tl-hnav__option[data-file-kind]").Count);
        var all = cut.Find("#tl-hnav-all-files");
        Assert.Contains("All supporting files (238)", all.TextContent, StringComparison.Ordinal);
        Assert.Contains("12 in this season · 238 in the show", all.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ASupportingFileRow_ShowsItsBadgeTitleGroupAndWhereItIsShown()
    {
        var cut = Show(EditorHeaderTestData.TvEpisode());

        cut.Find("#tl-hnav-item").Click();

        var rows = cut.FindAll(".tl-hnav__option[data-file-kind]");
        Assert.Equal("document", rows[0].GetAttribute("data-file-kind"));
        Assert.Contains("PDF", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Episode guide", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Scripts · with episode 3", rows[1].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheTitleHasNoMoreFilesThanAreShown_NoAllSupportingFilesRowIsDrawn()
    {
        var header = EditorHeaderTestData.Movie(withSupportingFiles: true);

        var cut = Show(header);
        cut.Find("#tl-hnav-item").Click();

        Assert.Empty(cut.FindAll("#tl-hnav-all-files"));
        Assert.Single(cut.FindAll(".tl-hnav__option[data-file-kind]"));
    }

    [Fact]
    public void TheFolderButton_ReadsBrowseFilesAndFoldersAndShowsThePath()
    {
        var cut = Show(EditorHeaderTestData.TvEpisode());

        cut.Find("#tl-hnav-item").Click();

        var folder = cut.Find("#tl-hnav-folder");
        Assert.Contains("Browse files and folders", folder.TextContent, StringComparison.Ordinal);
        Assert.Contains("The Last Signal / Season 02", folder.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ASupportingFileRow_AllSupportingFilesAndTheFolderButton_EachCallTheirHook()
    {
        var opened = new List<string>();
        var hooks = new EditorHeaderHooks
        {
            OpenSupportingFile = id => { opened.Add($"file:{id}"); return Task.CompletedTask; },
            OpenSupportingFiles = request => { opened.Add($"all:{request.EntityId}:{request.ScopeKey}"); return Task.CompletedTask; },
            OpenFolders = id => { opened.Add($"folders:{id}"); return Task.CompletedTask; },
        };
        var cut = Show(EditorHeaderTestData.TvEpisode(), hooks);

        cut.Find("#tl-hnav-item").Click();
        cut.FindAll(".tl-hnav__option[data-file-kind]")[0].Click();
        cut.Find("#tl-hnav-item").Click();
        cut.Find("#tl-hnav-all-files").Click();
        cut.Find("#tl-hnav-item").Click();
        cut.Find("#tl-hnav-folder").Click();

        Assert.Equal(
        [
            $"file:{EditorHeaderTestData.FileOneId}",
            $"all:{EditorHeaderTestData.EpisodeThreeId}:season:2",
            $"folders:{EditorHeaderTestData.EpisodeThreeId}",
        ], opened);
    }

    [Fact]
    public void WithNoHooksYet_TheSupportingFileRowsStillDrawAndAClickOpensNothing()
    {
        var cut = Show(EditorHeaderTestData.TvEpisode());

        cut.Find("#tl-hnav-item").Click();
        cut.Find("#tl-hnav-all-files").Click();

        Assert.Equal("false", cut.Find("#tl-hnav-item").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void TheRowsOfTheItemList_AreButtonsSoTheKeyboardReachesThem()
    {
        var cut = Show(EditorHeaderTestData.TvEpisode());

        cut.Find("#tl-hnav-item").Click();

        Assert.All(cut.FindAll(".tl-hnav__option"), option =>
        {
            Assert.Equal("BUTTON", option.TagName);
            Assert.Null(option.GetAttribute("tabindex"));
        });
        Assert.Equal("BUTTON", cut.Find("#tl-hnav-all-files").TagName);
        Assert.Equal("BUTTON", cut.Find("#tl-hnav-folder").TagName);
    }

    // -- Paging and search ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void AListLongerThanOnePage_SaysHowManyAreShownAndReadsTheNextPageOnRequest()
    {
        _page = (offset, _) => new EditorHeaderLevelDto
        {
            LevelId = EditorHeaderTestData.SeasonTwoId,
            Total = 120,
            Offset = offset,
            PageSize = 50,
            HasMore = false,
            Items =
            [
                EditorHeaderTestData.Episode(Guid.NewGuid(), "S02E04", "Four"),
                EditorHeaderTestData.Episode(Guid.NewGuid(), "S02E05", "Five"),
            ],
        };
        var cut = Show(EditorHeaderTestData.TvEpisode(hasMore: true));

        cut.Find("#tl-hnav-item").Click();
        Assert.Equal("Showing 3 of 120", cut.Find("#tl-hnav-more").TextContent.Trim());
        cut.Find("#tl-hnav-more button").Click();

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".tl-hnav__option[data-state]").Count));
        var read = Assert.Single(_pageReads);
        Assert.Equal((EditorHeaderTestData.EpisodeThreeId, (Guid?)EditorHeaderTestData.SeasonTwoId, (string?)null, 3), read);
        Assert.Empty(cut.FindAll("#tl-hnav-more"));
    }

    [Fact]
    public void Searching_ReadsTheLevelWithTheTypedText_AndShowsOnlyWhatComesBack()
    {
        _page = (_, query) => new EditorHeaderLevelDto
        {
            LevelId = EditorHeaderTestData.SeasonTwoId,
            Total = 1,
            PageSize = 50,
            Items = [EditorHeaderTestData.Episode(EditorHeaderTestData.EpisodeTwoId, "S02E02", "The Relay")],
        };
        var cut = Show(EditorHeaderTestData.TvEpisode());

        cut.Find("#tl-hnav-item").Click();
        cut.Find(".tl-hnav__search input").Input("relay");

        cut.WaitForAssertion(() => Assert.Equal(["The Relay"], cut.FindAll(".tl-hnav__option[data-state]").Select(OptionTitle)));
        Assert.Contains(_pageReads, read => read.Query == "relay" && read.Offset == 0);
    }

    [Fact]
    public void AListOfOneItem_OffersNoSearchBox()
    {
        var cut = Show(EditorHeaderTestData.Movie(withSupportingFiles: true));

        cut.Find("#tl-hnav-item").Click();

        Assert.Empty(cut.FindAll(".tl-hnav__search"));
    }
}
