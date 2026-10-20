using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The files that stayed on disk when their title was deleted from the library. Scans skip them; Settings lists them and
/// "Add back" removes the row. One row per place: a source folder and the file's path inside it.
/// </summary>
public interface IExcludedSourceFileRepository
{
    /// <summary>Adds the file, or replaces the row already at the same source folder and path (a new <see cref="ExcludedSourceFile.Id"/> is not created for it).</summary>
    Task AddOrReplaceAsync(ExcludedSourceFile file, CancellationToken ct = default);

    /// <summary>The excluded files of one library, newest first.</summary>
    Task<IReadOnlyList<ExcludedSourceFile>> ListByLibraryAsync(string libraryId, CancellationToken ct = default);

    /// <summary>Every excluded file, newest first.</summary>
    Task<IReadOnlyList<ExcludedSourceFile>> ListAllAsync(CancellationToken ct = default);

    /// <summary>The excluded file at one place, or null.</summary>
    Task<ExcludedSourceFile?> GetAsync(string sourceId, string relativePath, CancellationToken ct = default);

    /// <summary>The excluded file with this id, or null.</summary>
    Task<ExcludedSourceFile?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Removes one exclusion. Returns true when a row was removed.</summary>
    Task<bool> RemoveAsync(Guid id, CancellationToken ct = default);
}
