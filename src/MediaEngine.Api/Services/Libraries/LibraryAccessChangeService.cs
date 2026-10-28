using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>
/// What a library's access value may change to when the libraries file is saved (Library pages D9, D11). An administrator may switch a
/// library between Standard and Selected profiles; Vault-protected is chosen only by the Vault and is never switched on or off through the
/// libraries file. When the access value changes, or a library is removed, the profiles chosen for it are cleared first, so a library
/// switched to Selected starts with nobody ticked and never inherits an earlier choice.
/// </summary>
public sealed class LibraryAccessChangeService(ILibraryProfileGrantRepository grants)
{
    /// <summary>A plain refusal when the proposed file would turn a library into, or out of, Vault-protected. Null when the change is allowed.</summary>
    public static string? Validate(LibrariesConfiguration current, LibrariesConfiguration proposed, IReadOnlySet<string> vaultIds)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(vaultIds);
        foreach (var library in proposed.Libraries.Where(library => !vaultIds.Contains(library.Id)))
        {
            var before = current.Libraries.FirstOrDefault(candidate => string.Equals(candidate.Id, library.Id, StringComparison.OrdinalIgnoreCase));
            var wasVault = string.Equals(before?.Access, LibraryAccessLevels.Vault, StringComparison.OrdinalIgnoreCase);
            var isVault = string.Equals(library.Access, LibraryAccessLevels.Vault, StringComparison.OrdinalIgnoreCase);
            if (wasVault != isVault)
            {
                return "A library can't be turned into a Vault-protected library or back. Vault-protected libraries are made in the Vault.";
            }
        }

        return null;
    }

    /// <summary>Clears the chosen profiles of every library whose access value changed or that was removed. Call before the new file is saved.</summary>
    public async Task ClearChangedAsync(
        LibrariesConfiguration current, LibrariesConfiguration proposed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);
        foreach (var before in current.Libraries)
        {
            var after = proposed.Libraries.FirstOrDefault(candidate => string.Equals(candidate.Id, before.Id, StringComparison.OrdinalIgnoreCase));
            var changed = after is null || !string.Equals(before.Access, after.Access, StringComparison.OrdinalIgnoreCase);
            if (changed && Guid.TryParse(before.Id, out var libraryId) && libraryId != Guid.Empty)
            {
                await grants.ClearLibraryAsync(libraryId, ct).ConfigureAwait(false);
            }
        }

        // A library that is new in this file starts with nobody chosen, whatever an earlier library with the same id held.
        foreach (var added in proposed.Libraries.Where(library => !current.Libraries.Any(before =>
                     string.Equals(before.Id, library.Id, StringComparison.OrdinalIgnoreCase))))
        {
            if (Guid.TryParse(added.Id, out var libraryId) && libraryId != Guid.Empty)
            {
                await grants.ClearLibraryAsync(libraryId, ct).ConfigureAwait(false);
            }
        }
    }
}
