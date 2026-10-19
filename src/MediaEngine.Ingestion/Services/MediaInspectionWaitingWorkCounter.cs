using MediaEngine.Domain.Contracts;

namespace MediaEngine.Ingestion.Services;

/// <summary>Counts the media files whose inspection is held back because a tool is missing.</summary>
public sealed class MediaInspectionWaitingWorkCounter(IMediaInspectionHoldRepository holds) : IToolWaitingWorkCounter
{
    public Task<int> CountWaitingAsync(string toolId, CancellationToken ct) => holds.CountByToolAsync(toolId, ct);
}
