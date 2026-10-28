using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Plugins;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<PluginDependenciesResponse?> GetPluginDependenciesAsync(string pluginId, CancellationToken ct = default) =>
        GetAsync<PluginDependenciesResponse>("GET /plugins/{id}/dependencies", $"/plugins/{Uri.EscapeDataString(pluginId)}/dependencies", logAsWarning: false, ct: ct);

    public Task<PluginLibraryLimitResponse?> GetPluginLibrariesAsync(string pluginId, CancellationToken ct = default) =>
        GetAsync<PluginLibraryLimitResponse>("GET /plugins/{id}/libraries", $"/plugins/{Uri.EscapeDataString(pluginId)}/libraries", logAsWarning: false, ct: ct);

    public Task<bool> SetPluginLibrariesAsync(string pluginId, IReadOnlyList<string> libraryIds, CancellationToken ct = default) =>
        PutAsync(
            "PUT /plugins/{id}/libraries",
            $"/plugins/{Uri.EscapeDataString(pluginId)}/libraries",
            new PluginLibraryLimitRequest { LibraryIds = libraryIds.ToList() },
            ct: ct);

    public Task<PluginActivityResponse?> GetPluginActivityAsync(string pluginId, int limit = 20, CancellationToken ct = default) =>
        GetAsync<PluginActivityResponse>("GET /plugins/{id}/activity", $"/plugins/{Uri.EscapeDataString(pluginId)}/activity?limit={limit}", logAsWarning: false, ct: ct);

    public Task<PluginCheckNowResponse?> CheckPluginNowAsync(string pluginId, CancellationToken ct = default) =>
        PostAsync<object, PluginCheckNowResponse>("POST /plugins/{id}/check-now", $"/plugins/{Uri.EscapeDataString(pluginId)}/check-now", new { }, ct: ct);

    public Task<PluginRemovalPreviewResponse?> GetPluginRemovalPreviewAsync(string pluginId, CancellationToken ct = default) =>
        GetAsync<PluginRemovalPreviewResponse>("GET /plugins/{id}/removal-preview", $"/plugins/{Uri.EscapeDataString(pluginId)}/removal-preview", logAsWarning: false, ct: ct);

    public async Task<PluginActionResult> RemovePluginAsync(string pluginId, PluginRemoveRequest request, CancellationToken ct = default)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Delete, $"/plugins/{Uri.EscapeDataString(pluginId)}")
            {
                Content = JsonContent.Create(request),
            };
            using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return PluginActionResult.Success;
            }

            return PluginActionResult.Failure(await ReadProblemMessageAsync(response, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return PluginActionResult.Failure();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DELETE /plugins/{PluginId} failed", pluginId);
            return PluginActionResult.Failure();
        }
    }

    // The Engine explains a refusal in plain words in the problem's detail; anything else is not worth showing.
    private static async Task<string?> ReadProblemMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
            return document.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String
                ? detail.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
