using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Realtime;

/// <summary>
/// Says whether a file or folder path belongs to a Vault library. Live events about ingestion and folder health name a path, not
/// an item, so the live audience decides by where the path sits (Videos spec D17, D33).
/// </summary>
public interface IVaultPathClassifier
{
    /// <summary>True when <paramref name="path"/> is a Vault library's folder or inside one. An empty path is not.</summary>
    ValueTask<bool> IsVaultPathAsync(string? path, CancellationToken ct = default);
}

/// <summary>
/// Reads the folders of every Vault library from the library settings. A Vault library is one whose id is in the Vault list and whose
/// folders are listed under that same id. When no Vault library exists nothing is read at all.
/// </summary>
public sealed class VaultPathClassifier(IVaultLibraryRepository vaultLibraries, IConfigurationLoader configuration) : IVaultPathClassifier
{
    public async ValueTask<bool> IsVaultPathAsync(string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var ids = await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return false;
        }

        var candidate = Normalize(path);
        foreach (var library in configuration.LoadLibraries().Libraries)
        {
            if (!ids.Contains(library.Id))
            {
                continue;
            }

            foreach (var source in library.Sources)
            {
                var root = Normalize(source.Path);
                if (root.Length > 0 && (candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');
}
