using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.Declarative;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Settings;

/// <summary>One kind in the served list and, for a plugin's kind, the plugin that supplies it.</summary>
public sealed record LibraryKindEntry(LibraryKindDefinition Kind, string? PluginId, IReadOnlyList<string> LookupProviders);

/// <summary>
/// The one list of library kinds the Dashboard draws every screen from: the kinds in <c>config/library_kinds.json</c> (the ones
/// that ship with the Library) followed by the kinds of switched-on declarative plugins. Plugin kinds are merged in memory each time
/// the list is asked for, so switching a plugin on or off changes the list at once with nothing written to the config folder
/// (metadata sources, by contrast, are files that the providers read at start-up).
/// </summary>
public sealed class LibraryKindListService : ILibraryKindSource
{
    private readonly IConfigurationLoader _loader;
    private readonly PluginCatalog _plugins;

    public LibraryKindListService(IConfigurationLoader loader, PluginCatalog plugins)
    {
        _loader = loader;
        _plugins = plugins;
        plugins.Changed += () => Changed?.Invoke();
    }

    /// <inheritdoc/>
    public event Action? Changed;

    /// <inheritdoc/>
    public IReadOnlyList<LibraryKindDefinition> GetKinds() => [.. GetAll().Select(entry => entry.Kind)];

    private static readonly string[] AreaOrder =
        [LibraryAreas.Watch, LibraryAreas.Listen, LibraryAreas.Read, LibraryAreas.View, LibraryAreas.Vault];

    /// <summary>Every kind, available or not, ordered by area (Watch, Listen, Read, View, Vault), then by <c>order</c>, then by key.</summary>
    public IReadOnlyList<LibraryKindEntry> GetAll()
    {
        var entries = _loader.LoadLibraryKinds().Kinds
            .Select(kind => new LibraryKindEntry(kind, null, [.. kind.Lookup.Providers]))
            .ToList();
        var keys = new HashSet<string>(entries.Select(entry => entry.Kind.Key), StringComparer.Ordinal);

        foreach (var registration in _plugins.List().Where(registration => registration is { Enabled: true, LoadError: null }))
        {
            var ownSources = registration.Capabilities.OfType<DeclarativeMetadataSourceCapability>().Select(capability => capability.Source.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var capability in registration.Capabilities.OfType<DeclarativeLibraryKindCapability>())
            {
                var kind = capability.LibraryKind.Kind;
                // The catalog already refuses a colliding key with a sentence on the Plugins page; this only keeps the list free of repeats.
                if (!keys.Add(kind.Key))
                {
                    continue;
                }

                // A plugin names its own sources as it wrote them; the Library knows them by the name it gave them when it took them in.
                var providers = kind.Lookup.Providers
                    .Select(name => ownSources.Contains(name) ? PluginMetadataSourceFiles.ProviderNameFor(registration.Manifest.Id, name) : name)
                    .ToList();
                entries.Add(new LibraryKindEntry(kind, registration.Manifest.Id, providers));
            }
        }

        return [.. entries
            .OrderBy(entry => AreaRank(entry.Kind.Area))
            .ThenBy(entry => entry.Kind.Order)
            .ThenBy(entry => entry.Kind.Key, StringComparer.Ordinal)];
    }

    /// <summary>The list as the Dashboard reads it.</summary>
    public IReadOnlyList<LibraryKindDto> GetDtos() => [.. GetAll().Select(ToDto)];

    /// <summary>
    /// The first problem with how libraries link to kinds, checked against the full list (plugins included), or null. A
    /// library must name a kind that exists and whose format is one of the library's media types, and its own lookup settings must
    /// agree with the kind (a plain kind is local only with the YouTube lookup off). Only a save is refused for that; a file already on
    /// disk loads with the kind winning.
    /// </summary>
    public string? ValidateLibraries(LibrariesConfiguration proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        var problems = LibraryKindRules.LibraryProblems(proposed, [.. GetAll().Select(entry => entry.Kind)], checkLookupFields: true);
        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    internal static LibraryKindDto ToDto(LibraryKindEntry entry) => new()
    {
        Key = entry.Kind.Key,
        Area = entry.Kind.Area,
        Label = entry.Kind.Label,
        Icon = entry.Kind.Icon,
        Format = entry.Kind.Format,
        LookupProviders = [.. entry.LookupProviders],
        NamingTemplate = entry.Kind.NamingTemplate,
        Misfit = entry.Kind.Misfit,
        DefaultHandling = entry.Kind.DefaultHandling,
        Tooltip = entry.Kind.Tooltip,
        Order = entry.Kind.Order,
        BuiltIn = entry.Kind.BuiltIn,
        PluginId = entry.PluginId,
        Available = entry.Kind.Available,
        AllowsMultiple = entry.Kind.AllowsMultiple,
        MaxLibraries = entry.Kind.MaxLibraries,
    };

    private static int AreaRank(string area)
    {
        var index = Array.IndexOf(AreaOrder, area);
        return index < 0 ? AreaOrder.Length : index;
    }
}
