using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services.StashBox;

namespace MediaEngine.Api.Services.Plugins.Setup;

/// <summary>The outcome of changing a value held outside a plugin's saved settings. The message is plain words and never holds a secret.</summary>
public sealed record PluginSettingWriteResult(bool Ok, string Message, string? ItemId = null)
{
    public static PluginSettingWriteResult Unsupported { get; } = new(false, "That cannot be changed here.");
}

/// <summary>
/// A named place the Engine reads current setting values from when a plugin's setting says <c>"value_source": "name"</c> (Plugins and
/// Integrations screens v5 D49). It lets a plugin's JSON describe a repeating group whose items are kept in the data store (such as
/// the Stash-box servers) without the Dashboard knowing the plugin. Values are keyed by setting key. A repeating group's value is an
/// array of objects with <c>id</c>, <c>title</c>, optional <c>subtitle</c>, <c>state</c>, <c>key_missing</c> and <c>locked</c>, plus
/// one entry per field. A value source never returns a secret. The same place also takes the changes the page makes (a value, a row
/// added or removed, a row tested, a row's key), so the Dashboard calls one set of actions for every plugin.
/// </summary>
public interface IPluginSettingValueSource
{
    /// <summary>The name a plugin's setting uses in <c>value_source</c>.</summary>
    string Name { get; }

    /// <summary>True when the values belong to the Vault, so only a device session with Vault mode on may read or change them.</summary>
    bool VaultGated => false;

    Task<IReadOnlyDictionary<string, JsonElement>> ReadAsync(CancellationToken ct);

    /// <summary>Changes a plain value (a switch, a number).</summary>
    Task<PluginSettingWriteResult> SetValueAsync(string key, JsonElement value, CancellationToken ct) => Task.FromResult(PluginSettingWriteResult.Unsupported);

    /// <summary>Adds a row to a repeating group. Secret fields arrive with the other fields and are never echoed.</summary>
    Task<PluginSettingWriteResult> AddItemAsync(string key, IReadOnlyDictionary<string, JsonElement> values, CancellationToken ct) => Task.FromResult(PluginSettingWriteResult.Unsupported);

    Task<PluginSettingWriteResult> RemoveItemAsync(string key, string itemId, CancellationToken ct) => Task.FromResult(PluginSettingWriteResult.Unsupported);

    /// <summary>Tests one row (test connection).</summary>
    Task<PluginSettingWriteResult> TestItemAsync(string key, string itemId, CancellationToken ct) => Task.FromResult(PluginSettingWriteResult.Unsupported);

    /// <summary>Saves a secret field of one row (a row's key). It is checked first where the service allows.</summary>
    Task<PluginSettingWriteResult> SetItemSecretAsync(string key, string itemId, string field, string value, CancellationToken ct) => Task.FromResult(PluginSettingWriteResult.Unsupported);
}

/// <summary>The Stash-box servers and the fingerprint permission, kept in the data store by the Stash-box settings.</summary>
public sealed class StashBoxSettingValueSource(IStashBoxSettings settings, StashBoxClient client) : IPluginSettingValueSource
{
    private const string Servers = "servers";
    private const string Fingerprint = "fingerprint_permission";

    public string Name => "stash_box";

    // The plugin belongs to the Vault (Videos spec D17): its servers are not there for anyone whose Vault mode is off.
    public bool VaultGated => true;

