using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Vault;

/// <summary>
/// Whether a Vault section has "Look up details online" switched on (Vault sections D34, D43). It reads the same rule ingestion and the lookup
/// guard read (<see cref="LibraryKindLookupPolicy"/>), so a plugin that sends details out can never be allowed on a Vault item while ingestion
/// is still keeping it local, or the other way round. Off for anything that is not a Vault library, and off when it cannot tell.
/// </summary>
public sealed class VaultSectionOnlineLookup(
    IConfigurationLoader configuration,
    IVaultLibraryRepository vault,
    ILibraryKindSource? kindSource = null) : IVaultSectionOnlineLookup
{
    public async Task<bool> IsOnAsync(string libraryId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(libraryId) || !(await vault.GetIdsAsync(ct).ConfigureAwait(false)).Contains(libraryId))
        {
            return false;
        }

        var library = configuration.LoadLibraries().Libraries
            .FirstOrDefault(candidate => string.Equals(candidate.Id, libraryId, StringComparison.OrdinalIgnoreCase));
        if (library is null)
        {
            return false;
        }

        var kinds = kindSource?.GetKinds() ?? configuration.LoadLibraryKinds().Kinds;
        return !LibraryKindLookupPolicy.BypassesExternalIdentity(library, kinds);
    }
}
