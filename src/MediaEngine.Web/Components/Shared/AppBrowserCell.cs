namespace MediaEngine.Web.Components.Shared;

/// <summary>One plain text cell of an <see cref="AppBrowserItemRow"/>; Quiet draws it in the secondary colour (an empty group reads "Ungrouped").</summary>
public readonly record struct AppBrowserCell(string Text, bool Quiet = false);