    public async Task<IReadOnlyDictionary<string, JsonElement>> ReadAsync(CancellationToken ct)
    {
        var snapshot = await settings.GetAsync(ct).ConfigureAwait(false);
        var servers = snapshot.Servers.Select((server, index) => new Dictionary<string, object?>
        {
            ["id"] = server.Id,
            ["title"] = server.Name,
            ["subtitle"] = server.Address,
            ["state"] = server.Connection,
            ["key_missing"] = !server.HasKey,
            ["locked"] = index == 0,
            ["name"] = server.Name,
            ["address"] = server.Address,
        }).ToList();
        return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            [Servers] = JsonSerializer.SerializeToElement(servers),
            [Fingerprint] = JsonSerializer.SerializeToElement(snapshot.FingerprintPermission),
        };
    }

    public async Task<PluginSettingWriteResult> SetValueAsync(string key, JsonElement value, CancellationToken ct)
    {
        if (!string.Equals(key, Fingerprint, StringComparison.OrdinalIgnoreCase) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return PluginSettingWriteResult.Unsupported;
        }

        await settings.SetFingerprintPermissionAsync(value.GetBoolean(), ct).ConfigureAwait(false);
        return new PluginSettingWriteResult(true, "Saved.");
    }

    public async Task<PluginSettingWriteResult> AddItemAsync(string key, IReadOnlyDictionary<string, JsonElement> values, CancellationToken ct)
    {
        if (!string.Equals(key, Servers, StringComparison.OrdinalIgnoreCase))
        {
            return PluginSettingWriteResult.Unsupported;
        }

        var name = Text(values, "name");
        if (!StashBoxAddress.TryNormalize(Text(values, "address"), out var address))
        {
            return new PluginSettingWriteResult(false, "That address cannot be used. It must start with https:// (http:// is only allowed for a server on this computer) and have no sign-in details in it.");
        }

        var server = await settings.AddServerAsync(name, address.AbsoluteUri, ct).ConfigureAwait(false);
        if (server is null)
        {
            return new PluginSettingWriteResult(false, "That server is already in the list, or the list is full.");
        }

        var typedKey = Text(values, "api_key");
        if (typedKey.Length == 0)
        {
            return new PluginSettingWriteResult(true, "Server added. Add its key to use it.", server.Id);
        }

        var keyResult = await SetItemSecretAsync(key, server.Id, "api_key", typedKey, ct).ConfigureAwait(false);
        return new PluginSettingWriteResult(true, keyResult.Ok ? "Server added and connected." : "Server added. " + keyResult.Message, server.Id);
    }

    public async Task<PluginSettingWriteResult> RemoveItemAsync(string key, string itemId, CancellationToken ct)
    {
        if (!string.Equals(key, Servers, StringComparison.OrdinalIgnoreCase))
        {
            return PluginSettingWriteResult.Unsupported;
        }

        return await settings.RemoveServerAsync(itemId, ct).ConfigureAwait(false)
            ? new PluginSettingWriteResult(true, "Removed.")
            : new PluginSettingWriteResult(false, "There is no such server, or it cannot be removed.");
    }

    public async Task<PluginSettingWriteResult> TestItemAsync(string key, string itemId, CancellationToken ct)
    {
        if (!string.Equals(key, Servers, StringComparison.OrdinalIgnoreCase))
        {
            return PluginSettingWriteResult.Unsupported;
        }

        var server = (await settings.GetAsync(ct).ConfigureAwait(false)).Servers.FirstOrDefault(s => s.Id == itemId);
        if (server is null || !StashBoxAddress.TryNormalize(server.Address, out var address))
        {
            return new PluginSettingWriteResult(false, "There is no such server.");
        }

        var credential = await settings.GetCredentialAsync(server.Id, ct).ConfigureAwait(false);
        if (credential is null)
        {
            return new PluginSettingWriteResult(false, "Enter the account key first.");
        }

        var test = await client.TestAsync(address, credential.Value.Key, ct).ConfigureAwait(false);
        if (test.Status != "unavailable")
        {
            await settings.SetConnectionAsync(server.Id, test.Status, ct).ConfigureAwait(false);
        }

        return new PluginSettingWriteResult(test.Connected, test.Message);
    }

    public async Task<PluginSettingWriteResult> SetItemSecretAsync(string key, string itemId, string field, string value, CancellationToken ct)
    {
        if (!string.Equals(key, Servers, StringComparison.OrdinalIgnoreCase) || !string.Equals(field, "api_key", StringComparison.OrdinalIgnoreCase))
        {
            return PluginSettingWriteResult.Unsupported;
        }

        var server = (await settings.GetAsync(ct).ConfigureAwait(false)).Servers.FirstOrDefault(s => s.Id == itemId);
        if (server is null || !StashBoxAddress.TryNormalize(server.Address, out var address))
        {
            return new PluginSettingWriteResult(false, "There is no such server.");
        }

        var typed = value?.Trim() ?? string.Empty;
        if (typed.Length < 8 || typed.Length > 512 || typed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return new PluginSettingWriteResult(false, "That does not look like an account key.");
        }

        // Checked first, kept only when the server accepts it.
        var test = await client.TestAsync(address, typed, ct).ConfigureAwait(false);
        if (test.Connected)
        {
            await settings.SetKeyAsync(server.Id, typed, ct).ConfigureAwait(false);
            await settings.SetConnectionAsync(server.Id, StashBoxConnectionStates.Connected, ct).ConfigureAwait(false);
        }

        return new PluginSettingWriteResult(test.Connected, test.Message);
    }

    private static string Text(IReadOnlyDictionary<string, JsonElement> values, string name) =>
        values.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? string.Empty : string.Empty;
}
