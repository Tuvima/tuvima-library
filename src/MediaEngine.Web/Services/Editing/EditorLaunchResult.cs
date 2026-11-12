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
    /// Item editing is not available on this device: a TV, or anything the Dashboard cannot identify as a desktop, tablet or phone, never opens
    /// an editor. (Desktop-only tools in Settings and the Vault ask EditorDeviceRule.CanEdit themselves.)
    /// </summary>
    NotAvailableOnThisDevice,
}
