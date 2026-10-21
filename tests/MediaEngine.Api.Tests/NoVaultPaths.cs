using MediaEngine.Api.Realtime;

namespace MediaEngine.Api.Tests;

/// <summary>A path classifier for tests that have no Vault folders: nothing is a Vault path.</summary>
internal sealed class NoVaultPaths : IVaultPathClassifier
{
    public ValueTask<bool> IsVaultPathAsync(string? path, CancellationToken ct = default) => ValueTask.FromResult(false);
}
