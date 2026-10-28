using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>Whether one plugin a plugin needs is ready to be used.</summary>
public enum PluginDependencyState
{
    /// <summary>Switched on, loaded and at a version that fits.</summary>
    Ready,

    /// <summary>Installed but switched off.</summary>
    Off,

    /// <summary>Not installed.</summary>
    Missing,

    /// <summary>Installed, but its version is outside the range the plugin asked for.</summary>
    WrongVersion,

    /// <summary>Installed but could not load (it has its own problem to fix first).</summary>
    Broken,
}

/// <summary>One plugin another plugin needs, and how it stands right now.</summary>
public sealed record PluginDependency(string Id, string Name, bool Optional, string? VersionRange, PluginDependencyState State)
{
    /// <summary>True when a required dependency is not ready, so the plugin must wait instead of showing Working.</summary>
    public bool Blocks => !Optional && State != PluginDependencyState.Ready;
}

/// <summary>
/// The dependency map between plugins (Plugin architecture spec C22). A plugin lists the other plugins it needs in its declaration;
/// this works out the order to switch them on, who is waiting for whom, who depends on a plugin that is about to go, and which
/// plugins are tangled in a cycle (those are refused when the catalogue loads). Needs of other kinds (Local AI abilities, tools,
/// sources, host services) are checked where those are known; this covers the plugin-to-plugin part.
/// </summary>
public static class PluginDependencyGraph
{
    /// <summary>What a plugin needs from other plugins, with each one's state.</summary>
    public static IReadOnlyList<PluginDependency> Needs(PluginRegistration plugin, IReadOnlyList<PluginRegistration> all)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(all);
        return PluginNeeds(plugin)
            .Select(requirement =>
            {
                var found = all.FirstOrDefault(other => string.Equals(other.Manifest.Id, requirement.Id, StringComparison.OrdinalIgnoreCase));
                var state = found is null ? PluginDependencyState.Missing
                    : found.LoadError is not null ? PluginDependencyState.Broken
                    : !VersionFits(requirement.VersionRange, found.Manifest.Version) ? PluginDependencyState.WrongVersion
                    : found.Enabled ? PluginDependencyState.Ready
                    : PluginDependencyState.Off;
                return new PluginDependency(requirement.Id, found?.Manifest.Name ?? requirement.Id, requirement.Optional, requirement.VersionRange, state);
            })
            .ToList();
    }

    /// <summary>The plain sentence for a plugin that cannot work yet ("Waiting for Skip markers"), or null when nothing it requires is holding it up.</summary>
    public static string? WaitingFor(PluginRegistration plugin, IReadOnlyList<PluginRegistration> all)
    {
        var blocking = Needs(plugin, all).Where(need => need.Blocks).ToList();
        return blocking.Count == 0 ? null : "Waiting for " + string.Join(" and ", blocking.Select(need => need.Name));
    }

    /// <summary>The switched-on plugins that need <paramref name="pluginId"/>, directly or through another one, so removing it can offer to switch them off too. Optional needs do not count.</summary>
    public static IReadOnlyList<PluginRegistration> Dependants(string pluginId, IReadOnlyList<PluginRegistration> all)
    {
        var result = new List<PluginRegistration>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pluginId };
        var queue = new Queue<string>([pluginId]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var other in all.Where(plugin => plugin.Enabled && !seen.Contains(plugin.Manifest.Id)))
            {
                if (PluginNeeds(other).Any(need => !need.Optional && string.Equals(need.Id, current, StringComparison.OrdinalIgnoreCase)))
                {
                    seen.Add(other.Manifest.Id);
                    result.Add(other);
                    queue.Enqueue(other.Manifest.Id);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Every plugin to switch on so <paramref name="pluginId"/> can work, dependencies first and the plugin itself last. Plugins already
    /// on are left out. Required needs only; optional ones are never switched on for the person.
    /// </summary>
    /// <param name="blocked">Needs that cannot be switched on (not installed, broken or the wrong version), as plain sentences.</param>
    public static IReadOnlyList<PluginRegistration> EnableOrder(string pluginId, IReadOnlyList<PluginRegistration> all, out IReadOnlyList<string> blocked)
    {
        var order = new List<PluginRegistration>();
        var problems = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(pluginId);
        blocked = problems;
        return order;

        void Visit(string id)
        {
            var plugin = all.FirstOrDefault(other => string.Equals(other.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));
            if (plugin is null || !visiting.Add(plugin.Manifest.Id))
            {
                return;
            }

            foreach (var need in Needs(plugin, all).Where(need => !need.Optional))
            {
                switch (need.State)
                {
                    case PluginDependencyState.Off:
                        Visit(need.Id);
                        break;
                    case PluginDependencyState.Missing:
                        problems.Add($"{plugin.Manifest.Name} needs {need.Name}, which is not installed.");
                        break;
                    case PluginDependencyState.WrongVersion:
                        problems.Add($"{plugin.Manifest.Name} needs {need.Name} at a different version.");
                        break;
                    case PluginDependencyState.Broken:
                        problems.Add($"{plugin.Manifest.Name} needs {need.Name}, which could not load.");
                        break;
                }
            }

            if (!plugin.Enabled)
            {
                order.Add(plugin);
            }
        }
    }

    /// <summary>The plugins in an order where every plugin comes after the plugins it needs, so none runs before what it needs. Plugins in a cycle keep their place.</summary>
    public static IReadOnlyList<PluginRegistration> StartOrder(IReadOnlyList<PluginRegistration> all)
    {
        var ordered = new List<PluginRegistration>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in all)
        {
            Place(plugin);
        }

        return ordered;

        void Place(PluginRegistration plugin)
        {
            if (placed.Contains(plugin.Manifest.Id) || !visiting.Add(plugin.Manifest.Id))
            {
                return;
            }

            foreach (var need in PluginNeeds(plugin))
            {
                var found = all.FirstOrDefault(other => string.Equals(other.Manifest.Id, need.Id, StringComparison.OrdinalIgnoreCase));
                if (found is not null)
                {
                    Place(found);
                }
            }

            if (placed.Add(plugin.Manifest.Id))
            {
                ordered.Add(plugin);
            }
        }
    }

    /// <summary>The plugins that sit in a cycle (A needs B needs A), each with a plain sentence naming the plugins it is tangled with. The catalogue refuses them.</summary>
    public static IReadOnlyDictionary<string, string> FindCycles(IReadOnlyList<PluginRegistration> all)
    {
        var tangled = all.Where(plugin => Reaches(plugin.Manifest.Id, plugin.Manifest.Id)).ToList();
        return tangled.ToDictionary(
            plugin => plugin.Manifest.Id,
            plugin => "Plugins that need each other in a circle cannot be switched on: "
                + string.Join(", ", tangled.Where(other => !ReferenceEquals(other, plugin)).Select(other => other.Manifest.Name).DefaultIfEmpty(plugin.Manifest.Name))
                + ".",
            StringComparer.OrdinalIgnoreCase);

        bool Reaches(string from, string target)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>([from]);
            while (queue.Count > 0)
            {
                var currentId = queue.Dequeue();
                var plugin = all.FirstOrDefault(other => string.Equals(other.Manifest.Id, currentId, StringComparison.OrdinalIgnoreCase));
                if (plugin is null)
                {
                    continue;
                }

                foreach (var need in PluginNeeds(plugin))
                {
                    if (string.Equals(need.Id, target, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (seen.Add(need.Id))
                    {
                        queue.Enqueue(need.Id);
                    }
                }
            }

            return false;
        }
    }

    /// <summary>True when <paramref name="version"/> fits <paramref name="range"/>: "1.0.0" means that version or newer, "1.0.0-2.0.0" an inclusive range, absent means any.</summary>
    public static bool VersionFits(string? range, string version)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return true;
        }

        if (!Version.TryParse(Core(version), out var actual))
        {
            return false;
        }

        var parts = range.Split('-', 2, StringSplitOptions.TrimEntries);
        if (!Version.TryParse(Core(parts[0]), out var low))
        {
            return false;
        }

        if (parts.Length == 1)
        {
            return actual >= low;
        }

        return Version.TryParse(Core(parts[1]), out var high) && actual >= low && actual <= high;

        // A pre-release or build suffix ("1.2.0-beta+7") is not part of the number compared.
        static string Core(string text) => text.Split('+', 2)[0].Split('-', 2)[0];
    }

    private static IEnumerable<PluginRequirement> PluginNeeds(PluginRegistration plugin) =>
        plugin.Manifest.Declaration?.Requires.Where(requirement => string.Equals(requirement.Kind, "plugin", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(requirement.Id))
        ?? [];
}
