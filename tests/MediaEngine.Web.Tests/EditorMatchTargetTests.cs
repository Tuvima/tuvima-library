using System.Reflection;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Matching;
using MediaEngine.Web.Components.MediaEditor;

namespace MediaEngine.Web.Tests;

public sealed class EditorMatchTargetTests
{
    [Theory]
    [InlineData("TV", "series", true)]
    [InlineData("TV", "episode", true)]
    [InlineData("Movies", "item", true)]
    [InlineData("Books", "book", true)]
    [InlineData("Music", "album", true)]
    public void MatchingFollowsEditingTargetEvenWhenStaleContextAdvertisesLinks(string mediaType, string scopeId, bool expected)
    {
        var shell = new TargetShell();
        shell.Configure(mediaType, scopeId, "Selected item");
        Assert.Equal(expected, shell.MatchingAllowed);
        Assert.Equal(expected, shell.VisibleTabs.Contains("links"));
        Assert.Equal(expected, shell.SearchAllowed);
        Assert.Equal(expected, shell.CanApplyRetail());
    }

    [Fact]
    public void TargetHeadingDoesNotFollowTheMatchSearchTarget()
    {
        var shell = new TargetShell();
        shell.Configure("TV", "episode", "Pilot");
        shell.SetField("_canonicalTargetGroup", "show");
        Assert.Equal("Pilot", shell.TargetTitle);
    }

    [Fact]
    public void SeasonUsesTheScopedTvdbBrowserInsteadOfGenericSearch()
    {
        var shell = new TargetShell();
        shell.Configure("TV", "season", "Season 2");

        Assert.True(shell.MatchingAllowed);
        Assert.Contains("links", shell.VisibleTabs);
        Assert.False(shell.SearchAllowed);
        Assert.True(shell.CanApplyRetail());
    }

    [Theory]
    [InlineData("default", "Default order")]
    [InlineData("official", "Official order")]
    [InlineData("dvd", "DVD order")]
    [InlineData("absolute", "Absolute order")]
    public void TvdbOrderLabelsExplainTheSelectedNumberingScheme(string seasonType, string expectedLabel) =>
        Assert.Equal(expectedLabel, TargetShell.SeasonTypeLabel(seasonType));

    [Fact]
    public void EpisodePickerRequiresAConfirmedParentSeasonInTheSelectedOrder()
    {
        var shell = new TargetShell();
        shell.Configure("TV", "episode", "Episode one");

        shell.SetTvdbCandidates(hasConfirmedSeasonMatch: false, confirmedSeasonNumber: null);
        Assert.False(shell.CanSelectTvdbCandidate);
        Assert.Contains("Match the parent season", shell.TvdbSeasonRequirement);

        shell.SetTvdbCandidates(hasConfirmedSeasonMatch: true, confirmedSeasonNumber: 2);
        shell.SetTvdbSeasonSelection("1");
        Assert.False(shell.CanSelectTvdbCandidate);
        Assert.Contains("Select that season", shell.TvdbSeasonRequirement);

        shell.SetTvdbSeasonSelection("2");
        Assert.True(shell.CanSelectTvdbCandidate);
        Assert.Contains("Season 2 is confirmed", shell.TvdbSeasonRequirement);
    }

    [Fact]
    public void ScopedPickerRequiresTheBrowsedOrderToBeAppliedToTheShow()
    {
        var shell = new TargetShell();
        shell.Configure("TV", "episode", "Episode one");
        shell.SetTvdbCandidates(hasConfirmedSeasonMatch: true, confirmedSeasonNumber: 2,
            showSeasonType: "default");
        shell.SetTvdbSeasonSelection("2");

        Assert.False(shell.CanSelectTvdbCandidate);
        Assert.Contains("Apply this episode order to the show", shell.TvdbSeasonRequirement);
    }

    [Fact]
    public void SeasonPickerShowsOnlyTheSelectedProviderSeason()
    {
        var shell = new TargetShell();
        shell.Configure("TV", "season", "Season 2");
        shell.SetTvdbSeasonCandidates();
        shell.SetTvdbSeasonSelection("2");

        Assert.Equal(["Season 2"], shell.VisibleTvdbCandidateTitles);
    }

