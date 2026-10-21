namespace MediaEngine.Domain.Models;

// What re-filing an asset that is already in a library folder would do, and what it did. Re-filing renames and moves one file to
// the place the library's naming policy gives it for where the item now sits. The same planning answers the preview of a library
// move (what will happen to each file) and the durable job that carries it out, so the two cannot disagree.

/// <summary>What re-filing one asset would do right now.</summary>
public enum AssetRefileDisposition
{
    /// <summary>The file will be renamed and moved to <see cref="AssetRefilePlan.TargetPath"/>.</summary>
    Moves,

    /// <summary>The file already has the name and place the library gives it. Nothing to do.</summary>
    AlreadyFits,

    /// <summary>The file sits in an As-is folder: Tuvima only reads it, never renames or moves it.</summary>
    AsIs,

    /// <summary>The file sits in a managed folder that is read-only.</summary>
    ReadOnly,

    /// <summary>The mutation policy refuses to touch the file (for example it is the original of a folder merge).</summary>
    Protected,

    /// <summary>The name the file would take is already held by another file. The file stays where it is; nothing is overwritten.</summary>
    Collision,

    /// <summary>The file cannot be re-filed right now (missing from disk, not in a library folder, no usable name yet).</summary>
    Unavailable,
}

/// <param name="Disposition">What would happen.</param>
/// <param name="CurrentPath">Where the file is now (empty when the asset is not known).</param>
/// <param name="TargetPath">The full path it would take. Set for <see cref="AssetRefileDisposition.Moves"/> and <see cref="AssetRefileDisposition.Collision"/>.</param>
/// <param name="Reason">A plain line saying why the file is not renamed or moved. Null when it is.</param>
public sealed record AssetRefilePlan(AssetRefileDisposition Disposition, string CurrentPath, string? TargetPath, string? Reason)
{
    public static AssetRefilePlan Of(AssetRefileDisposition disposition, string currentPath, string? reason = null, string? targetPath = null) =>
        new(disposition, currentPath, targetPath, reason);
}

/// <summary>
/// Where an asset would sit after a library move, so its target path can be worked out before anything changes. The title-level numbers
/// the move writes are given in <see cref="Numbers"/>; a null value means the number is cleared (an item added anyway has none).
/// </summary>
/// <param name="WorkId">The Work whose own values (title, track number) the file would take: the entry's Work when the item merges into one, else its own.</param>
/// <param name="ParentWorkId">The album, season, series or audiobook the Work would sit under.</param>
/// <param name="ParentScopeWorkId">The Work that would hold the container-level values (album, album artist, show name, year).</param>
/// <param name="Numbers">The position numbers after the move, by key. Null clears the key.</param>
public sealed record AssetRefilePlacement(
    Guid WorkId,
    Guid? ParentWorkId,
    Guid ParentScopeWorkId,
    IReadOnlyDictionary<string, string?> Numbers);

public enum AssetRefileOutcomeKind
{
    /// <summary>The file was renamed and moved, and the stored path now points at it.</summary>
    Moved,

    /// <summary>The file already had the right name and place.</summary>
    AlreadyFits,

    /// <summary>The file was left exactly where it is, for the reason given. The library change stands.</summary>
    Kept,

    /// <summary>The move itself failed (a locked file, a disk error). Nothing was overwritten; trying again later may work.</summary>
    Failed,
}

/// <param name="Kind">What happened.</param>
/// <param name="Disposition">For <see cref="AssetRefileOutcomeKind.Kept"/>, why the file stayed.</param>
/// <param name="Path">Where the file is now.</param>
/// <param name="Reason">A plain line for the activity history and logs when the file did not move.</param>
public sealed record AssetRefileOutcome(AssetRefileOutcomeKind Kind, AssetRefileDisposition Disposition, string Path, string? Reason);
