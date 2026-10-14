namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// The language names the audio and subtitle pickers offer. The profile stores the display name, and the player matches it to a
/// track's language code (see <see cref="VideoPresentationResolver.LanguageMatches"/>).
/// </summary>
public static class PlaybackLanguageOptions
{
    public static IReadOnlyList<string> Common { get; } =
    [
        "English", "Spanish", "French", "German", "Italian", "Portuguese", "Dutch", "Swedish", "Norwegian", "Danish",
        "Finnish", "Polish", "Czech", "Hungarian", "Greek", "Turkish", "Russian", "Ukrainian", "Hebrew", "Arabic",
        "Hindi", "Japanese", "Korean", "Chinese", "Thai", "Vietnamese", "Indonesian",
    ];

    /// <summary>The common languages, plus the stored one when it is not among them, so the picker never hides a saved choice.</summary>
    public static IReadOnlyList<string> WithCurrent(string? current) =>
        string.IsNullOrWhiteSpace(current) || Common.Contains(current.Trim(), StringComparer.OrdinalIgnoreCase)
            ? Common
            : [.. Common, current.Trim()];
}
