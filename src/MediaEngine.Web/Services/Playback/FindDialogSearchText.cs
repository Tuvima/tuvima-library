namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// What the shared "pick a result" window's search box starts with (review mock-ups 2026-11-18 D7): a video's title and year ("Ferrari 2023"), a song's artist then its
/// title (artist falling back to the album artist). The host also asks <see cref="EditedOrNull"/> whether the person changed it, so a search the
/// person did not touch is the Engine's own lookup and only an edited box sends its text.
/// </summary>
public static class FindDialogSearchText
{
    public static string ForVideo(string? title, string? year) => Join(title, year);

    public static string ForSong(string? title, string? artist, string? albumArtist) =>
        Join(string.IsNullOrWhiteSpace(artist) ? albumArtist : artist, title);

    /// <summary>The box's text when it differs from what it started with (ignoring case and extra spaces); otherwise null.</summary>
    public static string? EditedOrNull(string? prefill, string? typed)
    {
        var text = Normalize(typed);
        return text.Length == 0 || string.Equals(text, Normalize(prefill), StringComparison.OrdinalIgnoreCase) ? null : text;
    }

    private static string Join(string? first, string? second) =>
        string.Join(' ', new[] { first, second }.Select(Normalize).Where(part => part.Length > 0));

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
