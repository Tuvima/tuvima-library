namespace MediaEngine.Web.Components.MediaEditor;

/// <summary>
/// Owns the four public editor destinations and normalizes legacy links into
/// their replacement destination. File inspection is now part of Details.
/// </summary>
public sealed class MediaEditorTabState
{
    public string ActiveTab { get; private set; } = "details";
    public string LastNonFileTab { get; private set; } = "details";

    public void Initialize(string? tabId)
    {
        ActiveTab = Normalize(tabId);
        LastNonFileTab = ActiveTab;
    }

    public void Activate(string? tabId)
    {
        ActiveTab = Normalize(tabId);
        LastNonFileTab = ActiveTab;
    }

    public void ActivateFile() => Activate("details");

    public void RememberCurrentNonFile()
    {
        LastNonFileTab = ActiveTab;
    }

    public void EnsureVisible(Func<string, bool> isVisible, IEnumerable<string> visibleTabs)
    {
        if (isVisible(ActiveTab))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(LastNonFileTab) && isVisible(LastNonFileTab))
        {
            Activate(LastNonFileTab);
            return;
        }

        Activate(visibleTabs.FirstOrDefault() ?? "details");
    }

    public static string Normalize(string? tabId) =>
        (tabId ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "identity" => "links",
            "universe" => "links",
            "id" => "links",
            "file" => "details",
            "files" => "details",
            "inspector" => "details",
            "options" => "details",
            "chapters" => "details",
            "episodes" => "details",
            "tracks" => "details",
            "retired-file" => "details",
            "" => "details",
            "details" or "artwork" or "links" or "history" => (tabId ?? string.Empty).Trim().ToLowerInvariant(),
            _ => "details",
        };
}
