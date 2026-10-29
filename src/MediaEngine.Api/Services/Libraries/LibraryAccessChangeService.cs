using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>
/// What a library may change to when the libraries file is saved. Vault-protected is chosen only by the Vault and is never switched on or
/// off through the libraries file. Who sees an ordinary library is no longer a property of the library (library access D3): it is set on
/// the household and on the person, so there is nothing else to check or clear here.
/// </summary>
public static class LibraryAccessChangeService
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
            if ((before?.IsVault ?? false) != library.IsVault)
            {
                return "A library can't be turned into a Vault-protected library or back. Vault-protected libraries are made in the Vault.";
            }
        }

        return null;
    }
}
