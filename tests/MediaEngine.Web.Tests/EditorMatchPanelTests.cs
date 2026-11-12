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
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Match tab's Media providers side (universal editor v5, 3c, mockups G1 and G2): a matched item opens on what it is matched to and
/// searches nothing; Change match opens the compact search; Apply match is always available and only stages (with the move the match
/// brings, in a Managed folder only); Remove match stages a removal; an unmatched item opens on the pre-filled search; after Save the panel
/// reads the Engine again; there is no Change placement and no line-up table. Editor v6 WP2 adds the frame: the source selector on its own line,
/// one search row on the shared form row with a labeled Search (nothing searches until Search or Enter), provider entries that show a state only
/// when the provider is not ready, the "Your file" bar, and the Engine's read deciding matched or not.
/// </summary>
public sealed class EditorMatchPanelTests : AsyncBunitContext
{
    private static readonly Guid MovieId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly EditSessionState _session = new(new PendingEditList());
    private readonly List<EditorMatchSearchRequestDto> _searches = [];
    private readonly List<string> _writes = [];
    private EditorMatchSearchResultDto? _answer;
    private EditorMatchPlacementDto? _placement;
    private TaskCompletionSource<EditorMatchSearchResultDto?>? _hold;
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
                return _hold is null ? Task.FromResult<EditorMatchSearchResultDto?>(_answer) : _hold.Task;
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

    // Types into the Title box and presses the labeled Search button: nothing searches until then.
    private static void SearchFor(IRenderedComponent<EditorMatchPanel> cut, string title)
    {
        cut.Find("input[aria-label='Title']").Input(title);
        cut.Find("[data-testid=match-search-button]").Click();
    }

