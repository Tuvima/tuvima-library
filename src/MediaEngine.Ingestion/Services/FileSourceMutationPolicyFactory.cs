using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Converts the authoritative configuration model into the immutable snapshot
/// consumed by the mutation gate. A folder is in one of two states, so the policy is
/// simple: a managed folder may be moved within, renamed, tagged and cleaned up;
/// an as-is folder allows nothing and is never a destination.
/// </summary>
public static class FileSourceMutationPolicyFactory
{
    /// <param name="allowDelete">The caller explicitly asks for deletion (duplicate removal, empty-folder clean-up).</param>
    /// <param name="isMergeTarget">The folder is the target of a merge the person started, so it may receive files even though it is not the destination.</param>
    /// <param name="isMergeJob">The policy is held by the folder merge job itself, which may act on originals its own ledger lists. Nothing else sets this.</param>
    public static FileSourceMutationPolicy Create(
        LibraryFolderEntry library,
        LibrarySourceEntry source,
        bool allowDelete = false,
        bool isMergeTarget = false,
        bool isMergeJob = false)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(source);

        bool belongsToLibrary = library.Sources.Any(candidate =>
            string.Equals(candidate.Id, source.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Path, source.Path, StringComparison.OrdinalIgnoreCase));
        if (!belongsToLibrary)
        {
            throw new ArgumentException("The source is not part of the supplied library.", nameof(source));
        }

        return CreateCore(
            library.Id,
            source.Id,
            source.Path,
            IsManaged(source.ManagementMode),
            source.IsWritable,
            isDestination: string.Equals(library.PrimaryDestination?.Id, source.Id, StringComparison.OrdinalIgnoreCase),
            isMergeTarget,
            LibraryFileHandlingResolver.ResolveWriteTags(library.WriteTags, source.WriteTagsOverride),
            allowDelete,
            isMergeJob,
            source.ScanEnabled);
    }

    public static FileSourceMutationPolicy Create(
        LibraryFolderConfig library,
        LibrarySourceConfig source,
        bool allowDelete = false,
        bool isMergeTarget = false,
        bool isMergeJob = false)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(source);

        bool belongsToLibrary = library.Sources.Any(candidate =>
            string.Equals(candidate.Id, source.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Path, source.Path, StringComparison.OrdinalIgnoreCase));
        if (!belongsToLibrary)
        {
            throw new ArgumentException("The source is not part of the supplied library.", nameof(source));
        }

        return CreateCore(
            library.Id,
            source.Id,
            source.Path,
            IsManaged(source.ManagementMode),
            source.IsWritable,
            isDestination: string.Equals(library.PrimaryDestination?.Id, source.Id, StringComparison.OrdinalIgnoreCase),
            isMergeTarget,
            LibraryFileHandlingResolver.ResolveWriteTags(library.OrganizationPolicy.WriteTags, source.FileHandlingOverride?.WriteTags),
            allowDelete,
            isMergeJob,
            source.ScanEnabled);
    }

    private static bool IsManaged(string managementMode) => string.Equals(
        managementMode,
        LibrarySourceManagementModes.ManagedByTuvima,
        StringComparison.OrdinalIgnoreCase);

    private static FileSourceMutationPolicy CreateCore(
        string libraryId,
        string sourceId,
        string sourcePath,
        bool managed,
        bool writable,
        bool isDestination,
        bool isMergeTarget,
        bool writeTags,
        bool allowDelete,
        bool isMergeJob,
        bool scanEnabled)
        => new()
        {
            LibraryId = libraryId,
            SourceId = sourceId,
            RootPath = sourcePath,
            ManagementMode = managed
                ? FileSourceManagementMode.ManagedByTuvima
                : FileSourceManagementMode.ExistingLibrary,
            IsWritable = writable,
            AllowMove = managed,
            AllowRename = managed,
            AllowMetadataWriteback = managed && writeTags,
            AllowDelete = managed && allowDelete,
            AllowAsDestination = managed && (isDestination || isMergeTarget),
            IsMergeJob = isMergeJob,
            ScanEnabled = scanEnabled,
        };
}
