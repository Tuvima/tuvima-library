namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>
/// The four picture copies the Engine keeps (right-sized pictures spec D1) and the one rule for
/// picking one: the smallest copy whose long side is at least the drawn size times the screen's
/// sharpness. Sizes are long-side pixels.
/// </summary>
public static class ArtworkSize
{
    /// <summary>96 px: faces, small list thumbnails, badges.</summary>
    public const string Tiny = "t";

    /// <summary>320 px: tiles, cards, lists.</summary>
    public const string Small = "s";

    /// <summary>960 px: details posters, TV tiles, phone heroes.</summary>
    public const string Medium = "m";

    /// <summary>2160 px: full-screen backgrounds, 4K TV.</summary>
    public const string Large = "l";

    public const int TinyPx = 96;
    public const int SmallPx = 320;
    public const int MediumPx = 960;
    public const int LargePx = 2160;

    /// <summary>The smallest copy at least <paramref name="pixels"/> on its long side.</summary>
    public static string ForPixels(int pixels) => pixels switch
    {
        <= TinyPx => Tiny,
        <= SmallPx => Small,
        <= MediumPx => Medium,
        _ => Large,
    };

    public static bool IsKnown(string? size) => size is Tiny or Small or Medium or Large;
}
