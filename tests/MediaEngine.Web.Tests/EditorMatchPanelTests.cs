using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Matching;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Match tab's Media providers side (universal editor v5, 3c, mockups G1 and G2): a matched item opens on what it is matched to and
/// searches nothing; Change match opens the compact search; Apply match is always available and only stages; an unmatched item opens on the
/// pre-filled search; there is no Change placement and no line-up table.
/// </summary>
public sealed class EditorMatchPanelTests : AsyncBunitContext
{
    private static readonly Guid MovieId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly EditSessionState _session = new(new PendingEditList());
    private readonly List<EditorMatchSearchRequestDto> _searches = [];
    private readonly List<string> _writes = [];
    private EditorMatchSearchResultDto? _answer;

    public EditorMatchPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _session.Begin([new EditTrailItem(MovieId, "Heat", "Item")]);
        _answer = Found(Result("Heat", "1995", EditorMatchVerdicts.Strong), Result("Heat (1986)", "1986", EditorMatchVerdicts.Check));
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorCurrentMatchAsync), _ =>
                Task.FromResult<EditorCurrentMatchDto?>(_matched ? Matched() : new EditorCurrentMatchDto { EntityId = MovieId }));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchSetupAsync), args =>
                Task.FromResult<EditorMatchSetupDto?>(_setup(args!)));
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
    private Func<object?[], EditorMatchSetupDto> _setup = args => Setup((string)args[1]!, noProvider: false);

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

    private static EditorCurrentMatchDto Matched() => new()
    {
        EntityId = MovieId,
        State = EditorCurrentMatchStates.Matched,
        Verdict = EditorMatchVerdicts.Strong,
        Record = new EditorMatchedRecordDto { ProviderId = "tmdb", ProviderName = "TMDB", ProviderItemId = "949", Kind = "Film", Title = "Heat", Year = "1995", Creator = "Michael Mann" },
        Why =
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
        Assert.Contains("Changing the match updates unlocked metadata. Locked fields stay unchanged.", cut.Find("[data-testid=match-why]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Strong match", cut.Find("[data-testid=match-why]").TextContent, StringComparison.Ordinal);
        Assert.Empty(_searches);
        Assert.Empty(cut.FindAll("[data-testid=match-search]"));
    }

    [Fact]
    public void TheFirstRow_HoldsTheProviderDropdownAnIconSearchAndTheSourceSwitch()
    {
        var cut = RenderPanel();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-row]")));
        var row = cut.Find("[data-testid=match-row]");
        Assert.NotEmpty(row.QuerySelectorAll("button[aria-label='Search']"));
        Assert.Contains("Media providers", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Wikidata", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("Heat (1995) 1080p.mkv", row.TextContent, StringComparison.Ordinal);
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
        Assert.Equal("movie", _searches[0].Kind);
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
        cut.FindAll("[data-testid=match-result] button").Last(button => button.TextContent.Trim() == "Select").Click();
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
        Assert.Equal("Match", row.What);
        Assert.Contains("Heat (1986)", row.After, StringComparison.Ordinal);
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

        cut.Find("button[aria-label='Search']").Click();

        cut.WaitForAssertion(() => Assert.Contains("TMDB did not answer", cut.Find("[data-testid=match-message]").TextContent, StringComparison.Ordinal));
        Assert.Single(_searches);
    }

    [Fact]
    public void AKindWithNoProvider_SaysSo_AndOffersNoSearch()
    {
        _setup = args => Setup((string)args[1]!, noProvider: true);
        var cut = RenderPanel(unmatched: true);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-no-provider]")));
        Assert.Empty(cut.FindAll("button[aria-label='Search']"));
        Assert.Empty(_searches);
    }
}
