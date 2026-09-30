using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<TvdbScopedMatchCandidatesDto?> GetTvdbScopedMatchCandidatesAsync(
        Guid entityId, string scopeId, int? seasonNumber, string? seasonType = null,
        CancellationToken ct = default, string? seriesId = null)
    {
        var query = new Dictionary<string, string?>();
        if (seasonNumber.HasValue) query["seasonNumber"] = seasonNumber.Value.ToString();
        if (!string.IsNullOrWhiteSpace(seasonType)) query["seasonType"] = seasonType;
        if (!string.IsNullOrWhiteSpace(seriesId)) query["seriesId"] = seriesId;
        return await GetAsync<TvdbScopedMatchCandidatesDto>(
            "TheTVDB match candidates",
            $"/metadata/{entityId}/tvdb-match/{Uri.EscapeDataString(scopeId)}/candidates",
            query, ct: ct);
    }

    public async Task<TvdbScopedMatchResultDto?> ApplyTvdbScopedMatchAsync(
        Guid entityId, string scopeId, ApplyTvdbScopedMatchDto request, CancellationToken ct = default)
    {
        return await PostAsync<ApplyTvdbScopedMatchDto, TvdbScopedMatchResultDto>(
            "TheTVDB scoped match",
            $"/metadata/{entityId}/tvdb-match/{Uri.EscapeDataString(scopeId)}",
            request, ct: ct);
    }

    public async Task<TvdbShowOrderPreviewDto?> GetTvdbShowOrderPreviewAsync(
        Guid entityId, string seasonType, CancellationToken ct = default)
    {
        return await GetAsync<TvdbShowOrderPreviewDto>(
            "TheTVDB show order preview",
            $"/metadata/{entityId}/tvdb-match/order-preview",
            new Dictionary<string, string?> { ["seasonType"] = seasonType }, ct: ct);
    }

    public async Task<TvdbShowOrderResultDto?> ApplyTvdbShowOrderAsync(
        Guid entityId, ApplyTvdbShowOrderDto request, CancellationToken ct = default)
    {
        return await PostAsync<ApplyTvdbShowOrderDto, TvdbShowOrderResultDto>(
            "TheTVDB show order",
            $"/metadata/{entityId}/tvdb-match/order",
            request, ct: ct);
    }
}
