using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Per-library limits for plugins and metadata sources (Plugin architecture spec D6). By default an item works on every library it can
/// work on; an administrator can choose libraries, and from then on it works only on those. Vault libraries cannot be chosen here:
/// whether an item may work on the Vault is the Vault rule's call (D4), never a limit's.
/// </summary>
public sealed class PluginLibraryLimitService(
    IPluginLibraryLimitRepository repository,
    IConfigurationLoader configuration,
    IVaultLibraryRepository vault)
{
    /// <summary>True when the item may work on something in <paramref name="libraryId"/>. No limit set means yes; a limit with an unknown library means no.</summary>
    public async Task<bool> IsAllowedAsync(string pluginId, string? libraryId, CancellationToken ct = default)
    {
        var chosen = await repository.GetAsync(pluginId, ct).ConfigureAwait(false);
        return chosen.Count == 0
            || (!string.IsNullOrWhiteSpace(libraryId) && chosen.Contains(libraryId, StringComparer.Ordinal));
    }

    public async Task<PluginLibraryLimitResponse> GetAsync(string pluginId, CancellationToken ct = default)
    {
        var chosen = await repository.GetAsync(pluginId, ct).ConfigureAwait(false);
        var vaultIds = await vault.GetIdsAsync(ct).ConfigureAwait(false);
        return new PluginLibraryLimitResponse
        {
            PluginId = pluginId,
            Limited = chosen.Count > 0,
            Libraries = configuration.LoadLibraries().Libraries
                .Where(library => !vaultIds.Contains(library.Id))
                .Select(library => new PluginLibraryChoiceDto
                {
                    Id = library.Id,
                    Name = library.Name,
                    Selected = chosen.Contains(library.Id, StringComparer.Ordinal),
                })
                .ToList(),
        };
    }

    /// <summary>Saves the choice. Returns a plain sentence when it is refused (an unknown library, or a Vault library), otherwise null.</summary>
    public async Task<string?> SetAsync(string pluginId, IReadOnlyCollection<string> libraryIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(libraryIds);
        var ids = libraryIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        var known = configuration.LoadLibraries().Libraries.Select(library => library.Id).ToHashSet(StringComparer.Ordinal);
        var vaultIds = await vault.GetIdsAsync(ct).ConfigureAwait(false);
        if (ids.Any(id => vaultIds.Contains(id) || !known.Contains(id)))
        {
            // The same answer for both, so the choice never confirms that a Vault library exists.
            return "One of those libraries is not available to choose.";
        }

        await repository.ReplaceAsync(pluginId, ids, ct).ConfigureAwait(false);
        return null;
    }

    public Task ForgetAsync(string pluginId, CancellationToken ct = default) => repository.DeleteForPluginAsync(pluginId, ct);
}
