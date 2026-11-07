using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Match tool's TV browser (universal editor v2, D4, WP6): an episode can be matched into another show's episode through
/// Show, Season and Episode; a show result is never offered for an episode; a show page needs only the Show column.
/// </summary>
public sealed class EditorTvMatchBrowserTests : AsyncBunitContext
{
    private static readonly Guid EpisodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly EditSessionState _session = new(new PendingEditList());
    private readonly List<string> _calls = [];

    public EditorTvMatchBrowserTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _session.Begin([new EditTrailItem(EpisodeId, "Pilot", "Item")]);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchContextAsync), _ => Task.FromResult(_context));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorCurrentMatchAsync), _ => Task.FromResult(_currentMatch));
            // The Match tab opens on its read of the item; the TV flows sit inside Change match, so these items open there (not matched yet).
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchSetupAsync), args =>
                Task.FromResult<EditorMatchSetupDto?>(new EditorMatchSetupDto
                {
                    EntityId = EpisodeId,
                    Kind = "tv_episode",
                    Providers = [new EditorMatchProviderDto { Name = "tvdb", DisplayName = "TheTVDB", State = EditorMatchProviderStates.Ready, IsDefault = true }],
                    Fields = [new EditorMatchFieldDto { Key = "title", Label = "Title" }],
                }));
            stub.SetHandler(nameof(IEngineApiClient.PreviewShowChildRematchAsync), args =>
                Task.FromResult<ShowChildRematchPreviewDto?>(new ShowChildRematchPreviewDto { EntityId = EpisodeId, SeriesId = (string)args![1]!, ShowName = "Other Show", EpisodeCount = 4, MatchableCount = 3, NotFoundCount = 1 }));
            stub.SetHandler(nameof(IEngineApiClient.SearchTvMatchBrowserShowsAsync), args =>
            {
                _calls.Add($"shows:{args![1]}");
                return Task.FromResult<TvMatchBrowserShowsDto?>(new TvMatchBrowserShowsDto((string)args[1]!,
                [
                    new TvMatchBrowserShowDto("100", "Original Show", "2010", null, null, "https://thetvdb.test/100", true),
                    new TvMatchBrowserShowDto("200", "Other Show", "2015", null, null, "https://thetvdb.test/200", false),
                ]));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetTvMatchBrowserSeasonsAsync), args =>
            {
                var seriesId = (string)args![1]!;
                _calls.Add($"seasons:{seriesId}");
                return Task.FromResult<TvMatchBrowserSeasonsDto?>(new TvMatchBrowserSeasonsDto(seriesId, seriesId == "200" ? "Other Show" : "Original Show",
                [
                    new TvMatchBrowserSeasonDto("s1", seriesId, 1, "Season 1", null, seriesId == "100"),
                ]));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetTvMatchBrowserEpisodesAsync), args =>
            {
                var seriesId = (string)args![1]!;
                _calls.Add($"episodes:{seriesId}:{args[2]}");
                var show = seriesId == "200" ? "Other Show" : "Original Show";
                return Task.FromResult<TvMatchBrowserEpisodesDto?>(new TvMatchBrowserEpisodesDto(seriesId, show, "s1", 1,
                [
                    new TvMatchBrowserEpisodeDto($"e{seriesId}1", seriesId, show, "s1", 1, 1, "Opening", "2015-03-01", null, null, "https://thetvdb.test/e", seriesId == "100", seriesId == "100", null),
                    new TvMatchBrowserEpisodeDto($"e{seriesId}2", seriesId, show, "s1", 1, 2, "Second", "2015-03-08", null, null, "https://thetvdb.test/e", false, false, null),
                ]));
            });
        }));
        Render<AppPopoverHost>();
    }

    private EditorMatchContextDto? _context;
    private EditorCurrentMatchDto? _currentMatch;

    private static EditorMatchSubject Episode() => new(
        EpisodeId, "TV", "episode", "show_episode", "Pilot", null, "Original Show", "2010",
        new Dictionary<string, string> { ["title"] = "Pilot" }, []);

    private static EditorMatchSubject Show() => new(
        EpisodeId, "TV", "series", "show", "Original Show", null, null, "2010",
        new Dictionary<string, string> { ["title"] = "Original Show" }, []);

    private IRenderedComponent<EditorMatchPanel> RenderPanel(EditorMatchSubject subject) =>
        Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, subject)
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.IsUnmatched, true));

    private static void Apply(IRenderedComponent<EditorMatchPanel> cut)
    {
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button").First(button => button.TextContent.Trim() == "Apply match").HasAttribute("disabled")));
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Apply match").Click();
    }

    private static EditorMatchContextDto ParsedFromTheFileName() => new()
    {
        EntityId = EpisodeId,
        MediaType = "TV",
        FileName = "Parsed.Show.S01E02.mkv",
        ParsedShow = "Original Show",
        ParsedSeason = 1,
        ParsedEpisode = 2,
        BestGuess = new EditorMatchBestGuessDto { ProviderName = "tvdb", ProviderItemId = "100", Name = "Original Show" },
    };

    [Fact]
    public void AnUnmatchedEpisode_StartsFromTheParsedShow_OpensTheParsedSeason_AndTagsTheBestGuess()
    {
        _context = ParsedFromTheFileName();
        var cut = RenderPanel(Episode());

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Episode'] .tl-tvm__row").Count));
        Assert.Contains("shows:Original Show", _calls);
        Assert.Contains("seasons:100", _calls);
        Assert.Contains("episodes:100:1", _calls);
        var guessRow = cut.FindAll("[aria-label='Show'] .tl-tvm__row").Single(row => row.TextContent.Contains("Best guess", StringComparison.Ordinal));
        Assert.Contains("Original Show", guessRow.TextContent, StringComparison.Ordinal);
        var episodeGuess = cut.FindAll("[aria-label='Episode'] .tl-tvm__row").Single(row => row.TextContent.Contains("Best guess", StringComparison.Ordinal));
        Assert.Contains("Second", episodeGuess.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEpisodeThatIsMatched_StartsFromItsOwnShow_WithNoBestGuess()
    {
        _context = ParsedFromTheFileName();
        _currentMatch = new EditorCurrentMatchDto
        {
            EntityId = EpisodeId,
            State = EditorCurrentMatchStates.Matched,
            Record = new EditorMatchedRecordDto { ProviderId = "tvdb", ProviderName = "TheTVDB", ProviderItemId = "e1002", Kind = "Episode", Title = "Second" },
        };
        var cut = Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, Episode())
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.IsUnmatched, false));

        // A matched item opens on its match; Change match opens the TV lists on its own show, where nothing is tagged as a guess.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));
        cut.Find("[data-testid=match-change]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Episode'] .tl-tvm__row").Count));
        Assert.Contains("shows:Original Show", _calls);
        Assert.DoesNotContain("Best guess", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchAllShows_ClearsTheBox_SoAnotherShowCanBeTyped()
    {
        _context = ParsedFromTheFileName();
        var cut = RenderPanel(Episode());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[aria-label='Episode'] .tl-tvm__row")));

        cut.Find("button[aria-label='Search all shows']").Click();

        cut.WaitForAssertion(() => Assert.Contains("Type a show name to search all shows.", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[aria-label='Episode'] .tl-tvm__row"));
    }

    [Fact]
    public void AnEpisodePage_OpensItsOwnShowAndSeason_AndListsRealEpisodes()
    {
        var cut = RenderPanel(Episode());

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Episode'] .tl-tvm__row").Count));
        Assert.Contains("shows:Original Show", _calls);
        Assert.Contains("seasons:100", _calls);
        Assert.Contains("episodes:100:1", _calls);
        Assert.Contains("This episode", cut.Find("[aria-label='Episode']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoosingAnEpisodeOfAnotherShow_StagesTheExactShowSeasonAndEpisode()
    {
        var cut = RenderPanel(Episode());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Show'] .tl-tvm__row").Count));

        cut.Find("button[aria-label='Open Other Show']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Open Season 1']")));
        cut.Find("button[aria-label='Open Season 1']").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Episode'] .tl-tvm__row").Count));
        cut.Find("button[aria-label='Select Second']").Click();
        Apply(cut);

        var change = _session.Pending.Changes.Single(c => c.Kind == EditSessionChangeKinds.Identity);
        Assert.True(EditSessionIdentityTarget.TryParse(change.TargetId, out var match));
        Assert.Equal("show_episode", match!.TargetFieldGroup);
        Assert.Equal("e2002", match.ProviderItemId);
        Assert.Equal("200", match.BridgeIds[BridgeIdKeys.TvdbId]);
        Assert.Equal("e2002", match.BridgeIds[BridgeIdKeys.TvdbEpisodeId]);
        Assert.Equal("Other Show", match.RequiredFields[MetadataFieldConstants.ShowName]);
        Assert.Equal("1", match.RequiredFields[MetadataFieldConstants.SeasonNumber]);
        Assert.Equal("2", match.RequiredFields[MetadataFieldConstants.EpisodeNumber]);
    }

    [Fact]
    public void AShowPage_ShowsOnlyTheShowColumn_AndStagesTheShow()
    {
        var cut = RenderPanel(Show());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Show'] .tl-tvm__row").Count));

        Assert.Empty(cut.FindAll("[aria-label='Season']"));
        Assert.Empty(cut.FindAll("[aria-label='Episode']"));
        cut.Find("button[aria-label='Select Other Show']").Click();
        Apply(cut);

        var change = _session.Pending.Changes.Single(c => c.Kind == EditSessionChangeKinds.Identity);
        Assert.True(EditSessionIdentityTarget.TryParse(change.TargetId, out var match));
        Assert.Equal("show", match!.TargetFieldGroup);
        Assert.Equal("200", match.ProviderItemId);
        Assert.False(match.BridgeIds.ContainsKey(BridgeIdKeys.TvdbEpisodeId));
    }

    [Fact]
    public void AnEpisodeMatch_NeverOffersTheSeasonsAndEpisodesRematch()
    {
        var cut = RenderPanel(Episode());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Episode'] .tl-tvm__row").Count));
        cut.Find("button[aria-label='Select Second']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-results]")));

        Assert.Empty(cut.FindAll("input[role=switch][aria-label='Re-match seasons and episodes']"));
    }

    [Fact]
    public void ARecordThatIsTheItemItself_CannotBeChosenAgain()
    {
        var cut = RenderPanel(Episode());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Episode'] .tl-tvm__row").Count));

        Assert.True(cut.Find("button[aria-label='Select Opening']").HasAttribute("disabled"));
        Assert.False(cut.Find("button[aria-label='Select Second']").HasAttribute("disabled"));
    }

    [Fact]
    public void AnEpisode_IsStagedWithApplyMatch_AndNothingIsWrittenUntilSave()
    {
        var cut = RenderPanel(Episode());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Episode'] .tl-tvm__row").Count));

        // Pick the episode, then Apply match: one Identity entry waits in the pending list; Save changes (the window's footer) writes it.
        cut.Find("button[aria-label='Select Second']").Click();
        Apply(cut);
        Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity);
        Assert.Contains("Nothing is saved until you press Save changes", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void APastedTheTvdbLink_OpensThatShowDirectly()
    {
        var cut = RenderPanel(Episode());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Show'] .tl-tvm__row").Count));

        cut.Find("input[aria-label='TheTVDB link or ID']").Input("https://thetvdb.com/series/200");
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Open show").Click();

        cut.WaitForAssertion(() => Assert.Contains("seasons:200", _calls));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Open Season 1']")));
    }

    [Fact]
    public void ALinkThatIsNotTheTvdbs_SaysSoInsteadOfSearching()
    {
        var cut = RenderPanel(Episode());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Show'] .tl-tvm__row").Count));

        cut.Find("input[aria-label='TheTVDB link or ID']").Input("https://www.imdb.com/title/tt3230854/");
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Open show").Click();

        cut.WaitForAssertion(() => Assert.Contains("Only TheTVDB", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public void TheEpisodeFilter_NarrowsByTitleWordsNumberOrAirDate()
    {
        var cut = RenderPanel(Episode());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Episode'] .tl-tvm__row").Count));

        cut.Find("input[aria-label='Find an episode']").Input("sec");
        Assert.Single(cut.FindAll("[aria-label='Episode'] .tl-tvm__row"));
        cut.Find("input[aria-label='Find an episode']").Input("2015-03-01");
        Assert.Single(cut.FindAll("[aria-label='Episode'] .tl-tvm__row"));
        Assert.Contains("Opening", cut.Find("[aria-label='Episode']").TextContent, StringComparison.Ordinal);
        cut.Find("input[aria-label='Find an episode']").Input("zzz");
        Assert.Empty(cut.FindAll("[aria-label='Episode'] .tl-tvm__row"));
        Assert.Contains("Nothing matches that.", cut.Find("[aria-label='Episode']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TypingTheNumbersByHand_StagesSeasonAndEpisodeAsTheItemsOwnDetails()
    {
        var cut = RenderPanel(Episode());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-manual-numbers]")));

        cut.Find("input[aria-label='Season number']").Input("2");
        cut.Find("input[aria-label='Episode number']").Input("7");
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Use these numbers").Click();

        Assert.True(_session.Pending.TryGetField(EpisodeId, MetadataFieldConstants.SeasonNumber, out var season));
        Assert.True(_session.Pending.TryGetField(EpisodeId, MetadataFieldConstants.EpisodeNumber, out var episode));
        Assert.Equal("2", season);
        Assert.Equal("7", episode);
        Assert.DoesNotContain(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity);
    }

    [Fact]
    public void OnlyShowAndEpisodePages_UseTheBrowser()
    {
        Assert.True(EditorTvMatchCandidates.UsesBrowser("TV", "series"));
        Assert.True(EditorTvMatchCandidates.UsesBrowser("TV", "episode"));
        Assert.False(EditorTvMatchCandidates.UsesBrowser("TV", "season"));
        Assert.False(EditorTvMatchCandidates.UsesBrowser("Music", "track"));
    }
}
