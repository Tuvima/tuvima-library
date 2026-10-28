namespace MediaEngine.Storage.Contracts;

/// <summary>One credential a profile has linked for a plugin. Never carries the value.</summary>
public sealed record PluginProfileConnectionRow(string CredentialName, DateTimeOffset LinkedAt);

/// <summary>
/// The per-profile connections of plugins (Plugin architecture spec C13). Values arrive and leave already protected; this store never
/// sees a plain value.
/// </summary>
public interface IPluginProfileConnectionRepository
{
    /// <summary>The credentials this profile has linked for the plugin (names and times only).</summary>
    Task<IReadOnlyList<PluginProfileConnectionRow>> ListAsync(string pluginId, Guid profileId, CancellationToken ct = default);

    /// <summary>The protected value, or null when this profile has not linked the credential.</summary>
    Task<string?> GetCiphertextAsync(string pluginId, Guid profileId, string credentialName, CancellationToken ct = default);

    Task UpsertAsync(string pluginId, Guid profileId, string credentialName, string ciphertext, DateTimeOffset linkedAt, CancellationToken ct = default);

    /// <summary>True when something was removed.</summary>
    Task<bool> DeleteAsync(string pluginId, Guid profileId, string credentialName, CancellationToken ct = default);

    /// <summary>Profiles that have at least one linked credential for the plugin, with no values.</summary>
    Task<IReadOnlyList<Guid>> ListProfilesAsync(string pluginId, CancellationToken ct = default);
}
