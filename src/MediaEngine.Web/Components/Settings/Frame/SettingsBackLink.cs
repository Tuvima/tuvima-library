namespace MediaEngine.Web.Components.Settings;

/// <summary>The one link back to the parent of a Settings page that sits below another page (one plugin, one person). Settings pages have no breadcrumb.</summary>
public sealed record SettingsBackLink(string Label, string Href);
