namespace MediaEngine.Web.Components.Settings;

/// <summary>One step of the breadcrumb a Settings page shows above its title. A step without a link is the current location.</summary>
public sealed record SettingsBreadcrumb(string Label, string? Href);
