using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The pure rules behind the editor's v3 leftovers (WP6, D7/D8): the Before value on every Review row, the named follow-up steps, and the
/// Artwork tool's levels.
/// </summary>
public sealed class EditorLeftoversTests
{
    private static readonly Guid Item = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private static EditorFollowUpDto FollowUp(params (string Key, string State, string? Reason)[] steps) => new()
    {
        Steps = steps.Select(step => new EditorFollowUpStepDto { Key = step.Key, Label = step.Key, State = step.State, Reason = step.Reason }).ToList(),
    };

    [Fact]
    public void PendingEditList_RemembersTheOldValueOfAStagedField()
    {
        var pending = new PendingEditList();

        pending.SetField(Item, "title", "New", before: "Old");

        Assert.Equal("Old", pending.Before(pending.Changes[0].Id + ":title"));
    }

    [Fact]
    public void PendingEditList_ForgetsTheOldValueWhenTheChangeIsDiscarded()
    {
        var pending = new PendingEditList();
        pending.SetField(Item, "title", "New", before: "Old");

        var rowId = pending.Changes[0].Id + ":title";
        pending.Discard();

        Assert.Null(pending.Before(rowId));
    }

    [Fact]
    public void EditorFollowUpView_Combine_ReportsAStepAsFailedWhenAnyItemFailed()
    {
        var combined = EditorFollowUpView.Combine([
            FollowUp((EditorFollowUpSteps.Matched, EditorFollowUpStates.Done, null)),
            FollowUp((EditorFollowUpSteps.Matched, EditorFollowUpStates.Failed, "No provider answered"))]);

        var step = Assert.Single(combined);
        Assert.Equal(EditorFollowUpStates.Failed, step.State);
        Assert.Equal("No provider answered", step.Reason);
    }

    [Fact]
    public void EditorFollowUpView_Combine_KeepsAStepRunningUntilEveryItemIsDone()
    {
        var combined = EditorFollowUpView.Combine([
            FollowUp((EditorFollowUpSteps.Details, EditorFollowUpStates.Done, null)),
            FollowUp((EditorFollowUpSteps.Details, EditorFollowUpStates.Running, null))]);

        Assert.Equal(EditorFollowUpStates.Running, Assert.Single(combined).State);
        Assert.False(EditorFollowUpView.IsFinished(combined));
    }

    [Fact]
    public void EditorFollowUpView_IsFinished_IsTrueForNoStepsAllDoneOrAFailure()
    {
        Assert.True(EditorFollowUpView.IsFinished([]));
        Assert.True(EditorFollowUpView.IsFinished(FollowUp((EditorFollowUpSteps.Matched, EditorFollowUpStates.Done, null)).Steps));
        Assert.True(EditorFollowUpView.IsFinished(FollowUp(
            (EditorFollowUpSteps.Matched, EditorFollowUpStates.Done, null),
            (EditorFollowUpSteps.Details, EditorFollowUpStates.Failed, "x")).Steps));
    }

    [Fact]
    public void EditorFollowUpView_FailureLine_NamesTheStepAndTheReason()
    {
        var steps = FollowUp((EditorFollowUpSteps.Details, EditorFollowUpStates.Failed, "The provider timed out.")).Steps;

        Assert.Equal("details: The provider timed out.", EditorFollowUpView.FailureLine(steps));
        Assert.Null(EditorFollowUpView.FailureLine([]));
    }

    [Fact]
    public void ArtworkPickerRules_GroupByLevel_ListsThisItemThenSeasonsThenEpisodes()
    {
        var tabs = new[]
        {
            new ArtworkPickerTab("EpisodeStill", "Still", ArtworkPickerRules.LevelOf("EpisodeStill")),
            new ArtworkPickerTab("CoverArt", "Poster", ArtworkPickerRules.LevelOf("CoverArt")),
            new ArtworkPickerTab("SeasonPoster", "Season poster", ArtworkPickerRules.LevelOf("SeasonPoster")),
        };

        var groups = ArtworkPickerRules.GroupByLevel(tabs);

        Assert.Equal(
            [ArtworkPickerRules.ThisItemLevel, ArtworkPickerRules.SeasonsLevel, ArtworkPickerRules.EpisodesLevel],
            groups.Select(group => group.Level));
    }

    [Fact]
    public void ArtworkPickerRules_GroupByLevel_LeavesOutALevelWithNoTab()
    {
        var groups = ArtworkPickerRules.GroupByLevel([new ArtworkPickerTab("CoverArt", "Cover")]);

        Assert.Equal(ArtworkPickerRules.ThisItemLevel, Assert.Single(groups).Level);
    }

    [Fact]
    public void EditSavedRibbon_DefinesTheStepLabelsTheEngineSends()
    {
        Assert.Equal(["matched", "details", "pictures_and_cast", "related", "files"],
            new[] { EditorFollowUpSteps.Matched, EditorFollowUpSteps.Details, EditorFollowUpSteps.PicturesAndCast, EditorFollowUpSteps.Related, EditorFollowUpSteps.Files });
    }
}
