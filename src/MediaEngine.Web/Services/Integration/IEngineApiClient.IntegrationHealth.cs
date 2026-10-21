using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /access/applications/integration-health: how many webhooks are broken and how many keys need a look. Null when the Engine could not be asked.</summary>
    Task<IntegrationHealthResponse?> GetIntegrationHealthAsync(CancellationToken ct = default);
}
