using Microsoft.AspNetCore.Components.Forms;

namespace MediaEngine.Web.Components.Profiles;

/// <summary>What someone chose in the profile editor sheet. The page that opened the sheet sends it to the Engine.</summary>
/// <param name="DisplayName">The trimmed name, 1 to 50 characters.</param>
/// <param name="Color">A palette colour in #RRGGBB form.</param>
/// <param name="Icon">Key of a built-in icon, or <see langword="null"/> to show the initial.</param>
/// <param name="IsChild">True for a Kids profile (only changeable while adding).</param>
/// <param name="NewPin">A new PIN of 4 to 12 digits to set, or <see langword="null"/> to leave the PIN alone.</param>
/// <param name="RemovePin">True when the person asked to remove the existing PIN.</param>
/// <param name="NewPhoto">A freshly picked photo to upload, if any.</param>
/// <param name="RemovePhoto">True when the existing saved photo should be removed.</param>
public sealed record ProfileEditorResult(
    string DisplayName,
    string Color,
    string? Icon,
    bool IsChild,
    string? NewPin,
    bool RemovePin,
    IBrowserFile? NewPhoto,
    bool RemovePhoto);
