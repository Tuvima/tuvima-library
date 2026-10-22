using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Builds the policy the shared source-file safety gate needs for a file in a View source. A managed View source is a
/// folder Tuvima created beneath the owner's own Personal Space, so its files may be deleted; a linked source is a
/// folder the person already had, so the gate sees it as an as-is folder and refuses everything. The root is always the
/// owner's profile folder (never the whole View root), so a path that leaves that folder is refused by the gate's own
/// containment check. A managed source on writable storage also lets Library write the profile name into a file's own metadata
/// (batched write-back D7); it never lets a file be moved or renamed.
/// </summary>
public static class ViewSourceMutationPolicyFactory
{
    /// <param name="space">The Personal Space the source belongs to; the source must be one of its sources.</param>
    /// <param name="source">The View source row the file is indexed under.</param>
    /// <param name="profileRoot">The owner's profile folder (for a managed source) or the linked folder (for a linked source).</param>
    /// <param name="storageIsWritable">Whether the View storage location allows writes.</param>
    public static FileSourceMutationPolicy Create(
        ViewPersonalSpace space,
        ViewSource source,
        string profileRoot,
        bool storageIsWritable)
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);

        if (source.PersonalSpaceId != space.Id)
        {
            throw new ArgumentException("The source does not belong to the supplied Personal Space.", nameof(source));
        }

        return Build(space.LibraryId, source.Id, source.StorageMode == ViewSourceStorageMode.Managed, profileRoot, storageIsWritable);
    }

    /// <summary>
    /// The same policy from stable ids alone, for a file in a source of the household's Shared library (no Personal Space) or when only
    /// the data store's row is at hand. A managed source is a folder Tuvima created; a linked one is a folder the person already had and
    /// is refused everything.
    /// </summary>
    /// <param name="libraryId">The library the file is indexed in.</param>
    /// <param name="sourceId">The source the file is indexed under.</param>
    /// <param name="managed">True for a managed source, false for a linked one.</param>
    /// <param name="rootPath">The folder the file must stay inside (the owner's profile folder or the Shared folder; never the whole View root).</param>
    /// <param name="storageIsWritable">Whether the View storage location allows writes.</param>
    public static FileSourceMutationPolicy CreateForRoot(
        Guid libraryId,
        Guid sourceId,
        bool managed,
        string rootPath,
        bool storageIsWritable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        return Build(libraryId, sourceId, managed, rootPath, storageIsWritable);
    }

    private static FileSourceMutationPolicy Build(Guid libraryId, Guid sourceId, bool managed, string root, bool storageIsWritable) => new()
    {
        LibraryId = libraryId.ToString("D"),
        SourceId = sourceId.ToString("D"),
        RootPath = root,
        ManagementMode = managed
            ? FileSourceManagementMode.ManagedByTuvima
            : FileSourceManagementMode.ExistingLibrary,
        IsWritable = managed && storageIsWritable,
        AllowMove = false,
        AllowRename = false,
        AllowMetadataWriteback = managed && storageIsWritable,
        AllowDelete = managed,
        AllowAsDestination = false,
        IsMergeJob = false,
    };
}
