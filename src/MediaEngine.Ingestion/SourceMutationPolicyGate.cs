using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion;

/// <summary>
/// Applies source ownership, writability, containment, and action-specific
/// policy before a filesystem mutation is planned or executed.
/// </summary>
public sealed class SourceMutationPolicyGate : ISourceMutationPolicyGate
{
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

        if (request.Source.ManagementMode == FileSourceManagementMode.ExistingLibrary)
        {
            return SourceMutationDecision.Deny("As-is folders are read-only: they cannot be changed, tagged, cleaned up or used as a destination.");
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

        return permitted
            ? SourceMutationDecision.Permit()
            : SourceMutationDecision.Deny($"The source policy does not permit {request.Mutation}.");
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
