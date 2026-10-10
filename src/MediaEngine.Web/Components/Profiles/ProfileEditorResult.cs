namespace MediaEngine.Web.Components.Profiles;

/// <summary>What someone chose in the profile editor sheet. The page that opened the sheet sends it to the Engine.</summary>
/// <param name="DisplayName">The trimmed name, 1 to 50 characters.</param>
/// <param name="Color">A palette colour in #RRGGBB form.</param>
/// <param name="Icon">Key of a built-in icon, or <see langword="null"/> to show the initial. When updating, send an empty string for null: the Engine reads an omitted icon as "keep".</param>
/// <param name="IsChild">True for a Kids profile (only changeable while adding).</param>
/// <param name="NewPin">A new PIN of 4 to 12 digits to set, or <see langword="null"/> to leave the PIN alone.</param>
/// <param name="RemovePin">True when the person asked to remove the existing PIN.</param>
/// <param name="PhotoBytes">The bytes of a freshly picked photo to upload (JPEG, PNG or WebP, up to 5 MB), if any.</param>
/// <param name="PhotoFileName">The picked photo's file name, for the upload.</param>
/// <param name="RemovePhoto">True when the existing saved photo should be removed.</param>
/// <param name="ContentLimit">What this person may watch: an empty string for Everything, or G, PG, PG-13 or R.</param>
/// <param name="ContentLimitAllowUnrated">True when items with no rating stay visible although a limit is set.</param>
public sealed record ProfileEditorResult(
    string DisplayName,
    string Color,
    string? Icon,
    bool IsChild,
    string? NewPin,
    bool RemovePin,
    byte[]? PhotoBytes,
    string? PhotoFileName,
    bool RemovePhoto,
    string ContentLimit = "",
    bool ContentLimitAllowUnrated = false);
