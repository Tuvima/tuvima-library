using System.Reflection;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Search;
using MediaEngine.Web.Components.MediaEditor;

namespace MediaEngine.Web.Tests;

public sealed class EditorMatchTargetTests
{
    [Fact]
    public void WikidataCandidateSubtitleShowsEntityQidAndHidesRawInstanceClassQid()
    {
        var shell = new TargetShell();
        var subtitle = shell.WikidataSubtitle(new ItemCanonicalLinkedCandidateDto
        {
            Qid = "Q123",
            InstanceOf = "Q999",
            Year = "2024",
            Author = "Example author",
        });

        Assert.StartsWith("Q123 · ", subtitle);
        Assert.DoesNotContain("Q999", subtitle);
        Assert.Contains("2024", subtitle);
        Assert.Contains("Example author", subtitle);
        Assert.EndsWith("Wikidata", subtitle);
    }

    [Theory]
    [InlineData(null, "Not linked")]
    [InlineData("Q200", "Q200")]
    public void OptionalEpisodeIdentityNeverBorrowsTheParentMatch(string? episodeQid, string expected)
    {
        var shell = new TargetShell();
        shell.ConfigureOptionalEpisode(episodeQid);
        shell.SetField("_canonicalTargetGroup", "show");

        Assert.Equal(expected, shell.CurrentQid);
        Assert.Equal("episode", shell.CanonicalScopeId);
        Assert.False(shell.CanEditCanonical);
    }

    [Fact]
    public void StaticHeaderKeepsTheParentWhenTheEditingTargetChanges()
    {
        var shell = new TargetShell();
        shell.ConfigureOptionalEpisode(null);

        Assert.Equal("Parent show", shell.StaticTitle);
        Assert.Equal("2024", shell.StaticYear);
        Assert.Equal("Episode one", shell.TargetTitle);
        shell.SetField("_activeScopeId", "series");
        Assert.Equal("Parent show", shell.StaticTitle);
    }

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

    [Fact]
    public void EpisodePickerAllowsSelectingAnEpisodeFromTheCurrentSeason()
    {
        var shell = new TargetShell();
        shell.Configure("TV", "episode", "Episode one");

        shell.SetTvdbCandidates(hasConfirmedSeasonMatch: false, confirmedSeasonNumber: null);
        Assert.True(shell.CanSelectTvdbCandidate);

        shell.SetTvdbCandidates(hasConfirmedSeasonMatch: true, confirmedSeasonNumber: 2);
        Assert.True(shell.CanSelectTvdbCandidate);
    }

    [Fact]
    public void SeasonPickerShowsEveryMatchingSeason()
    {
        var shell = new TargetShell();
        shell.Configure("TV", "season", "Season 2");
        shell.SetTvdbSeasonCandidates();

        Assert.Equal(["Season 1", "Season 2"], shell.VisibleTvdbCandidateTitles);
    }

