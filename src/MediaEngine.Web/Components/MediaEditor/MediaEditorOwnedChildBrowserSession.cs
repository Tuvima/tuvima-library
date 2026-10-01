using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Components.MediaEditor;

/// <summary>Keeps the local review list in place while the editor targets one child.</summary>
public sealed class MediaEditorOwnedChildBrowserSession
{
    public Guid ParentEntityId { get; set; }
    public MediaEditorOwnedChildSearchDto? Result { get; set; }
    /// <summary>Focus is an inspection target; checked files are independent edit intent.</summary>
    public Guid? SelectedAssetId { get; set; }
    public HashSet<Guid> CheckedAssetIds { get; } = [];
    public Dictionary<Guid, string> CheckedSelectionRevisions { get; } = [];
    public Guid? RangeAnchorAssetId { get; private set; }
    public int VisibleSelectedCount => Result?.Items.Count(item => CheckedAssetIds.Contains(item.AssetId)) ?? 0;
    public int HiddenSelectedCount => Math.Max(0, CheckedAssetIds.Count - VisibleSelectedCount);
    public bool HasCompleteSelectionRevisions => CheckedAssetIds.All(id =>
        CheckedSelectionRevisions.TryGetValue(id, out var revision) && !string.IsNullOrWhiteSpace(revision));
    public string Query { get; set; } = string.Empty;
    public int? Season { get; set; }
    public int? Disc { get; set; }
    public int? Volume { get; set; }
    public string? MatchStatus { get; set; }
    public string? FileStatus { get; set; }
    public double ScrollTop { get; set; }
    public bool RefreshOnReturn { get; set; }

    public bool IsChecked(Guid assetId) => CheckedAssetIds.Contains(assetId);

    public void Focus(Guid assetId) => SelectedAssetId = assetId;

    public void SetChecked(Guid assetId, bool isChecked, string? selectionRevision = null)
    {
        if (isChecked)
        {
            CheckedAssetIds.Add(assetId);
            if (!string.IsNullOrWhiteSpace(selectionRevision))
                CheckedSelectionRevisions.TryAdd(assetId, selectionRevision);
        }
        else { CheckedAssetIds.Remove(assetId); CheckedSelectionRevisions.Remove(assetId); }
        RangeAnchorAssetId = assetId;
    }

    public void SelectVisible(bool isChecked)
    {
        if (Result is null) return;
        foreach (var item in Result.Items)
        {
            if (isChecked) SetChecked(item.AssetId, true, item.SelectionRevision);
            else { CheckedAssetIds.Remove(item.AssetId); CheckedSelectionRevisions.Remove(item.AssetId); }
        }
    }

    public void SelectRange(Guid assetId)
    {
        if (Result is null || RangeAnchorAssetId is not { } anchor)
        {
            SetChecked(assetId, true, Result?.Items.FirstOrDefault(item => item.AssetId == assetId)?.SelectionRevision);
            return;
        }

        var items = Result.Items;
        var start = items.ToList().FindIndex(item => item.AssetId == anchor);
        var end = items.ToList().FindIndex(item => item.AssetId == assetId);
        if (start < 0 || end < 0)
        {
            SetChecked(assetId, true, Result.Items.FirstOrDefault(item => item.AssetId == assetId)?.SelectionRevision);
            return;
        }

        for (var index = Math.Min(start, end); index <= Math.Max(start, end); index++)
            SetChecked(items[index].AssetId, true, items[index].SelectionRevision);
    }

    public void ClearSelection()
    {
        CheckedAssetIds.Clear();
        CheckedSelectionRevisions.Clear();
        RangeAnchorAssetId = null;
    }

    public bool TryAddSelectionSnapshot(MediaEditorOwnedChildSelectionSnapshotDto snapshot, int maximum,
        out int combinedCount)
    {
        var ids = snapshot.Items.Select(item => item.AssetId).ToHashSet();
        combinedCount = CheckedAssetIds.Union(ids).Count();
        if (snapshot.ParentEntityId != ParentEntityId || snapshot.Count != ids.Count
            || ids.Contains(Guid.Empty) || snapshot.Items.Any(item => string.IsNullOrWhiteSpace(item.SelectionRevision))
            || combinedCount > maximum)
            return false;
        foreach (var item in snapshot.Items)
        {
            CheckedAssetIds.Add(item.AssetId);
            CheckedSelectionRevisions[item.AssetId] = item.SelectionRevision;
        }
        return true;
    }

    public void ResetForParent(Guid parentEntityId)
    {
        ParentEntityId = parentEntityId;
        Result = null;
        SelectedAssetId = null;
        ClearSelection();
        Query = string.Empty;
        Season = Disc = Volume = null;
        MatchStatus = FileStatus = null;
        ScrollTop = 0;
        RefreshOnReturn = false;
    }
}
