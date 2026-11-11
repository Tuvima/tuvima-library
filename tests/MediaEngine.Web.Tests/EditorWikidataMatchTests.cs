using Bunit;
using MediaEngine.Contracts.Editing;
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
/// The Match tab's Wikidata side (universal editor v5, 3c, mockup G5): nothing is searched on open; a pasted link or QID resolves straight to
/// its item; a parent (the show for an episode) is shown for context and cannot be applied; Apply and Remove match only stage a change that
/// Review words; the provider match and the Wikidata link never change each other, and one amber line shows when they disagree.
/// </summary>
public sealed class EditorWikidataMatchTests : AsyncBunitContext
{
    private static readonly Guid ItemId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private readonly EditSessionState _session = new(new PendingEditList());
    private readonly List<EditorWikidataSearchRequestDto> _searches = [];
    private readonly List<EditorWikidataConflictRequestDto> _comparisons = [];
    private int _identityReads;
    private string _kind = "movie";
    private EditorWikidataIdentityDto _identity = new() { EntityId = ItemId, Qid = "Q202", Label = "Heat", Type = "film", Year = "1995", Url = "https://www.wikidata.org/wiki/Q202" };
    private EditorWikidataSearchResultDto _answer = new() { EntityId = ItemId };
    private EditorWikidataConflictDto _conflict = new() { Conflict = false };

