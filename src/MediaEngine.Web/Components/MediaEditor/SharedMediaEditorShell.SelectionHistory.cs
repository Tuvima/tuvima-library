namespace MediaEngine.Web.Components.MediaEditor;

public partial class SharedMediaEditorShell
{
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
        foreach (var entry in _history)
        {
            yield return new(entry.OccurredAt, entry.Category, entry.Label,
                    entry.Detail, entry.ActorLabel, null, 0, entry.EventType);
        }
    }

}
