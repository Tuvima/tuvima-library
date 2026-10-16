namespace MediaEngine.Web.Services.Editing;

/// <summary>Outcome of asking a launcher to open an editor dialog.</summary>
public enum EditorLaunchResult
{
    /// <summary>The editor opened and the user applied a change.</summary>
    Applied,

    /// <summary>The editor opened and was closed without a change.</summary>
    Dismissed,

    /// <summary>The request was invalid, locked, or the dialog could not open.</summary>
    NotAvailable,

    /// <summary>
    /// Editing is desktop-only: phones, tablets, TVs and anything the Dashboard cannot identify as a desktop with a
    /// fine, hover-capable pointer never open an editor.
    /// </summary>
    NotAvailableOnThisDevice,
}
