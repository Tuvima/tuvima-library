using MediaEngine.Web.Services.Vault;

namespace MediaEngine.Web.Tests.Support;

/// <summary>Keeps Vault viewing options in memory so tests never touch browser storage.</summary>
internal sealed class InMemoryVaultPreferenceStore : IVaultPreferenceStore
{
    private readonly Dictionary<Guid, VaultViewPreferences> _saved = [];

    public ValueTask<VaultViewPreferences> LoadAsync(Guid profileId) =>
        ValueTask.FromResult(_saved.TryGetValue(profileId, out var saved) ? saved : VaultViewPreferences.Default);

    public ValueTask SaveAsync(Guid profileId, VaultViewPreferences preferences)
    {
        _saved[profileId] = preferences;
        return ValueTask.CompletedTask;
    }
}
