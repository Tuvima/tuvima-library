namespace MediaEngine.Web.Components.Collections;

public partial class CollectionEditorShell
{
    private string? _noticeText;
    private AppUiTone _noticeTone = AppUiTone.Warning;
    private bool _nameMissing;

    private void ShowNotice(string text, AppUiTone tone)
    {
        _noticeText = text;
        _noticeTone = tone;
    }

    private void ClearNotice()
    {
        _noticeText = null;
        _nameMissing = false;
    }

    // A save that made something but not everything ends here: one summary alert in place with a Done button. The editor
    // stays open until Done, so the person reads what did not happen; Done closes it and the made tiles glow.
    private int _summaryMade;
    private int _summaryNotMade;
    private List<string>? _summaryReasons;

    private bool SummaryShown => _summaryReasons is not null;

    private void ShowSaveSummary(int made, int notMade, IReadOnlyList<string> reasons)
    {
        _summaryMade = made;
        _summaryNotMade = notMade;
        _summaryReasons = reasons.ToList();
        _hasUnsavedChanges = false;
    }

    private async Task DismissSaveSummaryAsync()
    {
        _summaryReasons = null;
        await CloseEditorAsync(applied: true);
    }

    private void RememberSaved(Guid collectionId)
    {
        if (!Request.SavedCollectionIds.Contains(collectionId))
            Request.SavedCollectionIds.Add(collectionId);
    }
}
