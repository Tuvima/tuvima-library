using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    Task<TvdbScopedMatchCandidatesDto?> GetTvdbScopedMatchCandidatesAsync(
        Guid entityId, string scopeId, int? seasonNumber, string? seasonType = null, CancellationToken ct = default);

    Task<TvdbScopedMatchResultDto?> ApplyTvdbScopedMatchAsync(
        Guid entityId, string scopeId, ApplyTvdbScopedMatchDto request, CancellationToken ct = default);

    Task<TvdbShowOrderPreviewDto?> GetTvdbShowOrderPreviewAsync(
        Guid entityId, string seasonType, CancellationToken ct = default);

    Task<TvdbShowOrderResultDto?> ApplyTvdbShowOrderAsync(
        Guid entityId, ApplyTvdbShowOrderDto request, CancellationToken ct = default);
}
