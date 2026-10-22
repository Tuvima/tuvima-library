using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Components.Settings;

namespace MediaEngine.Web.Tests.Support;

/// <summary>The Engine's list of library kinds as the Dashboard reads it, built from the kinds that ship with the Library.</summary>
internal static class LibraryKindsTestData
{
    /// <summary>The catalog the Libraries screens build from the built-in kinds.</summary>
    public static LibraryKindCatalog Catalog { get; } = new(BuiltIn());

    /// <summary>The kinds that ship with the Library (the ones not offered yet are marked unavailable), in the Engine's order.</summary>
    public static List<LibraryKindDto> BuiltIn(params LibraryKindDto[] extra) =>
    [
        .. LibraryKindsConfiguration.BuiltIn().Select(kind => new LibraryKindDto
        {
            Key = kind.Key,
            Area = kind.Area,
            Label = kind.Label,
            Icon = kind.Icon,
            Format = kind.Format,
            LookupProviders = [.. kind.Lookup.Providers],
            NamingTemplate = kind.NamingTemplate,
            Misfit = kind.Misfit,
            DefaultHandling = kind.DefaultHandling,
            Tooltip = kind.Tooltip,
            Order = kind.Order,
            BuiltIn = kind.BuiltIn,
            Available = kind.Available,
        }),
        .. extra,
    ];

    /// <summary>A kind a plugin could add, for example Podcasts under Listen.</summary>
    public static LibraryKindDto Podcasts() => new()
    {
        Key = "podcasts",
        Area = "listen",
        Label = "Podcasts",
        Icon = "Podcasts",
        Format = "Music",
        NamingTemplate = "default",
        Misfit = "keep",
        DefaultHandling = "read_only",
        Tooltip = "Episodes of shows you follow, one folder per show.",
        Order = 100,
        PluginId = "example.podcasts",
        Available = true,
    };
}
