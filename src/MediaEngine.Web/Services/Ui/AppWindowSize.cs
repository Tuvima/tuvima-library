namespace MediaEngine.Web.Services.Ui;

/// <summary>The Window size ladder (Look v3 D14): S 480 px, M 640 px, L 900 px, XL 1200 px (by name only). The widths are the <c>--tl-window-*</c> tokens.</summary>
public enum AppWindowSize
{
    /// <summary>480 px: a confirmation or a short form.</summary>
    Small = 0,

    /// <summary>640 px: a form or a picker.</summary>
    Medium = 1,

    /// <summary>900 px: a workspace such as the artwork picker or the collection editor.</summary>
    Large = 2,

    /// <summary>1200 px: the shared file browser, which needs room for its list, table and filters. Only asked for by name, never reached by a width hint.</summary>
    ExtraLarge = 3,
}

/// <summary>Snaps a dialog's older width hints (<see cref="AppMaxWidth"/> and the shell's size name) onto the three-step Window ladder.</summary>
public static class AppWindowSizing
{
    /// <summary>The window size for these options: an explicit <see cref="AppDialogOptions.Size"/> wins, otherwise the narrower of the two hints, rounded up to the next step.</summary>
    public static AppWindowSize Resolve(AppDialogOptions? options, string? shellSize = null)
    {
        if (options?.Size is { } explicitSize)
        {
            return explicitSize;
        }
        var width = Math.Min(CapFor(options?.MaxWidth), ShellWidth(shellSize));
        return width <= 480 ? AppWindowSize.Small : width <= 640 ? AppWindowSize.Medium : AppWindowSize.Large;
    }

    /// <summary>The CSS class suffix for a size: <c>s</c>, <c>m</c> or <c>l</c>.</summary>
    public static string Suffix(AppWindowSize size) => size switch
    {
        AppWindowSize.Small => "s",
        AppWindowSize.Medium => "m",
        AppWindowSize.ExtraLarge => "xl",
        _ => "l",
    };

    private static int CapFor(AppMaxWidth? width) => width switch
    {
        AppMaxWidth.ExtraSmall => 444,
        null or AppMaxWidth.Small => 600,
        AppMaxWidth.Medium => 960,
        _ => int.MaxValue,
    };

    private static int ShellWidth(string? shellSize) => shellSize?.Trim().ToLowerInvariant() switch
    {
        "small" => 420,
        "medium" => 640,
        null or "" => int.MaxValue,
        _ => int.MaxValue,
    };
}
