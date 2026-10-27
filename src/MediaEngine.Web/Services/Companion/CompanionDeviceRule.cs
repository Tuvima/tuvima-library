using MediaEngine.Web.Services.Theming;

namespace MediaEngine.Web.Services.Companion;

/// <summary>
/// The one device rule for the Companion. It is a second-screen helper for a phone or tablet held beside the TV or computer
/// that is playing, so the menu item, the Home card and the page are offered only on those form factors. A desktop, a TV
/// and an unknown device all mean "no".
/// </summary>
public static class CompanionDeviceRule
{
    public static bool IsAvailable(string? formFactor) =>
        string.Equals(formFactor, "phone", StringComparison.OrdinalIgnoreCase)
        || string.Equals(formFactor, "tablet", StringComparison.OrdinalIgnoreCase);

    public static bool IsAvailable(DeviceContextService? device) => device is not null && IsAvailable(device.FormFactor);
}
