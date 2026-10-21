using MediaEngine.Contracts.Plugins;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /plugin-pages/menu — the menu entries plugins add for the active profile. Empty when the Engine could not be asked.</summary>
    Task<IReadOnlyList<PluginMenuEntryDto>> GetPluginMenuAsync(CancellationToken ct = default);

    /// <summary>GET /plugin-pages/{pluginId}/{pageKey} — one plugin page. Null when it does not exist for this profile or the Engine could not be asked.</summary>
    Task<PluginPageResponse?> GetPluginPageAsync(string pluginId, string pageKey, CancellationToken ct = default);

    /// <summary>POST /plugin-pages/{pluginId}/{pageKey}/submit — saves a form page. Null when it was not saved.</summary>
    Task<PluginPageSubmitResponse?> SubmitPluginPageFormAsync(string pluginId, string pageKey, IReadOnlyDictionary<string, string> values, CancellationToken ct = default);
}
