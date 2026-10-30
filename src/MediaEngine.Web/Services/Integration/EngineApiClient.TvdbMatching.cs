using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public async Task<TvdbScopedMatchCandidatesDto?> GetTvdbScopedMatchCandidatesAsync(
        Guid entityId, string scopeId, int? seasonNumber, string? seasonType = null,
        CancellationToken ct = default, string? seriesId = null)
    {
        var query = new Dictionary<string, string?>();
        if (seasonNumber.HasValue)
        {
            query["seasonNumber"] = seasonNumber.Value.ToString();
        }
        if (!string.IsNullOrWhiteSpace(seasonType))
        {
            query["seasonType"] = seasonType;
        }
        if (!string.IsNullOrWhiteSpace(seriesId))
        {
            query["seriesId"] = seriesId;
        }
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

    public async Task<TvMatchBrowserShowsDto?> SearchTvMatchBrowserShowsAsync(Guid entityId, string query, CancellationToken ct = default) =>
        await GetAsync<TvMatchBrowserShowsDto>(
            "TV match browser shows",
            $"/metadata/{entityId}/tv-match-browser/shows",
            new Dictionary<string, string?> { ["query"] = query }, ct: ct);

    public async Task<TvMatchBrowserSeasonsDto?> GetTvMatchBrowserSeasonsAsync(Guid entityId, string seriesId, CancellationToken ct = default) =>
        await GetAsync<TvMatchBrowserSeasonsDto>(
            "TV match browser seasons",
            $"/metadata/{entityId}/tv-match-browser/shows/{Uri.EscapeDataString(seriesId)}/seasons", ct: ct);

    public async Task<TvMatchBrowserEpisodesDto?> GetTvMatchBrowserEpisodesAsync(Guid entityId, string seriesId, int seasonNumber, CancellationToken ct = default) =>
        await GetAsync<TvMatchBrowserEpisodesDto>(
            "TV match browser episodes",
            $"/metadata/{entityId}/tv-match-browser/shows/{Uri.EscapeDataString(seriesId)}/seasons/{seasonNumber}/episodes", ct: ct);
}
