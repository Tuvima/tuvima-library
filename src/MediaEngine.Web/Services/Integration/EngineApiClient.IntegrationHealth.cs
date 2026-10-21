using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<IntegrationHealthResponse?> GetIntegrationHealthAsync(CancellationToken ct = default) =>
        GetAsync<IntegrationHealthResponse>("GET /access/applications/integration-health", "/access/applications/integration-health", ct: ct);
}
