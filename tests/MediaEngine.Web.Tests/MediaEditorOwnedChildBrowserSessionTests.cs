using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.MediaEditor;

namespace MediaEngine.Web.Tests;

public sealed class MediaEditorOwnedChildBrowserSessionTests
{
    [Fact]
    public void FocusDoesNotReplaceCheckedFilesAcrossPages()
    {
        var parent = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var session = new MediaEditorOwnedChildBrowserSession { ParentEntityId = parent };
        session.Result = Page(parent, first, second);
        session.SetChecked(first, true);

        session.Focus(second);
        session.Result = Page(parent, Guid.NewGuid());

        Assert.Equal(second, session.SelectedAssetId);
        Assert.Contains(first, session.CheckedAssetIds);
        Assert.Equal(1, session.HiddenSelectedCount);
    }

    [Fact]
    public void SelectVisibleChangesOnlyCurrentPageAndPreservesHiddenSelection()
    {
        var parent = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var visible = Guid.NewGuid();
        var session = new MediaEditorOwnedChildBrowserSession { ParentEntityId = parent, Result = Page(parent, visible) };
        session.SetChecked(hidden, true);

        session.SelectVisible(true);
        session.SelectVisible(false);

        Assert.Equal([hidden], session.CheckedAssetIds);
        Assert.Equal(1, session.HiddenSelectedCount);
    }

    [Fact]
    public void RangeSelectUsesCurrentPageAndParentResetClearsPreviousContext()
    {
        var parent = Guid.NewGuid();
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var session = new MediaEditorOwnedChildBrowserSession { ParentEntityId = parent, Result = Page(parent, ids) };
        session.SetChecked(ids[1], true);

        session.SelectRange(ids[3]);

        Assert.Equal([ids[1], ids[2], ids[3]], session.CheckedAssetIds.OrderBy(id => Array.IndexOf(ids, id)));
        session.ResetForParent(Guid.NewGuid());
        Assert.Empty(session.CheckedAssetIds);
        Assert.Null(session.Result);
        Assert.Null(session.SelectedAssetId);
    }

    [Fact]
    public void AllMatchingSnapshotPreservesExistingSelectionAndRevisions()
    {
        var parent = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var matching = Guid.NewGuid();
        var session = new MediaEditorOwnedChildBrowserSession { ParentEntityId = parent, Result = Page(parent, matching) };
        session.SetChecked(hidden, true);
        var snapshot = Snapshot(parent, matching, "revision-1");

        Assert.True(session.TryAddSelectionSnapshot(snapshot, 1000, out var combined));
        Assert.Equal(2, combined);
        Assert.Equal(2, session.CheckedAssetIds.Count);
        Assert.Equal("revision-1", session.CheckedSelectionRevisions[matching]);
        session.SetChecked(matching, false);
        Assert.DoesNotContain(matching, session.CheckedSelectionRevisions.Keys);
    }

    [Fact]
    public void AllMatchingOverCapDoesNotDropOldSelectionOrAddSnapshot()
    {
        var parent = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var session = new MediaEditorOwnedChildBrowserSession { ParentEntityId = parent };
        session.SetChecked(hidden, true);
        var snapshot = Snapshot(parent, Enumerable.Range(0, 1000)
            .Select(index => (Guid.NewGuid(), $"revision-{index}")));

        Assert.False(session.TryAddSelectionSnapshot(snapshot, 1000, out var combined));
        Assert.Equal(1001, combined);
        Assert.Equal([hidden], session.CheckedAssetIds);
        Assert.Empty(session.CheckedSelectionRevisions);
    }

    [Fact]
    public void ManualAndRangeChecksCaptureRevisionAndMissingRevisionBlocksPreview()
    {
        var parent = Guid.NewGuid();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var session = new MediaEditorOwnedChildBrowserSession { ParentEntityId = parent, Result = Page(parent, ids) };
        session.SetChecked(ids[0], true, session.Result.Items[0].SelectionRevision);
        session.SelectRange(ids[2]);

        Assert.True(session.HasCompleteSelectionRevisions);
        Assert.Equal(3, session.CheckedSelectionRevisions.Count);
        session.SetChecked(Guid.NewGuid(), true);
        Assert.False(session.HasCompleteSelectionRevisions);
    }

    private static MediaEditorOwnedChildSelectionSnapshotDto Snapshot(Guid parent, Guid id, string revision) =>
        Snapshot(parent, [(id, revision)]);

    private static MediaEditorOwnedChildSelectionSnapshotDto Snapshot(Guid parent,
        IEnumerable<(Guid Id, string Revision)> items) => new()
        {
            ParentEntityId = parent,
            Items = items.Select(item => new MediaEditorOwnedChildSelectionItemDto
                { AssetId = item.Id, SelectionRevision = item.Revision }).ToList(),
            Count = items.Count(),
        };

    private static MediaEditorOwnedChildSearchDto Page(Guid parent, params Guid[] assetIds) => new()
    {
        ParentEntityId = parent,
        Items = assetIds.Select(id => new MediaEditorOwnedChildDto { AssetId = id, SelectionRevision = $"revision-{id:D}" }).ToList(),
    };
}
