using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Components.MediaEditor;

public sealed class MediaEditorEditionCoverReviewState
{
    public MediaEditorEditionCoverPreviewDto? Review { get; private set; }
    public Guid? RouteEntityId { get; private set; }

    public bool IsCurrent(Guid routeEntityId, Guid assetId, Guid artworkAssetId) =>
        Review is { } review
        && review.ExpiresAt > DateTimeOffset.UtcNow
        && RouteEntityId == routeEntityId
        && review.AssetId == assetId
        && review.ArtworkAssetId == artworkAssetId;

    public void Set(Guid routeEntityId, MediaEditorEditionCoverPreviewDto review)
    {
        RouteEntityId = routeEntityId;
        Review = review;
    }

    public void Clear()
    {
        RouteEntityId = null;
        Review = null;
    }
}
