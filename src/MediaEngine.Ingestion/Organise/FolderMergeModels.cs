using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;

namespace MediaEngine.Ingestion.Organise;

/// <summary>Why a merge cannot be started (or can no longer carry on).</summary>
public enum FolderMergeProblem
{
    None,
    SourceRemoved,
    TargetRemoved,
    NotSameLibrary,
    SameFolder,
    NestedFolders,
    TargetNotManaged,
    SourceIsNewMediaFolder,
    SourceBusy,
    MoveNeedsManagedSource,
    InvalidMode,
    NotActive,
}

/// <summary>The outcome of checking a merge: fine, or a plain-English reason it cannot go ahead.</summary>
public sealed record FolderMergeCheck(FolderMergeProblem Problem, string? Message = null)
{
    public static FolderMergeCheck Ok { get; } = new(FolderMergeProblem.None);

    public bool IsOk => Problem == FolderMergeProblem.None;
}

/// <summary>
/// Everything the merge job needs to know about the merge it is running, read from the saved folder settings at one moment.
/// The job reads it again every few seconds, so cancelling the merge, removing a folder or switching the target to As-is stops
/// the work. The source is never written in Copy mode; in Move mode it is only ever deleted from, one verified file at a time.
/// </summary>
public sealed class FolderMergeContext
{
    /// <summary>Where files that cannot be named yet are put inside the target, so a person can sort them in Review.</summary>
    public const string HoldingFolderName = "From merge";

    private FolderMergeContext(
        Guid operationId,
        LibraryFolderConfig library,
        LibrarySourceConfig source,
        LibrarySourceConfig target,
        string sourceRoot,
        string targetRoot,
        bool isMove,
        string duplicatePolicy)
    {
        OperationId = operationId;
        Library = library;
        Source = source;
        Target = target;
        SourceRoot = sourceRoot;
        TargetRoot = targetRoot;
        IsMove = isMove;
        DuplicatePolicy = duplicatePolicy;
        LibraryEntry = LibraryFolderEntryFactory.Create(library);
        SourcePolicy = FileSourceMutationPolicyFactory.Create(library, source, allowDelete: isMove);
        TargetPolicy = FileSourceMutationPolicyFactory.Create(library, target, allowDelete: false, isMergeTarget: true);
    }

    public Guid OperationId { get; }

    public LibraryFolderConfig Library { get; }

    public LibrarySourceConfig Source { get; }

    public LibrarySourceConfig Target { get; }

    public Guid SourceId => Guid.Parse(Source.Id);

    public Guid TargetId => Guid.Parse(Target.Id);

    /// <summary>The folder being merged, as a full path without a trailing separator.</summary>
    public string SourceRoot { get; }

    /// <summary>The managed folder the files go into, as a full path without a trailing separator.</summary>
    public string TargetRoot { get; }

    /// <summary>True when each original is removed once its copy has been checked; false when the source is never written.</summary>
    public bool IsMove { get; }

    /// <summary>The global "same file twice" rule (<see cref="LibraryDuplicatePolicies"/>).</summary>
    public string DuplicatePolicy { get; }

    /// <summary>The library as the naming rules see it. Source and target are in the same library, so one set of rules names both.</summary>
    public LibraryFolderEntry LibraryEntry { get; }

    /// <summary>What the source's settings allow: reading always; moving and removing only for a Move out of a managed folder.</summary>
    public FileSourceMutationPolicy SourcePolicy { get; }

    /// <summary>What the target's settings allow: receiving files, because the person chose it as the merge target.</summary>
    public FileSourceMutationPolicy TargetPolicy { get; }

    public bool WriteTags => Library.OrganizationPolicy.WriteTags;

    public string LibraryId => Library.Id;

    /// <summary>Where a file that cannot be named yet is put: the target's From merge folder, keeping its place inside the source.</summary>
    public string HoldingPathFor(string sourceFile)
    {
        var folderName = Path.GetFileName(SourceRoot);
        if (string.IsNullOrWhiteSpace(folderName))
        {
            folderName = "Source";
        }

        return Path.GetFullPath(Path.Combine(
            TargetRoot, HoldingFolderName, folderName, Path.GetRelativePath(SourceRoot, sourceFile)));
    }

    /// <summary>
    /// Builds the context for a merge that is under way, from the source folder's saved merge details. Returns null (with the
    /// reason) when the merge was cancelled, a folder is gone, or the target is no longer a folder Tuvima manages.
    /// </summary>
    public static FolderMergeContext? TryCreateForJob(
        LibrariesConfiguration libraries, Guid operationId, Guid sourceId, out FolderMergeCheck check)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        var source = Find(libraries, sourceId, out var library);
        if (source is null || library is null)
        {
            check = new FolderMergeCheck(FolderMergeProblem.SourceRemoved, "The folder is no longer part of your libraries.");
            return null;
        }

