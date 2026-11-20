namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// One result in the shared "pick a result" dialog (<see cref="AppPickResultDialog"/>): a title, a muted second line and a small cover.
/// <paramref name="InUse"/> marks the one the Library already uses (it shows <paramref name="Note"/> and cannot be chosen again).
/// </summary>
public sealed record AppPickResult(string Key, string Title, string? Subtitle = null, string? CoverUrl = null, string? Note = null, bool InUse = false);

/// <summary>The shape of the small cover in each result row: portrait for video, square for songs.</summary>
public enum AppPickCoverShape
{
    Portrait,
    Square,
}
