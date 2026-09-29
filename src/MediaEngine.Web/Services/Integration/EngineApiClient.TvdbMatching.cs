using System.Net.Http.Json;
using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<TvdbScopedMatchCandidatesDto?> GetTvdbScopedMatchCandidatesAsync(
        Guid entityId, string scopeId, int? seasonNumber, CancellationToken ct = default)
    {
        try
        {
            var suffix = seasonNumber.HasValue ? $"?seasonNumber={seasonNumber.Value}" : string.Empty;
            using var response = await _http.GetAsync(
                $"/metadata/{entityId}/tvdb-match/{Uri.EscapeDataString(scopeId)}/candidates{suffix}", ct);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<TvdbScopedMatchCandidatesDto>(ct);
            LastError = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { LastError = ex.Message; }
        return null;
    }

    public async Task<TvdbScopedMatchResultDto?> ApplyTvdbScopedMatchAsync(
        Guid entityId, string scopeId, ApplyTvdbScopedMatchDto request, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"/metadata/{entityId}/tvdb-match/{Uri.EscapeDataString(scopeId)}", request, ct);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<TvdbScopedMatchResultDto>(ct);
            LastError = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { LastError = ex.Message; }
        return null;
    }
}
