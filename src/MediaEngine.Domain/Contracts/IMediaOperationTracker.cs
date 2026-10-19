using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

public interface IMediaOperationTracker
{
    Task<MediaOperation> EnsureQueuedAsync(MediaOperation operation, CancellationToken ct = default);
    Task UpdateStageAsync(Guid operationId, string stage, int? progressPercent = null, string? message = null, object? detail = null, CancellationToken ct = default);
    Task MarkSucceededAsync(Guid operationId, string? resultSummary = null, object? detail = null, CancellationToken ct = default);
    Task MarkNoResultAsync(Guid operationId, string? reason = null, object? detail = null, CancellationToken ct = default);
    /// <summary>Closes an operation whose subject (usually a file) is confirmed to be gone, with a plain reason, a history entry and a published change.</summary>
    Task MarkMissingConfirmedAsync(Guid operationId, string? reason = null, object? detail = null, CancellationToken ct = default);
    Task MarkBlockedAsync(Guid operationId, string reason, object? detail = null, CancellationToken ct = default);
    Task MarkFailedAsync(Guid operationId, Exception exception, bool terminal, CancellationToken ct = default);

    /// <summary>
    /// Records how far a long job has got and tells the Dashboard, without adding a timeline entry. The caller decides how
    /// often to call it (the organising job reports at most once a second).
    /// </summary>
    Task ReportProgressAsync(
        Guid operationId,
        int itemsTotal,
        int itemsCompleted,
        int itemsFailed,
        int progressPercent,
        TimeSpan? extendLease = null,
        CancellationToken ct = default);
}
