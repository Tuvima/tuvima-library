using MediaEngine.Contracts.Plugins;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Plugins.Sources;

/// <summary>
/// The capability sheets of the metadata sources (Developer tools spec WP20, D-J), worked out from the provider files.
/// Everything the Plugins page, the removal warning, the swap comparison and the first-run question say about what a
/// source supplies comes from here, so there is one source of truth. It only reads configuration; it changes nothing about
/// how items are identified or enriched.
/// </summary>
public sealed class ProviderCapabilityCatalogue(IConfigurationLoader loader, ProviderStartupSnapshot? startup = null)
{
    public const string Keeps = "Everything already in your Library is kept. Nothing is matched again unless you ask.";

    /// <summary>The list for the Plugins page, with whether the Engine has to restart before it matches what is running.</summary>
    public PluginSourcesResponse Overview()
    {
        var sources = Sources();
        var now = sources.Where(s => s.PluginId is not null && s.Enabled).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new PluginSourcesResponse
        {
            Sources = [.. sources],
            RestartNeeded = startup is not null && !now.SetEquals(startup.EnabledPluginSourceNames),
        };
    }

    public IReadOnlyList<PluginSourceDto> Sources() => Load().Select(ToDto).OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>What would have no source if the named source were off. Null when no such source exists.</summary>
    public PluginSourceImpactDto? SwitchOffImpact(string name)
    {
        var all = Load();
        var source = all.FirstOrDefault(s => Same(s.Config.Name, name));
        if (source is null)
        {
            return null;
        }

        var others = all.Where(s => s.Config.Enabled && !Same(s.Config.Name, name)).ToList();
        var impact = new PluginSourceImpactDto { Name = source.Config.Name, DisplayName = source.DisplayName, Keeps = Keeps };
        foreach (var supplies in source.Supplies)
        {
            var covering = others.Where(other => other.Supplies.Contains(supplies, StringComparer.OrdinalIgnoreCase)).Select(other => other.DisplayName).ToList();
            if (covering.Count == 0)
            {
                impact.WouldLose.Add(supplies);
            }
            else
            {
                impact.StillCovered.Add(new PluginSourceCoverageDto { Supplies = supplies, By = covering });
            }
        }

        return impact;
    }

    /// <summary>Two sources side by side. Null when either does not exist.</summary>
    public PluginSourceComparisonDto? Compare(string from, string to)
    {
        var all = Load();
        var a = all.FirstOrDefault(s => Same(s.Config.Name, from));
        var b = all.FirstOrDefault(s => Same(s.Config.Name, to));
        if (a is null || b is null)
        {
            return null;
        }

        var comparer = StringComparer.OrdinalIgnoreCase;
        return new PluginSourceComparisonDto
        {
            From = a.DisplayName,
            To = b.DisplayName,
            Both = a.Supplies.Intersect(b.Supplies, comparer).ToList(),
            OnlyFrom = a.Supplies.Except(b.Supplies, comparer).ToList(),
            OnlyTo = b.Supplies.Except(a.Supplies, comparer).ToList(),
            Keeps = Keeps,
        };
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static PluginSourceDto ToDto(Source source) => new()
    {
        Name = source.Config.Name,
        DisplayName = source.DisplayName,
        Enabled = source.Config.Enabled,
        Origin = source.PluginId is null ? "official" : "plugin",
        PluginId = source.PluginId,
        RequiresKey = source.Config.RequiresApiKey,
        MediaTypes = source.Pairs.Select(pair => ProviderCapabilityPhrases.MediaLabel(pair.Media)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Supplies = source.Supplies,
        SendsDataTo = source.Config.Endpoints.Values
            .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : null)
            .Where(host => host is not null)
            .Select(host => host!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList(),
    };

    private List<Source> Load() => loader.LoadAllProviders()
        .Where(config => config.ProviderId is not null)
        .Select(config => new Source(config))
        .ToList();

    private sealed record Pair(string Media, string Phrase);

    private sealed class Source
    {
        public Source(ProviderConfiguration config)
        {
            Config = config;
            DisplayName = config.DisplayName ?? config.Name;
            PluginId = config.PluginId;
            var media = config.CanHandle?.MediaTypes is { Count: > 0 } listed ? listed : [.. ProviderCapabilityPhrases.AllMediaTypes];
            Pairs = media
                .SelectMany(mediaType => config.ProviderCapabilities.Select(capability => new Pair(mediaType, ProviderCapabilityPhrases.Phrase(mediaType, capability))))
                .DistinctBy(pair => pair.Phrase, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Supplies = Pairs.Select(pair => pair.Phrase).ToList();
        }

        public ProviderConfiguration Config { get; }

        public string DisplayName { get; }

        public string? PluginId { get; }

        public List<Pair> Pairs { get; }

        public List<string> Supplies { get; }
    }
}
