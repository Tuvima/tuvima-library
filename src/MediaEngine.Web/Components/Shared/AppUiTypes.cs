namespace MediaEngine.Web.Components.Shared;

public enum AppSkeletonShape
{
    Rectangle,
    Text,
    Circle,
}

public enum AppMediaCardVariant
{
    Portrait,
    Square,
    Landscape,
    Wide,
    Compact,
    List,
}

public enum AppCardHoverBehavior
{
    None,
    Quiet,
    Standard,
    Media,
}

public enum AppPageStateKind
{
    Loading,
    Empty,
    Error,
    Unavailable,
    Redirecting,
}

public enum AppSurfaceDensity
{
    Compact,
    Normal,
    Comfortable,
}

/// <summary>
/// The one icon size ladder (Look v3 D13). The icon parts take only these names and nothing else sizes an icon:
/// 16 inline and chips, 20 in 36 px controls, 24 in 44 and 48 px controls, menus, rail rows and list rows,
/// 32 in dialog and card icon tiles, 48 in choice tiles, 64 in empty states and the hero of a setup step.
/// </summary>
public enum AppIconSize
{
    Inline16,
    Control20,
    Row24,
    Tile32,
    Choice48,
    Hero64,
}

public static class AppIconSizes
{
    /// <summary>The token behind a ladder step (tuvima.tokens.css).</summary>
    public static string Token(AppIconSize size) => size switch
    {
        AppIconSize.Inline16 => "var(--tl-icon-size-xs)",
        AppIconSize.Control20 => "var(--tl-icon-size-sm)",
        AppIconSize.Tile32 => "var(--tl-icon-size-lg)",
        AppIconSize.Choice48 => "var(--tl-icon-size-xl)",
        AppIconSize.Hero64 => "var(--tl-icon-size-2xl)",
        _ => "var(--tl-icon-size-md)",
    };

    /// <summary>The icon inside a button: 20 in the 36 px size, 24 in 44 and 48.</summary>
    public static AppIconSize ForControl(AppControlSize size) => size == AppControlSize.Compact ? AppIconSize.Control20 : AppIconSize.Row24;
}

public enum AppControlSize
{
    Compact,
    Normal,
    Large,
}

public enum AppUiTone
{
    Neutral,
    Primary,
    Info,
    Success,
    Warning,
    Error,
}

public enum AppButtonStyle
{
    Filled,
    Outlined,
    Text,
}

public enum AppEmphasis
{
    Low,
    Medium,
    High,
}

public enum AppHorizontalAlignment
{
    Start,
    Center,
    End,
    Stretch,
}

public enum AppSurfaceVariant
{
    Default,
    Raised,
    Selected,
    Warning,
    Danger,
}

public sealed record AppSelectOption(
    string Value,
    string Label,
    string? Icon = null,
    bool Disabled = false,
    bool SeparatorBefore = false,
    string? Title = null,
    string? AccessibleLabel = null);
