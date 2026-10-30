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
            .Add(panel => panel.CanEdit, true));

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
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#tl-match-review")));
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this match").Click();

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
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#tl-match-review")));
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Use this match").Click();

        var change = _session.Pending.Changes.Single(c => c.Kind == EditSessionChangeKinds.Identity);
        Assert.True(EditSessionIdentityTarget.TryParse(change.TargetId, out var match));
        Assert.Equal("show", match!.TargetFieldGroup);
        Assert.Equal("200", match.ProviderItemId);
        Assert.False(match.BridgeIds.ContainsKey(BridgeIdKeys.TvdbEpisodeId));
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
    public void OnlyShowAndEpisodePages_UseTheBrowser()
    {
        Assert.True(EditorTvMatchCandidates.UsesBrowser("TV", "series"));
        Assert.True(EditorTvMatchCandidates.UsesBrowser("TV", "episode"));
        Assert.False(EditorTvMatchCandidates.UsesBrowser("TV", "season"));
        Assert.False(EditorTvMatchCandidates.UsesBrowser("Music", "track"));
    }
}
