using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Components.MediaEditor;

/// <summary>One shared TV artwork review bound to a pairing choice and checked-file snapshot.</summary>
public sealed class MediaEditorPairingSharedArtworkReviewState
{
    public MediaEditorPairingSharedArtworkPreviewDto? Review { get; private set; }
    private string? _operationToken;
    private string? _selectionSignature;

    public void Invalidate()
    {
        Review = null;
        _operationToken = null;
        _selectionSignature = null;
    }

    public void Set(MediaEditorPairingSharedArtworkPreviewDto review,
        string operationToken, string selectionSignature)
    {
        Review = review;
        _operationToken = operationToken;
        _selectionSignature = selectionSignature;
    }

    public bool IsCurrent(Guid? ownerWorkId, string? scope, string? role,
        Guid? artworkAssetId, string? operationToken, string selectionSignature,
        DateTimeOffset now) => Review is { } review
        && !string.IsNullOrWhiteSpace(review.SharedArtworkReviewToken)
        && review.AffectedFiles.Count > 0
        && review.ExpiresAt > now
        && review.OwnerWorkId == ownerWorkId
        && review.Scope == scope
        && review.Role == role
        && review.ArtworkAssetId == artworkAssetId
        && _operationToken == operationToken
        && _selectionSignature == selectionSignature
        && !string.IsNullOrWhiteSpace(_operationToken);
}
