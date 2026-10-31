using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The Extras found beside titles (Extras spec). Rows are keyed by file path and belong to an item folder. Every call uses a
/// short-lived connection. Paths compare without regard to case, as on Windows.
/// </summary>
public interface IItemExtraRepository
{
    /// <summary>Every Extra a person has not moved to an episode, in the given item folders, in display order.</summary>
    Task<IReadOnlyList<ItemExtra>> ListByItemFoldersAsync(IReadOnlyCollection<string> itemFolders, CancellationToken ct = default);

    /// <summary>Every row (Extras and moved-to-episode markers) in one item folder.</summary>
    Task<IReadOnlyList<ItemExtra>> ListAllInItemFolderAsync(string itemFolder, CancellationToken ct = default);

    /// <summary>The Extra with this id, or null (also null for a file a person moved to an episode).</summary>
    Task<ItemExtra?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>The row at this file path (any origin), or null.</summary>
    Task<ItemExtra?> GetByPathAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Makes the rule's Extras of one item folder match <paramref name="found"/>: new files are added, known files keep their id,
    /// rule rows that are no longer found are removed. Rows a person decided (<see cref="ItemExtraOrigins.UserExtra"/>,
    /// <see cref="ItemExtraOrigins.UserEpisode"/>) are never added over or removed here unless their file is in <paramref name="goneFiles"/>.
    /// </summary>
    Task SyncItemFolderAsync(string itemFolder, IReadOnlyList<ItemExtra> found, IReadOnlyCollection<string> goneFiles, CancellationToken ct = default);

    /// <summary>Adds or replaces the row at a file path with a person's decision (see <see cref="ItemExtraOrigins"/>).</summary>
    Task SetDecisionAsync(ItemExtra extra, CancellationToken ct = default);

    /// <summary>Removes the row at this file path. Returns true when a row was removed.</summary>
    Task<bool> RemoveByPathAsync(string filePath, CancellationToken ct = default);

    /// <summary>Removes every row whose item folder is this folder or inside it. Returns how many.</summary>
    Task<int> RemoveUnderFolderAsync(string folder, CancellationToken ct = default);

    /// <summary>Every item folder that has rows, for a full re-check.</summary>
    Task<IReadOnlyList<string>> ListItemFoldersAsync(CancellationToken ct = default);
}
