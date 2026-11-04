namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The one order of the actions on a photo, video or document, in the selection bar, the tile's "..." menu and the viewer (spec
/// photos-documents-editor D18): the same action is always in the same place, so people learn it once. A surface shows only the
/// actions that apply to it, but never in another order. A guardrail test reads each surface and fails when one drifts.
/// </summary>
public static class ViewItemActions
{
    public const string Favorite = "Favorite";
    public const string AddToGallery = "AddToGallery";
    public const string Download = "Download";
    public const string Share = "Share";
    public const string Archive = "Archive";
    public const string Edit = "Edit";
    public const string Trash = "Trash";

    /// <summary>Favorite, Add to gallery (with New gallery), Download, Share to Shared Library, Archive, Edit, Trash.</summary>
    public static IReadOnlyList<string> Order { get; } = [Favorite, AddToGallery, Download, Share, Archive, Edit, Trash];
}
