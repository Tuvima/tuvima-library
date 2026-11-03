using System.Text.Json;
using MediaEngine.Contracts.Plugins;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /plugins/{id}/page — the plugin's Set up page as cards, built by the Engine from the plugin's own JSON. Null when the Engine could not be asked or there is no such plugin.</summary>
    Task<PluginSetupPageResponse?> GetPluginSetupPageAsync(string pluginId, CancellationToken ct = default);

    /// <summary>PUT /plugins/{id}/page/values/{key} — saves one value the plugin's JSON says is kept elsewhere.</summary>
    Task<PluginSetupChangeResult?> SavePluginSetupValueAsync(string pluginId, string key, JsonElement value, CancellationToken ct = default);

    /// <summary>POST /plugins/{id}/page/items/{key} — adds a row to a repeating group.</summary>
    Task<PluginSetupChangeResult?> AddPluginSetupItemAsync(string pluginId, string key, IReadOnlyDictionary<string, JsonElement> values, CancellationToken ct = default);

    /// <summary>DELETE /plugins/{id}/page/items/{key}/{itemId} — removes a row of a repeating group.</summary>
    Task<PluginSetupChangeResult?> RemovePluginSetupItemAsync(string pluginId, string key, string itemId, CancellationToken ct = default);

    /// <summary>POST /plugins/{id}/page/items/{key}/{itemId}/test — tests one row (test connection).</summary>
    Task<PluginSetupChangeResult?> TestPluginSetupItemAsync(string pluginId, string key, string itemId, CancellationToken ct = default);

    /// <summary>PUT /plugins/{id}/page/items/{key}/{itemId}/secret/{field} — saves a row's key.</summary>
    Task<PluginSetupChangeResult?> SavePluginSetupItemSecretAsync(string pluginId, string key, string itemId, string field, string value, CancellationToken ct = default);

    /// <summary>GET /plugins/{id}/file — the text of the file that defines the plugin, with its state and problems. Null when the Engine could not be asked or the plugin has no file.</summary>
    Task<PluginFileResponse?> GetPluginFileAsync(string pluginId, CancellationToken ct = default);

    /// <summary>POST /plugins/{id}/file/check — checks proposed text for the plugin's file. Writes nothing.</summary>
    Task<PluginFileCheckResponse?> CheckPluginFileAsync(string pluginId, string text, CancellationToken ct = default);

    /// <summary>PUT /plugins/{id}/activity-cap — how much of the plugin's Activity list is kept.</summary>
    Task<PluginActivityCapDto?> SavePluginActivityCapAsync(string pluginId, int maxEntries, int maxDays, CancellationToken ct = default);
}
