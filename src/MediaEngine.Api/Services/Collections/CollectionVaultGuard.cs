using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Services.Collections;

/// <summary>
/// Keeps Vault titles out of collections other people can see. The picker and every collection read already leave the Vault out
/// for anyone without an unlocked Vault session; this is the write-side check, so a title that sits in a Vault library can only be
/// put into a collection that belongs to one profile and is shown to nobody else (Vault sections spec; Collections and lists D19).
/// </summary>
internal static class CollectionVaultGuard
{
    public const string Message = "Vault titles can only be added to a collection that only you see.";

    /// <summary>True when everyone who can see the collection is its one owner.</summary>
    public static bool IsOwnerPrivate(ContainerOwnerKind ownerKind, ContainerAudience audience) =>
        ownerKind == ContainerOwnerKind.Profile && audience == ContainerAudience.Private;

    /// <summary>True when the collection is shared and at least one of the titles is a Vault title (so the add must be refused).</summary>
    public static async Task<bool> RefusesAsync(
        ContainerOwnerKind ownerKind,
        ContainerAudience audience,
        IEnumerable<Guid> workIds,
        IVaultLibraryRepository vault,
        CancellationToken ct)
    {
        if (IsOwnerPrivate(ownerKind, audience))
        {
            return false;
        }

        foreach (var workId in workIds.Distinct())
        {
            if (await vault.IsInVaultAsync(workId, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }
}
