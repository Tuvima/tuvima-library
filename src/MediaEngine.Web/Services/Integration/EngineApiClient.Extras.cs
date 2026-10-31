using System.Net.Http.Json;
using MediaEngine.Contracts.Details;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<ExtraViewDto?> GetExtraAsync(string extraId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(extraId, out var id))
        {
            return null;
        }

        try
        {
            return await _http.GetFromJsonAsync<ExtraViewDto>($"/api/v1/extras/{id:D}", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /api/v1/extras/{ExtraId} failed", id);
            return null;
        }
    }
}
