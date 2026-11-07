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
    /// and the values read from the folder and file names (inferred role, group, scope, shown-alongside, the title read from the file
    /// name, sizes and facts) are recomputed. Everything a person owns (own title and description, role, scope, shown-alongside, a group
    /// they chose, tags) is never changed by a pass. A group read from a folder is created here when the title has none of that name.
    /// Rule rows that are no longer found are removed, unless their file is in <paramref name="goneFiles"/> (no longer on disk): those
    /// keep their record as "File missing" (<see cref="ItemExtra.MissingSince"/>) and are dropped only once they have been missing since
    /// before <paramref name="purgeMissingBefore"/> (null never drops them). Rows a person decided (<see cref="ItemExtraOrigins.UserExtra"/>,
    /// <see cref="ItemExtraOrigins.UserEpisode"/>) are never added over, and follow the same missing rule when their file is gone.
    /// </summary>
    Task SyncItemFolderAsync(
        string itemFolder,
        IReadOnlyList<ItemExtra> found,
        IReadOnlyCollection<string> goneFiles,
        DateTimeOffset? purgeMissingBefore = null,
        CancellationToken ct = default);

    /// <summary>The groups of one root title, in their order, with how many files each holds.</summary>
    Task<IReadOnlyList<ExtraGroup>> ListGroupsAsync(string itemFolder, CancellationToken ct = default);

    /// <summary>The group of this name under the root title (ignoring case), created at the end of the order when there is none.</summary>
    Task<ExtraGroup> EnsureGroupAsync(string itemFolder, string name, CancellationToken ct = default);

    /// <summary>
    /// Renames a group. Returns false when the group does not exist or the root title already has another group of that name.
    /// </summary>
    Task<bool> RenameGroupAsync(Guid groupId, string newName, CancellationToken ct = default);

    /// <summary>Deletes a group; its files become ungrouped (a file whose folder names a group gets it back on the next pass). Returns false when there is no such group.</summary>
    Task<bool> DeleteGroupAsync(Guid groupId, CancellationToken ct = default);

    /// <summary>Sets the order of a root title's groups to the order of <paramref name="orderedGroupIds"/>; groups not listed follow in their old order.</summary>
    Task ReorderGroupsAsync(string itemFolder, IReadOnlyList<Guid> orderedGroupIds, CancellationToken ct = default);

    /// <summary>
    /// Changes the values a person owns on one supporting file. Returns the row as it now reads, or null when there is no such file.
    /// A role is stored as given: the caller checks it against the role list of the file's kind (<c>config/extras.json</c>).
    /// </summary>
    Task<ItemExtra?> UpdateOwnedAsync(Guid id, ExtraOwnedUpdate update, CancellationToken ct = default);

    /// <summary>The same change applied to many files in one step (the browser's bulk actions). Returns how many files exist and were changed.</summary>
    Task<int> UpdateOwnedManyAsync(IReadOnlyCollection<Guid> ids, ExtraOwnedUpdate update, CancellationToken ct = default);

    /// <summary>Adds or replaces the row at a file path with a person's decision (see <see cref="ItemExtraOrigins"/>).</summary>
    Task SetDecisionAsync(ItemExtra extra, CancellationToken ct = default);

    /// <summary>Removes the row at this file path. Returns true when a row was removed.</summary>
    Task<bool> RemoveByPathAsync(string filePath, CancellationToken ct = default);

    /// <summary>Removes every row whose item folder is this folder or inside it. Returns how many.</summary>
    Task<int> RemoveUnderFolderAsync(string folder, CancellationToken ct = default);

    /// <summary>Every item folder that has rows, for a full re-check.</summary>
    Task<IReadOnlyList<string>> ListItemFoldersAsync(CancellationToken ct = default);
}
