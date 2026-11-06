namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Where Match and Identity can be used: everywhere except a TV target below the episode level (a TV file group has no
/// identity of its own). The editor and its tool tabs both ask this one rule, so the tabs never offer a
/// tool the editor would leave out.
/// </summary>
public static class MediaEditorMatchRule
{
    public static bool CanMatch(string? editorMediaType, string? scopeId) =>
        // A folder of Other videos (a course, a saved channel) has no identity to match; its single videos do (details page D15).
        !(string.Equals(editorMediaType, "Videos", StringComparison.OrdinalIgnoreCase) && scopeId is "series")
        && (!string.Equals(editorMediaType, "TV", StringComparison.Ordinal)
        || scopeId is "series" or "season" or "episode");
}
