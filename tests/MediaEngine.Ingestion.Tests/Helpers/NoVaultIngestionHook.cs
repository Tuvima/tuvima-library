using MediaEngine.Domain.Contracts;

namespace MediaEngine.Ingestion.Tests;

/// <summary>For tests that have no Vault: no library or file is ever a Vault one, and nothing is looked up.</summary>
internal sealed class NoVaultIngestionHook : IVaultIngestionHook
{
    public Task<bool> IsVaultLibraryAsync(string? libraryId, CancellationToken ct = default) => Task.FromResult(false);

    public Task<bool> IsVaultAssetAsync(Guid assetId, CancellationToken ct = default) => Task.FromResult(false);

    public Task OnFileRegisteredAsync(VaultRegisteredFile file, CancellationToken ct = default) => Task.CompletedTask;
}
