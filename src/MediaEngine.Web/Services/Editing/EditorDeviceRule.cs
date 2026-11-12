using MediaEngine.Web.Services.Theming;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The device rules for editing. There are two, and every edit handler calls one of them first so hiding a button is
/// never the only protection. The Dashboard server and the browser snapshot feed the same inputs.
/// <list type="bullet">
/// <item><description><b>Item editing</b> (<see cref="CanEditItems(DeviceContextService?)"/>, <see cref="TryRefuseItems"/>): the editor, everything that opens it,
/// Delete and collection membership. Allowed on a desktop, tablet or phone, whatever the pointer; a TV, an unknown form factor and a missing
/// device context mean "no". The Engine's permission check still decides who may edit.</description></item>
/// <item><description><b>Desktop-only tools</b> (<see cref="CanEdit(DeviceContextService?)"/>, <see cref="TryRefuse"/>, <see cref="Message"/>): Settings configuration screens
/// and the Vault tools only. Allowed only on a desktop form factor whose primary pointer is fine and can hover; a phone, tablet, TV, native app
/// shell, unknown form factor and a browser that cannot report its pointer all mean "no".</description></item>
/// </list>
/// </summary>
public static class EditorDeviceRule
{
    /// <summary>The line for the desktop-only tools (Settings, the Vault tools).</summary>
    public const string Message = "Edit on a computer";

    /// <summary>The line for the item rule: only a TV or an unknown device is refused, and a phone or tablet works, so it never names a computer.</summary>
    public const string ItemMessage = "Editing is not available on this device";

    /// <summary>The pure item-editing rule: desktop, tablet and phone may edit; TV, an unknown or missing form factor and a television device class may not.</summary>
    public static bool CanEditItems(string? deviceClass, string? formFactor) =>
        !string.Equals(deviceClass, "television", StringComparison.OrdinalIgnoreCase)
        && (string.Equals(formFactor, "desktop", StringComparison.OrdinalIgnoreCase)
            || string.Equals(formFactor, "tablet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(formFactor, "phone", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True only when the current circuit's device may edit items. A missing device context never edits, and neither does one the browser has not
    /// reported yet (its form factor is still a guess).
    /// </summary>
    public static bool CanEditItems(DeviceContextService? device) =>
        device is { IsInitialised: true } && CanEditItems(device.DeviceClass, device.FormFactor);

    /// <summary>
    /// Returns true when the current device may not edit items. Launchers and edit handlers call this before any other work so no editor
    /// surface ever opens; a handler that has room for a line shows <see cref="ItemMessage"/> in place (an alert), nothing is drawn over the page.
    /// </summary>
    public static bool TryRefuseItems(DeviceContextService? device) => !CanEditItems(device);

    /// <summary>The pure desktop-tools rule over the four signals the browser reports.</summary>
    public static bool CanEdit(string? deviceClass, string? formFactor, bool finePointer, bool canHover) =>
        string.Equals(deviceClass, "web", StringComparison.OrdinalIgnoreCase)
        && string.Equals(formFactor, "desktop", StringComparison.OrdinalIgnoreCase)
        && finePointer
        && canHover;

    /// <summary>True only when the current circuit's device may use a desktop-only tool. A missing device context never does.</summary>
    public static bool CanEdit(DeviceContextService? device) =>
        device is not null && CanEdit(device.DeviceClass, device.FormFactor, device.HasFinePointer, device.CanHover);

    /// <summary>
    /// Returns true when the current device may not use a desktop-only tool (Settings configuration, Vault tools). Those handlers call this first and
    /// show <see cref="Message"/> in place.
    /// </summary>
    public static bool TryRefuse(DeviceContextService? device) => !CanEdit(device);
}
