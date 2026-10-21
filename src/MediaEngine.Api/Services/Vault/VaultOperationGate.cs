using MediaEngine.Api.Realtime;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.Vault;

/// <summary>
/// Keeps the Vault out of the administrator's operations screens (Videos spec D17, D33). A durable operation is about a file by its
/// id or its path; one about a file in a Vault library does not exist for these screens, whoever asks, until a viewer-aware
/// version is built. A single operation that is hidden answers exactly like an id that does not exist.
/// </summary>
public sealed class VaultOperationGate(IVaultLibraryRepository vaultLibraries, IVaultPathClassifier vaultPaths)
{
    /// <summary>True when the operation is about a file in a Vault library, by its id or by where its path sits.</summary>
    public async ValueTask<bool> HidesAsync(MediaOperation operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.EntityId is { } entityId && await vaultLibraries.IsInVaultAsync(entityId, ct).ConfigureAwait(false))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(operation.SourcePath)
            && await vaultPaths.IsVaultPathAsync(operation.SourcePath, ct).ConfigureAwait(false);
    }

    /// <summary>The operations that are not about a Vault file, in their order.</summary>
    public async ValueTask<IReadOnlyList<MediaOperation>> VisibleAsync(IEnumerable<MediaOperation> operations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var visible = new List<MediaOperation>();
        foreach (var operation in operations)
        {
            if (!await HidesAsync(operation, ct).ConfigureAwait(false))
            {
                visible.Add(operation);
            }
        }

        return visible;
    }
}
