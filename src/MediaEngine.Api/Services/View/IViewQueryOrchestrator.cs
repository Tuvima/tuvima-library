namespace MediaEngine.Api.Services.View;

public interface IViewQueryOrchestrator
{
    Task<ViewQueryResult> QueryAsync(ViewAssetQueryRequest request, CancellationToken ct = default);
    Task<ViewTimelineIndexResult> IndexAsync(ViewAssetQueryRequest request, CancellationToken ct = default);

    /// <summary>
    /// The profiles that added items in the requested scope, with how many each. It follows the same scope and
    /// permission rules as a listing: a profile whose only items the caller cannot see is left out.
    /// </summary>
    Task<ViewAddedByResult> AddedByAsync(ViewAssetQueryRequest request, CancellationToken ct = default);
}
