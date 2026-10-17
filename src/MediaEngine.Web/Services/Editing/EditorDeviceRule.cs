using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The one device rule for editing. Editing is allowed only on a desktop form factor whose primary pointer is fine
/// and can hover. Phone, tablet, TV, a native app shell, an unknown form factor and a browser that cannot report its
/// pointer all mean "no". The Dashboard server and the browser snapshot feed the same inputs, and every edit handler
/// calls this rule, so hiding a button is never the only protection.
/// </summary>
public static class EditorDeviceRule
{
    public const string Message = "Edit on a computer";

    /// <summary>The pure rule over the four signals the browser reports.</summary>
    public static bool CanEdit(string? deviceClass, string? formFactor, bool finePointer, bool canHover) =>
        string.Equals(deviceClass, "web", StringComparison.OrdinalIgnoreCase)
        && string.Equals(formFactor, "desktop", StringComparison.OrdinalIgnoreCase)
        && finePointer
        && canHover;

    /// <summary>True only when the current circuit's device may edit. A missing device context never edits.</summary>
    public static bool CanEdit(DeviceContextService? device) =>
        device is not null && CanEdit(device.DeviceClass, device.FormFactor, device.HasFinePointer, device.CanHover);

    /// <summary>
    /// Returns true (and shows the standard toast) when the current device may not edit.
    /// Launchers and edit handlers call this before any other work so no editor surface ever opens.
    /// </summary>
    public static bool TryRefuse(DeviceContextService? device, IAppToastService? toasts)
    {
        if (CanEdit(device))
        {
            return false;
        }

        toasts?.Add(Message, AppSeverity.Info);
        return true;
    }
}
