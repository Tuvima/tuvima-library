using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
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
/// Match for failed items and an episode to any show (universal editor v3, WP4): the search starts from the file name, the top answer is
/// tagged "Best guess", the other episodes of the season can be matched by number, and a move to another place is flagged in Review when
/// the item's match does not name it.
/// </summary>
public sealed class EditorFailedItemMatchTests : AsyncBunitContext
{
    private static readonly Guid EpisodeId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SeasonId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly EditSessionState _session = new(new PendingEditList());
    private readonly List<string> _calls = [];
    private EditorMatchContextDto? _context;
    private EpisodeSiblingMatchPreviewDto? _siblings;

    public EditorFailedItemMatchTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _session.Begin([new EditTrailItem(EpisodeId, "Untitled file", "Item")]);
        _context = new EditorMatchContextDto
        {
            EntityId = EpisodeId,
            MediaType = "TV",
            FileName = "Parsed.Show.S02E02.mkv",
            ParsedShow = "Parsed Show",
            ParsedSeason = 2,
            ParsedEpisode = 2,
            BestGuess = new EditorMatchBestGuessDto { ProviderName = "tvdb", ProviderItemId = "200", Name = "Parsed Show" },
        };
        _siblings = new EpisodeSiblingMatchPreviewDto { EntityId = EpisodeId, SeriesId = "200", ShowName = "Parsed Show", SeasonNumber = 2, MatchableCount = 3 };
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchContextAsync), _ => Task.FromResult(_context));
            stub.SetHandler(nameof(IEngineApiClient.PreviewEpisodeSiblingMatchAsync), args =>
            {
                _calls.Add($"siblings:{args![1]}:{args[2]}");
                return Task.FromResult(_siblings);
            });
            stub.SetHandler(nameof(IEngineApiClient.SearchTvMatchBrowserShowsAsync), args =>
            {
                _calls.Add($"shows:{args![1]}");
                return Task.FromResult<TvMatchBrowserShowsDto?>(new TvMatchBrowserShowsDto((string)args[1]!,
                [
                    new TvMatchBrowserShowDto("100", "Folder Show", "2010", null, null, "https://thetvdb.test/100", true),
                    new TvMatchBrowserShowDto("200", "Parsed Show", "2015", null, null, "https://thetvdb.test/200", false),
                ]));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetTvMatchBrowserSeasonsAsync), args =>
            {
                var seriesId = (string)args![1]!;
                _calls.Add($"seasons:{seriesId}");
                return Task.FromResult<TvMatchBrowserSeasonsDto?>(new TvMatchBrowserSeasonsDto(seriesId, "Parsed Show",
                [
                    new TvMatchBrowserSeasonDto("s1", seriesId, 1, "Season 1", null, false),
                    new TvMatchBrowserSeasonDto("s2", seriesId, 2, "Season 2", null, false),
                ]));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetTvMatchBrowserEpisodesAsync), args =>
            {
                var seriesId = (string)args![1]!;
                var season = (int)args[2]!;
                _calls.Add($"episodes:{seriesId}:{season}");
                return Task.FromResult<TvMatchBrowserEpisodesDto?>(new TvMatchBrowserEpisodesDto(seriesId, "Parsed Show", $"s{season}", season,
                [
                    new TvMatchBrowserEpisodeDto($"e{season}1", seriesId, "Parsed Show", $"s{season}", season, 1, "First", null, null, null, "https://thetvdb.test/e", false, false, null),
                    new TvMatchBrowserEpisodeDto($"e{season}2", seriesId, "Parsed Show", $"s{season}", season, 2, "Second", null, null, null, "https://thetvdb.test/e", false, false, null),
                ]));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryMoveDestinationsAsync), _ =>
                Task.FromResult(LibraryMoveOutcome<LibraryMoveDestinationsResponse>.Ok(new LibraryMoveDestinationsResponse
                {
                    Destinations = [new LibraryMoveDestination { Id = SeasonId, Kind = LibraryMoveDestinationKinds.Season, Label = "Season 3", ParentLabel = "Other Show", HasList = true }],
                })));
            stub.SetHandler(nameof(IEngineApiClient.PreviewLibraryMoveAsync), _ =>
                Task.FromResult(LibraryMoveOutcome<LibraryMovePreview>.Ok(new LibraryMovePreview
                {
                    Rows = [new LibraryMovePreviewRow { ItemId = EpisodeId, FileNote = "Read-only folder: the file stays where it is." }],
                })));
        }));
        Render<AppPopoverHost>();
    }

    private static EditorMatchSubject Episode() => new(
        EpisodeId, "TV", "episode", "show_episode", "Untitled file", null, "Folder Show", null,
        new Dictionary<string, string> { ["title"] = "Untitled file" }, []);

    private IRenderedComponent<EditorMatchPanel> RenderPanel(bool unmatched = true) =>
        Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, Episode())
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.IsUnmatched, unmatched));

    [Fact]
    public void AnUnmatchedEpisode_StartsFromTheParsedShow_OpensTheParsedSeason_AndTagsTheBestGuess()
    {
        var cut = RenderPanel();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Episode'] .tl-tvm__row").Count));
        Assert.Contains("shows:Parsed Show", _calls);
        Assert.Contains("seasons:200", _calls);
        Assert.Contains("episodes:200:2", _calls);
        Assert.Contains("From the file name", cut.Find("[data-testid='match-file-hint']").TextContent, StringComparison.Ordinal);
        Assert.Contains("S02 E02", cut.Find("[data-testid='match-file-hint']").TextContent, StringComparison.Ordinal);
        var guessRow = cut.FindAll("[aria-label='Show'] .tl-tvm__row").Single(row => row.TextContent.Contains("Best guess", StringComparison.Ordinal));
        Assert.Contains("Parsed Show", guessRow.TextContent, StringComparison.Ordinal);
        var episodeGuess = cut.FindAll("[aria-label='Episode'] .tl-tvm__row").Single(row => row.TextContent.Contains("Best guess", StringComparison.Ordinal));
        Assert.Contains("Second", episodeGuess.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEpisodeThatIsMatched_StartsFromItsOwnShow_WithNoBestGuessOrFileHint()
    {
        var cut = RenderPanel(unmatched: false);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[aria-label='Show'] .tl-tvm__row")));
        Assert.Contains("shows:Folder Show", _calls);
        Assert.Empty(cut.FindAll("[data-testid='match-file-hint']"));
        Assert.DoesNotContain("Best guess", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchAllShows_ClearsTheBox_SoAnotherShowCanBeTyped()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[aria-label='Episode'] .tl-tvm__row")));

        cut.Find("button[aria-label='Search all shows']").Click();

        cut.WaitForAssertion(() => Assert.Contains("Type a show name to search all shows.", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[aria-label='Episode'] .tl-tvm__row"));
    }

    [Fact]
    public void ChoosingAnEpisode_OffersToMatchTheRestOfTheSeasonByNumber_OnByDefault_AndStagesIt()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Episode'] .tl-tvm__row").Count));

        cut.Find("button[aria-label='Select Second']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input[role=switch][aria-label='Match the other 3 episodes in this season by number']")));

        Assert.Contains("siblings:200:2", _calls);
        Assert.True(cut.Find("input[role=switch][aria-label='Match the other 3 episodes in this season by number']").HasAttribute("checked"));
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this match").Click();

        var identity = _session.Pending.Changes.Single(change => change.Kind == EditSessionChangeKinds.Identity);
        var siblings = _session.Pending.Changes.Single(change => change.Kind == EditSessionChangeKinds.SiblingMatch);
        Assert.Equal(EpisodeId, siblings.EntityId);
        Assert.True(EditSessionSiblingMatchTarget.TryParse(siblings.TargetId, out var target));
        Assert.Equal("200", target!.SeriesId);
        Assert.Equal(2, target.SeasonNumber);
        Assert.True(EditSessionIdentityTarget.TryParse(identity.TargetId, out var match));
        Assert.Equal("e22", match!.ProviderItemId);
        Assert.Equal("Parsed Show", match.RequiredFields[MetadataFieldConstants.ShowName]);
    }

    [Fact]
    public void TurningTheSeasonSwitchOff_StagesOnlyTheChosenEpisode()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Episode'] .tl-tvm__row").Count));
        cut.Find("button[aria-label='Select Second']").Click();
        var label = "Match the other 3 episodes in this season by number";
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll($"input[role=switch][aria-label='{label}']")));

        cut.Find($"input[role=switch][aria-label='{label}']").Change(false);
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this match").Click();

        Assert.Single(_session.Pending.Changes, change => change.Kind == EditSessionChangeKinds.Identity);
        Assert.DoesNotContain(_session.Pending.Changes, change => change.Kind == EditSessionChangeKinds.SiblingMatch);
    }

    [Fact]
    public void WhenNoOtherEpisodeHasANumberTheSeasonHas_TheSwitchIsOffAndSaysWhy()
    {
        _siblings = new EpisodeSiblingMatchPreviewDto { EntityId = EpisodeId, SeriesId = "200", ShowName = "Parsed Show", SeasonNumber = 2, MatchableCount = 0 };
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Episode'] .tl-tvm__row").Count));

        cut.Find("button[aria-label='Select Second']").Click();

        cut.WaitForAssertion(() => Assert.Contains("No other episode in this folder has a number", cut.Markup, StringComparison.Ordinal));
        var sw = cut.Find("input[role=switch][aria-label='Match the other 0 episodes in this season by number']");
        Assert.False(sw.HasAttribute("checked"));
        Assert.True(sw.HasAttribute("disabled"));
    }

    [Fact]
    public void MovingToAnotherSeasonOfAnotherShow_ShowsTheDestinationPreview_AndFlagsItInReviewWhenTheMatchDoesNotNameIt()
    {
        var cut = Render<EditorPlacementPicker>(parameters => parameters
            .Add(picker => picker.Subject, Episode())
            .Add(picker => picker.Session, _session)
            .Add(picker => picker.CanEdit, true));
        Assert.Contains("Move to another season or show", cut.Markup, StringComparison.Ordinal);

        cut.FindAll("button").First(button => button.TextContent.Trim() == "Move to another season or show").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Select Season 3']")));
        cut.Find("button[aria-label='Select Season 3']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Read-only folder", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Season 3 (Other Show)", cut.Find("[aria-label='Where it goes']").TextContent, StringComparison.Ordinal);
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this place").Click();

        var move = _session.Pending.Changes.Single(change => change.Kind == EditSessionChangeKinds.Relationship);
        Assert.Equal(SeasonId.ToString("D"), move.TargetId);
        Assert.DoesNotContain(_session.Pending.Changes, change => change.Kind == EditSessionChangeKinds.FileMove);
        var row = EditReviewRows.Build(_session.Pending, _ => "Untitled file", null).Single(r => r.Kind == EditSessionChangeKinds.Relationship);
        Assert.Contains("Not in this season", row.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void StagingAPlacement_CanBeUndone()
    {
        var cut = Render<EditorPlacementPicker>(parameters => parameters
            .Add(picker => picker.Subject, Episode())
            .Add(picker => picker.Session, _session)
            .Add(picker => picker.CanEdit, true));
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Move to another season or show").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Select Season 3']")));
        cut.Find("button[aria-label='Select Season 3']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[aria-label='Where it goes']")));
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this place").Click();

        cut.FindAll("button").First(button => button.TextContent.Trim() == "Undo").Click();

        Assert.Empty(_session.Pending.Changes);
    }
}
