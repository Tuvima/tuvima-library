using System.Net.Http.Json;
using MediaEngine.Contracts.Universe;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    // -- Universe "Also appears in" ---------------------------------------------

    public async Task<EntityAppearancesDto?> GetEntityAppearancesAsync(
        string universeQid, string? entityQid, Guid? personId, CancellationToken ct = default)
    {
        // The Engine takes exactly one of the two; asking for neither or both is a mistake here, not a request.
        if ((string.IsNullOrWhiteSpace(entityQid)) == (personId is null))
        {
            _logger.LogWarning("GET /universe/{Qid}/also-appears-in needs exactly one of entity or person", universeQid);
            return null;
        }

        try
        {
            var subject = string.IsNullOrWhiteSpace(entityQid)
                ? $"person={personId!.Value:D}"
                : $"entity={Uri.EscapeDataString(entityQid.Trim())}";
            return await _http.GetFromJsonAsync<EntityAppearancesDto>(
                $"universe/{Uri.EscapeDataString(universeQid)}/also-appears-in?{subject}", ct);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GET /universe/{Qid}/also-appears-in failed", universeQid);
            LastError = ex.Message;
            return null;
        }
    }
}
