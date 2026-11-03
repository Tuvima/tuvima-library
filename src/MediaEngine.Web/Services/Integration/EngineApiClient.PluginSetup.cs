using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Plugins;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<PluginSetupPageResponse?> GetPluginSetupPageAsync(string pluginId, CancellationToken ct = default) =>
        GetAsync<PluginSetupPageResponse>("GET /plugins/{id}/page", $"/plugins/{Uri.EscapeDataString(pluginId)}/page", logAsWarning: false, ct: ct);

    public Task<PluginSetupChangeResult?> SavePluginSetupValueAsync(string pluginId, string key, JsonElement value, CancellationToken ct = default) =>
        PutAsync<PluginSetupValueRequest, PluginSetupChangeResult>(
            "PUT /plugins/{id}/page/values/{key}",
            $"/plugins/{Uri.EscapeDataString(pluginId)}/page/values/{Uri.EscapeDataString(key)}",
            new PluginSetupValueRequest { Value = value },
            ct: ct);

    public Task<PluginSetupChangeResult?> AddPluginSetupItemAsync(string pluginId, string key, IReadOnlyDictionary<string, JsonElement> values, CancellationToken ct = default) =>
        PostAsync<PluginSetupItemRequest, PluginSetupChangeResult>(
            "POST /plugins/{id}/page/items/{key}",
            $"/plugins/{Uri.EscapeDataString(pluginId)}/page/items/{Uri.EscapeDataString(key)}",
            new PluginSetupItemRequest { Values = new Dictionary<string, JsonElement>(values, StringComparer.Ordinal) },
            ct: ct);

    public async Task<PluginSetupChangeResult?> RemovePluginSetupItemAsync(string pluginId, string key, string itemId, CancellationToken ct = default)
    {
        var removed = await DeleteAsync(
            "DELETE /plugins/{id}/page/items/{key}/{itemId}",
            $"/plugins/{Uri.EscapeDataString(pluginId)}/page/items/{Uri.EscapeDataString(key)}/{Uri.EscapeDataString(itemId)}",
            logAsWarning: false,
            ct: ct).ConfigureAwait(false);
        return removed
            ? new PluginSetupChangeResult { Ok = true, Message = "Removed." }
            : new PluginSetupChangeResult { Ok = false, Message = LastError ?? "The Engine did not remove it." };
    }

    public Task<PluginSetupChangeResult?> TestPluginSetupItemAsync(string pluginId, string key, string itemId, CancellationToken ct = default) =>
        PostAsync<object, PluginSetupChangeResult>(
            "POST /plugins/{id}/page/items/{key}/{itemId}/test",
            $"/plugins/{Uri.EscapeDataString(pluginId)}/page/items/{Uri.EscapeDataString(key)}/{Uri.EscapeDataString(itemId)}/test",
            new { },
            ct: ct);

    public Task<PluginSetupChangeResult?> SavePluginSetupItemSecretAsync(string pluginId, string key, string itemId, string field, string value, CancellationToken ct = default) =>
        PutAsync<PluginSetupSecretRequest, PluginSetupChangeResult>(
            "PUT /plugins/{id}/page/items/{key}/{itemId}/secret/{field}",
            $"/plugins/{Uri.EscapeDataString(pluginId)}/page/items/{Uri.EscapeDataString(key)}/{Uri.EscapeDataString(itemId)}/secret/{Uri.EscapeDataString(field)}",
            new PluginSetupSecretRequest { Value = value },
            ct: ct);

    public Task<PluginFileResponse?> GetPluginFileAsync(string pluginId, CancellationToken ct = default) =>
        GetAsync<PluginFileResponse>("GET /plugins/{id}/file", $"/plugins/{Uri.EscapeDataString(pluginId)}/file", logAsWarning: false, ct: ct);

    public Task<PluginFileCheckResponse?> CheckPluginFileAsync(string pluginId, string text, CancellationToken ct = default) =>
        PostAsync<PluginFileCheckRequest, PluginFileCheckResponse>(
            "POST /plugins/{id}/file/check",
            $"/plugins/{Uri.EscapeDataString(pluginId)}/file/check",
            new PluginFileCheckRequest { Text = text },
            ct: ct);

    public Task<PluginActivityCapDto?> SavePluginActivityCapAsync(string pluginId, int maxEntries, int maxDays, CancellationToken ct = default) =>
        PutAsync<PluginActivityCapDto, PluginActivityCapDto>(
            "PUT /plugins/{id}/activity-cap",
            $"/plugins/{Uri.EscapeDataString(pluginId)}/activity-cap",
            new PluginActivityCapDto { MaxEntries = maxEntries, MaxDays = maxDays },
            ct: ct);
}
