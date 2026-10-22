using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Components.Settings;

namespace MediaEngine.Web.Tests.Support;

/// <summary>The Engine's list of library kinds as the Dashboard reads it, built from the kinds that ship with the Library.</summary>
internal static class LibraryKindsTestData
{
    // The Engine now offers YouTube, Other audio and Other reading, but the Libraries screens do not draw them until the Dashboard part
    // of Library setup (WP7c). Until then these tests keep the screens they have always drawn; All() is the full list.
    private static readonly string[] NotDrawnYet = ["youtube", "other_audio", "other_reading"];

    /// <summary>The catalog the Libraries screens build from the built-in kinds.</summary>
    public static LibraryKindCatalog Catalog { get; } = new(BuiltIn());

    /// <summary>The kinds that ship with the Library as the Libraries screens draw them today, in the Engine's order.</summary>
    public static List<LibraryKindDto> BuiltIn(params LibraryKindDto[] extra) => Build(everyKindAvailable: false, extra);

    /// <summary>Every kind the Engine offers, as the Engine lists them (YouTube, Other audio and Other reading included).</summary>
    public static List<LibraryKindDto> All(params LibraryKindDto[] extra) => Build(everyKindAvailable: true, extra);

    private static List<LibraryKindDto> Build(bool everyKindAvailable, LibraryKindDto[] extra) =>
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
            Available = kind.Available && (everyKindAvailable || !NotDrawnYet.Contains(kind.Key)),
            AllowsMultiple = kind.AllowsMultiple,
            MaxLibraries = kind.MaxLibraries,
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
