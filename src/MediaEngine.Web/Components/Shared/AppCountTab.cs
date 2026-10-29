namespace MediaEngine.Web.Components.Shared;

/// <summary>One tab in an <see cref="AppCountTabs"/> strip: a stable key, its words and how many items it holds (none for a tab that holds no list).</summary>
public sealed record AppCountTab(string Key, string Label, int? Count, string? Href = null, string? Icon = null);
