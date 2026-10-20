namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Where Match and Identity can be used: everywhere except a TV target below the episode level (a TV file group has no
/// identity of its own). The editor and the Editing Mode banner both ask this one rule, so the banner never offers a
/// section the editor would leave out.
/// </summary>
public static class MediaEditorMatchRule
{
    public static bool CanMatch(string? editorMediaType, string? scopeId) =>
        !string.Equals(editorMediaType, "TV", StringComparison.Ordinal)
        || scopeId is "series" or "season" or "episode";
}
