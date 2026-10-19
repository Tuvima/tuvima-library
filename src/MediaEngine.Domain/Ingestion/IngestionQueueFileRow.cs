namespace MediaEngine.Domain.Ingestion;

/// <summary>
/// One file in the live ingestion queue, as the data store holds it: its intake operation, the identity job of its
/// media asset (when it has one) and the few facts the live numbers need.
/// </summary>
/// <param name="OperationId">The file's intake operation.</param>
/// <param name="CreatedAt">When the file was first noticed.</param>
/// <param name="UpdatedAt">When the intake operation last changed.</param>
/// <param name="Stage">The intake operation's stage (see <c>MediaOperationStage</c>).</param>
/// <param name="Status">The intake operation's status (see <c>MediaOperationStatus</c>).</param>
/// <param name="ResultSummary">What the intake operation recorded when it finished, for example <c>duplicate</c>.</param>
/// <param name="AssetId">The media asset the file became, once it is registered (or the existing one it duplicates).</param>
/// <param name="IdentityState">The newest identity job's state (an <c>IdentityJobState</c> name), if the asset has a job.</param>
/// <param name="IdentityUpdatedAt">When that identity job last changed.</param>
/// <param name="MediaType">The file's media type name once known, otherwise <see langword="null"/>.</param>
/// <param name="HeldToolId">The missing tool the file's inspection is waiting for, if it is held.</param>
/// <param name="HasPendingReview">Whether the file (or its work) has a Pending Needs Review item.</param>
public sealed record IngestionQueueFileRow(
    Guid OperationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Stage,
    string Status,
    string? ResultSummary,
    Guid? AssetId,
    string? IdentityState,
    DateTimeOffset? IdentityUpdatedAt,
    string? MediaType,
    string? HeldToolId,
    bool HasPendingReview);
