using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The group Match (universal editor v5, D36): with several episodes ticked, one show and season is picked once; each episode is lined up
/// by its own number; the ones that do not fit are listed as "Weak match" and left out until ticked; there is no table of every file.
/// </summary>
public sealed class EditorGroupMatchPanelTests : AsyncBunitContext
{
    private static readonly Guid[] Ids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

    private readonly PendingEditList _pending = new();
    private EditorGroupMatchRequestDto? _request;
    private bool _emptyLineUp;
    private bool _thirdNamesAnotherSeason;

    public EditorGroupMatchPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchContextAsync), _ =>
                Task.FromResult<EditorMatchContextDto?>(new EditorMatchContextDto { ParsedShow = "The Last Signal", ParsedSeason = 2 }));
            stub.SetHandler(nameof(IEngineApiClient.SearchTvMatchBrowserShowsAsync), _ =>
                Task.FromResult<TvMatchBrowserShowsDto?>(new TvMatchBrowserShowsDto("The Last Signal",
                    [new TvMatchBrowserShowDto("100", "The Last Signal", "2019", null, null, string.Empty, false)])));
            stub.SetHandler(nameof(IEngineApiClient.GetTvMatchBrowserSeasonsAsync), _ =>
                Task.FromResult<TvMatchBrowserSeasonsDto?>(new TvMatchBrowserSeasonsDto("100", "The Last Signal",
                [
                    new TvMatchBrowserSeasonDto("s1", "100", 1, "Season 1", null, false),
                    new TvMatchBrowserSeasonDto("s2", "100", 2, "Season 2", null, false),
                ])));
            stub.SetHandler(nameof(IEngineApiClient.LineUpEditorGroupMatchAsync), args =>
            {
                _request = (EditorGroupMatchRequestDto)args![1]!;
                if (_emptyLineUp)
                {
                    return Task.FromResult<EditorGroupMatchDto?>(new EditorGroupMatchDto { Kind = EditorGroupMatchKinds.Episodes, MessageCode = EditorMatchMessageCodes.NotOnProvider });
                }

                return Task.FromResult<EditorGroupMatchDto?>(new EditorGroupMatchDto
                {
                    Kind = EditorGroupMatchKinds.Episodes,
                    TargetLabel = "The Last Signal Season 2",
                    SubjectMediaType = "TV",
                    SubjectScopeId = "episode",
                    SubjectTargetGroup = "show_episode",
                    Lines =
                    [
                        Line(0, fits: true),
                        Line(1, fits: true),
                        _thirdNamesAnotherSeason
                            ? new EditorGroupMatchLineDto
                            {
                                EntityId = Ids[2], Title = "s01e03.mkv", NumberText = "S01E03", TargetTitle = "Episode 3", Fits = false, NoteCode = EditorMatchMessageCodes.OtherSeason, NoteValue = "1",
                                Verdict = EditorMatchVerdicts.Weak, Candidate = Line(2, fits: true).Candidate, Placement = Line(2, fits: true).Placement,
                            }
                            : new EditorGroupMatchLineDto
                            {
                                EntityId = Ids[2], Title = "bonus-feature.mkv", NumberText = "9", Fits = false, NoteCode = EditorMatchMessageCodes.NoNumber, Verdict = EditorMatchVerdicts.Weak,
                            },
                    ],
                });
            });
        }));
    }

    private static EditorGroupMatchLineDto Line(int index, bool fits) => new()
    {
        EntityId = Ids[index],
        Title = $"s02e0{index + 1}.mkv",
        NumberText = $"S02E0{index + 1}",
        TargetTitle = $"Episode {index + 1}",
        Verdict = EditorMatchVerdicts.Strong,
        Fits = fits,
        Candidate = EditorMatchCandidates.ForTvEpisode("100", "The Last Signal", $"e{index + 1}", 2, index + 1, $"Episode {index + 1}", "2021-01-04", null, null, 45),
        Placement = new EditorMatchPlacementDto
        {
            DestinationId = Guid.NewGuid(),
            ParentLabel = "The Last Signal · Season 2",
            CurrentParentLabel = "Signal Lost · Season 2",
            Managed = true,
            FromPath = $"/tv/old/{index}.mkv",
            ToPath = $"/tv/new/{index}.mkv",
        },
    };

    private IRenderedComponent<EditorGroupMatchPanel> Open()
    {
        IReadOnlyList<BulkEditRow> rows = [.. Ids.Select((id, index) => new BulkEditRow(id, $"s02e0{index + 1}.mkv", DetailEntityType.TvEpisode, Guid.NewGuid(), "TV"))];
        var cut = Render<EditorGroupMatchPanel>(parameters => parameters
            .Add(panel => panel.Rows, rows)
            .Add(panel => panel.MediaType, "TV")
            .Add(panel => panel.Pending, _pending)
            .Add(panel => panel.CanChange, true));
        cut.WaitForAssertion(() => Assert.Equal("The Last Signal", cut.Find("input[aria-label='Show']").GetAttribute("value")));
        cut.Find("[data-testid=group-match-search]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=group-match-shows] button")));
        cut.Find("[data-testid=group-match-shows] button").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=group-match-picked]")));
        cut.Find("[data-testid=group-match-lineup]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=group-match-summary]")));
        return cut;
    }

    [Fact]
    public void PickingOneShowAndSeasonOnce_LinesUpEveryTickedEpisodeByItsOwnNumber()
    {
        var cut = Open();

        Assert.Equal(EditorGroupMatchKinds.Episodes, _request!.Kind);
        Assert.Equal(Ids, _request.EntityIds);
        Assert.Equal("100", _request.SeriesId);
        Assert.Equal(2, _request.SeasonNumber);
        Assert.Equal("2 of 3 line up with The Last Signal Season 2 by their own numbers.", cut.Find("[data-testid=group-match-summary]").TextContent.Trim());
    }

    [Fact]
    public void OnlyTheMisfits_AreListed_AsWeakMatch_AndAreNotTicked()
    {
        var cut = Open();

        var misfit = Assert.Single(cut.FindAll("[data-testid=group-match-misfit]"));
        Assert.Contains("bonus-feature.mkv", misfit.TextContent, StringComparison.Ordinal);
        Assert.Contains("No number could be read from the file name.", misfit.TextContent, StringComparison.Ordinal);
        Assert.Contains("Episode 9", misfit.TextContent, StringComparison.Ordinal);
        Assert.False(misfit.QuerySelector("input[type=checkbox]")!.HasAttribute("checked"));
        Assert.Empty(cut.FindAll("table"));
        Assert.Contains("Apply match to 2", cut.Find("[data-testid=group-match-apply]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Applying_StagesEachFittingEpisode_WithItsMoveAndFileMove_AndLeavesTheMisfitOut()
    {
        var cut = Open();

        cut.Find("[data-testid=group-match-apply]").Click();

        Assert.Equal(2, _pending.Changes.Count(change => change.Kind == EditSessionChangeKinds.Identity));
        Assert.Equal(2, _pending.Changes.Count(change => change.Kind == EditSessionChangeKinds.Relationship));
        Assert.Equal(2, _pending.Changes.Count(change => change.Kind == EditSessionChangeKinds.FileMove));
        Assert.DoesNotContain(_pending.Changes, change => change.EntityId == Ids[2]);
    }

    [Fact]
    public void Applying_UsesTheKindOfItemTheEngineNamedInItsAnswer_NotNamesOfItsOwn()
    {
        var cut = Open();

        cut.Find("[data-testid=group-match-apply]").Click();

        var identity = _pending.Changes.First(change => change.Kind == EditSessionChangeKinds.Identity);
        Assert.True(EditSessionIdentityTarget.TryParse(identity.TargetId, out var match));
        Assert.Equal("show_episode", match!.TargetFieldGroup);
    }

    [Fact]
    public void AMessageTheEngineSendsAsACode_IsSaidInTheDashboardsOwnWords()
    {
        _emptyLineUp = true;
        IReadOnlyList<BulkEditRow> rows = [.. Ids.Select((id, index) => new BulkEditRow(id, $"s02e0{index + 1}.mkv", DetailEntityType.TvEpisode, Guid.NewGuid(), "TV"))];
        var cut = Render<EditorGroupMatchPanel>(parameters => parameters
            .Add(panel => panel.Rows, rows)
            .Add(panel => panel.MediaType, "TV")
            .Add(panel => panel.Pending, _pending)
            .Add(panel => panel.CanChange, true));
        cut.WaitForAssertion(() => Assert.Equal("The Last Signal", cut.Find("input[aria-label='Show']").GetAttribute("value")));
        cut.Find("[data-testid=group-match-search]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=group-match-shows] button")));
        cut.Find("[data-testid=group-match-shows] button").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=group-match-picked]")));
        cut.Find("[data-testid=group-match-lineup]").Click();

        cut.WaitForAssertion(() => Assert.Equal("The provider does not have that.", cut.Find("[data-testid=group-match-message]").TextContent.Trim()));
    }

    [Fact]
    public void TheShowSearch_IsTheSharedFormRow_AndTheShowsAreTheSameListRowsAsEveryOtherMatchStep()
    {
        IReadOnlyList<BulkEditRow> rows = [.. Ids.Select((id, index) => new BulkEditRow(id, $"s02e0{index + 1}.mkv", DetailEntityType.TvEpisode, Guid.NewGuid(), "TV"))];
        var cut = Render<EditorGroupMatchPanel>(parameters => parameters
            .Add(panel => panel.Rows, rows)
            .Add(panel => panel.MediaType, "TV")
            .Add(panel => panel.Pending, _pending)
            .Add(panel => panel.CanChange, true));
        cut.WaitForAssertion(() => Assert.Equal("The Last Signal", cut.Find("input[aria-label='Show']").GetAttribute("value")));

        var row = cut.Find("[data-testid=match-row]");
        Assert.True(row.HasAttribute("data-form-row"));
        Assert.Equal("Search", cut.Find("[data-testid=group-match-search]").TextContent.Trim());

        cut.Find("[data-testid=group-match-search]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=group-match-show-row]")));

        Assert.All(cut.FindAll("[data-testid=group-match-show-row]"), show => Assert.Empty(show.QuerySelectorAll(".app-chip")));
        Assert.NotEmpty(cut.FindAll("[data-testid=group-match-shows] .tl-mlist__title"));
    }

    [Fact]
    public void AFileThatNamesAnotherSeasonThanThePickedOne_IsListedAsAWeakMatch_UntickedWithTheSeasonItNames_AndStagesOnlyWhenTheItemTicksIt()
    {
        _thirdNamesAnotherSeason = true;
        var cut = Open();

        // The picked season (2) travels with the request, so the Engine compares the file's season as well as its episode number.
        Assert.Equal(2, _request!.SeasonNumber);
        var misfit = Assert.Single(cut.FindAll("[data-testid=group-match-misfit]"));
        Assert.Contains("The file name says season 1.", misfit.TextContent, StringComparison.Ordinal);
        Assert.Contains("Weak match", misfit.TextContent, StringComparison.Ordinal);
        Assert.False(misfit.QuerySelector("input[type=checkbox]")!.HasAttribute("checked"));
        Assert.Contains("Apply match to 2", cut.Find("[data-testid=group-match-apply]").TextContent, StringComparison.Ordinal);

        misfit.QuerySelector("input[type=checkbox]")!.Change(true);

        Assert.Contains("Apply match to 3", cut.Find("[data-testid=group-match-apply]").TextContent, StringComparison.Ordinal);
    }
}
