using System.Net.Http.Json;
using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<NetworkSettingsDto?> GetNetworkSettingsAsync(CancellationToken ct = default) =>
        GetAsync<NetworkSettingsDto>("GET /settings/network", "/settings/network", ct: ct);

    public async Task<NetworkSettingsDto?> UpdateNetworkSettingsAsync(NetworkSettingsDto settings, CancellationToken ct = default)
    {
        if (!await PutAsync("PUT /settings/network", "/settings/network", settings, ct: ct))
        {
            return null;
        }

        return settings;
    }

    public Task<NetworkRuntimeStatusDto?> GetNetworkRuntimeStatusAsync(CancellationToken ct = default) =>
        GetAsync<NetworkRuntimeStatusDto>("GET /network/status", "/network/status", ct: ct);

    public Task<NetworkAppAddressesDto?> GetNetworkAppAddressesAsync(CancellationToken ct = default) =>
        GetAsync<NetworkAppAddressesDto>("GET /network/app-addresses", "/network/app-addresses", ct: ct);

    public Task<NetworkTestResultDto?> TestLocalNetworkAsync(CancellationToken ct = default) =>
        PostNetworkAsync<NetworkTestResultDto>("POST /network/tests/local", "/network/tests/local", new { }, ct);

    public Task<NetworkTestResultDto?> TestRemoteNetworkAsync(CancellationToken ct = default) =>
        PostNetworkAsync<NetworkTestResultDto>("POST /network/tests/remote", "/network/tests/remote", new { }, ct);

    public Task<NetworkBandwidthStatusDto?> TestNetworkBandwidthAsync(CancellationToken ct = default) =>
        PostNetworkAsync<NetworkBandwidthStatusDto>("POST /network/bandwidth-test", "/network/bandwidth-test", new { }, ct);

    public Task<PortAvailabilityResultDto?> CheckNetworkPortAsync(int port, CancellationToken ct = default) =>
        PostNetworkAsync<PortAvailabilityResultDto>(
            "POST /network/port-change/check",
            "/network/port-change/check",
            new PortAvailabilityRequest { Port = port },
            ct);

    public Task<PortAvailabilityResultDto?> ApplyNetworkPortAsync(int port, CancellationToken ct = default) =>
        PostNetworkAsync<PortAvailabilityResultDto>(
            "POST /network/port-change/apply",
            "/network/port-change/apply",
            new PortAvailabilityRequest { Port = port },
            ct);

    public async Task<NetworkSettingsDto?> ResetNetworkSettingsAsync(CancellationToken ct = default)
    {
        return await PostNetworkAsync<NetworkSettingsDto>("POST /network/reset", "/network/reset", new { }, ct);
    }

    public Task<NetworkRuntimeStatusDto?> RenewNetworkRouterMappingAsync(CancellationToken ct = default) =>
        PostNetworkAsync<NetworkRuntimeStatusDto>("POST /network/router/renew", "/network/router/renew", new { }, ct);

    private async Task<T?> PostNetworkAsync<T>(string operation, string path, object payload, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(path, payload, ct);
            if (!response.IsSuccessStatusCode)
            {
                await RecordHttpFailureAsync(operation, response, ct);
                return default;
            }

            ClearFailure(operation);
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (OperationCanceledException) { return default; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Operation} failed", operation);
            RecordExceptionFailure(operation, ex);
            return default;
        }
    }
}
