namespace MediaEngine.Web.Components.Shared;

internal static class AppUiMaps
{
    public static AppSize ToAppSize(AppControlSize size) => size switch
    {
        AppControlSize.Compact => AppSize.Small,
        AppControlSize.Large => AppSize.Large,
        _ => AppSize.Medium,
    };

    public static AppSize ToAppSize(object? size) => size switch
    {
        AppSize appIconSize => appIconSize,
        AppControlSize appSize => ToAppSize(appSize),
        string text => ToAppSize(ToAppControlSize(text)),
        _ => AppSize.Medium,
    };

    public static AppControlSize ToAppControlSize(object? size) => size switch
    {
        AppControlSize appSize => appSize,
        AppSize.Small => AppControlSize.Compact,
        AppSize.Large => AppControlSize.Large,
        string text => ParseSize(text),
        _ => AppControlSize.Normal,
    };

    private static AppControlSize ParseSize(string value)
    {
        // An object-valued Razor parameter can arrive as its literal enum name.
        // Accept the same named choices as the strongly typed call shapes.
        var name = value[(value.LastIndexOf('.') + 1)..].Trim();
        return name.ToLowerInvariant() switch
        {
            "small" or "compact" => AppControlSize.Compact,
            "large" => AppControlSize.Large,
            _ => AppControlSize.Normal,
        };
    }

    public static AppColor ToAppColor(AppUiTone tone) => tone switch
    {
        AppUiTone.Primary => AppColor.Primary,
        AppUiTone.Info => AppColor.Info,
        AppUiTone.Success => AppColor.Success,
        AppUiTone.Warning => AppColor.Warning,
        AppUiTone.Error => AppColor.Error,
        _ => AppColor.Default,
    };

    public static AppVariant ToAppVariant(AppButtonStyle style) => style switch
    {
        AppButtonStyle.Filled => AppVariant.Filled,
        AppButtonStyle.Text or AppButtonStyle.Ghost => AppVariant.Text,
        _ => AppVariant.Outlined,
    };

    public static string SizeClass(AppControlSize size) =>
        $"app-control--{size.ToString().ToLowerInvariant()}";

    public static string ToneClass(AppUiTone tone) =>
        $"app-tone--{tone.ToString().ToLowerInvariant()}";
}
