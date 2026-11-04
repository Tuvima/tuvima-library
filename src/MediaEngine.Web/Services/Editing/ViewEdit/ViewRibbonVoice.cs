using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// How the Photos, Documents and gallery pages say what a quick action did ("Favorited 4 items", "Added to Lisbon 2026"). It uses the slim ribbon
/// (spec photos-documents-editor D6), never a toast, and an error shows in the same place. The one call the pages already used (Add) keeps its shape.
/// </summary>
public sealed class ViewRibbonVoice(ViewRibbonState ribbon)
{
    public void Add(string message, AppSeverity severity = AppSeverity.Normal) => ribbon.Show(message, severity == AppSeverity.Error);
}
