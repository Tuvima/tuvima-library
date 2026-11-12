namespace MediaEngine.Application.ReadModels;

/// <summary>
/// What the Needs Review list knows about the Work behind one row. <see cref="FamilyWorkId"/> is the top parent Work (episode to season to
/// show, track to album) and is null for a Work with no parent. <see cref="CoverUrl"/> is the file's own picture, set only when the Engine
/// holds one it can serve; <see cref="FamilyCoverUrl"/> likewise for the family's top Work.
/// </summary>
public sealed record NeedsReviewRowContext(
    Guid? WorkId,
    Guid? FamilyWorkId,
    string? FamilyTitle,
    string? FamilyCoverUrl,
    string? CoverUrl);

/// <summary>The contexts of a list of files and Works, found by the file's media asset id or by the Work's id.</summary>
public sealed class NeedsReviewRowContexts
{
    public static NeedsReviewRowContexts Empty { get; } = new();

    public IReadOnlyDictionary<Guid, NeedsReviewRowContext> ByAsset { get; init; } = new Dictionary<Guid, NeedsReviewRowContext>();

    public IReadOnlyDictionary<Guid, NeedsReviewRowContext> ByWork { get; init; } = new Dictionary<Guid, NeedsReviewRowContext>();
}