    public EditorWikidataMatchTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _session.Begin([new EditTrailItem(ItemId, "Heat", "Item")]);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorCurrentMatchAsync), _ => Task.FromResult<EditorCurrentMatchDto?>(new EditorCurrentMatchDto
            {
                EntityId = ItemId,
                State = EditorCurrentMatchStates.Matched,
                Record = new EditorMatchedRecordDto { ProviderId = "tmdb", ProviderName = "TMDB", ProviderItemId = "949", Kind = "Film", Title = "Heat", Year = "1995" },
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchSetupAsync), _ => Task.FromResult<EditorMatchSetupDto?>(new EditorMatchSetupDto
            {
                EntityId = ItemId,
                Kind = _kind,
                Providers = [new EditorMatchProviderDto { Name = "tmdb", DisplayName = "TMDB", State = EditorMatchProviderStates.Ready, IsDefault = true }],
                Fields = [new EditorMatchFieldDto { Key = "title", Label = "Title", Value = "Heat", From = "file_name" }],
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorCurrentWikidataAsync), _ =>
            {
                _identityReads++;
                return Task.FromResult<EditorWikidataIdentityDto?>(_identity);
            });
            stub.SetHandler(nameof(IEngineApiClient.SearchEditorWikidataAsync), args =>
            {
                _searches.Add((EditorWikidataSearchRequestDto)args![1]!);
                return Task.FromResult<EditorWikidataSearchResultDto?>(_answer);
            });
            stub.SetHandler(nameof(IEngineApiClient.CompareEditorWikidataAsync), args =>
            {
                _comparisons.Add((EditorWikidataConflictRequestDto)args![1]!);
                return Task.FromResult<EditorWikidataConflictDto?>(_conflict);
            });
        }));
        Render<AppPopoverHost>();
    }

    private static EditorMatchSubject Subject() => new(
        ItemId, "Movies", "movie", "movie", "Heat", "Michael Mann", null, "1995",
        new Dictionary<string, string> { ["title"] = "Heat" }, []);

    private static EditorWikidataResultDto Result(string qid, string label, string relation = EditorWikidataRelations.Direct, bool canApply = true, string? reason = null) => new()
    {
        Qid = qid,
        Label = label,
        Description = "1995 crime film",
        Type = "film",
        Fact = "1995",
        Relation = relation,
        CanApply = canApply,
        Reason = reason,
        Url = $"https://www.wikidata.org/wiki/{qid}",
        Evidence =
        [
            new EditorWikidataEvidenceDto { Fact = EditorWikidataEvidenceFacts.Year, State = EditorWikidataEvidenceStates.Agrees, Yours = "1995", Wikidata = "1995" },
            new EditorWikidataEvidenceDto { Fact = EditorWikidataEvidenceFacts.Identifier, Label = "IMDb", State = EditorWikidataEvidenceStates.NotChecked },
        ],
    };

    private IRenderedComponent<EditorMatchPanel> RenderPanel()
    {
        var cut = Render<EditorMatchPanel>(parameters => parameters
            .Add(panel => panel.Subject, Subject())
            .Add(panel => panel.Session, _session)
            .Add(panel => panel.CanEdit, true)
            .Add(panel => panel.IsUnmatched, false));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=match-current]")));
        return cut;
    }

    private static void SwitchToWikidata(IRenderedComponent<EditorMatchPanel> cut)
    {
        cut.FindAll("button, [role=radio], [role=tab]").First(element => element.TextContent.Trim() == "Wikidata").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=wikidata-match]")));
    }

    private IRenderedComponent<EditorMatchPanel> RenderWikidataSide()
    {
        var cut = RenderPanel();
        SwitchToWikidata(cut);
        return cut;
    }

    [Fact]
    public void TheWikidataSide_ReadsTheStoredLink_AndSearchesNothingOnOpen()
    {
        var cut = RenderWikidataSide();

        cut.WaitForAssertion(() => Assert.Contains("Q202", cut.Find("[data-testid=wikidata-current]").TextContent, StringComparison.Ordinal));
        Assert.Empty(_searches);
        Assert.True(_identityReads >= 1);
        Assert.Empty(cut.FindAll("[data-testid=wikidata-results]"));
        var row = cut.Find("[data-testid=match-row]");
        Assert.Contains("Search Wikidata", row.InnerHtml, StringComparison.Ordinal);
        Assert.Contains("Paste a Wikidata link or QID", row.InnerHtml, StringComparison.Ordinal);
        Assert.Contains("Media providers", row.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePastedQid_ResolvesDirectlyToItsItem_AndSearchesNoWords()
    {
        _answer = new EditorWikidataSearchResultDto { EntityId = ItemId, FromPaste = true, Results = [Result("Q123", "Heat (film)")] };
        var cut = RenderWikidataSide();

        cut.Find("input[aria-label='Paste a Wikidata link or QID']").Change("https://www.wikidata.org/wiki/Q123");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=wikidata-result]")));
        var request = Assert.Single(_searches);
        Assert.Equal("https://www.wikidata.org/wiki/Q123", request.Paste);
        Assert.Null(request.Query);
        Assert.Contains("Q123", cut.Find("[data-testid=wikidata-result]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AResult_IsTextOnly_WithItsTypeAndFact_AndTheEvidenceTicksCrossesAndNotChecked()
    {
        _answer = new EditorWikidataSearchResultDto { EntityId = ItemId, Results = [Result("Q123", "Heat")] };
        var cut = RenderWikidataSide();

        cut.Find("input[aria-label='Search Wikidata']").Change("Heat");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=wikidata-result]")));
        var result = cut.Find("[data-testid=wikidata-result]");
        Assert.Empty(result.QuerySelectorAll("img"));
        Assert.Equal("1995 crime film", cut.Find("[data-testid=wikidata-result-description]").TextContent.Trim());
        Assert.Equal("film", cut.Find("[data-testid=wikidata-result-type]").TextContent.Trim());
        var evidence = cut.Find("[data-testid=wikidata-evidence]");
        Assert.Contains("Alignment evidence", evidence.TextContent, StringComparison.Ordinal);
        Assert.Contains("✓", evidence.TextContent, StringComparison.Ordinal);
        Assert.Contains("IMDb was not checked", cut.Find("[data-testid=wikidata-not-checked]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ForAnEpisode_TheShowIsMarkedParent_AndApplyIsDisabledWithTheReasonShown()
    {
        _kind = "tv_episode";
        _answer = new EditorWikidataSearchResultDto
        {
            EntityId = ItemId,
            Results = [Result("Q900", "The Wire", EditorWikidataRelations.Parent, canApply: false, reason: EditorWikidataMessageCodes.ParentNotAllowed)],
        };
        var cut = RenderWikidataSide();

        cut.Find("input[aria-label='Search Wikidata']").Change("The Wire");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=wikidata-result]")));
        Assert.Contains("Parent: the show, not this episode", cut.Find("[data-testid=wikidata-result]").TextContent, StringComparison.Ordinal);
        Assert.Contains("cannot be its Wikidata link", cut.Find("[data-testid=wikidata-reason]").TextContent, StringComparison.Ordinal);
        var apply = cut.Find("[data-testid=wikidata-apply]");
        Assert.True(apply.HasAttribute("disabled"));
        apply.Click();
        Assert.Empty(_session.Pending.Changes);
    }

    [Fact]
    public void Apply_StagesOnlyTheWikidataLink_AndReviewSaysTheLinkChanged()
    {
        _answer = new EditorWikidataSearchResultDto { EntityId = ItemId, Results = [Result("Q123", "Heat (film)")] };
        var cut = RenderWikidataSide();
        cut.Find("input[aria-label='Search Wikidata']").Change("Heat");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=wikidata-result]")));

        cut.Find("[data-testid=wikidata-apply]").Click();

        var change = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.WikidataLink, change.Kind);
        Assert.Equal("Q123", change.TargetId);
        var row = Assert.Single(EditReviewRows.Build(_session.Pending, _ => "Heat", null));
        Assert.Equal("Wikidata link changed", row.What);
        Assert.Contains("Q202", row.Before, StringComparison.Ordinal);
        Assert.Contains("Q123", row.After, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=wikidata-staged]")));
    }

    [Fact]
    public void RemoveMatch_StagesOnlyTheRemovalOfTheLink_AndReviewSaysTheLinkWasRemoved()
    {
        var cut = RenderWikidataSide();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=wikidata-remove]")));

        cut.Find("[data-testid=wikidata-remove]").Click();

        var change = Assert.Single(_session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.WikidataRemoval, change.Kind);
        var row = Assert.Single(EditReviewRows.Build(_session.Pending, _ => "Heat", null));
        Assert.Equal("Wikidata link removed", row.What);
        Assert.Contains("Q202", row.Before, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingTheProviderMatch_LeavesTheWikidataLinkUntouched_AndShowsTheAmberLineWhenTheYearsDiffer()
    {
        _conflict = new EditorWikidataConflictDto
        {
            Conflict = true,
            Reason = EditorWikidataConflictReasons.Year,
            WikidataLabel = "Heat",
            WikidataYear = "1995",
            WikidataType = "film",
            ProviderName = "TMDB",
            ProviderYear = "1986",
        };
        var cut = RenderPanel();

        // A provider match with another year is staged, as Apply match does.
        var other = new ReplaceRetailMatchRequestDto { ProviderName = "TMDB", ProviderItemId = "id-1986" };
        _session.Pending.SetTarget(EditSessionChangeKinds.Identity, ItemId, EditSessionIdentityTarget.Serialize(other), label: "Heat (1986) (TMDB)");
        SwitchToWikidata(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=wikidata-conflict]")));
        Assert.Equal("Your Wikidata item is a 1995 film; the TMDB match is from 1986. Check which is right.", cut.Find("[data-testid=wikidata-conflict]").TextContent.Trim());
        Assert.DoesNotContain(_session.Pending.Changes, change => change.Kind is EditSessionChangeKinds.WikidataLink or EditSessionChangeKinds.WikidataRemoval);
        Assert.Contains("Q202", cut.Find("[data-testid=wikidata-current]").TextContent, StringComparison.Ordinal);
        Assert.All(_comparisons, comparison => Assert.Equal("Q202", comparison.Qid));
        Assert.All(_comparisons, comparison => Assert.Equal("tmdb", comparison.ProviderId));
        Assert.All(_comparisons, comparison => Assert.Equal("TMDB", comparison.ProviderName));
    }

    [Fact]
    public void WhenTheTwoSidesAgree_NoConflictLineShows()
    {
        var cut = RenderWikidataSide();

        cut.WaitForAssertion(() => Assert.NotEmpty(_comparisons));
        Assert.Empty(cut.FindAll("[data-testid=wikidata-conflict]"));
    }

    [Fact]
    public void TheIdentityCard_SaysTheProviderMatchIsKeptSeparately()
    {
        var cut = RenderWikidataSide();

        Assert.Equal("The TMDB match is kept separately. Linking here never changes it.", cut.Find("[data-testid=wikidata-independent]").TextContent.Trim());
    }

    [Fact]
    public void Results_AreOneSelectableRowEach_WithOneEvidenceCardForTheSelectedRow()
    {
        _answer = new EditorWikidataSearchResultDto
        {
            EntityId = ItemId,
            Results = [Result("Q1", "Heat"), Result("Q2", "Heat (1986)")],
        };
        var cut = RenderWikidataSide();
        cut.Find("input[aria-label='Search Wikidata']").Change("Heat");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=wikidata-result]").Count));

        var rows = cut.FindAll("[data-testid=wikidata-result]");
        Assert.Equal("true", rows[0].GetAttribute("aria-selected"));
        Assert.Equal("false", rows[1].GetAttribute("aria-selected"));
        Assert.Equal("0", rows[1].GetAttribute("tabindex"));
        Assert.Single(cut.FindAll("[data-testid=wikidata-evidence]"));
        Assert.Empty(cut.FindAll("[data-testid=wikidata-result] button"));

        rows[1].Click();

        rows = cut.FindAll("[data-testid=wikidata-result]");
        Assert.Equal("false", rows[0].GetAttribute("aria-selected"));
        Assert.Equal("true", rows[1].GetAttribute("aria-selected"));
        Assert.Single(cut.FindAll("[data-testid=wikidata-evidence]"));

        rows[0].KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        Assert.Equal("true", cut.FindAll("[data-testid=wikidata-result]")[0].GetAttribute("aria-selected"));
    }

    [Fact]
    public void AParentRow_CanBeSelectedForContext_ButApplyIdentityStaysOffWithTheReasonShown()
    {
        _kind = "tv_episode";
        _answer = new EditorWikidataSearchResultDto
        {
            EntityId = ItemId,
            Results =
            [
                Result("Q777", "Pilot"),
                Result("Q900", "The Wire", EditorWikidataRelations.Parent, canApply: false, reason: EditorWikidataMessageCodes.ParentNotAllowed),
            ],
        };
        var cut = RenderWikidataSide();
        cut.Find("input[aria-label='Search Wikidata']").Change("Wire");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=wikidata-result]").Count));
        Assert.False(cut.Find("[data-testid=wikidata-apply]").HasAttribute("disabled"));

        cut.FindAll("[data-testid=wikidata-result]")[1].Click();

        Assert.True(cut.Find("[data-testid=wikidata-apply]").HasAttribute("disabled"));
        Assert.Contains("cannot be its Wikidata link", cut.Find("[data-testid=wikidata-reason]").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid=wikidata-apply]").Click();
        Assert.Empty(_session.Pending.Changes);

        cut.FindAll("[data-testid=wikidata-result]")[0].Click();
        cut.Find("[data-testid=wikidata-apply]").Click();

        Assert.Equal("Q777", Assert.Single(_session.Pending.Changes).TargetId);
    }

    [Fact]
    public void Cancel_ClosesTheResults_AndStagesNothing()
    {
        _answer = new EditorWikidataSearchResultDto { EntityId = ItemId, Results = [Result("Q1", "Heat")] };
        var cut = RenderWikidataSide();
        cut.Find("input[aria-label='Search Wikidata']").Change("Heat");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=wikidata-result]")));

        cut.Find("[data-testid=wikidata-cancel]").Click();

        Assert.Empty(cut.FindAll("[data-testid=wikidata-result]"));
        Assert.Empty(_session.Pending.Changes);
    }

    [Fact]
    public void ForAComic_TheParentIsTheSeries_AndTheItemIsAnIssue()
    {
        _kind = "comic";
        _answer = new EditorWikidataSearchResultDto
        {
            EntityId = ItemId,
            Results = [Result("Q900", "Saga", EditorWikidataRelations.Parent, canApply: false, reason: EditorWikidataMessageCodes.ParentNotAllowed)],
        };
        var cut = RenderWikidataSide();
        cut.Find("input[aria-label='Search Wikidata']").Change("Saga");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=wikidata-result]")));

        Assert.Contains("Parent: the series, not this issue", cut.Find("[data-testid=wikidata-result]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOldReviewSwitch_ForCheckingTheWikidataLinkAgain_IsGone()
    {
        var cut = RenderWikidataSide();

        Assert.DoesNotContain("Check the Wikidata link again", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(typeof(ReplaceRetailMatchRequestDto).GetProperties(), property => property.Name.Contains("ClearAutoAligned", StringComparison.Ordinal));
    }
}
