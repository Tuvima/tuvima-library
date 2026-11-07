namespace MediaEngine.Web.Services.Editing;

/// <summary>What the supporting-files browser asks the editor to do when it closes.</summary>
public enum EditorFileBrowserAction
{
    /// <summary>Back to the item the person was on.</summary>
    Close,

    /// <summary>"Edit selected": the ticked files open together as "Editing N files" (one ticked file opens as itself).</summary>
    EditSelected,

    /// <summary>One supporting file opens in the editor, on Details or on Preview.</summary>
    OpenFile,

    /// <summary>One of the title's own media files (an episode, a track) opens in the editor.</summary>
    OpenItem,
}

/// <summary>The answer of the browser dialog.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Id">The file or item to open, for <see cref="EditorFileBrowserAction.OpenFile"/> and <see cref="EditorFileBrowserAction.OpenItem"/>.</param>
/// <param name="Preview">True to open the file on its Preview tab instead of Details.</param>
public sealed record EditorFileBrowserOutcome(EditorFileBrowserAction Action, Guid Id = default, bool Preview = false)
{
    public static EditorFileBrowserOutcome Closed { get; } = new(EditorFileBrowserAction.Close);
}
