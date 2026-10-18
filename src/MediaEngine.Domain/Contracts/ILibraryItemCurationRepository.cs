using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

public interface ILibraryItemCurationRepository
{
    Task<IReadOnlyDictionary<Guid, LibraryItemRemovalTarget>> GetRemovalTargetsAsync(
        IReadOnlyCollection<Guid> workIds,
        CancellationToken ct = default);

    Task DeleteWorkRecordsAsync(LibraryItemRemovalTarget target, CancellationToken ct = default);

    Task<IReadOnlyList<LibraryItemHistoryEntry>> GetHistoryAsync(
        Guid workId,
        CancellationToken ct = default);
}
