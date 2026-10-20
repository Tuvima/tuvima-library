using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Builds the policy the shared source-file safety gate needs for a file in a View source. A managed View source is a
/// folder Tuvima created beneath the owner's own Personal Space, so its files may be deleted; a linked source is a
/// folder the person already had, so the gate sees it as an as-is folder and refuses everything. The root is always the
/// owner's profile folder (never the whole View root), so a path that leaves that folder is refused by the gate's own
/// containment check.
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

        var managed = source.StorageMode == ViewSourceStorageMode.Managed;
        return new FileSourceMutationPolicy
        {
            LibraryId = space.LibraryId.ToString("D"),
            SourceId = source.Id.ToString("D"),
            RootPath = profileRoot,
            ManagementMode = managed
                ? FileSourceManagementMode.ManagedByTuvima
                : FileSourceManagementMode.ExistingLibrary,
            IsWritable = managed && storageIsWritable,
            AllowMove = false,
            AllowRename = false,
            AllowMetadataWriteback = false,
            AllowDelete = managed,
            AllowAsDestination = false,
            IsMergeJob = false,
        };
    }
}
