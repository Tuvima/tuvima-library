using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Match tab's TV flow (universal editor v5, 3c, mockup G3): the show card and the plain episode table, the episode the file name names
/// pre-selected with the reason, Specials as season 0, an absolute number shown as the provider's season and episode, the verdict with its
/// length line, and Apply match staging the match with its move and, for a show, the one re-match row.
/// </summary>
public sealed class EditorTvMatchFlowTests : AsyncBunitContext
{
    private static readonly Guid ItemId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly EditSessionState _session = new(new PendingEditList());
    private readonly List<string> _calls = [];
    private readonly List<ItemCanonicalRetailCandidateDto> _judged = [];
    private EditorMatchContextDto _context = new() { EntityId = ItemId, ParsedShow = "The Last Signal", ParsedSeason = 2, ParsedEpisode = 3 };
    private EditorMatchSetupDto _setup;
    private EditorMatchPlacementDto? _placement;
    private TvMatchBrowserEpisodeDto? _absolute;

    public EditorTvMatchFlowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _session.Begin([new EditTrailItem(ItemId, "Echoes in the Dark", "Item")]);
        _setup = SetupOf("tv_episode", 2752);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchContextAsync), _ => Task.FromResult<EditorMatchContextDto?>(_context));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorCurrentMatchAsync), _ => Task.FromResult<EditorCurrentMatchDto?>(new EditorCurrentMatchDto { EntityId = ItemId }));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchSetupAsync), _ => Task.FromResult<EditorMatchSetupDto?>(_setup));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchPlacementAsync), _ => Task.FromResult(_placement));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchVerdictAsync), args =>
            {
                var candidate = (ItemCanonicalRetailCandidateDto)args![1]!;
                _judged.Add(candidate);
                return Task.FromResult<EditorMatchResultDto?>(new EditorMatchResultDto
                {
                    Candidate = candidate,
                    ProviderName = "tvdb",
                    Title = candidate.Title,
                    Verdict = EditorMatchVerdicts.Strong,
                    Reasons =
                    [
                        new EditorMatchReasonDto { Fact = "number", State = EditorMatchEvidenceStates.Agrees, Text = "Season and episode match" },
                        new EditorMatchReasonDto { Fact = "runtime", State = EditorMatchEvidenceStates.Agrees, Text = "Runtime consistent", Note = "45:52 measured · 46 min (rounded)" },
                    ],
                });
            });
            stub.SetHandler(nameof(IEngineApiClient.PreviewShowChildRematchAsync), args =>
                Task.FromResult<ShowChildRematchPreviewDto?>(new ShowChildRematchPreviewDto
                {
                    EntityId = ItemId,
                    SeriesId = (string)args![1]!,
                    ShowName = "The Last Signal",
                    EpisodeCount = 26,
                    MatchableCount = 24,
                    NotFoundCount = 2,
                }));
            stub.SetHandler(nameof(IEngineApiClient.SearchTvMatchBrowserShowsAsync), args =>
            {
                _calls.Add($"shows:{args![1]}");
                return Task.FromResult<TvMatchBrowserShowsDto?>(new TvMatchBrowserShowsDto((string)args[1]!,
                [
                    new TvMatchBrowserShowDto("100", "The Last Signal", "2019", "A radio operator hears a message.", null, "https://example.test/100", false),
                    new TvMatchBrowserShowDto("200", "Signal Lost", "2015", null, null, "https://example.test/200", false),
                ]));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetTvMatchBrowserSeasonsAsync), args =>
                Task.FromResult<TvMatchBrowserSeasonsDto?>(new TvMatchBrowserSeasonsDto((string)args![1]!, "The Last Signal",
                [
                    new TvMatchBrowserSeasonDto("s0", "100", 0, "Specials", null, false),
                    new TvMatchBrowserSeasonDto("s1", "100", 1, "Season 1", null, false),
                    new TvMatchBrowserSeasonDto("s2", "100", 2, "Season 2", null, false),
                    new TvMatchBrowserSeasonDto("s3", "100", 3, "Season 3", null, false),
                ], "Drama", "A radio operator hears a message.", "2019", null)));
            stub.SetHandler(nameof(IEngineApiClient.GetTvMatchBrowserEpisodesAsync), args =>
            {
                var season = (int)args![2]!;
                _calls.Add($"episodes:{season}");
                return Task.FromResult<TvMatchBrowserEpisodesDto?>(new TvMatchBrowserEpisodesDto("100", "The Last Signal", $"s{season}", season, Episodes(season)));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetTvMatchBrowserAbsoluteEpisodeAsync), args =>
            {
                _calls.Add($"absolute:{args![2]}");
                return Task.FromResult(_absolute);
            });
        }));
        Render<AppPopoverHost>();
    }

    private static EditorMatchSetupDto SetupOf(string kind, double? measuredSeconds) => new()
    {
        EntityId = ItemId,
        Kind = kind,
        Providers = [new EditorMatchProviderDto { Name = "tvdb", DisplayName = "TheTVDB", State = EditorMatchProviderStates.Ready, IsDefault = true }],
        Fields = [new EditorMatchFieldDto { Key = "show", Label = "Show", Value = "The Last Signal" }],
        Evidence = new EditorMatchLocalEvidenceDto { MeasuredSeconds = measuredSeconds, MeasuredText = "45:52" },
    };

    private static IReadOnlyList<TvMatchBrowserEpisodeDto> Episodes(int season) =>
    [
        Episode(season, 1, "Static", "2021-01-04", 58),
        Episode(season, 2, "Carrier Wave", "2021-01-11", 44),
        Episode(season, 3, "Echoes in the Dark", "2021-01-18", 46),
        Episode(season, 4, "Dead Air", "2021-01-25", 52),
    ];

    private static TvMatchBrowserEpisodeDto Episode(int season, int number, string title, string aired, int runtime) =>
        new($"e{season}{number}", "100", "The Last Signal", $"s{season}", season, number, title, aired, null, null, "https://example.test/e", false, false, null, runtime);

    private static EditorMatchSubject EpisodeSubject() => new(
        ItemId, "TV", "episode", "show_episode", "Echoes in the Dark", null, "The Last Signal", "2021",
        new Dictionary<string, string> { ["title"] = "Echoes in the Dark" }, []);

    private static EditorMatchSubject ShowSubject() => new(
        ItemId, "TV", "series", "show", "The Last Signal", null, null, "2019",
        new Dictionary<string, string> { ["title"] = "The Last Signal" }, []);

    private IRenderedComponent<EditorMatchPanel> RenderPanel(EditorMatchSubject subject)
    {
        return Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, subject)
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.IsUnmatched, true));
    }

    // Opening only fills the boxes; the person presses Search and chooses the show.
    private static void SearchAndChooseTheFirstShow(IRenderedComponent<EditorMatchPanel> cut)
    {
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=tv-flow-search]").HasAttribute("disabled")));
        cut.Find("[data-testid=tv-flow-search]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=tv-flow-show-row]")));
        cut.FindAll("[data-testid=tv-flow-show-row]").First().Click();
    }

    private IRenderedComponent<EditorMatchPanel> OpenEpisode()
    {
        var cut = RenderPanel(EpisodeSubject());
        SearchAndChooseTheFirstShow(cut);
        return cut;
    }

    private static void Apply(IRenderedComponent<EditorMatchPanel> cut) =>
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Apply match").Click();

    [Fact]
    public void TheEpisodeTheFileNameNames_IsPreSelected_WithTheReasonAndTheLengthLine()
    {
        var cut = OpenEpisode();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=tv-flow-suggested]")));
        Assert.Equal("Suggested from filename: S02E03", cut.Find("[data-testid=tv-flow-suggested]").TextContent.Trim());
        var selected = Assert.Single(cut.FindAll("[data-testid=tv-flow-episode].is-active"));
        Assert.Equal("3", selected.GetAttribute("data-number"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-length-line]")));
        Assert.Equal("Your file: 45:52 measured · Provider: 46 min (rounded)", cut.Find("[data-testid=match-length-line]").TextContent.Trim());
        Assert.Contains("Strong match", cut.Find("[data-testid=match-verdict]").TextContent, StringComparison.Ordinal);
        Assert.Equal("Echoes in the Dark", Assert.Single(_judged).Title);
        Assert.Contains("episodes:2", _calls);
    }

    [Fact]
    public void TheEpisodeTable_IsPlainText_WithEpisodeTitleAirDateAndRuntime()
    {
        var cut = OpenEpisode();

        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("[data-testid=tv-flow-episode]").Count));
        var headings = cut.FindAll("[data-testid=tv-flow-table] th").Select(heading => heading.TextContent.Trim()).ToList();
        Assert.Equal(["Episode", "Title", "Air date", "Runtime", "Chosen"], headings);
        var third = cut.FindAll("[data-testid=tv-flow-episode]")[2];
        Assert.Contains("Echoes in the Dark", third.TextContent, StringComparison.Ordinal);
        Assert.Contains("S02E03", third.TextContent, StringComparison.Ordinal);
        Assert.Contains("18 Jan 2021", third.TextContent, StringComparison.Ordinal);
        Assert.Contains("46 min", third.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpecial_OpensOnSeasonZero_NamedSpecials()
    {
        _context = new EditorMatchContextDto { EntityId = ItemId, ParsedShow = "The Last Signal", ParsedSeason = 0, ParsedEpisode = 2 };
        var cut = OpenEpisode();

        cut.WaitForAssertion(() => Assert.Contains("episodes:0", _calls));
        cut.WaitForAssertion(() => Assert.Equal("Suggested from filename: S00E02", cut.Find("[data-testid=tv-flow-suggested]").TextContent.Trim()));
        Assert.Contains("Specials", cut.Find("[data-testid=tv-flow-season]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsoluteNumber_IsShownAsTheProvidersSeasonAndEpisode()
    {
        _context = new EditorMatchContextDto { EntityId = ItemId, ParsedShow = "The Last Signal", ParsedAbsolute = 47 };
        _absolute = Episode(3, 2, "Carrier Wave", "2022-02-14", 44);
        var cut = OpenEpisode();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=tv-flow-suggested]")));
        Assert.Contains("absolute number 47 is S03E02", cut.Find("[data-testid=tv-flow-suggested]").TextContent, StringComparison.Ordinal);
        Assert.Contains("episodes:3", _calls);
        Assert.Equal("2", Assert.Single(cut.FindAll("[data-testid=tv-flow-episode].is-active")).GetAttribute("data-number"));
    }

    [Fact]
    public void AFileWithNoNumber_FallsBackToTheNearestRuntime()
    {
        _context = new EditorMatchContextDto { EntityId = ItemId, ParsedShow = "The Last Signal", ParsedSeason = 2 };
        var cut = OpenEpisode();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=tv-flow-suggested]")));
        Assert.Equal("Suggested from runtime: S02E03", cut.Find("[data-testid=tv-flow-suggested]").TextContent.Trim());
    }

    [Fact]
    public void ApplyingAnEpisode_StagesTheMatch_TheMove_AndAManagedFileMove()
    {
        _placement = new EditorMatchPlacementDto
        {
            DestinationId = Guid.NewGuid(),
            ParentLabel = "The Last Signal · Season 2",
            CurrentParentLabel = "Signal Lost · Season 2",
            Managed = true,
            FromPath = "/media/tv/Signal Lost/Season 02/s02e03.mkv",
            ToPath = "/media/tv/The Last Signal/Season 02/s02e03.mkv",
        };
        var cut = OpenEpisode();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-length-line]")));

        Apply(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-staged]")));
        var kinds = _session.Pending.Changes.Select(change => change.Kind).Order().ToList();
        Assert.Equal(new[] { EditSessionChangeKinds.FileMove, EditSessionChangeKinds.Identity, EditSessionChangeKinds.Relationship }.Order().ToList(), kinds);
    }

    [Fact]
    public void ApplyingAnEpisode_InAReadOnlyFolder_StagesNoFileMove()
    {
        _placement = new EditorMatchPlacementDto
        {
            DestinationId = Guid.NewGuid(),
            ParentLabel = "The Last Signal · Season 2",
            CurrentParentLabel = "Signal Lost · Season 2",
            Managed = false,
        };
        var cut = OpenEpisode();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-length-line]")));

        Apply(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-staged]")));
        Assert.DoesNotContain(_session.Pending.Changes, change => change.Kind == EditSessionChangeKinds.FileMove);
        Assert.Contains(_session.Pending.Changes, change => change.Kind == EditSessionChangeKinds.Relationship);
    }

    [Fact]
    public void ApplyingANewShow_StagesOneRematchRow_SayingHowManyEpisodesFollowAndHowManyGoToNeedsReview()
    {
        _setup = SetupOf("tv_show", null);
        var cut = RenderPanel(ShowSubject());

        SearchAndChooseTheFirstShow(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=tv-flow-card]")));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-verdict-bar] [data-testid=match-verdict]")));
        Assert.Empty(cut.FindAll("[data-testid=tv-flow-table]"));

        Apply(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-staged]")));
        var rematch = Assert.Single(_session.Pending.Changes, change => change.Kind == EditSessionChangeKinds.ChildRematch);
        Assert.Equal("24 episodes follow The Last Signal by number; 2 go to Needs Review", _session.Pending.Label(rematch.Id));
        Assert.True(EditSessionChildRematchTarget.TryParse(rematch.TargetId, out var target));
        Assert.Equal("100", target!.SeriesId);
        Assert.Single(_session.Pending.Changes, change => change.Kind == EditSessionChangeKinds.Identity);
    }

    [Fact]
    public void ChangingTheShow_ListsTheShowsAgain_AndAPickedShowOpensItsSeasons()
    {
        var cut = OpenEpisode();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=tv-flow-card]")));

        cut.Find("[data-testid=tv-flow-change-show]").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=tv-flow-show-row]").Count));
        Assert.Empty(cut.FindAll("[data-testid=tv-flow-table]"));
    }

    [Fact]
    public void AnItemThatIsNotMatchedYet_IsNotSearchedOnOpening_UntilThePersonPressesSearch()
    {
        var cut = RenderPanel(EpisodeSubject());

        cut.WaitForAssertion(() => Assert.Equal("The Last Signal", cut.Find("input[aria-label='Show']").GetAttribute("value")));
        Assert.DoesNotContain(_calls, call => call.StartsWith("shows:", StringComparison.Ordinal));
        Assert.Single(cut.FindAll("[data-testid=match-row]"));

        cut.Find("[data-testid=tv-flow-search]").Click();

        cut.WaitForAssertion(() => Assert.Contains("shows:The Last Signal", _calls));
    }

    [Fact]
    public void TheShowBoxes_SitInTheMatchTabsOneFirstRow_BesideTheProvider_AndTheSourceSelectorIsOnItsOwnLine()
    {
        var cut = RenderPanel(EpisodeSubject());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=tv-flow-show]")));
        var row = Assert.Single(cut.FindAll("[data-testid=match-row]"));
        Assert.NotNull(row.QuerySelector("[data-testid=tv-flow-show]"));
        Assert.NotNull(row.QuerySelector("[data-testid=tv-flow-year]"));
        Assert.NotNull(row.QuerySelector("[data-testid=tv-flow-search]"));
        Assert.Empty(row.QuerySelectorAll("[data-testid=match-source]"));
        Assert.Single(cut.FindAll("[data-testid=match-source]"));
    }

    [Fact]
    public void AnEpisodeRow_IsOneButton_ShowsTheSlotAndATick_AndPickingAnotherJudgesIt()
    {
        var cut = OpenEpisode();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-length-line]")));

        var picks = cut.FindAll("[data-testid=tv-flow-episode-pick]");
        Assert.Equal("S02E03", picks[2].TextContent.Trim());
        Assert.Equal("true", picks[2].GetAttribute("aria-pressed"));
        Assert.Equal("Episode S02E03, Echoes in the Dark", picks[2].GetAttribute("aria-label"));
        Assert.Contains("✓", cut.FindAll("[data-testid=tv-flow-episode]")[2].TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=tv-flow-episode][aria-selected]"));

        picks[1].Click();

        cut.WaitForAssertion(() => Assert.Equal("Carrier Wave", _judged[^1].Title));
        cut.WaitForAssertion(() => Assert.Equal("true", cut.FindAll("[data-testid=tv-flow-episode-pick]")[1].GetAttribute("aria-pressed")));
    }

    [Fact]
    public void TheShowAndEpisodeLists_AreListsOfItems_NotButtonsPretendingToBeItems()
    {
        var cut = RenderPanel(EpisodeSubject());
        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=tv-flow-search]").HasAttribute("disabled")));
        cut.Find("[data-testid=tv-flow-search]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=tv-flow-show-row]")));

        Assert.Empty(cut.FindAll("button[role=listitem]"));
        Assert.All(cut.FindAll("[data-testid=tv-flow-shows] [role=listitem]"), item => Assert.NotNull(item.QuerySelector("button")));
    }
}