    private static EditorMatchSetupDto BookSetup() => new()
    {
        EntityId = MovieId,
        Kind = "book",
        FileName = "The Percy Jackson and the Olympians, Book - Rick Riordan.epub",
        Providers =
        [
            new EditorMatchProviderDto { Name = "apple_api", DisplayName = "Apple Books", State = EditorMatchProviderStates.Ready, IsDefault = true },
            new EditorMatchProviderDto { Name = "openlibrary", DisplayName = "Open Library", State = EditorMatchProviderStates.NeedsKey },
            new EditorMatchProviderDto { Name = "hardcover", DisplayName = "Hardcover", State = EditorMatchProviderStates.Off },
        ],
        Fields =
        [
            new EditorMatchFieldDto { Key = "title", Label = "Title", Value = "The Sea of Monsters", From = "tags" },
            new EditorMatchFieldDto { Key = "author", Label = "Author", Value = "Rick Riordan", From = "tags" },
            new EditorMatchFieldDto { Key = "isbn", Label = "ISBN", Value = string.Empty },
        ],
        Evidence = new EditorMatchLocalEvidenceDto
        {
            FileTitle = "The Sea of Monsters",
            Creator = "Rick Riordan",
            Identifiers = [new EditorMatchIdentifierDto { Kind = EditorMatchIdentifierKinds.EmbeddedTitle, Value = "The Sea of Monsters" }],
        },
    };

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
    public void AMatchedItem_ShowsTheSourceSelectorThenTheFileBarThenTheCard_AndNoSearchRowUntilChangeMatch()
    {
        var cut = RenderPanel();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));
        var source = Assert.Single(cut.FindAll("[data-testid=match-source]"));
        Assert.Contains("Media providers", source.TextContent, StringComparison.Ordinal);
        Assert.Contains("Wikidata", source.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=match-row]"));
        Assert.Empty(cut.FindAll("[data-testid=match-search-button]"));
        // Mock B: the selector, then "Your file", then the card it is matched to.
        var file = Assert.Single(cut.FindAll("[data-testid=match-your-file]"));
        Assert.Contains("Heat (1995) 1080p.mkv", file.TextContent, StringComparison.Ordinal);
        Assert.True(cut.Markup.IndexOf("match-source", StringComparison.Ordinal) < cut.Markup.IndexOf("match-your-file", StringComparison.Ordinal));
        Assert.True(cut.Markup.IndexOf("match-your-file", StringComparison.Ordinal) < cut.Markup.IndexOf("match-current", StringComparison.Ordinal));

        ClickButton(cut, "Change match");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-row]")));
        Assert.Single(cut.FindAll("[data-testid=match-your-file]"));
        Assert.Empty(_searches);
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
    public void ChangeMatch_OpensTheSearchInPlace_AndOnlySearchRunsTheSearch()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));

        ClickButton(cut, "Change match");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search]")));
        Assert.Empty(_searches);
        Assert.Equal("Heat", cut.Find("input[aria-label='Title']").GetAttribute("value"));

        cut.Find("input[aria-label='Title']").Input("Heat 1995");
        Assert.Empty(_searches);
        cut.Find("[data-testid=match-search-button]").Click();

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
        SearchFor(cut, "Heat");
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
        SearchFor(cut, "Heat");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=match-result]").Count));
        cut.FindAll("[data-testid=match-result]").Last().Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-verdict]")));
        cut.FindAll("button").First(button => button.TextContent.Trim() == "Apply match").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-staged]")));
        return cut;
    }

    [Fact]
    public void RemoveMatch_StagesOneRemoval_OpensTheSearch_AndWritesNothing()
    {
        var cut = RenderPanel();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));

        cut.Find("[data-testid=match-remove]").Click();

        var change = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.MatchRemoval, change.Kind);
        Assert.Equal("Heat · TMDB", _session.Pending.Before(change.Id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search-button]")));
        Assert.Equal("Heat", cut.Find("input[aria-label='Title']").GetAttribute("value"));
        Assert.Empty(_writes);
        Assert.Empty(_searches);
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
        SearchFor(cut, "Heat");
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
    public void AnUnmatchedItem_OpensOnThePrefilledSearch_AndSearchesNothing()
    {
        var cut = RenderPanel(unmatched: true);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search]")));
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
        // One line naming the provider, with a Try again button that asks again.
        Assert.DoesNotContain("search in another provider", cut.Find("[data-testid=match-message]").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid=match-try-again]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, _searches.Count));
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

    // -- The frame (editor v6 WP2) --------------------------------------------------------------------------------------------------

    [Fact]
    public void TypingInABox_ChangingTheProvider_AndLeavingABox_SearchNothing_ButEnterAndTheSearchButtonDo()
    {
        _setup = _ => BookSetup();
        var cut = RenderPanel(unmatched: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search-button]")));

        cut.Find("input[aria-label='Title']").Input("Sea of Monsters");
        cut.Find("input[aria-label='Title']").Change("Sea of Monsters");
        cut.Find("input[aria-label='Author']").Input("Riordan");
        cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync("openlibrary"));
        Assert.Empty(_searches);

        // The typed words survive a provider change, and the results (none yet) are cleared without a new search.
        Assert.Equal("Sea of Monsters", cut.Find("input[aria-label='Title']").GetAttribute("value"));
        cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync("apple_api"));
        Assert.Empty(_searches);

        cut.Find("input[aria-label='Author']").KeyDown(new KeyboardEventArgs { Key = "a" });
        Assert.Empty(_searches);
        cut.Find("input[aria-label='Author']").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        cut.WaitForAssertion(() => Assert.Single(_searches));
        Assert.Equal("Sea of Monsters", _searches[0].Fields["title"]);
        Assert.Equal("Riordan", _searches[0].Fields["author"]);
        Assert.Equal("apple_api", _searches[0].Provider);

        cut.Find("[data-testid=match-search-button]").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, _searches.Count));
    }

    [Fact]
    public void TheSourceSelector_IsDrawnOnce_OnItsOwnLine_AboveTheSearchRow()
    {
        var cut = RenderPanel(unmatched: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-row]")));

        var source = Assert.Single(cut.FindAll("[data-testid=match-source]"));
        Assert.Single(cut.FindAll(".app-segmented-control"));
        Assert.Contains("Media providers", source.TextContent, StringComparison.Ordinal);
        Assert.Contains("Wikidata", source.TextContent, StringComparison.Ordinal);
        Assert.Empty(source.QuerySelectorAll("input, [data-testid=match-row]"));
        Assert.True(cut.Markup.IndexOf("match-source", StringComparison.Ordinal) < cut.Markup.IndexOf("match-row", StringComparison.Ordinal));
        Assert.Empty(cut.Find("[data-testid=match-row]").QuerySelectorAll(".app-segmented-control"));
    }

    [Fact]
    public void TheSearchRow_IsTheSharedFormRow_WhoseDirectChildrenAreTheProvider_TheBoxes_AndTheLabeledSearchButton()
    {
        _setup = _ => BookSetup();
        var cut = RenderPanel(unmatched: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-row]")));

        var row = cut.Find("[data-testid=match-row]");
        Assert.Contains("app-form-row", row.ClassList);
        var children = row.QuerySelector(".app-form-row__fields")!.Children.ToList();
        Assert.Equal(5, children.Count);
        Assert.All(children.Take(4), child => Assert.Contains("app-field", child.ClassList));
        Assert.Equal("Provider", children[0].QuerySelector("label")!.TextContent.Trim());
        Assert.Equal(["Title", "Author", "ISBN (optional)"], children.Skip(1).Take(3).Select(child => child.QuerySelector("label")!.TextContent.Trim()).ToList());
        // The button is a form-row field with a blank label line, so it shares the controls' top on every visual line (also when the row wraps).
        var go = children[4];
        Assert.True(go.HasAttribute("data-form-row-field"));
        Assert.Equal("fit", go.GetAttribute("data-form-row-field"));
        Assert.Equal("true", go.QuerySelector(".app-field__label")!.GetAttribute("aria-hidden"));
        var button = go.QuerySelector("button")!;
        Assert.Equal("Search", button.TextContent.Trim());
        Assert.Equal("match-search-button", button.GetAttribute("data-testid"));
        // The Search button carries an outlined search icon before its word (mock A, D18).
        Assert.NotNull(button.QuerySelector("svg"));
        // The Provider is fixed, the ISBN box is about 220 px, the button is as wide as its word.
        var columns = row.QuerySelector(".app-form-row__fields")!.GetAttribute("style")!;
        Assert.Contains("--tl-form-row-columns: 220px ", columns, StringComparison.Ordinal);
        Assert.EndsWith("220px auto", columns.TrimEnd(';', ' '), StringComparison.Ordinal);
        Assert.Contains("app-form-row--wrap", row.ClassList);
        // The message line is reserved so nothing below it moves when text appears.
        Assert.NotNull(row.QuerySelector(".app-form-row__message--reserved"));
    }

    [Fact]
    public void TheSearchRowMessageLine_SaysWhyAProviderCannotSearch_AndDisablesSearch()
    {
        _setup = _ => BookSetup();
        var cut = RenderPanel(unmatched: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search-button]")));
        Assert.Equal(string.Empty, cut.Find("[data-testid=match-row-message]").TextContent.Trim());

        cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync("openlibrary"));

        cut.WaitForAssertion(() => Assert.Contains("Open Library needs a key", cut.Find("[data-testid=match-row-message]").TextContent, StringComparison.Ordinal));
        Assert.True(cut.Find("[data-testid=match-search-button]").HasAttribute("disabled"));

        cut.InvokeAsync(() => cut.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync("hardcover"));

        cut.WaitForAssertion(() => Assert.Contains("Hardcover is switched off", cut.Find("[data-testid=match-row-message]").TextContent, StringComparison.Ordinal));
        Assert.True(cut.Find("[data-testid=match-search-button]").HasAttribute("disabled"));
        Assert.Empty(_searches);
    }

    [Theory]
    [InlineData("Apple Books cannot search by ISBN. Add a title.")]
    [InlineData("That does not look like an ISBN. An ISBN has 10 or 13 digits.")]
    [InlineData("Enter something to search for.")]
    public void ASentenceTheEngineGivesWithoutSearching_GoesOnTheRowsMessageLine_NotInTheList(string sentence)
    {
        _setup = _ => BookSetup();
        _answer = new EditorMatchSearchResultDto { EntityId = MovieId, Provider = "apple_api", Message = sentence };
        var cut = RenderPanel(unmatched: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search-button]")));

        cut.Find("[data-testid=match-search-button]").Click();

        cut.WaitForAssertion(() => Assert.Equal(sentence, cut.Find("[data-testid=match-row-message]").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-testid=match-message]"));
    }

    [Fact]
    public void WhileSearching_TheListAreaSaysLookingUp_AndTheSearchRowKeepsItsFields()
    {
        _hold = new TaskCompletionSource<EditorMatchSearchResultDto?>();
        var cut = RenderPanel(unmatched: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search-button]")));

        cut.Find("[data-testid=match-search-button]").Click();

        cut.WaitForAssertion(() => Assert.Contains("Looking up", cut.Find("[data-testid=match-looking-up]").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.Find("[data-testid=match-row]").QuerySelectorAll("[data-testid=match-looking-up]"));
        Assert.Equal("Heat", cut.Find("input[aria-label='Title']").GetAttribute("value"));
        _hold.SetResult(_answer);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=match-looking-up]")));
    }

    [Theory]
    [InlineData(false, "Nothing found for that. Try fewer words.")]
    [InlineData(true, "Nothing found for that. Try fewer words or the ISBN.")]
    public void ASearchThatFindsNothing_SaysSoInTheListArea_AndTheISBNPartOnlyForKindsWithAnISBNBox(bool withIsbn, string expected)
    {
        if (withIsbn)
        {
            _setup = _ => BookSetup();
        }

        _answer = new EditorMatchSearchResultDto { EntityId = MovieId, Provider = "tmdb", Query = "Heat", Message = "Nothing matches that. Change the words, or search in another provider." };
        var cut = RenderPanel(unmatched: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search-button]")));

        cut.Find("[data-testid=match-search-button]").Click();

        cut.WaitForAssertion(() => Assert.Equal(expected, cut.Find("[data-testid=match-message]").TextContent.Replace("Try again", string.Empty).Trim()));
        Assert.NotEmpty(cut.FindAll("[data-testid=match-your-file]"));
        Assert.NotEmpty(cut.FindAll("input[aria-label='Title']"));
        Assert.Empty(cut.FindAll("[data-testid=match-try-again]"));
    }

    [Fact]
    public void AProvidersEntry_ShowsItsMarkAndName_AndAStateOnlyWhenItIsNotReady_NeverTheWordReady()
    {
        _setup = _ => BookSetup();
        var cut = RenderPanel(unmatched: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search-button]")));

        var options = cut.FindComponent<AppSelect>().Instance.Options!;

        Assert.Equal(["Apple Books", "Open Library · Needs a key", "Hardcover · Off"], options.Select(option => option.Label).ToList());
        Assert.Equal(["A", "O", "H"], options.Select(option => option.Mark).ToList());
        Assert.DoesNotContain(options, option => option.Label.Contains("ready", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("ready", cut.Markup, StringComparison.OrdinalIgnoreCase);
        // The closed control shows the mark of the chosen provider before its name.
        Assert.Equal("A", cut.Find(".tl-select-trigger .app-select__mark").TextContent.Trim());
        Assert.Contains("Apple Books", cut.Find(".tl-select-trigger .tl-select-value").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheYourFileBar_ShowsTheFileName_ThenWhatTheFileSays_AndItsFormat()
    {
        _setup = _ =>
        {
            var setup = BookSetup();
            return new EditorMatchSetupDto
            {
                EntityId = setup.EntityId,
                Kind = setup.Kind,
                FileName = setup.FileName,
                Providers = setup.Providers,
                Fields = setup.Fields,
                Evidence = new EditorMatchLocalEvidenceDto
                {
                    Creator = "Rick Riordan",
                    MeasuredText = "1:56:21",
                    Identifiers = setup.Evidence.Identifiers,
                },
            };
        };
        var cut = RenderPanel(unmatched: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-your-file]")));

        var bar = cut.Find("[data-testid=match-your-file]");
        Assert.Contains("Your file", bar.TextContent, StringComparison.Ordinal);
        Assert.Equal("The Percy Jackson and the Olympians, Book - Rick Riordan.epub", cut.Find("[data-testid=match-file-name]").TextContent.Trim());
        Assert.Equal("Embedded title: The Sea of Monsters · Rick Riordan · 1:56:21 measured · EPUB", cut.Find("[data-testid=match-file-facts]").TextContent.Trim());
        // The bar sits under the search row, not inside it.
        Assert.Empty(cut.Find("[data-testid=match-row]").QuerySelectorAll("[data-testid=match-your-file]"));
        Assert.True(cut.Markup.IndexOf("match-row", StringComparison.Ordinal) < cut.Markup.IndexOf("match-your-file", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEnginesRead_DecidesMatchedOrNot_WhateverThePageFlagSays()
    {
        // The page says "unmatched", the Engine's read says matched: the tab opens on the current match.
        var matchedCut = Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, Movie())
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.IsUnmatched, true));
        matchedCut.WaitForAssertion(() => Assert.NotEmpty(matchedCut.FindAll("[data-testid=match-current]")));
        Assert.Empty(matchedCut.FindAll("[data-testid=match-search-button]"));
        Assert.Empty(_searches);
        matchedCut.Dispose();

        // The page says "matched", the Engine's read says not: the tab opens on the search.
        _matched = false;
        var unmatchedCut = Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, Movie())
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.IsUnmatched, false));
        unmatchedCut.WaitForAssertion(() => Assert.NotEmpty(unmatchedCut.FindAll("[data-testid=match-search-button]")));
        Assert.Empty(unmatchedCut.FindAll("[data-testid=match-current]"));
    }

    [Fact]
    public void ThePanel_TellsTheHeaderWhatTheEngineRead_AndAgainWhenARemovalIsStaged()
    {
        var states = new List<EditorMatchReadState?>();
        _matched = true;
        var cut = Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, Movie())
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.IsUnmatched, true)
            .Add(panel => panel.OnMatchState, EventCallback.Factory.Create<EditorMatchReadState?>(this, state => states.Add(state))));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));

        cut.WaitForAssertion(() => Assert.Equal(new EditorMatchReadState(MovieId, true, "TMDB"), states.Last()));

        cut.Find("[data-testid=match-remove]").Click();

        cut.WaitForAssertion(() => Assert.Equal(new EditorMatchReadState(MovieId, false, null), states.Last()));
    }

    [Fact]
    public void WhenTheMatchIsReadAgain_ThePanelFirstTakesTheOldAnswerBack_SoTheHeaderNeverShowsItOverTheLoadingPlaceholder()
    {
        var states = new List<EditorMatchReadState?>();
        _matched = true;
        var cut = Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, Movie())
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.OnMatchState, EventCallback.Factory.Create<EditorMatchReadState?>(this, state => states.Add(state))));
        cut.WaitForAssertion(() => Assert.Equal(new EditorMatchReadState(MovieId, true, "TMDB"), states.LastOrDefault()));

        // A staged match that is then saved leaves the pending list, which makes the panel read the Engine again.
        cut.Find("[data-testid=match-remove]").Click();
        cut.WaitForAssertion(() => Assert.Equal(new EditorMatchReadState(MovieId, false, null), states.Last()));
        _matched = false;
        _session.Pending.Discard();

        cut.WaitForAssertion(() => Assert.Contains(null, states));
        cut.WaitForAssertion(() => Assert.Equal(new EditorMatchReadState(MovieId, false, null), states.Last()));
    }

    [Fact]
    public void AfterASearch_TheRowsMessageLineSaysHowManyResultsCameBackAndFromWhom()
    {
        var cut = RenderPanel(unmatched: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search-button]")));

        cut.Find("[data-testid=match-search-button]").Click();

        cut.WaitForAssertion(() => Assert.Equal("2 results from TMDB.", cut.Find("[data-testid=match-row-message]").TextContent.Trim()));
    }

    [Fact]
    public void AfterASearchWithOneResult_TheRowsMessageLineUsesTheSingularForm()
    {
        _answer = Found(Result("Heat", "1995", EditorMatchVerdicts.Strong));
        var cut = RenderPanel(unmatched: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search-button]")));

        cut.Find("[data-testid=match-search-button]").Click();

        cut.WaitForAssertion(() => Assert.Equal("1 result from TMDB.", cut.Find("[data-testid=match-row-message]").TextContent.Trim()));
    }

    [Fact]
    public void WhenTheKindHasNoBoxesToSearchWith_SearchIsOff_AndTheMessageLineSaysWhy()
    {
        _setup = _ =>
        {
            var setup = Setup("movie", noProvider: false);
            return new EditorMatchSetupDto { EntityId = setup.EntityId, Kind = setup.Kind, FileName = setup.FileName, Providers = setup.Providers, Fields = [] };
        };
        var cut = RenderPanel(unmatched: true);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-search-button]")));

        Assert.True(cut.Find("[data-testid=match-search-button]").HasAttribute("disabled"));
        Assert.Equal("There is nothing to search with for this item.", cut.Find("[data-testid=match-row-message]").TextContent.Trim());
    }
}
