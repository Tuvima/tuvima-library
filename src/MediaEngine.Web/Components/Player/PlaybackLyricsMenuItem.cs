using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Components.Player;

/// <summary>One row of the lyrics footer's "..." menu. <paramref name="Key"/> names the action; a divider separates the groups.</summary>
public sealed record PlaybackLyricsMenuItem(string Key, string Label, PlaybackUtilityGlyphKind Glyph, bool DividerBefore = false);

/// <summary>The actions the lyrics footer's menu can offer (unified player v2 D17), as the keys of <see cref="PlaybackLyricsMenuItem"/>.</summary>
public static class PlaybackLyricsMenuActions
{
    public const string FindAnother = "find-another";
    public const string AddFile = "add-file";
    public const string About = "about";
    public const string Edit = "edit";
    public const string ThisOneIsWrong = "wrong";
}
