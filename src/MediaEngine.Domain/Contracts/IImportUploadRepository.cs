using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The resumable uploads into the Import folder (Library setup spec D44), kept in the data store so an upload resumes after an Engine
/// restart. Recording a piece twice changes nothing, which is what makes a resent piece harmless.
/// </summary>
public interface IImportUploadRepository
{
    Task CreateAsync(ImportUpload upload, CancellationToken ct = default);

    /// <summary>The upload with this id in any state, or null.</summary>
    Task<ImportUpload?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The indexes of the pieces received so far, in order.</summary>
    Task<IReadOnlyList<int>> ListPiecesAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Records that a piece arrived. Returns true when it was new, false when it had been recorded before. Only an open upload takes
    /// pieces; for any other the answer is false and nothing changes. A new piece moves the upload's last-piece time to <paramref name="at"/>.
    /// </summary>
    Task<bool> RecordPieceAsync(Guid id, int index, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Closes an open upload as completed (with the path it was saved under) or cancelled. Returns false when it was not open.</summary>
    Task<bool> CloseAsync(Guid id, string state, string? finalPath, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>The open uploads whose last piece (or start) is older than <paramref name="cutoff"/>.</summary>
    Task<IReadOnlyList<ImportUpload>> ListIdleOpenAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>Deletes the finished (completed or cancelled) uploads closed before <paramref name="cutoff"/>. Returns how many.</summary>
    Task<int> DeleteClosedBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
