using MediaEngine.Domain.Ingestion;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Reads the per-file work rows the live ingestion numbers are worked out from: each file's intake operation, its
/// media asset's newest identity job, its media type, any tool hold and any Pending review.
/// </summary>
public interface IIngestionQueueProgressRepository
{
    /// <summary>
    /// The files in the active period's set: every file noticed at or after <paramref name="since"/>, the files named in
    /// <paramref name="joinedOperationIds"/> (older files that joined the period), plus every file that is still being
    /// worked on (see <c>IngestionFileStepMapper.IsOperationOpen</c>) or is held, however old. With no
    /// <paramref name="since"/> (no period yet) only the last two groups are returned.
    /// </summary>
    Task<IReadOnlyList<IngestionQueueFileRow>> ReadAsync(
        DateTimeOffset? since,
        DateTimeOffset now,
        IReadOnlyCollection<Guid> joinedOperationIds,
        CancellationToken ct = default);
}