        if (source.MergeState != LibrarySourceMergeStates.Merging
            || !string.Equals(source.MergeOperationId, operationId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            check = new FolderMergeCheck(FolderMergeProblem.NotActive, "This merge is no longer running.");
            return null;
        }

        return Build(libraries, library, source, source.MergeTargetSourceId, source.MergeMode, operationId, requireIdle: false, out check);
    }

    /// <summary>
    /// Builds the context for starting or previewing a merge the person asked for. Nothing is saved. Returns null (with the
    /// reason) when the merge cannot go ahead.
    /// </summary>
    public static FolderMergeContext? TryCreateForPlan(
        LibrariesConfiguration libraries, Guid sourceId, string? targetSourceId, string? mode, out FolderMergeCheck check)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        var source = Find(libraries, sourceId, out var library);
        if (source is null || library is null)
        {
            check = new FolderMergeCheck(FolderMergeProblem.SourceRemoved, "This folder is not part of your libraries.");
            return null;
        }

        return Build(libraries, library, source, targetSourceId, mode, Guid.Empty, requireIdle: true, out check);
    }

    private static FolderMergeContext? Build(
        LibrariesConfiguration libraries,
        LibraryFolderConfig library,
        LibrarySourceConfig source,
        string? targetSourceId,
        string? mode,
        Guid operationId,
        bool requireIdle,
        out FolderMergeCheck check)
    {
        if (!LibraryMergeModes.IsValid(mode))
        {
            check = new FolderMergeCheck(FolderMergeProblem.InvalidMode, "Choose Copy or Move.");
            return null;
        }

        var target = library.Sources.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, targetSourceId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            check = libraries.Libraries.SelectMany(item => item.Sources).Any(candidate =>
                string.Equals(candidate.Id, targetSourceId, StringComparison.OrdinalIgnoreCase))
                ? new FolderMergeCheck(FolderMergeProblem.NotSameLibrary, "Choose a managed folder of the same kind of media.")
                : new FolderMergeCheck(FolderMergeProblem.TargetRemoved, "The folder you are merging into is no longer part of your libraries.");
            return null;
        }

        if (string.Equals(target.Id, source.Id, StringComparison.OrdinalIgnoreCase))
        {
            check = new FolderMergeCheck(FolderMergeProblem.SameFolder, "A folder cannot be merged into itself.");
            return null;
        }

        // Only a folder Tuvima manages can receive files: an As-is folder is never written to, whatever else is chosen.
        if (!target.IsManaged || !target.IsWritable)
        {
            check = new FolderMergeCheck(
                FolderMergeProblem.TargetNotManaged,
                "Files can only be merged into a folder Tuvima manages. An As-is folder is never written to.");
            return null;
        }

        var isMove = string.Equals(mode, LibraryMergeModes.Move, StringComparison.Ordinal);
        if (isMove && (!source.IsManaged || !source.IsWritable))
        {
            check = new FolderMergeCheck(
                FolderMergeProblem.MoveNeedsManagedSource,
                "Move is only available for a folder Tuvima manages. This folder is used as-is, so its files can only be copied.");
            return null;
        }

        string sourceRoot;
        string targetRoot;
        try
        {
            sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path));
            targetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A saved path that is not a valid path cannot be merged.
            _ = ex;
            check = new FolderMergeCheck(FolderMergeProblem.SourceRemoved, "A folder's saved path is not valid.");
            return null;
        }

        if (PathSafety.Overlaps(sourceRoot, targetRoot))
        {
            check = new FolderMergeCheck(
                FolderMergeProblem.NestedFolders,
                "These two folders are inside each other, so merging would copy files onto themselves.");
            return null;
        }

        if (requireIdle)
        {
            if (source.MergeState != LibrarySourceMergeStates.None || source.ConversionState == LibrarySourceConversionStates.Converting)
            {
                check = new FolderMergeCheck(
                    FolderMergeProblem.SourceBusy,
                    source.MergeState == LibrarySourceMergeStates.Merged
                        ? "This folder has already been merged."
                        : "This folder is being organised or merged right now. Wait for that to finish first.");
                return null;
            }

            if (string.Equals(library.PrimaryDestinationSourceId, source.Id, StringComparison.OrdinalIgnoreCase))
            {
                check = new FolderMergeCheck(
                    FolderMergeProblem.SourceIsNewMediaFolder,
                    "This folder is where new media goes. Make another folder the place for new media first.");
                return null;
            }
        }

        check = FolderMergeCheck.Ok;
        return new FolderMergeContext(operationId, library, source, target, sourceRoot, targetRoot, isMove, libraries.FileHandling.DuplicatePolicy);
    }

    private static LibrarySourceConfig? Find(LibrariesConfiguration libraries, Guid sourceId, out LibraryFolderConfig? library)
    {
        foreach (var candidate in libraries.Libraries)
        {
            var source = candidate.Sources.FirstOrDefault(item => Guid.TryParse(item.Id, out var id) && id == sourceId);
            if (source is not null)
            {
                library = candidate;
                return source;
            }
        }

        library = null;
        return null;
    }
}
