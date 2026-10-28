using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;

namespace MediaEngine.Ingestion.Organise;

/// <summary>
/// Everything the organising job needs to know about the folder it is converting, read from the saved folder settings at
/// one moment. The job reads it again every few seconds, so switching the folder back to As-is stops the work.
/// </summary>
public sealed class FolderConversionContext
{
    private FolderConversionContext(
        Guid operationId,
        Guid sourceId,
        LibraryFolderConfig library,
        LibrarySourceConfig source,
        string sourceRoot,
        string duplicatePolicy)
    {
        OperationId = operationId;
        SourceId = sourceId;
        Library = library;
        Source = source;
        SourceRoot = sourceRoot;
        DuplicatePolicy = duplicatePolicy;
        LibraryEntry = LibraryFolderEntryFactory.Create(library);
        Policy = FileSourceMutationPolicyFactory.Create(library, source);
        DeletePolicy = FileSourceMutationPolicyFactory.Create(library, source, allowDelete: true);
    }

    public Guid OperationId { get; }

    public Guid SourceId { get; }

    public LibraryFolderConfig Library { get; }

    public LibrarySourceConfig Source { get; }

    /// <summary>The folder being converted, as a full path without a trailing separator.</summary>
    public string SourceRoot { get; }

    /// <summary>The global "same file twice" rule (<see cref="LibraryDuplicatePolicies"/>).</summary>
    public string DuplicatePolicy { get; }

    public LibraryFolderEntry LibraryEntry { get; }

    /// <summary>What the folder's settings allow: moving, renaming and tagging, never deleting.</summary>
    public FileSourceMutationPolicy Policy { get; }

    /// <summary>The same, but also allowing the deletions a conversion is permitted to make (exact duplicates, empty folders).</summary>
    public FileSourceMutationPolicy DeletePolicy { get; }

    public bool WriteTags => Library.OrganizationPolicy.WriteTags;

    public string LibraryId => Library.Id;

    /// <summary>
    /// Builds the context, or returns <see langword="null"/> (with the reason) when the folder is gone, not a stable
    /// source, or no longer one Tuvima manages.
    /// </summary>
    public static FolderConversionContext? TryCreate(
        LibrariesConfiguration libraries,
        Guid operationId,
        Guid sourceId,
        out FolderConversionUnavailable reason)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        reason = FolderConversionUnavailable.None;

        foreach (var library in libraries.Libraries)
        {
            var source = library.Sources.FirstOrDefault(candidate =>
                Guid.TryParse(candidate.Id, out var id) && id == sourceId);
            if (source is null)
            {
                continue;
            }

            if (!source.IsManaged || !source.IsWritable)
            {
                reason = FolderConversionUnavailable.NotManaged;
                return null;
            }

            string root;
            try
            {
                root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A saved path that is not a valid path cannot be organised.
                _ = ex;
                reason = FolderConversionUnavailable.FolderMissing;
                return null;
            }

            return new FolderConversionContext(
                operationId, sourceId, library, source, root,
                library.ResolveDuplicatePolicy(source, libraries.FileHandling.DuplicatePolicy));
        }

        reason = FolderConversionUnavailable.SourceRemoved;
        return null;
    }

    /// <summary>
    /// The context for a preview: the folder is still As-is, so it is looked at as if it were already Managed. Nothing is
    /// saved and nothing on disk is touched.
    /// </summary>
    public static FolderConversionContext? TryCreateAsIfManaged(
        LibrariesConfiguration libraries,
        Guid sourceId,
        out FolderConversionUnavailable reason)
    {
        ArgumentNullException.ThrowIfNull(libraries);

        foreach (var library in libraries.Libraries)
        {
            var source = library.Sources.FirstOrDefault(candidate =>
                Guid.TryParse(candidate.Id, out var id) && id == sourceId);
            if (source is null)
            {
                continue;
            }

            var copy = System.Text.Json.JsonSerializer.Deserialize<LibraryFolderConfig>(
                System.Text.Json.JsonSerializer.Serialize(library))!;
            var managed = copy.Sources.First(candidate => string.Equals(candidate.Id, source.Id, StringComparison.OrdinalIgnoreCase));
            managed.ManagementMode = LibrarySourceManagementModes.ManagedByTuvima;
            managed.AccessMode = LibrarySourceAccessModes.Writable;
            var clone = new LibrariesConfiguration
            {
                Libraries = [copy],
                FileHandling = libraries.FileHandling,
            };
            return TryCreate(clone, Guid.Empty, sourceId, out reason);
        }

        reason = FolderConversionUnavailable.SourceRemoved;
        return null;
    }
}

public enum FolderConversionUnavailable
{
    None,
    SourceRemoved,
    NotManaged,
    FolderMissing,
}

public enum OrganiseDecisionKind
{
    /// <summary>The file already has the standard name.</summary>
    AlreadyCorrect,

    /// <summary>The file moves to <see cref="OrganiseDecision.Target"/>, which is free.</summary>
    Move,

    /// <summary>An identical file already has the standard name. The duplicate rule decides.</summary>
    Duplicate,

    /// <summary>A different file already has the standard name. The file moves to a numbered name and a person is asked.</summary>
    Collision,

    /// <summary>The file needs a person to confirm its identity first, so it stays where it is.</summary>
    NeedsReview,

    /// <summary>The file is not there, or is not ready to be named yet. Normal scans handle it later.</summary>
    Skip,

    /// <summary>The folder's settings do not allow this move.</summary>
    Denied,
}

/// <param name="Target">Where the file would go (the free numbered name for a collision).</param>
/// <param name="Occupant">The existing file in the way, when there is one.</param>
public sealed record OrganiseDecision(
    OrganiseDecisionKind Kind,
    string? Target = null,
    string? Reason = null,
    string? Occupant = null);

/// <summary>The standard name of a file, or why it cannot be named yet (<see cref="Blocked"/>).</summary>
public sealed record StandardName(string? RelativePath, OrganiseDecision? Blocked);

/// <summary>What applying a decision did. <see cref="Locked"/> means the file could not be touched yet and should be retried.</summary>
public sealed record ConversionOutcome(string Outcome, string? NewPath = null, string? Detail = null, bool Locked = false);

/// <summary>What a media file that is not in the library yet turns out to be.</summary>
/// <param name="Duplicate">True when it is an exact copy of a file the library already holds.</param>
/// <param name="KeptPath">The copy the library holds, when <paramref name="Duplicate"/> is true.</param>
/// <param name="Locked">True when the file could not be read yet (it is in use).</param>
public sealed record UncataloguedClassification(bool Duplicate, string? KeptPath, string Reason, bool Locked = false);
