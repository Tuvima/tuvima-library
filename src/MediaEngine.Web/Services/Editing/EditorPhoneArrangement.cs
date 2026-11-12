using MediaEngine.Web.Services.Theming;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// When the editor is drawn in its phone arrangement (universal editor v6, section 4a and D19 to D22): a full-screen sheet, the one-line header with the
/// navigation sheet, the footer with Discard and Save changes side by side and Delete in the header's "..." menu, and Review changes as a step. Tablets in
/// portrait use it too. The device's form factor says so for a phone or tablet (it is told before the first draw), and the browser says so for a window no
/// wider than a tablet (see <see cref="NarrowWindowQuery"/>), which is the same width the form factor is read from. This is an arrangement only: whether
/// the editor may open at all is <see cref="EditorDeviceRule"/>'s, and nothing here changes it.
/// </summary>
public static class EditorPhoneArrangement
{
    /// <summary>The browser's own test for "a window the phone arrangement fits", the widest window being the last one still read as a tablet.</summary>
    public const string NarrowWindowQuery = "(max-width: 1023.98px)";

    /// <summary>True for a phone or a tablet form factor.</summary>
    public static bool Applies(DeviceContextService? device) => device is { } && (device.IsPhone || device.IsTablet);

    /// <summary>True when the device says so, or the browser reports a narrow window.</summary>
    public static bool Applies(DeviceContextService? device, bool narrowWindow) => narrowWindow || Applies(device);
}
