namespace MediaEngine.Web.Components.Shared;

/// <summary>What the person chose in <see cref="AppConfirmDialog"/>.</summary>
public enum AppConfirmResult
{
    /// <summary>The primary button (the step the dialog asks about).</summary>
    Primary,

    /// <summary>The secondary button (the other choice, such as Discard next to Save).</summary>
    Secondary,

    /// <summary>The dialog was closed without choosing (Esc, the close button or the backdrop): nothing happens.</summary>
    Cancelled,
}
