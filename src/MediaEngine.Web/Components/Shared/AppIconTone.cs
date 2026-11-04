namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// Icon tone by meaning (Look pass 4.7). A page asks for a tone, never a color; the color for each tone lives in
/// <c>tuvima.tokens.css</c>. Success means healthy or done and nothing else; Danger means delete or broken only.
/// </summary>
public enum AppIconTone
{
    Neutral,
    Accent,
    Info,
    Success,
    Warning,
    Danger,
    Watch,
    Listen,
    Read,
    View,
    Vault,
}

public static class AppIconToneMap
{
    public static string Color(AppIconTone tone) => tone switch
    {
        AppIconTone.Neutral => "var(--tl-tone-neutral)",
        AppIconTone.Accent => "var(--tl-tone-accent)",
        AppIconTone.Info => "var(--tl-tone-info)",
        AppIconTone.Success => "var(--tl-tone-success)",
        AppIconTone.Warning => "var(--tl-tone-warning)",
        AppIconTone.Danger => "var(--tl-tone-danger)",
        AppIconTone.Watch => "var(--tl-area-watch)",
        AppIconTone.Listen => "var(--tl-area-listen)",
        AppIconTone.Read => "var(--tl-area-read)",
        AppIconTone.View => "var(--tl-area-view)",
        AppIconTone.Vault => "var(--tl-area-vault)",
        _ => "var(--tl-tone-neutral)",
    };
}
