using MediaEngine.Contracts.Plugins;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<IReadOnlyList<PluginMenuEntryDto>> GetPluginMenuAsync(CancellationToken ct = default) =>
        (await GetAsync<PluginMenuResponse>("GET /plugin-pages/menu", "/plugin-pages/menu", ct: ct).ConfigureAwait(false))?.Entries ?? [];

    public Task<PluginPageResponse?> GetPluginPageAsync(string pluginId, string pageKey, CancellationToken ct = default) =>
        GetAsync<PluginPageResponse>(
            "GET /plugin-pages/{pluginId}/{pageKey}",
            $"/plugin-pages/{Uri.EscapeDataString(pluginId)}/{Uri.EscapeDataString(pageKey)}",
            logAsWarning: false,
            ct: ct);

    public Task<PluginPageSubmitResponse?> SubmitPluginPageFormAsync(string pluginId, string pageKey, IReadOnlyDictionary<string, string> values, CancellationToken ct = default) =>
        PostAsync<PluginPageSubmitRequest, PluginPageSubmitResponse>(
            "POST /plugin-pages/{pluginId}/{pageKey}/submit",
            $"/plugin-pages/{Uri.EscapeDataString(pluginId)}/{Uri.EscapeDataString(pageKey)}/submit",
            new PluginPageSubmitRequest { Values = new Dictionary<string, string>(values, StringComparer.Ordinal) },
            ct: ct);
}
