using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

public interface ILibraryItemCurationRepository
{
    Task<IReadOnlyList<LibraryItemHistoryEntry>> GetHistoryAsync(
        Guid workId,
        CancellationToken ct = default);
}