    [Fact]
    public void ShowWideOrderRequiresAValidPreviewBeforeItCanApply()
    {
        var shell = new TargetShell();
        shell.Configure("TV", "season", "Season 2");
        shell.SetTvdbSeasonCandidates();
        shell.SetTvdbSeasonType("official");

        Assert.True(shell.CanReviewShowOrder);
        Assert.False(shell.CanApplyShowOrder);

        shell.SetShowOrderPreview(canApply: true, currentSeasonType: "default", requestedSeasonType: "official");
        Assert.True(shell.CanApplyShowOrder);

        shell.SetShowOrderPreview(canApply: false, currentSeasonType: "default", requestedSeasonType: "official");
        Assert.False(shell.CanApplyShowOrder);
    }

    [Fact]
    public void ShowWideOrderImpactExplainsWhetherAnExistingChildMatchIsSafe()
    {
        var mapped = new TvdbShowOrderImpactDto(Guid.NewGuid(), "episode", "episode-2", 2, 4, true, null);
        var blocked = new TvdbShowOrderImpactDto(Guid.NewGuid(), "season", "season-1", 1, null, false, null);

        Assert.Equal("S2 E4", TargetShell.ShowOrderImpactLabel(mapped));
        Assert.Contains("stays valid", TargetShell.ShowOrderImpactMessage(mapped), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Season 1", TargetShell.ShowOrderImpactLabel(blocked));
        Assert.Contains("rematched", TargetShell.ShowOrderImpactMessage(blocked), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("TV", "series", "show")]
    [InlineData("TV", "episode", "show_episode")]
    [InlineData("Music", "album", "album")]
    [InlineData("Music", "track", "track")]
    [InlineData("Comics", "series", "series")]
    [InlineData("Comics", "issue", "issue")]
    [InlineData("Books", "series", "series")]
    [InlineData("Books", "book", "book_identity")]
    [InlineData("Audiobooks", "series", "series")]
    [InlineData("Audiobooks", "audiobook", "audiobook_identity")]
    public void SearchTargetFollowsTheActiveHierarchyScope(string mediaType, string scopeId, string expectedTarget)
    {
        var shell = new TargetShell();
        shell.Configure(mediaType, scopeId, "Selected item");

        Assert.Equal(expectedTarget, shell.SearchTarget);
        Assert.Equal(expectedTarget, shell.DefaultTarget);
    }

    [Fact]
    public void TrackPlacementMakesTheOneTrackMoveDistinctFromAlbumMatching()
    {
        var shell = new TargetShell();
        shell.Configure("Music", "track", "Track one");

        Assert.Contains("Move this track to album", shell.PlacementLabels);
        Assert.Contains("only this track", shell.PlacementDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("every track", shell.AlbumPlacementHelp, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("TV", "series", "Solo Leveling", "Series title")]
    [InlineData("Music", "album", "GNX", "Album title")]
    [InlineData("Comics", "series", "Saga", "Series title")]
    public void ParentComparisonUsesParentTitleInsteadOfOpenedChild(
        string mediaType, string scopeId, string expectedTitle, string expectedLabel)
    {
        var shell = new TargetShell();
        shell.Configure(mediaType, scopeId, "Selected parent");
        var draft = new Dictionary<string, string>
        {
            ["title"] = "Opened child title",
            ["show_name"] = "Solo Leveling",
            ["album"] = "GNX",
            ["series"] = "Saga",
        };

        Assert.Equal(expectedTitle, shell.ComparisonTitle(draft));
        Assert.Equal(expectedLabel, shell.ComparisonTitleLabel);
    }

    [Fact]
    public void ComicIssuePlacementUsesSeriesRunWithoutChangingTheIssueMatch()
    {
        var shell = new TargetShell();
        shell.Configure("Comics", "issue", "Issue one");

        Assert.Equal(new[] { "series", "series_position" }, shell.PlacementKeys);
        Assert.Equal("Issue", shell.SearchTargetLabel);
    }

    [Theory]
    [InlineData("Books", "series", true)]
    [InlineData("Audiobooks", "series", true)]
    [InlineData("Books", "book", false)]
    [InlineData("Audiobooks", "audiobook", false)]
    [InlineData("Comics", "series", false)]
    public void SeriesScopesWithLeafOnlyProviderIdsUseWikidataInsteadOfRetail(string mediaType, string scopeId, bool expected)
    {
        var shell = new TargetShell();
        shell.Configure(mediaType, scopeId, "Selected item");

        Assert.Equal(expected, shell.RequiresWikidataOnly);
        Assert.Equal(!expected, shell.RetailSearchAllowed);
    }

    private sealed class TargetShell : SharedMediaEditorShell
    {
        public static string SeasonTypeLabel(string seasonType) => TvdbSeasonTypeLabel(seasonType);
        public static string ShowOrderImpactLabel(TvdbShowOrderImpactDto impact) => TvdbShowOrderImpactLabel(impact);
        public static string ShowOrderImpactMessage(TvdbShowOrderImpactDto impact) => TvdbShowOrderImpactMessage(impact);
        public bool CanSelectTvdbCandidate => CanSelectTvdbCandidates;
        public bool CanReviewShowOrder => CanReviewTvdbShowOrder;
        public bool CanApplyShowOrder => CanApplyTvdbShowOrder;
        public string TvdbSeasonRequirement => TvdbEpisodeSeasonRequirement;
        public IReadOnlyList<string> VisibleTvdbCandidateTitles => FilteredTvdbCandidates.Select(candidate => candidate.Title).ToList();
        public bool MatchingAllowed => CanMatchCurrentTarget;
        public bool SearchAllowed => SupportsCanonicalSearch;
        public string TargetTitle => CurrentTargetTitle;
        public string SearchTarget => QuickSearchTargets.Single().Key;
        public string SearchTargetLabel => QuickSearchTargets.Single().Label;
        public string DefaultTarget => GetDefaultCanonicalTargetGroupForActiveScope("fallback");
        public IReadOnlyList<string> PlacementLabels => GetParentPositionFields().Select(placementField => placementField.Label).ToList();
        public IReadOnlyList<string> PlacementKeys => GetParentPositionFields().Select(placementField => placementField.Key).ToList();
        public string PlacementDescription => GetLibraryPlacementDescription();
        public string AlbumPlacementHelp => GetParentPositionHelp("album");
        public bool RequiresWikidataOnly => RequiresWikidataOnlySearch;
        public bool RetailSearchAllowed => SupportsRetailSearch;
        public string ComparisonTitle(IReadOnlyDictionary<string, string> draft) => GetScopedComparisonTitle(draft);
        public string ComparisonTitleLabel => GetScopedComparisonTitleLabel();
        public IEnumerable<string> VisibleTabs => Tabs.Select(tab => tab.Id);
        public bool CanApplyRetail()
        {
            var candidate = new ItemCanonicalRetailCandidateDto { CandidateId = "test", ProviderName = "tmdb", ProviderItemId = "123", IsApplicable = true };
            SetField("_selectedRetailCandidateId", "retail:test:tmdb:123:");
            return CanApplyRetailCandidate(candidate);
        }
        public void Configure(string mediaType, string scopeId, string title)
        {
            SetField("_editorContext", new MediaEditorContextDto
            {
                MediaType = mediaType,
                Scopes = [
                    new() { ScopeId = scopeId, DisplayTitle = title, AvailableTabs = ["details", "artwork", "links"] },
                    new() { ScopeId = "series", DisplayTitle = "Parent series" }
                ]
            });
            SetField("_activeScopeId", scopeId);
        }
        public void SetField(string name, object value) =>
            typeof(SharedMediaEditorShell).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, value);
        public void SetTvdbCandidates(bool hasConfirmedSeasonMatch, int? confirmedSeasonNumber,
            string showSeasonType = "official") =>
            SetField("_tvdbCandidates", new TvdbScopedMatchCandidatesDto(
                "episode", "Show", "show-id", "revision", 2, 1, [2], [], "official", ["official"],
                hasConfirmedSeasonMatch, confirmedSeasonNumber, showSeasonType));
        public void SetTvdbSeasonSelection(string seasonNumber) =>
            SetField("_tvdbSeasonSelection", seasonNumber);
        public void SetTvdbSeasonType(string seasonType) =>
            SetField("_tvdbSeasonTypeSelection", seasonType);
        public void SetShowOrderPreview(bool canApply, string currentSeasonType, string requestedSeasonType) =>
            SetField("_tvdbShowOrderPreview", new TvdbShowOrderPreviewDto(
                "show-id", currentSeasonType, requestedSeasonType, "revision", ["default", "official"], [], canApply, null));
        public void SetTvdbSeasonCandidates() =>
            SetField("_tvdbCandidates", new TvdbScopedMatchCandidatesDto(
                "season", "Show", "show-id", "revision", 2, null, [1, 2],
                [
                    new TvdbMatchCandidateDto("season-1", "show-id", "Season 1", 1, null, null, null, null, "official", "https://thetvdb.com/seasons/season-1"),
                    new TvdbMatchCandidateDto("season-2", "show-id", "Season 2", 2, null, null, null, null, "official", "https://thetvdb.com/seasons/season-2"),
                ], "official", ["official"], ShowSeasonType: "official"));
    }
}