    [Fact]
    public void RetailComparisonDoesNotCallTwoMissingCreatorsDifferent()
    {
        var shell = new TargetShell();
        shell.Configure("TV", "series", "Solo Leveling");
        var candidate = new ItemCanonicalRetailCandidateDto
        {
            Title = "Solo Leveling",
            MatchScores = new() { AuthorScore = 0 },
        };

        Assert.Equal(-1, shell.RetailCreatorComparisonScore(candidate));
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

    [Fact]
    public void AlbumRecordingListCannotMutateAFileWithoutProvenReleaseTrackMove()
    {
        var shell = new TargetShell();
        shell.Configure("Music", "track", "Track one");
        shell.SetField("_canonicalTargetGroup", "track");
        var album = new ItemCanonicalRetailCandidateDto
        {
            ProviderName = "musicbrainz",
            BridgeIds = new(StringComparer.OrdinalIgnoreCase) { ["musicbrainz_release_id"] = "release-id" },
        };
        var detail = new RetailCandidateDetailDto
        {
            DetailKind = "track_list",
            Items = [new() { Title = "Track one", ProviderItemId = "recording-id", Ordinal = 1 }],
        };

        Assert.False(shell.CanSelectAlbumTrack(album, detail));
        Assert.False(shell.CanApplyRetail());
        album.BridgeIds.Clear();
        Assert.False(shell.CanSelectAlbumTrack(album, detail));
    }

    [Fact]
    public void SelectedMatchIsGuardedAndCancelDiscardsWithoutChangingFields()
    {
        var shell = new TargetShell();
        shell.Configure("Books", "book", "Book");
        shell.SetField("_selectedWikidataCandidateId", "qid:Q123");
        Assert.True(shell.PendingNavigation);
        shell.CancelCandidate();
        Assert.False(shell.PendingNavigation);
    }

    [Fact]
    public async Task ClosingWithMatchDraftOnLinksShowsAndCanDismissDiscardPrompt()
    {
        var shell = new TargetShell();
        shell.Configure("Books", "book", "Book");
        shell.SetActiveTab("links");
        shell.SetField("_selectedWikidataCandidateId", "qid:Q123");

        Assert.True(shell.PendingNavigation);
        Assert.False(shell.NonInlineFooterVisible);

        await shell.RequestClose();

        Assert.True(shell.ConfirmDiscard);
        Assert.True(shell.NonInlineFooterVisible);

        shell.KeepEditing();

        Assert.False(shell.ConfirmDiscard);
        Assert.False(shell.NonInlineFooterVisible);
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

    [Theory]
    [InlineData("album-1", true)]
    [InlineData("recording-2", false)]
    public void TrackIdentityKeepsAlbumProviderContextSeparate(string trackProviderId, bool parentOnly)
    {
        var shell = new TargetShell();
        shell.SetField("_editorContext", new MediaEditorContextDto
        {
            MediaType = "Music",
            Scopes =
            [
                new() { ScopeId = "album", Order = 0, DisplayTitle = "Parent album",
                    IdentitySummary = new() { ProviderName = "musicbrainz", ProviderItemId = "album-1", WikidataQid = "Q100" } },
                new() { ScopeId = "track", Order = 1, DisplayTitle = "Owned track", IdentityMatchOptional = true,
                    CanonicalIdentityMode = "inherited", CanonicalIdentityOwnerScopeId = "album",
                    IdentitySummary = new() { ProviderName = "musicbrainz", ProviderItemId = trackProviderId, MatchLevel = "track" } },
            ],
        });
        shell.SetField("_activeScopeId", "track");

        Assert.Equal(parentOnly, shell.ParentRetailOnly);
        Assert.Equal("Not linked", shell.CurrentQid);
    }

    private sealed class TargetShell : SharedMediaEditorShell
    {
        public bool ParentRetailOnly => UsesParentRetailIdentityOnly;
        public string CurrentQid => CurrentCanonicalQid;
        public string? CanonicalScopeId => CanonicalIdentityTargetScope?.ScopeId;
        public bool CanEditCanonical => CanEditCanonicalIdentity;
        public string StaticTitle => StaticHeaderTitle;
        public string? StaticYear => StaticHeaderYear;
        public bool PendingNavigation => HasPendingNavigationChanges;
        public bool ConfirmDiscard => (bool)typeof(SharedMediaEditorShell).GetField("_confirmDiscard", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!;
        public bool NonInlineFooterVisible => ShouldShowNonInlineEditorFooter;
        public Task RequestClose() => HandleClose();
        public void KeepEditing() => SetField("_confirmDiscard", false);
        public void SetActiveTab(string tabId) =>
            ((MediaEditorTabState)typeof(SharedMediaEditorShell).GetField("_tabState", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this)!).Activate(tabId);
        public void CancelCandidate() => CancelMatchDraft();
        public bool CanSelectTvdbCandidate => CanSelectTvdbCandidates;
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
        public string WikidataSubtitle(ItemCanonicalLinkedCandidateDto candidate) => BuildWikidataCandidateSubtitle(candidate);
        public bool RetailSearchAllowed => SupportsRetailSearch;
        public bool CanSelectAlbumTrack(ItemCanonicalRetailCandidateDto album, RetailCandidateDetailDto detail) =>
            CanSelectRetailCatalogChild(album, detail);
        public string ComparisonTitle(IReadOnlyDictionary<string, string> draft) => GetScopedComparisonTitle(draft);
        public double RetailCreatorComparisonScore(ItemCanonicalRetailCandidateDto candidate) =>
            BuildRetailComparisonRows(candidate).Single(row => row.Label == "Creator").Score;
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
        public void ConfigureOptionalEpisode(string? episodeQid)
        {
            SetField("_editorContext", new MediaEditorContextDto
            {
                MediaType = "TV",
                Scopes =
                [
                    new() { ScopeId = "series", Order = 0, FieldEntityId = Guid.NewGuid(), DisplayTitle = "Parent show",
                        DisplaySubtitle = "2024", IdentitySummary = new() { WikidataQid = "Q100" } },
                    new() { ScopeId = "episode", Order = 2, FieldEntityId = Guid.NewGuid(), DisplayTitle = "Episode one",
                        IdentityMatchOptional = true, CanonicalIdentityMode = "inherited", CanonicalIdentityOwnerScopeId = "series",
                        IdentitySummary = new() { WikidataQid = episodeQid } },
                ],
            });
            SetField("_activeScopeId", "episode");
        }
        public void SetField(string name, object value) =>
            typeof(SharedMediaEditorShell).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(this, value);
        public void SetTvdbCandidates(bool hasConfirmedSeasonMatch, int? confirmedSeasonNumber,
            string showSeasonType = "official") =>
            SetField("_tvdbCandidates", new TvdbScopedMatchCandidatesDto(
                "episode", "Show", "show-id", "revision", 2, 1, [2], [], "official", ["official"],
                hasConfirmedSeasonMatch, confirmedSeasonNumber, showSeasonType));
        public void SetTvdbSeasonCandidates() =>
            SetField("_tvdbCandidates", new TvdbScopedMatchCandidatesDto(
                "season", "Show", "show-id", "revision", 2, null, [1, 2],
                [
                    new TvdbMatchCandidateDto("season-1", "show-id", "Season 1", 1, null, null, null, null, "official", "https://thetvdb.com/seasons/season-1"),
                    new TvdbMatchCandidateDto("season-2", "show-id", "Season 2", 2, null, null, null, null, "official", "https://thetvdb.com/seasons/season-2"),
                ], "official", ["official"], ShowSeasonType: "official"));
    }
}
