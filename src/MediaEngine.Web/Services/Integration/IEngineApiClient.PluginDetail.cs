using MediaEngine.Contracts.Plugins;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /plugins/{id}/dependencies — what the plugin needs and what needs it. Null when the Engine could not be asked.</summary>
    Task<PluginDependenciesResponse?> GetPluginDependenciesAsync(string pluginId, CancellationToken ct = default);

    /// <summary>GET /plugins/{id}/libraries — which libraries the plugin works on. Null when the Engine could not be asked.</summary>
    Task<PluginLibraryLimitResponse?> GetPluginLibrariesAsync(string pluginId, CancellationToken ct = default);

    /// <summary>PUT /plugins/{id}/libraries — limits the plugin to the chosen libraries; an empty list removes the limit.</summary>
    Task<bool> SetPluginLibrariesAsync(string pluginId, IReadOnlyList<string> libraryIds, CancellationToken ct = default);

    /// <summary>GET /plugins/{id}/activity — what the plugin did, newest first. Null when the Engine could not be asked.</summary>
    Task<PluginActivityResponse?> GetPluginActivityAsync(string pluginId, int limit = 20, CancellationToken ct = default);

    /// <summary>POST /plugins/{id}/check-now — runs the plugin's check and says in plain words whether it works. Null when the Engine could not be asked.</summary>
    Task<PluginCheckNowResponse?> CheckPluginNowAsync(string pluginId, CancellationToken ct = default);

    /// <summary>GET /plugins/{id}/removal-preview — what removing the plugin would clean up. Null when the Engine could not be asked.</summary>
    Task<PluginRemovalPreviewResponse?> GetPluginRemovalPreviewAsync(string pluginId, CancellationToken ct = default);

    /// <summary>DELETE /plugins/{id} with the person's choices (keep or remove what it saved, switch off what needs it).</summary>
    Task<PluginActionResult> RemovePluginAsync(string pluginId, PluginRemoveRequest request, CancellationToken ct = default);
}
