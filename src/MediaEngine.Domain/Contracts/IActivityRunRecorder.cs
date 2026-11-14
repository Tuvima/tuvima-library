namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Writes the Engine's activity runs as work happens (spec library-activity WP1, D21). A recording call never throws and never blocks
/// ingestion: a missed run only loses a line of History.
/// </summary>
public interface IActivityRunRecorder
{
    /// <summary>A Work became Ready: it joins this library's open "added" run, or opens one.</summary>
    Task RecordAddedAsync(
        Guid workId, Guid? assetId, string mediaType, string title, string? groupTitle, Guid? groupWorkId, CancellationToken ct = default);

    /// <summary>A file left the library: it joins this library's open "removed" run, or opens one.</summary>
    Task RecordRemovedAsync(Guid assetId, Guid? libraryId, string filePath, CancellationToken ct = default);

    /// <summary>A title was refreshed or fixed: it joins this library's open run of that kind, or opens one.</summary>
    Task RecordTouchedAsync(
        string kind, Guid libraryId, Guid workId, string mediaType, string title, string? groupTitle, Guid? groupWorkId, CancellationToken ct = default);

    /// <summary>Closes every run whose newest item joined more than <paramref name="quiet"/> ago; returns how many closed.</summary>
    Task<int> CloseQuietRunsAsync(TimeSpan quiet, CancellationToken ct = default);

    /// <summary>
    /// Called once when the Engine starts: whatever the last session left open was cut off by the Engine stopping, so it is completed and
    /// marked, and reopens if the same work is picked up again shortly after the restart (D30). Returns how many were closed.
    /// </summary>
    Task<int> CloseLeftOpenRunsAsync(CancellationToken ct = default);
}
