using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The four areas whose libraries an administrator can tick for a household or a person (library access D1, D2): their name, icon and colour,
/// shared by the household ceiling and the person's access list so both read the same. The Vault is never one of them.
/// </summary>
internal static class AccessAreas
{
    public sealed record Area(string Key, string Label, string Icon, AppColor Color, string Description);

    public static readonly IReadOnlyList<Area> All =
    [
        new("watch", "Watch", AppMaterialIcons.Outlined.Tv, AppColor.Primary, "Movies and TV shows"),
        new("listen", "Listen", AppMaterialIcons.Outlined.Headphones, AppColor.Info, "Music and audiobooks"),
        new("read", "Read", AppMaterialIcons.Outlined.MenuBook, AppColor.Warning, "Books and comics"),
        new("view", "View", AppMaterialIcons.Outlined.Visibility, AppColor.Success, "Shared content and the active profile's Personal Space"),
    ];

    public static Area? Find(string? key) =>
        All.FirstOrDefault(area => string.Equals(area.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));
}
