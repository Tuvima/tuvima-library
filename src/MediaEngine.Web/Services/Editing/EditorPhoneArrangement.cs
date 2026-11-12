using MediaEngine.Web.Services.Theming;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// When the editor is drawn in its phone arrangement (universal editor v6, section 4a and D19 to D22): a full-screen sheet, the one-line header with the
/// navigation sheet, the footer with Discard and Save changes side by side and Delete in the header's "..." menu, and Review changes as a step. Tablets in
/// portrait use it too. The one signal is the device's form factor, which the browser reports before the first draw and again whenever the window crosses
/// a form-factor width (a phone or tablet is under 1024 wide). This is an arrangement only: whether the editor may open at all is
/// <see cref="EditorDeviceRule"/>'s, and nothing here changes it.
/// </summary>
public static class EditorPhoneArrangement
{
    /// <summary>True for a phone or a tablet form factor.</summary>
    public static bool Applies(DeviceContextService? device) => device is { } && (device.IsPhone || device.IsTablet);
}
