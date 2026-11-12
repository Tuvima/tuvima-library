using MediaEngine.Web.Services.Theming;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The arrangement the editor window is drawn in, cascaded from the window (<c>EditorPopover</c>, <c>ViewEditorPopover</c>) so a panel inside it reads
/// <see cref="Phone"/> instead of having it threaded down as a parameter. Only the two shared instances exist, so a change of arrangement is a change of
/// reference and the parts below draw again. A part drawn outside a window sees no value and means the desktop arrangement.
/// </summary>
public sealed class EditorWindowLayout
{
    private EditorWindowLayout(bool phone) => Phone = phone;

    /// <summary>The desktop arrangement: the framed window with the header's dropdowns.</summary>
    public static EditorWindowLayout Desktop { get; } = new(false);

    /// <summary>The phone arrangement (a phone, or a tablet in portrait): the full-screen sheet with the one-line header and the navigation sheet.</summary>
    public static EditorWindowLayout PhoneSheet { get; } = new(true);

    /// <summary>True in the phone arrangement.</summary>
    public bool Phone { get; }

    /// <summary>The layout for a device: the phone arrangement for a phone or tablet form factor.</summary>
    public static EditorWindowLayout For(DeviceContextService? device) => EditorPhoneArrangement.Applies(device) ? PhoneSheet : Desktop;

    /// <summary>The layout a part is drawn in: its own <c>Phone</c> parameter says phone, or the window it sits in does.</summary>
    public static bool IsPhone(bool phoneParameter, EditorWindowLayout? layout) => phoneParameter || layout is { Phone: true };
}
