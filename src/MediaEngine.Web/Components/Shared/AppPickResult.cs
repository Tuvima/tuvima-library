namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// One result in the shared "pick a result" dialog (<see cref="AppPickResultDialog"/>): a title, a muted second line and a small cover.
/// <paramref name="InUse"/> marks the one the Library already uses (it shows <paramref name="Note"/> and cannot be chosen again).
/// </summary>
public sealed record AppPickResult(
    string Key,
    string Title,
    string? Subtitle = null,
    string? CoverUrl = null,
    string? Note = null,
    bool InUse = false,
    IReadOnlyList<AppPickCheck>? Checks = null,
    IReadOnlyList<AppPickLine>? Lines = null,
    AppPickPreview Preview = AppPickPreview.None);

/// <summary>How a status check reads: plain, a pass (green), a warning (amber) or a failure (red).</summary>
public enum AppPickTone
{
    Neutral,
    Ok,
    Warn,
    Bad,
}

/// <summary>One plain status on a result's row ("Synced", "Fits the song", "12 seconds longer than the song").</summary>
public sealed record AppPickCheck(string Text, AppPickTone Tone = AppPickTone.Neutral);

/// <summary>One real line of the result's words, with the time it comes up when it is timed.</summary>
public sealed record AppPickLine(string? Time, string Text);

/// <summary>Whether a result's words are shown, can be read on request, are being read, or could not be read.</summary>
public enum AppPickPreview
{
    /// <summary>Nothing to read on request (the words are shown, or the result has none).</summary>
    None,

    /// <summary>The words have not been read yet; selecting the row reads them.</summary>
    Available,

    Reading,
    Failed,
}

/// <summary>The shape of the small cover in each result row: portrait for video, square for songs.</summary>
public enum AppPickCoverShape
{
    Portrait,
    Square,
}
