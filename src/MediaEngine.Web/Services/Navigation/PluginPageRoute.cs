using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>Addresses and icons for the pages plugins add (WP16). The page type decides the icon; the plugin only supplies the label.</summary>
public static class PluginPageRoute
{
    public const string Prefix = "/plugin/";

    public static string Href(string pluginId, string pageKey) =>
        $"{Prefix}{Uri.EscapeDataString(pluginId)}/{Uri.EscapeDataString(pageKey)}";

    public static string Href(PluginMenuEntryDto entry) => Href(entry.PluginId, entry.PageKey);

    public static string Icon(string pageType) => pageType switch
    {
        "grid" => AppMaterialIcons.Outlined.GridView,
        "guide" => AppMaterialIcons.Outlined.Schedule,
        "form" => AppMaterialIcons.Outlined.EditNote,
        "detail" => AppMaterialIcons.Outlined.Info,
        _ => AppMaterialIcons.Outlined.ViewList,
    };

    /// <summary>The menu entries a device shows: a phone lists only the page types that suit it.</summary>
    public static IReadOnlyList<PluginMenuEntryDto> ForDevice(IEnumerable<PluginMenuEntryDto> entries, bool isPhone) =>
        [.. entries.Where(entry => !isPhone || entry.PhoneSupported)];
}
