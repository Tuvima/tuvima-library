using MediaEngine.Contracts.Plugins;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /plugins/connections: the switched-on plugins that ask this profile to link an account, and what it has linked. Null when the Engine could not be asked.</summary>
    Task<PluginConnectionsListResponse?> GetPluginConnectionsAsync(CancellationToken ct = default);

    /// <summary>POST /plugins/{id}/connection/{name}/sign-in: asks the site for a code to approve on another device. Null when it could not start.</summary>
    Task<PluginDeviceSignInStartResponse?> StartPluginSignInAsync(string pluginId, string credentialName, CancellationToken ct = default);

    /// <summary>POST /plugins/{id}/connection/{name}/sign-in/check: one look at whether the code was approved. Null when the Engine could not be asked.</summary>
    Task<PluginDeviceSignInCheckResponse?> CheckPluginSignInAsync(string pluginId, string credentialName, CancellationToken ct = default);

    /// <summary>PUT /plugins/{id}/connection/{name}: links an account the person typed in (a token), after they agreed.</summary>
    Task<bool> LinkPluginConnectionAsync(string pluginId, string credentialName, string value, CancellationToken ct = default);

    /// <summary>DELETE /plugins/{id}/connection/{name}: signs this profile out of the account.</summary>
    Task<bool> UnlinkPluginConnectionAsync(string pluginId, string credentialName, CancellationToken ct = default);

    /// <summary>GET /plugins/{id}/secrets: the household keys the plugin asks an administrator for, and whether each is set. Never a value.</summary>
    Task<IReadOnlyList<PluginSecretStatusResponse>?> GetPluginSecretsAsync(string pluginId, CancellationToken ct = default);

    /// <summary>PUT /plugins/{id}/secrets/{name}: sets one household key.</summary>
    Task<bool> SetPluginSecretAsync(string pluginId, string secretName, string value, CancellationToken ct = default);

    /// <summary>DELETE /plugins/{id}/secrets/{name}: removes one household key.</summary>
    Task<bool> RemovePluginSecretAsync(string pluginId, string secretName, CancellationToken ct = default);
}
