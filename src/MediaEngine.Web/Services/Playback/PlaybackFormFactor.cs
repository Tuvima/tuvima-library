using MediaEngine.Web.Services.Theming;

namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// The one rule for "is the player drawn for a phone" (unified player v2 D26). A phone form factor or a window no wider than <see cref="PhoneMaxWidth"/> pixels
/// both count, so the audio bar and the video overlay always agree: a phone turned sideways keeps its phone player, and a narrow desktop window gets it too.
/// </summary>
public static class PlaybackFormFactor
{
    /// <summary>The widest window, in CSS pixels, that still gets the phone player.</summary>
    public const double PhoneMaxWidth = 720;

    /// <summary>Whether a window of this width gets the phone player; an unknown width does not.</summary>
    public static bool IsPhoneWidth(double? viewportWidth) => viewportWidth is > 0 and <= PhoneMaxWidth;

    /// <summary>Whether the player is drawn for a phone: the device is a phone, or the window is phone width.</summary>
    public static bool IsPhone(DeviceContextService? device, double? viewportWidth) =>
        device?.IsPhone == true || IsPhoneWidth(viewportWidth);

    /// <summary>The same rule read from the one device context, which holds the width the player last reported.</summary>
    public static bool IsPhone(DeviceContextService? device) => IsPhone(device, device?.ViewportWidth);
}
