using System.Text.Json;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Plugins.Setup;

/// <summary>
/// A named place the Engine reads current setting values from when a plugin's setting says <c>"value_source": "name"</c> (Plugins and
/// Integrations screens v5 D49). It lets a plugin's JSON describe a repeating group whose items are kept in the data store (such as
/// the Stash-box servers) without the Dashboard knowing the plugin. Values are keyed by setting key. A repeating group's value is an
/// array of objects with <c>id</c>, <c>title</c>, optional <c>subtitle</c>, <c>state</c> and <c>locked</c>, plus one entry per field.
/// A value source never returns a secret.
/// </summary>
public interface IPluginSettingValueSource
{
    /// <summary>The name a plugin's setting uses in <c>value_source</c>.</summary>
    string Name { get; }

    Task<IReadOnlyDictionary<string, JsonElement>> ReadAsync(CancellationToken ct);
}

/// <summary>The Stash-box servers and the fingerprint permission, kept in the data store by the Stash-box settings.</summary>
public sealed class StashBoxSettingValueSource(IStashBoxSettings settings) : IPluginSettingValueSource
{
    public string Name => "stash_box";

    public async Task<IReadOnlyDictionary<string, JsonElement>> ReadAsync(CancellationToken ct)
    {
        var snapshot = await settings.GetAsync(ct).ConfigureAwait(false);
        var servers = snapshot.Servers.Select((server, index) => new Dictionary<string, object?>
        {
            ["id"] = server.Id,
            ["title"] = server.Name,
            ["subtitle"] = server.Address,
            ["state"] = server.Connection,
            ["locked"] = index == 0,
            ["name"] = server.Name,
            ["address"] = server.Address,
        }).ToList();
        return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["servers"] = JsonSerializer.SerializeToElement(servers),
            ["fingerprint_permission"] = JsonSerializer.SerializeToElement(snapshot.FingerprintPermission),
        };
    }
}
