namespace MediaEngine.Web.Components.Details;

/// <summary>How the person left Review changes.</summary>
public enum ReviewChangesOutcome
{
    /// <summary>Back to editing (also Esc, the close button, and Close after a partly failed save). Edit mode and the pending list stay.</summary>
    Back,

    /// <summary>Edit: the dialog closed and the editor jumped to the row's tool.</summary>
    Edit,

    /// <summary>Everything was saved and edit mode ended.</summary>
    Saved,

    /// <summary>Discard changes was confirmed: the pending list is gone and edit mode ended.</summary>
    Discarded,
}
