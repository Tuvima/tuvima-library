using MediaEngine.Api.Endpoints;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Vault;

/// <summary>
/// Keeps Vault libraries out of the ordinary library settings (Videos spec D45: the Vault is in no Libraries list). A Vault library's
/// entry stays in <c>libraries.json</c> (ingestion and the path classifier read it there), but the settings routes never show it,
/// never let a settings save change or drop it, and do not count it against the one-library-per-type rule.
/// </summary>
public static class VaultLibrarySettings
{
    /// <summary>
    /// Whether an entry is a Vault library: it has a row in the Vault's own tables, or it carries the vault marker. Either one is enough, so an entry that
    /// says vault without a row (a hand-edited file, a half-finished save) is still kept out of every ordinary list.
    /// </summary>
    public static bool IsVault(LibraryFolderConfig library, IReadOnlySet<string> vaultIds) =>
        vaultIds.Contains(library.Id)
        || library.IsVault;

    /// <summary>A copy of <paramref name="settings"/> without the Vault libraries. The libraries inside are the same objects.</summary>
    public static LibrariesConfiguration WithoutVault(LibrariesConfiguration settings, IReadOnlySet<string> vaultIds)
    {
        return new LibrariesConfiguration
        {
            SchemaVersion = settings.SchemaVersion,
            StorageLocations = settings.StorageLocations,
            ViewStorage = settings.ViewStorage,
            Libraries = settings.Libraries.Where(library => !IsVault(library, vaultIds)).ToList(),
            PersonalLibraryPolicy = settings.PersonalLibraryPolicy,
            FileHandling = settings.FileHandling,
            UnmappedProperties = settings.UnmappedProperties,
        };
    }

    /// <summary>
    /// Drops a removed Vault library's entry (and so its folders) from <c>libraries.json</c>. Saving stops the watcher for those folders.
    /// Does nothing when there is no such entry.
    /// </summary>
    public static async Task RemoveEntryAsync(IConfigurationLoader configuration, string libraryId, CancellationToken ct)
    {
        await LibraryMutationEndpoints.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = configuration.LoadLibraries();
            if (settings.Libraries.RemoveAll(library => string.Equals(library.Id, libraryId, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                configuration.SaveLibraries(settings);
            }
        }
        finally
        {
            LibraryMutationEndpoints.WriteGate.Release();
        }
    }

    /// <summary>
    /// Makes <paramref name="proposed"/> (what a settings screen sent back, which never held the Vault) carry exactly the Vault libraries
    /// <paramref name="current"/> has, unchanged, so saving ordinary settings can neither edit nor drop them.
    /// </summary>
    public static void KeepVault(LibrariesConfiguration current, LibrariesConfiguration proposed, IReadOnlySet<string> vaultIds)
    {
        proposed.Libraries.RemoveAll(library => IsVault(library, vaultIds));
        proposed.Libraries.AddRange(current.Libraries.Where(library => IsVault(library, vaultIds)));
    }
}
