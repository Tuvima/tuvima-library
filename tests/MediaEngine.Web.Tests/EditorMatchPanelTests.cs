using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Matching;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Match tab's Media providers side (universal editor v5, 3c, mockups G1 and G2): a matched item opens on what it is matched to and
/// searches nothing; Change match opens the compact search; Apply match is always available and only stages (with the move the match
/// brings, in a Managed folder only); Remove match stages a removal; an unmatched item opens on the pre-filled search; after Save the panel
/// reads the Engine again; there is no Change placement and no line-up table.
/// </summary>
public sealed class EditorMatchPanelTests : AsyncBunitContext
{
    private static readonly Guid MovieId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly EditSessionState _session = new(new PendingEditList());
    private readonly List<EditorMatchSearchRequestDto> _searches = [];
    private readonly List<string> _writes = [];
    private EditorMatchSearchResultDto? _answer;
    private EditorMatchPlacementDto? _placement;
    private int _currentReads;

    public EditorMatchPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _session.Begin([new EditTrailItem(MovieId, "Heat", "Item")]);
        _answer = Found(Result("Heat", "1995", EditorMatchVerdicts.Strong), Result("Heat (1986)", "1986", EditorMatchVerdicts.Check));
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorCurrentMatchAsync), _ =>
            {
                _currentReads++;
                return Task.FromResult<EditorCurrentMatchDto?>(_matched ? _current ?? Matched() : new EditorCurrentMatchDto { EntityId = MovieId });
            });
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchSetupAsync), args =>
                Task.FromResult<EditorMatchSetupDto?>(_setup(args!)));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchPlacementAsync), _ =>
                Task.FromResult(_placement));
            stub.SetHandler(nameof(IEngineApiClient.SearchEditorMatchAsync), args =>
            {
                _searches.Add((EditorMatchSearchRequestDto)args![1]!);
                return Task.FromResult(_answer);
            });
            stub.SetHandler(nameof(IEngineApiClient.ReplaceRetailMatchAsync), _ =>
            {
                _writes.Add("retail");
                return Task.FromResult<ItemCanonicalApplyResponseDto?>(null);
            });
            stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), _ =>
            {
                _writes.Add("session");
                return Task.FromResult<LibraryBatchEditOutcome<EditSessionResult>>(default!);
            });
        }));
        Render<AppPopoverHost>();
    }

    private bool _matched = true;
    private EditorCurrentMatchDto? _current;
    private Func<object?[], EditorMatchSetupDto> _setup = _ => Setup("movie", noProvider: false);

    private static EditorMatchSubject Movie() => new(
        MovieId, "Movies", "movie", "movie", "Heat", "Michael Mann", null, "1995",
        new Dictionary<string, string> { ["title"] = "Heat" }, []);

    private static EditorMatchSetupDto Setup(string kind, bool noProvider) => new()
    {
        EntityId = MovieId,
        Kind = kind,
        FileName = "Heat (1995) 1080p.mkv",
        NoProvider = noProvider,
        NoProviderMessage = noProvider ? "No provider matches this kind of item." : null,
        Providers = noProvider ? [] :
        [
            new EditorMatchProviderDto { Name = "tmdb", DisplayName = "TMDB", State = EditorMatchProviderStates.Ready, IsDefault = true },
            new EditorMatchProviderDto { Name = "apple_api", DisplayName = "Apple", State = EditorMatchProviderStates.NeedsKey },
        ],
        Fields = noProvider ? [] :
        [
            new EditorMatchFieldDto { Key = "title", Label = "Title", Value = "Heat", From = "file_name" },
            new EditorMatchFieldDto { Key = "year", Label = "Year", Value = "1995", From = "file_name" },
        ],
    };

    private static EditorCurrentMatchDto Matched(string title = "Heat", string itemId = "949", IReadOnlyList<EditorMatchEvidenceRowDto>? why = null) => new()
    {
        EntityId = MovieId,
        State = EditorCurrentMatchStates.Matched,
        Verdict = EditorMatchVerdicts.Strong,
        Record = new EditorMatchedRecordDto { ProviderId = "tmdb", ProviderName = "TMDB", ProviderItemId = itemId, Kind = "Film", Title = title, Year = "1995", Creator = "Michael Mann" },
        Why = why ??
        [
            new EditorMatchEvidenceRowDto { Field = "title", YourFile = "Heat", MatchedRecord = "Heat", State = EditorMatchEvidenceStates.Agrees, Verdict = EditorMatchVerdicts.Strong },
            new EditorMatchEvidenceRowDto { Field = "year", YourFile = "1995", MatchedRecord = "1995", State = EditorMatchEvidenceStates.Agrees, Verdict = EditorMatchVerdicts.Strong },
        ],
    };

    private static EditorMatchResultDto Result(string title, string year, string verdict) => new()
    {
        Candidate = new ItemCanonicalRetailCandidateDto
        {
            CandidateId = $"tmdb:{title}",
            ProviderId = "tmdb",
            ProviderName = "TMDB",
            ProviderItemId = $"id-{year}",
            Title = title,
            Year = year,
            RequiredFields = new Dictionary<string, string> { ["title"] = title },
        },
        ProviderName = "TMDB",
        Title = title,
        Year = year,
        Creator = "Michael Mann",
        LengthText = "170 min",
        Verdict = verdict,
        Reasons = [new EditorMatchReasonDto { Fact = "title", State = EditorMatchEvidenceStates.Agrees, Text = "Title matches" }],
    };

    private static EditorMatchSearchResultDto Found(params EditorMatchResultDto[] results) => new()
    {
        EntityId = MovieId,
        Provider = "tmdb",
        Query = "Heat",
        Results = results,
    };

    private IRenderedComponent<EditorMatchPanel> RenderPanel(bool unmatched = false)
    {
        _matched = !unmatched;
        return Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, Movie())
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.IsUnmatched, unmatched));
    }

    private static void ClickButton(IRenderedComponent<EditorMatchPanel> cut, string text) =>
        cut.FindAll("button").First(button => button.TextContent.Trim() == text).Click();

    [Fact]
    public void AMatchedItem_OpensOnItsMatchAndWhy_AndSearchesNothing()
    {
        var cut = RenderPanel();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));
        Assert.Contains("Heat", cut.Find("[data-testid=match-current]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Fields you changed stay yours; the rest update.", cut.Find("[data-testid=match-why]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Strong match", cut.Find("[data-testid=match-why]").TextContent, StringComparison.Ordinal);
        Assert.Empty(_searches);
        Assert.Empty(cut.FindAll("[data-testid=match-search]"));
    }

    [Fact]
    public void TheFirstRowOfAMatchedItem_HoldsTheLabelledProviderAndTheSourceSwitch_ButNoSearch()
    {
        var cut = RenderPanel();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-row]")));
        var row = cut.Find("[data-testid=match-row]");
        Assert.Contains("Provider", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Media providers", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Wikidata", row.TextContent, StringComparison.Ordinal);
        Assert.Empty(row.QuerySelectorAll("[data-testid=match-search-button]"));
    }

    [Fact]
    public void TheCurrentMatch_ShowsTheProviderIdTheCreatorLine_AndBothActions()
    {
        var cut = RenderPanel();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));
        Assert.Equal("TMDB · 949", cut.Find("[data-testid=match-current-id]").TextContent.Trim());
        Assert.Equal("Directed by Michael Mann", cut.Find("[data-testid=match-current-creator]").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll("[data-testid=match-remove]"));
        Assert.Contains("Change match", cut.Find("[data-testid=match-change]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWhyTable_PutsFactsTheFileCannotSpeakToInAFootnote_AndLabelsTheRuntimeValues()
    {
        _current = Matched(why:
        [
            new EditorMatchEvidenceRowDto { Field = "title", YourFile = "Heat", MatchedRecord = "Heat", State = EditorMatchEvidenceStates.Agrees, Verdict = EditorMatchVerdicts.Strong },
            new EditorMatchEvidenceRowDto { Field = "runtime", YourFile = "2:50:00", MatchedRecord = "170 min", State = EditorMatchEvidenceStates.Agrees, Verdict = EditorMatchVerdicts.Strong },
            new EditorMatchEvidenceRowDto { Field = "year", State = EditorMatchEvidenceStates.NotChecked },
        ]);
        var cut = RenderPanel();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-why]")));
        var rows = cut.FindAll("[data-testid=match-why] tbody tr");
        Assert.Equal(2, rows.Count);
        Assert.Contains("2:50:00 measured", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("170 min (rounded)", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("Year: not in your file, so it was not checked.", cut.Find("[data-testid=match-why-footnote]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangeMatch_OpensTheSearchInPlace_AndAFieldChangeRunsTheSearch()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));

        ClickButton(cut, "Change match");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search]")));
        Assert.Empty(_searches);
        Assert.Equal("Heat", cut.Find("input[aria-label='Title']").GetAttribute("value"));

        cut.Find("input[aria-label='Title']").Change("Heat 1995");

        cut.WaitForAssertion(() => Assert.Single(_searches));
        Assert.Equal("tmdb", _searches[0].Provider);
        Assert.Equal("Heat 1995", _searches[0].Fields["title"]);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=match-result]").Count));
    }

    [Fact]
    public void ApplyMatch_IsAvailableForACheckResult_AndOnlyStages()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));
        ClickButton(cut, "Change match");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search]")));
        cut.Find("input[aria-label='Title']").Change("Heat");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=match-result]").Count));

        // The second result is a Check match; choosing it still leaves Apply match enabled (the verdict is advice).
        cut.FindAll("[data-testid=match-result]").Last().Click();
        cut.WaitForAssertion(() => Assert.Contains("Check match", cut.Find("[data-testid=match-verdict]").TextContent, StringComparison.Ordinal));
        var apply = cut.FindAll("button").First(button => button.TextContent.Trim() == "Apply match");
        Assert.False(apply.HasAttribute("disabled"));
        apply.Click();

        var change = Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity);
        Assert.True(EditSessionIdentityTarget.TryParse(change.TargetId, out var match));
        Assert.Equal("id-1986", match!.ProviderItemId);
        Assert.Equal("Heat · TMDB", _session.Pending.Before(change.Id));
        Assert.Empty(_writes);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-staged]")));

        var row = Assert.Single(EditReviewRows.Build(_session.Pending, _ => "Heat", null));
        Assert.Equal("Match changed", row.What);
        Assert.Contains("Heat (1986)", row.After, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyMatch_ThatNamesAnotherPlace_StagesTheMoveAndTheFileMoveWithPaths_InAManagedFolder()
    {
        var destination = Guid.Parse("55555555-5555-5555-5555-555555555555");
        _placement = new EditorMatchPlacementDto
        {
            DestinationId = destination,
            ParentLabel = "Heat Collection",
            CurrentParentLabel = "Old Collection",
            Managed = true,
            FromPath = "Movies/Heat (1986)/Heat.mkv",
            ToPath = "Movies/Heat (1995)/Heat (1995).mkv",
        };
        var cut = StageSecondResult();

        var relationship = Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Relationship);
        Assert.Equal(destination.ToString("D"), relationship.TargetId);
        Assert.Equal("Heat Collection", _session.Pending.Label(relationship.Id));
        Assert.Equal("Old Collection", _session.Pending.Before(relationship.Id));
        var move = Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.FileMove);
        Assert.Equal("Movies/Heat (1986)/Heat.mkv", _session.Pending.Before(move.Id));
        Assert.Equal("Movies/Heat (1995)/Heat (1995).mkv", _session.Pending.Label(move.Id));
        var footer = EditorFooterRules.For(new FileWriteSetting(FileWriteState.Off), _session.Pending);
        Assert.Equal(EditorFooterStatus.WillMove, footer.Status);
        Assert.Equal("Movies/Heat (1986)/Heat.mkv", footer.MoveFrom);
        Assert.Equal("Movies/Heat (1995)/Heat (1995).mkv", footer.MoveTo);
        var rows = EditReviewRows.Build(_session.Pending, _ => "Heat", null);
        Assert.Contains(rows, row => row.What == "Match changed");
        Assert.Contains(rows, row => row.What == "Moves to" && row.After == "Heat Collection");
        Assert.Contains("Heat Collection", cut.Find("[data-testid=match-staged-move]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyMatch_InAReadOnlyFolder_StagesTheMatchAndTheMove_ButNeverAFileMove()
    {
        _placement = new EditorMatchPlacementDto
        {
            DestinationId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            ParentLabel = "Heat Collection",
            Managed = false,
            FromPath = "Movies/Heat (1986)/Heat.mkv",
            ToPath = "Movies/Heat (1995)/Heat (1995).mkv",
        };
        var cut = StageSecondResult();

        Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity);
        Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Relationship);
        Assert.DoesNotContain(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.FileMove);
        Assert.Equal(EditorFooterStatus.WriteBackOff, EditorFooterRules.For(new FileWriteSetting(FileWriteState.Off), _session.Pending).Status);
        Assert.Contains("only reads", cut.Find("[data-testid=match-staged-move]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyMatch_WithNoPlaceToGo_StagesOnlyTheIdentity()
    {
        _placement = new EditorMatchPlacementDto { Managed = true };
        StageSecondResult();

        Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.Identity, _session.Pending.Changes[0].Kind);
    }

    private IRenderedComponent<EditorMatchPanel> StageSecondResult()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));
        ClickButton(cut, "Change match");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search]")));
        cut.Find("input[aria-label='Title']").Change("Heat");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=match-result]").Count));
        cut.FindAll("[data-testid=match-result]").Last().Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-verdict]")));
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Apply match").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-staged]")));
        return cut;
    }

    [Fact]
    public void RemoveMatch_StagesOneRemoval_ShowsNotMatchedYet_AndWritesNothing()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));

        cut.Find("[data-testid=match-remove]").Click();

        var change = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.MatchRemoval, change.Kind);
        Assert.Equal("Heat · TMDB", _session.Pending.Before(change.Id));
        cut.WaitForAssertion(() => Assert.Contains("Not matched yet", cut.Find("[data-testid=match-search]").TextContent, StringComparison.Ordinal));
        Assert.Equal("Heat", cut.Find("input[aria-label='Title']").GetAttribute("value"));
        Assert.Empty(_writes);
        var row = Assert.Single(EditReviewRows.Build(_session.Pending, _ => "Heat", null));
        Assert.Equal("Match removed", row.What);
        Assert.Equal("No match", row.After);
    }

    [Fact]
    public void RemoveMatch_ThenApplyingAnother_ReplacesTheRemoval()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));
        cut.Find("[data-testid=match-remove]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search]")));
        cut.Find("input[aria-label='Title']").Change("Heat");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=match-result]").Count));

        cut.FindAll("button").First(button => button.TextContent.Trim() == "Apply match").Click();

        cut.WaitForAssertion(() => Assert.Single(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.Identity));
        Assert.DoesNotContain(_session.Pending.Changes, c => c.Kind == EditSessionChangeKinds.MatchRemoval);
    }

    [Fact]
    public void AfterSave_TheStagedNoteClears_AndThePanelReadsTheMatchAgain()
    {
        var cut = StageSecondResult();
        var readsBefore = _currentReads;
        Assert.NotEmpty(cut.FindAll("[data-testid=match-staged]"));

        // Save writes the change and takes it out of the pending list; the panel then reads what the Engine has now.
        _current = Matched("Heat (1986)", "id-1986");
        _session.Pending.Discard();

        cut.WaitForAssertion(() => Assert.True(_currentReads > readsBefore));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=match-staged]")));
        cut.WaitForAssertion(() => Assert.Contains("id-1986", cut.Find("[data-testid=match-current-id]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void AVideoWithNoYouTubeId_SaysSoAndStillOffersThePasteBox()
    {
        _setup = _ => new EditorMatchSetupDto
        {
            EntityId = MovieId,
            Kind = "video",
            NoProvider = true,
            NoProviderMessage = "No provider matches this video yet. Paste a YouTube link or ID to match it.",
            Providers = [new EditorMatchProviderDto { Name = "youtube", DisplayName = "YouTube", State = EditorMatchProviderStates.Ready, IsDefault = true }],
            Fields = [new EditorMatchFieldDto { Key = "provider_id", Label = "YouTube link or ID", Value = string.Empty }],
        };
        var cut = RenderPanel(unmatched: true);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-no-provider]")));
        Assert.NotEmpty(cut.FindAll("input[aria-label='YouTube link or ID']"));
        Assert.NotEmpty(cut.FindAll("[data-testid=match-search-button]"));
    }

    [Fact]
    public void AMatchedVideo_StagesTheYouTubeMatch_WithoutAskingWhereItGoes()
    {
        _placement = new EditorMatchPlacementDto { DestinationId = Guid.NewGuid(), ParentLabel = "Should not be asked", Managed = true };
        _setup = _ => Setup("video", noProvider: false);
        _answer = new EditorMatchSearchResultDto
        {
            EntityId = MovieId,
            Provider = "youtube",
            Query = "dQw4w9WgXcQ",
            Results =
            [
                new EditorMatchResultDto
                {
                    Candidate = new ItemCanonicalRetailCandidateDto { CandidateId = "youtube:dQw4w9WgXcQ", ProviderId = "youtube", ProviderName = "youtube", ProviderItemId = "dQw4w9WgXcQ", Title = "Never Gonna Give You Up" },
                    ProviderName = "youtube",
                    Title = "Never Gonna Give You Up",
                    Verdict = EditorMatchVerdicts.Strong,
                },
            ],
        };
        var cut = RenderPanel(unmatched: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search]")));
        cut.Find("[data-testid=match-search-button]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-result]")));

        cut.FindAll("button").First(button => button.TextContent.Trim() == "Apply match").Click();

        cut.WaitForAssertion(() => Assert.Single(_session.Pending.Changes));
        var change = _session.Pending.Changes[0];
        Assert.Equal(EditSessionChangeKinds.Identity, change.Kind);
        Assert.True(EditSessionIdentityTarget.TryParse(change.TargetId, out var match));
        Assert.Equal("youtube", match!.ProviderName);
        Assert.Equal("dQw4w9WgXcQ", match.ProviderItemId);
    }

    [Fact]
    public void ThereIsNoChangePlacementButton_AndNoLineUpTable()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));

        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Change placement", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Change placement", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("[data-testid=line-up]"));
        Assert.Empty(cut.FindAll("[aria-label='Line-up']"));
    }

    [Fact]
    public void AnUnmatchedItem_OpensOnThePrefilledSearch_WithNotMatchedYet_AndSearchesNothing()
    {
        var cut = RenderPanel(unmatched: true);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search]")));
        Assert.Contains("Not matched yet", cut.Find("[data-testid=match-search]").TextContent, StringComparison.Ordinal);
        Assert.Equal("Heat", cut.Find("input[aria-label='Title']").GetAttribute("value"));
        Assert.Equal("1995", cut.Find("input[aria-label='Year']").GetAttribute("value"));
        Assert.Empty(_searches);
        Assert.Empty(cut.FindAll("[data-testid=match-current]"));
    }

    [Fact]
    public void AProviderThatDidNotAnswer_IsNamed()
    {
        _answer = new EditorMatchSearchResultDto
        {
            EntityId = MovieId,
            Provider = "tmdb",
            Query = "Heat",
            FailedProviders = [new EditorMatchFailedProviderDto { Name = "tmdb", DisplayName = "TMDB" }],
            Message = "TMDB did not answer. Try again, or search in another provider.",
        };
        var cut = RenderPanel(unmatched: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search]")));

        cut.Find("[data-testid=match-search-button]").Click();

        cut.WaitForAssertion(() => Assert.Contains("TMDB did not answer", cut.Find("[data-testid=match-message]").TextContent, StringComparison.Ordinal));
        Assert.Single(_searches);
    }

    [Fact]
    public void AKindWithNoProvider_SaysSo_AndOffersNoSearch()
    {
        _setup = _ => Setup("course", noProvider: true);
        var cut = RenderPanel(unmatched: true);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-no-provider]")));
        Assert.Empty(cut.FindAll("[data-testid=match-search-button]"));
        Assert.Empty(_searches);
    }
}
