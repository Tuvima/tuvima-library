using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Display;

/// <summary>One item a plugin suggested for the Home hero, ready to be matched against the items the Home hero may show.</summary>
public sealed record HeroPluginNomination(Guid ItemId, string PluginId, string PluginName, string Reason, int Strength, bool FromLocalAi);

/// <summary>
/// Where the Home hero learns what plugins suggested (Home page redesign spec WP8, Plugin architecture C21). A suggestion only names an
/// item and a reason; the Library checks it and draws it with its own components. The hero never waits on a plugin: this reads what is
/// already stored.
/// </summary>
public interface IHeroPluginSlides
{
    /// <summary>The suggestions this person may see: from plugins that are on, not switched off by this profile, and allowed for the item.</summary>
    Task<IReadOnlyList<HeroPluginNomination>> NominationsAsync(Guid? profileId, PluginAudience? requester, CancellationToken ct);

    /// <summary>Every plugin that can suggest slides, with this profile's switch for each, for the Home settings page.</summary>
    Task<IReadOnlyList<(string PluginId, string Name, bool Enabled)>> PluginsAsync(Guid profileId, CancellationToken ct);
}

public sealed class HeroPluginSlides(
    PluginDisplayService display,
    PluginCatalog catalog,
    IPluginAccessGate gate,
    IProfileHeroSettingsRepository settings) : IHeroPluginSlides
{
    public const int MaximumNominations = 20;

    public async Task<IReadOnlyList<HeroPluginNomination>> NominationsAsync(Guid? profileId, PluginAudience? requester, CancellationToken ct)
    {
        var offered = await display.ListSlotAsync(PluginDisplaySlot.HomeHeroCandidate, MaximumNominations, ct).ConfigureAwait(false);
        if (offered.Count == 0)
        {
            return [];
        }

        var switchedOff = profileId is { } profile
            ? await settings.GetSwitchedOffPluginsAsync(profile, ct).ConfigureAwait(false)
            : new HashSet<string>();
        var result = new List<HeroPluginNomination>(offered.Count);
        foreach (var item in offered)
        {
            if (switchedOff.Contains(item.PluginId) || !Guid.TryParse(item.SubjectId, out var itemId))
            {
                continue;
            }

            var access = await gate.CheckAsync(new PluginAccessQuery(item.PluginId, requester, null, itemId), ct).ConfigureAwait(false);
            if (access != PluginAccessOutcome.Allowed)
            {
                continue;
            }

            result.Add(new HeroPluginNomination(itemId, item.PluginId, item.PluginName, item.Text, item.Strength, item.FromLocalAi));
        }

        return result;
    }

    public async Task<IReadOnlyList<(string PluginId, string Name, bool Enabled)>> PluginsAsync(Guid profileId, CancellationToken ct)
    {
        var switchedOff = await settings.GetSwitchedOffPluginsAsync(profileId, ct).ConfigureAwait(false);
        return catalog.List()
            .Where(plugin => plugin.Enabled && plugin.LoadError is null
                && plugin.Manifest.Permissions.Contains(PluginPermissionIds.DisplaySlots, StringComparer.Ordinal))
            .OrderBy(plugin => plugin.Manifest.Name, StringComparer.OrdinalIgnoreCase)
            .Select(plugin => (plugin.Manifest.Id, plugin.Manifest.Name, !switchedOff.Contains(plugin.Manifest.Id)))
            .ToList();
    }
}
