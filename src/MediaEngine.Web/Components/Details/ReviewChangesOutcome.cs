namespace MediaEngine.Web.Components.Details;

/// <summary>How the person left Review changes.</summary>
public enum ReviewChangesOutcome
{
    /// <summary>Back to editing (also Esc, the close button, and Close after a partly failed save). The pending list stays.</summary>
    Back,

    /// <summary>Edit: the dialog closed and the editor jumped to the row's tool.</summary>
    Edit,

    /// <summary>Everything was saved and the editor closed.</summary>
    Saved,

    /// <summary>Discard changes was confirmed: the pending list is gone and the editor closed.</summary>
    Discarded,
}
