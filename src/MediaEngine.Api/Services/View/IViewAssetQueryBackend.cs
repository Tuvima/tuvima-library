using MediaEngine.Contracts.LocalAssets;

namespace MediaEngine.Api.Services.View;

public interface IViewAssetQueryBackend
{
    Task<ViewAssetTimelinePageDto> QueryAsync(ViewAssetQueryPlan plan, CancellationToken ct = default);
    Task<ViewTimelineIndexDto> IndexAsync(ViewAssetQueryPlan plan, CancellationToken ct = default);
    Task<IReadOnlyList<ViewAddedByDto>> AddedByAsync(ViewAssetQueryPlan plan, CancellationToken ct = default);
}
