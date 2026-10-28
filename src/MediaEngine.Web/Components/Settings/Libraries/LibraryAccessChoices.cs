using MediaEngine.Domain.Configuration;

namespace MediaEngine.Web.Components.Settings;

/// <summary>The access values a library can be created with, as the Create library dialog names them (Razor components never import the configuration namespace).</summary>
internal static class LibraryAccessChoices
{
    public const string Standard = LibraryAccessLevels.Standard;
    public const string Selected = LibraryAccessLevels.Selected;
    public const string Vault = LibraryAccessLevels.Vault;
}
