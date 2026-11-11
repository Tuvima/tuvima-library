using MediaEngine.Contracts.Plugins;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<PluginConnectionsListResponse?> GetPluginConnectionsAsync(CancellationToken ct = default) =>
        GetAsync<PluginConnectionsListResponse>("GET /plugins/connections", "/plugins/connections", logAsWarning: false, ct: ct);

    public Task<PluginDeviceSignInStartResponse?> StartPluginSignInAsync(string pluginId, string credentialName, CancellationToken ct = default) =>
        PostAsync<PluginDeviceSignInStartRequest, PluginDeviceSignInStartResponse>(
            "POST /plugins/{id}/connection/{name}/sign-in",
            ConnectionPath(pluginId, credentialName) + "/sign-in",
            new PluginDeviceSignInStartRequest { Agreed = true },
            logAsWarning: false,
            ct: ct);

    public Task<PluginDeviceSignInCheckResponse?> CheckPluginSignInAsync(string pluginId, string credentialName, CancellationToken ct = default) =>
        PostAsync<object, PluginDeviceSignInCheckResponse>(
            "POST /plugins/{id}/connection/{name}/sign-in/check",
            ConnectionPath(pluginId, credentialName) + "/sign-in/check",
            new { },
            logAsWarning: false,
            ct: ct);

    public Task<bool> LinkPluginConnectionAsync(string pluginId, string credentialName, string value, CancellationToken ct = default) =>
        PutAsync(
            "PUT /plugins/{id}/connection/{name}",
            ConnectionPath(pluginId, credentialName),
            new PluginConnectionLinkRequest { Value = value, Agreed = true },
            logAsWarning: false,
            ct: ct);

    public Task<bool> UnlinkPluginConnectionAsync(string pluginId, string credentialName, CancellationToken ct = default) =>
        DeleteAsync("DELETE /plugins/{id}/connection/{name}", ConnectionPath(pluginId, credentialName), logAsWarning: false, ct: ct);

    public async Task<IReadOnlyList<PluginSecretStatusResponse>?> GetPluginSecretsAsync(string pluginId, CancellationToken ct = default) =>
        await GetAsync<List<PluginSecretStatusResponse>>(
            "GET /plugins/{id}/secrets", $"/plugins/{Uri.EscapeDataString(pluginId)}/secrets", logAsWarning: false, ct: ct).ConfigureAwait(false);

    public Task<bool> SetPluginSecretAsync(string pluginId, string secretName, string value, CancellationToken ct = default) =>
        PutAsync(
            "PUT /plugins/{id}/secrets/{name}",
            $"/plugins/{Uri.EscapeDataString(pluginId)}/secrets/{Uri.EscapeDataString(secretName)}",
            new PluginSecretWriteRequest { Value = value },
            logAsWarning: false,
            ct: ct);

    public Task<bool> RemovePluginSecretAsync(string pluginId, string secretName, CancellationToken ct = default) =>
        DeleteAsync(
            "DELETE /plugins/{id}/secrets/{name}",
            $"/plugins/{Uri.EscapeDataString(pluginId)}/secrets/{Uri.EscapeDataString(secretName)}",
            logAsWarning: false,
            ct: ct);

    private static string ConnectionPath(string pluginId, string credentialName) =>
        $"/plugins/{Uri.EscapeDataString(pluginId)}/connection/{Uri.EscapeDataString(credentialName)}";
}
