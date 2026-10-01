namespace MediaEngine.Web.Components.MediaEditor;

public partial class SharedMediaEditorShell
{
    protected sealed record HistoryTimelineEntry(
        DateTimeOffset OccurredAt, string Category, string Label, string? Detail,
        string ActorLabel, string? Scope, int AffectedSelectedFiles);

    protected IReadOnlyList<HistoryTimelineEntry> FilteredHistory => CurrentHistoryEntries()
        .Where(entry => _historyFilter switch
        {
            "match" => string.Equals(entry.Category, "match", StringComparison.OrdinalIgnoreCase),
            "artwork" => string.Equals(entry.Category, "artwork", StringComparison.OrdinalIgnoreCase),
            "metadata" => entry.Category is "metadata" or "manual",
            "file" => string.Equals(entry.Category, "file", StringComparison.OrdinalIgnoreCase),
            _ => true,
        })
        .OrderByDescending(entry => entry.OccurredAt)
        .ToArray();

    private IEnumerable<HistoryTimelineEntry> CurrentHistoryEntries()
    {
        if (CheckedOwnedFileCount > 0)
        {
            if (_selectionHistory is not null)
                foreach (var entry in _selectionHistory.Items)
                    yield return new(entry.OccurredAt, entry.Category, entry.Label,
                        entry.Detail, entry.ActorLabel, entry.Scope, entry.SelectedAssetIds.Count);
            yield break;
        }

        foreach (var entry in _history)
            yield return new(entry.OccurredAt, entry.Category, entry.Label,
                entry.Detail, entry.ActorLabel, null, 0);
    }

    protected static string FormatSelectedHistoryScope(string? scope) => scope switch
    {
        "file" => "File event",
        "edition" => "Edition event",
        "work" => "Work event",
        "parent" => "Parent event",
        _ => "Library event",
    };

    private string GetSelectionHistorySignature() =>
        $"{OwnedFilesParentEntityId:D}|{string.Join('|', _originBrowserSession.CheckedAssetIds.OrderBy(id => id).Select(id => id.ToString("D")))}";

    private async Task LoadSelectionHistoryAsync()
    {
        var selectedIds = _originBrowserSession.CheckedAssetIds.OrderBy(id => id).ToArray();
        if (selectedIds.Length == 0)
        {
            _selectionHistoryCancellation?.Cancel();
            _selectionHistorySignature = string.Empty;
            _selectionHistory = null;
            _selectionHistoryError = null;
            _selectionHistoryLoading = false;
            StateHasChanged();
            return;
        }

        var signature = GetSelectionHistorySignature();
        if (signature == _selectionHistorySignature) return;

        _selectionHistoryCancellation?.Cancel();
        _selectionHistoryCancellation?.Dispose();
        var cancellation = _selectionHistoryCancellation = new CancellationTokenSource();
        _selectionHistorySignature = signature;
        _selectionHistory = null;
        _selectionHistoryError = null;
        _selectionHistoryLoading = true;
        StateHasChanged();
        try
        {
            var result = await ApiClient.GetMediaEditorSelectionHistoryAsync(
                OwnedFilesParentEntityId, selectedIds, cancellation.Token);
            if (cancellation.IsCancellationRequested || signature != GetSelectionHistorySignature()) return;
            _selectionHistory = result.History;
            _selectionHistoryError = result.Error;
        }
        finally
        {
            if (ReferenceEquals(_selectionHistoryCancellation, cancellation))
            {
                _selectionHistoryLoading = false;
                _selectionHistoryCancellation = null;
                cancellation.Dispose();
                StateHasChanged();
            }
        }
    }

    protected async Task RetrySelectionHistoryAsync()
    {
        _selectionHistorySignature = string.Empty;
        await LoadSelectionHistoryAsync();
    }
}
