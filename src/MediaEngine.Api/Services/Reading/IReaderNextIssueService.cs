namespace MediaEngine.Api.Services.Reading;

/// <summary>The next owned issue of a comic's Series, for the page reader's end-of-issue card (PDF and comic reader spec D13).</summary>
public sealed record ReaderNextIssue(Guid AssetId, Guid WorkId, Guid CollectionId, string? Series, string? Title);

public interface IReaderNextIssueService
{
    /// <summary>The next owned, readable issue after <paramref name="assetId"/> in its Series that this profile may read, or null when there is none.</summary>
    Task<ReaderNextIssue?> FindAsync(HttpContext context, Guid assetId, Guid? profileId, CancellationToken ct);
}
