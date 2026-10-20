using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;

namespace MediaEngine.Web.Tests;

/// <summary>The order of the details "..." menu: personal and playback, other utilities, edit, then destructive last.</summary>
public sealed class DetailMenuGroupingTests
{
    private static DetailAction Action(string key, bool destructive = false) =>
        new() { Key = key, Label = key, IsDestructive = destructive };

    private static string[] Keys(IEnumerable<DetailMenuEntry> entries) =>
        entries.Select(entry => entry.Action?.Key ?? "<note>").ToArray();

    [Fact]
    public void Arrange_OrdersPlaybackThenVisibilityThenUtilitiesThenEditThenDestructive()
    {
        var entries = DetailMenuGrouping.Arrange(
        [
            Action("delete", destructive: true), Action("edit"), Action("add-queue"),
            Action("status-hide"), Action("restart"),
        ]);

        Assert.Equal(["restart", "status-hide", "add-queue", "edit", "delete"], Keys(entries));
    }

    [Fact]
    public void Arrange_PutsAnyDestructiveActionLastEvenWhenItsKeyIsNotDelete()
    {
        var entries = DetailMenuGrouping.Arrange([Action("remove-thing", destructive: true), Action("edit"), Action("add-queue")]);

        Assert.Equal("remove-thing", Keys(entries).Last());
    }

    [Fact]
    public void Arrange_TreatsTheDeleteKeyAsDestructiveWithoutTheFlag()
    {
        Assert.Equal(DetailMenuGrouping.DestructiveGroup, DetailMenuGrouping.GroupOf(Action("delete")));
        Assert.True(DetailMenuGrouping.IsDestructive(Action("delete")));
    }

    [Fact]
    public void Arrange_PutsADividerBeforeTheFirstRowOfEveryLaterGroupOnly()
    {
        var entries = DetailMenuGrouping.Arrange(
            [Action("restart"), Action("status-complete"), Action("add-queue"), Action("edit"), Action("delete")]);

        Assert.Equal([false, false, true, true, true], entries.Select(entry => entry.DividerBefore).ToArray());
    }

    [Fact]
    public void Arrange_PutsAPlayFromBeginningFirstWithinItsGroup()
    {
        var entries = DetailMenuGrouping.Arrange([Action("status-complete"), Action("play-from-beginning")]);

        Assert.Equal(["play-from-beginning", "status-complete"], Keys(entries));
    }

    [Fact]
    public void Arrange_KeepsTheEngineOrderWithinAGroup()
    {
        var entries = DetailMenuGrouping.Arrange([Action("add-queue"), Action("add-playlist"), Action("play-next")]);

        Assert.Equal(["add-queue", "add-playlist", "play-next"], Keys(entries));
    }

    [Fact]
    public void Arrange_PlacesTheQuietNoteWhereEditWouldBeBeforeTheDestructiveGroup()
    {
        var entries = DetailMenuGrouping.Arrange([Action("delete", destructive: true), Action("add-queue")], note: "Edit on a computer");

        Assert.Equal(["add-queue", "<note>", "delete"], Keys(entries));
        Assert.Equal("Edit on a computer", entries[1].Note);
        Assert.True(entries[1].DividerBefore);
        Assert.True(entries[2].DividerBefore);
    }

    [Fact]
    public void Arrange_LeavesRoomForActionsThatLaterSitAfterEditAndBeforeDelete()
    {
        Assert.True(DetailMenuGrouping.EditGroup + 1 < DetailMenuGrouping.DestructiveGroup);
    }

    [Theory]
    [InlineData("edit", true)]
    [InlineData("edit-media", true)]
    [InlineData("add-queue", false)]
    public void IsEditAction_MatchesTheTwoEditKeysOnly(string key, bool expected)
    {
        Assert.Equal(expected, DetailMenuGrouping.IsEditAction(Action(key)));
    }
}
