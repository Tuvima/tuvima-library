using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Per-profile connections (Plugin architecture spec C13). A plugin lists some of its credentials as <c>personal</c>; each profile
/// links its own, encrypted with this machine's Data Protection key. A profile can only ever reach its own: every call takes the
/// signed-in profile, there is no way to name another one, and administrators have no way to read a value.
/// </summary>
public sealed class PluginProfileConnectionService(
    PluginCatalog catalog,
    IPluginProfileConnectionRepository repository,
    IPluginOutboxRepository outbox,
    ISecretStore protector,
    TimeProvider clock)
{
    public const int MaximumValueLength = 8192;

    /// <summary>The credentials the plugin asks each profile to link. Empty when none, or when the plugin is not installed.</summary>
    public IReadOnlyList<PluginSecretDeclaration> PersonalCredentials(string pluginId)
    {
        var registration = catalog.Get(pluginId);
        if (registration is null)
        {
            return [];
        }

        return registration.Manifest.Secrets
            .Where(secret => PluginDeclarationRules.IsPersonalCredential(registration.Manifest.Declaration, secret.Name))
            .ToList();
    }

    /// <summary>True when the credential is declared personal, so a household-wide value must never be set for it.</summary>
    public bool IsPersonalCredential(string pluginId, string name) =>
        PersonalCredentials(pluginId).Any(secret => string.Equals(secret.Name, name, StringComparison.OrdinalIgnoreCase));

    public async Task<PluginConnectionsResponse?> StatusAsync(string pluginId, Guid profileId, CancellationToken ct)
    {
        var registration = catalog.Get(pluginId);
        if (registration is null)
        {
            return null;
        }

        var linked = (await repository.ListAsync(registration.Manifest.Id, profileId, ct).ConfigureAwait(false))
            .ToDictionary(row => row.CredentialName, StringComparer.OrdinalIgnoreCase);
        return new PluginConnectionsResponse
        {
            PluginId = registration.Manifest.Id,
            PluginName = registration.Manifest.Name,
            Consent = $"Linking lets {registration.Manifest.Name} act for your profile only. Nobody else in the household can see or use it, and you can unlink it at any time.",
            Connections = PersonalCredentials(pluginId).Select(secret => new PluginConnectionDto
            {
                Name = secret.Name,
                Label = secret.Label,
                Description = secret.Description,
                Linked = linked.ContainsKey(secret.Name),
                LinkedAt = linked.TryGetValue(secret.Name, out var row) ? row.LinkedAt : null,
            }).ToList(),
        };
    }

    /// <summary>Links the credential for this profile. The caller has checked the person agreed.</summary>
    public async Task<ConnectionOutcome> LinkAsync(string pluginId, Guid profileId, string name, string value, CancellationToken ct)
    {
        var declared = PersonalCredentials(pluginId).FirstOrDefault(secret => string.Equals(secret.Name, name, StringComparison.OrdinalIgnoreCase));
        if (declared is null)
        {
            return ConnectionOutcome.NotAsked;
        }

        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumValueLength)
        {
            return ConnectionOutcome.BadValue;
        }

        var canonicalId = catalog.Get(pluginId)!.Manifest.Id;
        await repository.UpsertAsync(
            canonicalId, profileId, declared.Name, protector.Encrypt(Bind(canonicalId, profileId, declared.Name, value.Trim())), clock.GetUtcNow(), ct).ConfigureAwait(false);
        return ConnectionOutcome.Done;
    }

    /// <summary>True when the profile has linked the credential. Never the value.</summary>
    public async Task<bool> IsLinkedAsync(string pluginId, Guid profileId, string name, CancellationToken ct) =>
        (await repository.ListAsync(pluginId, profileId, ct).ConfigureAwait(false))
            .Any(row => string.Equals(row.CredentialName, name, StringComparison.OrdinalIgnoreCase));

    public async Task<ConnectionOutcome> UnlinkAsync(string pluginId, Guid profileId, string name, CancellationToken ct)
    {
        var declared = PersonalCredentials(pluginId).FirstOrDefault(secret => string.Equals(secret.Name, name, StringComparison.OrdinalIgnoreCase));
        if (declared is null)
        {
            return ConnectionOutcome.NotAsked;
        }

        var canonicalId = catalog.Get(pluginId)!.Manifest.Id;
        await repository.DeleteAsync(canonicalId, profileId, declared.Name, ct).ConfigureAwait(false);

        // With no account left linked, nothing queued for this profile should still go out.
        if ((await repository.ListAsync(canonicalId, profileId, ct).ConfigureAwait(false)).Count == 0)
        {
            await outbox.DeleteForProfileAsync(canonicalId, profileId, ct).ConfigureAwait(false);
        }

        return ConnectionOutcome.Done;
    }

    /// <summary>
    /// For the host only: the value this profile linked, or null. Never reachable from a request; a plugin gets it for the profile
    /// whose play it is handling (Plugin architecture spec PA6), and for no one else.
    /// </summary>
    internal async Task<string?> ReadForHostAsync(string pluginId, Guid profileId, string name, CancellationToken ct)
    {
        var declared = PersonalCredentials(pluginId).FirstOrDefault(secret => string.Equals(secret.Name, name, StringComparison.OrdinalIgnoreCase));
        if (declared is null)
        {
            return null;
        }

        var canonicalId = catalog.Get(pluginId)!.Manifest.Id;
        var ciphertext = await repository.GetCiphertextAsync(canonicalId, profileId, declared.Name, ct).ConfigureAwait(false);
        var plain = ciphertext is null ? null : protector.Decrypt(ciphertext);
        var prefix = Bind(canonicalId, profileId, declared.Name, "");

        // A value copied from another row or profile no longer matches the plugin, profile and name it was stored for.
        return string.IsNullOrEmpty(plain) || !plain.StartsWith(prefix, StringComparison.Ordinal) ? null : plain[prefix.Length..];
    }

    /// <summary>The value with who it belongs to, so the stored text only decrypts to a usable value for its own row.</summary>
    private static string Bind(string pluginId, Guid profileId, string name, string value) => $"{pluginId}\n{profileId:N}\n{name}\n{value}";
}

/// <summary>How a link or unlink request ended.</summary>
public enum ConnectionOutcome
{
    Done,

    /// <summary>The plugin does not ask profiles for that credential.</summary>
    NotAsked,

    /// <summary>The value was empty or too long.</summary>
    BadValue,
}
