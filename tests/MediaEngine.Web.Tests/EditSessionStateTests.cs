using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>The universal editor's session state and pending list (spec WP2).</summary>
public sealed class EditSessionStateTests
{
    private static readonly Guid Show = Guid.NewGuid();
    private static readonly Guid Season = Guid.NewGuid();

    [Fact]
    public void SetField_StagesTheValue_AndStagingTheSameFieldAgainReplacesIt()
    {
        var pending = new PendingEditList();

        pending.SetField(Show, "title", "Le Continental");
        pending.SetField(Show, "Title", "Le Continental 2");

        var change = Assert.Single(pending.Changes);
        var field = Assert.Single(change.Fields);
        Assert.Equal("Le Continental 2", field.Value);
        Assert.True(pending.TryGetField(Show, "title", out var value));
        Assert.Equal("Le Continental 2", value);
    }

    [Fact]
    public void SetField_KeepsOneEntryPerItem_AndSeparateEntriesForSeparateItems()
    {
        var pending = new PendingEditList();

        pending.SetField(Show, "title", "a");
        pending.SetField(Show, "year", "2024");
        pending.SetField(Season, "title", "b");

        Assert.Equal(2, pending.Count);
        Assert.Equal(2, pending.Changes.Single(c => c.EntityId == Show).Fields.Count);
        Assert.Equal(1, pending.CountForItem(Season));
    }

    [Fact]
    public void ClearField_RemovesTheEntryWhenItsLastFieldGoes()
    {
        var pending = new PendingEditList();
        pending.SetField(Show, "title", "a");
        pending.SetField(Show, "year", "2024");

        Assert.True(pending.ClearField(Show, "title"));
        Assert.Single(pending.Changes.Single().Fields);
        Assert.True(pending.ClearField(Show, "year"));
        Assert.True(pending.IsEmpty);
        Assert.False(pending.ClearField(Show, "year"));
    }

    [Fact]
    public void SetTarget_KeepsIdentityRelationshipAndMovesApart_AndRefusesDetailsAndArtwork()
    {
        var pending = new PendingEditList();

        pending.SetTarget(EditSessionChangeKinds.Identity, Show, "record-1");
        pending.SetTarget(EditSessionChangeKinds.Relationship, Show, "album-1");
        pending.SetTarget(EditSessionChangeKinds.Identity, Show, "record-2");

        Assert.Equal(2, pending.Count);
        Assert.Equal("record-2", pending.Changes.Single(c => c.Kind == EditSessionChangeKinds.Identity).TargetId);
        Assert.Throws<ArgumentException>(() => pending.SetTarget(EditSessionChangeKinds.Details, Show, "x"));
        Assert.Throws<ArgumentException>(() => pending.SetTarget("delete", Show, "x"));
    }

    [Fact]
    public void SetArtwork_ReplacesTheChoiceForTheSameSlotOnly()
    {
        var pending = new PendingEditList();

        pending.SetArtwork(Show, "Background", "one");
        pending.SetArtwork(Show, "Logo", "logo");
        pending.SetArtwork(Show, "background", "two");

        Assert.Equal(2, pending.Count);
        Assert.Equal("two", pending.Changes.Single(c => c.Slot!.Equals("Background", StringComparison.OrdinalIgnoreCase)).TargetId);
    }

    [Fact]
    public void Remove_DropsOneChangeById_AndRemoveForItemDropsAllOfThatItem()
    {
        var pending = new PendingEditList();
        pending.SetField(Show, "title", "a");
        pending.SetField(Season, "title", "b");
        pending.SetTarget(EditSessionChangeKinds.Relationship, Season, "x");

        Assert.True(pending.Remove(pending.Changes.First(c => c.EntityId == Show).Id));
        Assert.False(pending.Remove("missing"));
        Assert.Equal(2, pending.RemoveForItem(Season));
        Assert.True(pending.IsEmpty);
    }

    [Fact]
    public void ToRequest_CarriesEveryChange_AndTheCheckOnlyFlag()
    {
        var pending = new PendingEditList();
        pending.SetField(Show, "title", "a");

        var request = pending.ToRequest(previewOnly: true);

        Assert.True(request.PreviewOnly);
        Assert.Single(request.Changes);
    }

    [Fact]
    public void SwitchItem_KeepsPendingChangesOnEveryItem()
    {
        var state = new EditSessionState(new PendingEditList());
        state.Begin([new EditTrailItem(Show, "Le Continental", "show")]);
        state.Pending.SetField(Show, "title", "a");

        state.SwitchItem([new EditTrailItem(Show, "Le Continental", "show"), new EditTrailItem(Season, "Season 1", "season")]);
        state.Pending.SetField(Season, "title", "b");
        state.SwitchItem([new EditTrailItem(Show, "Le Continental", "show")]);

        Assert.Equal(Show, state.ActiveItem!.EntityId);
        Assert.Equal(2, state.Pending.Count);
        Assert.True(state.HasPendingChanges);
    }

    [Fact]
    public void ClosingThePanel_KeepsEditModeAndThePendingList()
    {
        var state = new EditSessionState(new PendingEditList());
        state.Begin([new EditTrailItem(Show, "Show", "show")]);
        state.Pending.SetField(Show, "title", "a");
        state.SetTool(EditTool.Details);

        state.SetTool(null);

        Assert.True(state.IsActive);
        Assert.Null(state.ActiveTool);
        Assert.Equal(1, state.Pending.Count);
    }

    [Fact]
    public void LeavingTheArtworkTool_ReturnsThePageToItsNormalView()
    {
        var state = new EditSessionState(new PendingEditList());
        state.Begin([new EditTrailItem(Season, "Season 1", "season")]);
        state.SetTool(EditTool.Artwork);
        state.SetViewContext(EditViewContext.TemporaryArtwork);

        state.SetTool(EditTool.Details);

        Assert.Equal(EditViewContext.Normal, state.ViewContext);
    }

    [Fact]
    public void Cancel_DiscardsThePendingListAndLeavesEditMode()
    {
        var state = new EditSessionState(new PendingEditList());
        state.Begin([new EditTrailItem(Show, "Show", "show")]);
        state.Pending.SetField(Show, "title", "a");

        state.Cancel();

        Assert.False(state.IsActive);
        Assert.Null(state.ActiveItem);
        Assert.True(state.Pending.IsEmpty);
    }

    [Fact]
    public void Changed_FiresWhenPendingChangesChange()
    {
        var state = new EditSessionState(new PendingEditList());
        var raised = 0;
        state.Changed += () => raised++;

        state.Pending.SetField(Show, "title", "a");
        state.Pending.Discard();

        Assert.Equal(2, raised);
    }

    [Fact]
    public void ToolsAndSwitching_RefuseToRunWithoutASession()
    {
        var state = new EditSessionState(new PendingEditList());

        Assert.Throws<InvalidOperationException>(() => state.SetTool(EditTool.Details));
        Assert.Throws<InvalidOperationException>(() => state.SwitchItem([new EditTrailItem(Show, "x", "show")]));
        Assert.Throws<ArgumentException>(() => state.Begin([]));
    }
}
