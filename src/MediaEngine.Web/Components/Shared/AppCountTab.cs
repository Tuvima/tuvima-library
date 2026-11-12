namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// One tab in an <see cref="AppCountTabs"/> strip: a stable key, its words, how many items it holds (none for a tab that holds no list) and its
/// outline icon (every tab has one, Look v3.1 D1; take it from <c>AppMaterialIcons.Outlined</c>).
/// An <paramref name="Advanced"/> tab is drawn last, at the right end, small and amber with no pill; the strip puts it there whatever its position in the list.
/// </summary>
public sealed record AppCountTab(string Key, string Label, int? Count, string Icon, string? Href = null, bool Advanced = false);
