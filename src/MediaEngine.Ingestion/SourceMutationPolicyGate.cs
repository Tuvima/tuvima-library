using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion;

/// <summary>
/// Applies source ownership, writability, containment, and action-specific
/// policy before a filesystem mutation is planned or executed. It also refuses to delete,
/// move or rename a file a folder merge listed as an original that stays in its source folder
/// (a cancelled merge leaves originals beside their copies, and ordinary duplicate handling
/// must not mistake them for spare copies); only the merge job itself may act on those files.
/// </summary>
public sealed class SourceMutationPolicyGate : ISourceMutationPolicyGate
{
    private readonly IFolderOrganiseLogRepository? _mergeLedger;

    /// <param name="mergeLedger">The merge ledger. Without it the merge-original rule cannot be checked; the running Engine always supplies it.</param>
    public SourceMutationPolicyGate(IFolderOrganiseLogRepository? mergeLedger = null)
    {
        _mergeLedger = mergeLedger;
    }

    public SourceMutationDecision Evaluate(SourceMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);

        if (string.IsNullOrWhiteSpace(request.Source.LibraryId))
        {
            return SourceMutationDecision.Deny("The source has no library identity.");
        }

        if (string.IsNullOrWhiteSpace(request.Source.SourceId))
        {
            return SourceMutationDecision.Deny("The source has no stable identity.");
        }

        if (!PathSafety.TryNormalizeRoot(request.Source.RootPath, out var root, out var rootError))
        {
            return SourceMutationDecision.Deny(rootError!);
        }

        if (!PathSafety.TryNormalizePath(request.Path, out var path, out var pathError))
        {
            return SourceMutationDecision.Deny(pathError!);
        }

        if (!PathSafety.IsContainedBy(path!, root!))
        {
            return SourceMutationDecision.Deny("The requested path is outside the configured source root.");
        }

        if (!request.Source.ScanEnabled && !request.Source.IsMergeJob)
        {
            return SourceMutationDecision.Deny("Scanning is paused for this folder, so its files are left as they are.");
        }

        if (request.Source.ManagementMode == FileSourceManagementMode.ExistingLibrary)
        {
            return SourceMutationDecision.Deny("Read-only folders cannot be changed: they cannot be changed, tagged, cleaned up or used as a destination.");
        }

        if (request.Source.ManagementMode != FileSourceManagementMode.ManagedByTuvima)
        {
            return SourceMutationDecision.Deny("The source management mode is not supported.");
        }

        if (!request.Source.IsWritable)
        {
            return SourceMutationDecision.Deny("The managed source is not writable.");
        }

        bool permitted = request.Mutation switch
        {
            SourceMutationKind.Move => request.Source.AllowMove,
            SourceMutationKind.Rename => request.Source.AllowRename,
            SourceMutationKind.MetadataWriteback => request.Source.AllowMetadataWriteback,
            SourceMutationKind.Delete => request.Source.AllowDelete,
            SourceMutationKind.UseAsDestination => request.Source.AllowAsDestination,
            _ => false,
        };

        if (!permitted)
        {
            return SourceMutationDecision.Deny($"The source policy does not permit {request.Mutation}.");
        }

        return MergeOriginalDenial(request, path!) ?? SourceMutationDecision.Permit();
    }

    /// <summary>
    /// A file the merge ledger lists as an original that stays in its source folder is never deleted, moved or renamed by
    /// anything except the merge job itself, whatever state the folder is in now. If the ledger cannot be read, the answer is
    /// no: a refused delete costs nothing, a wrong one loses a person's file.
    /// </summary>
    private SourceMutationDecision? MergeOriginalDenial(SourceMutationRequest request, string path)
    {
        if (_mergeLedger is null
            || request.Source.IsMergeJob
            || request.Mutation is not (SourceMutationKind.Delete or SourceMutationKind.Move or SourceMutationKind.Rename))
        {
            return null;
        }

        bool isMergeOriginal;
        try
        {
            isMergeOriginal = _mergeLedger.IsMergeOriginal(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: when the merge ledger cannot be read the file is treated as an original that must be kept.
            return SourceMutationDecision.Deny(
                $"This file could not be checked against the folder merge log, so it was left where it is ({ex.Message}).");
        }

        return isMergeOriginal
            ? SourceMutationDecision.Deny(
                "This file is the original of a folder merge, so it is kept as it is until you merge the folder again or remove it yourself.")
            : null;
    }
}

internal static class PathSafety
{
    internal static StringComparer Comparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    internal static bool TryNormalizeRoot(string? value, out string? normalized, out string? error)
    {
        if (!TryNormalizePath(value, out normalized, out error))
        {
            return false;
        }

        normalized = Path.TrimEndingDirectorySeparator(normalized!);
        return true;
    }

    internal static bool TryNormalizePath(string? value, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "A filesystem path is required.";
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(value);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"The filesystem path is invalid: {ex.Message}";
            return false;
        }
    }

    internal static bool IsContainedBy(string path, string root)
    {
        if (Comparer.Equals(path, root))
        {
            return true;
        }

        string prefix = root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    internal static bool Overlaps(string firstRoot, string secondRoot)
        => IsContainedBy(firstRoot, secondRoot) || IsContainedBy(secondRoot, firstRoot);
}
