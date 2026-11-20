namespace MediaEngine.Web.Services.Theming;

/// <summary>
/// The three device sizes every layout rule uses (Look v3.2 D1): Compact under 600, Medium 600 to 839, Expanded 840 and up,
/// with 1180 kept as a wide-page extra. The numbers come from <c>design/tokens/tuvima.tokens.json</c>
/// (<c>--tl-breakpoint-phone</c>, <c>--tl-breakpoint-navigation</c>, <c>--tl-breakpoint-wide</c> in tuvima.tokens.css); a test keeps these constants equal to that file.
/// </summary>
public static class DeviceSizes
{
    /// <summary>The first width, in CSS pixels, that is no longer Compact (a phone).</summary>
    public const double MediumMin = 600;

    /// <summary>The first width that is Expanded (sidebar and desktop bar).</summary>
    public const double ExpandedMin = 840;

    /// <summary>The first width that counts as a wide page.</summary>
    public const double WideMin = 1180;

    /// <summary>Which device size a window of this width has; an unknown or non-positive width is Expanded, the desktop default.</summary>
    public static DeviceSize Of(double? width) => width switch
    {
        null or <= 0 => DeviceSize.Expanded,
        < MediumMin => DeviceSize.Compact,
        < ExpandedMin => DeviceSize.Medium,
        _ => DeviceSize.Expanded,
    };
}

/// <summary>The Look v3.2 device sizes.</summary>
public enum DeviceSize
{
    /// <summary>Under 600 px: phones.</summary>
    Compact,

    /// <summary>600 to 839 px: large phones turned sideways, small tablets.</summary>
    Medium,

    /// <summary>840 px and up: tablets in landscape, laptops, desktops.</summary>
    Expanded,
}
