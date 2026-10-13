using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Services;

/// <summary>
/// Default <see cref="ILibraryFolderResolver"/> backed by the configured
/// <see cref="IngestionOptions.LibraryFolders"/>. Builds a flat
/// <c>(sourcePath, library)</c> index at construction and serves
/// longest-prefix lookups in O(n) per call.
///
/// The resolver is registered as a singleton; <see cref="IOptionsMonitor{T}"/>
/// rebuilds the index automatically when <c>config/libraries.json</c> changes
/// at runtime.
///
/// Spec: side-by-side-with-Plex plan §F.
/// </summary>
public sealed class LibraryFolderResolver : ILibraryFolderResolver
{
    private readonly IOptionsMonitor<IngestionOptions> _options;

    public LibraryFolderResolver(IOptionsMonitor<IngestionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc/>
    public LibraryFolderEntry? ResolveById(string libraryId)
    {
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            return null;
        }

        return _options.CurrentValue.LibraryFolders.FirstOrDefault(entry =>
            string.Equals(entry.Id, libraryId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc/>
    public LibraryFolderEntry? ResolveForPath(string absolutePath)
        => ResolveSourceForPath(absolutePath)?.Library;

    /// <inheritdoc/>
    public ResolvedLibrarySource? ResolveSourceForPath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return null;
        }

        string normalized = NormalizePath(absolutePath);

        LibraryFolderEntry? bestEntry = null;
        LibrarySourceEntry? bestSource = null;
        int bestLength = -1;

        foreach (var entry in _options.CurrentValue.LibraryFolders)
        {
            foreach (var source in entry.Sources)
            {
                var sourcePath = source.Path;
                if (string.IsNullOrWhiteSpace(sourcePath) || source.IsMergedAway)
                {
                    continue;
                }

                string candidate = NormalizePath(sourcePath);
                if (!IsUnderPrefix(normalized, candidate))
                {
                    continue;
                }

                if (candidate.Length > bestLength)
                {
                    bestEntry = entry;
                    bestSource = source;
                    bestLength = candidate.Length;
                }
            }
        }

        return bestEntry is null || bestSource is null
            ? null
            : new ResolvedLibrarySource(bestEntry, bestSource);
    }

    /// <inheritdoc/>
    public string? ResolveSourcePath(string absolutePath)
        => ResolveSourceForPath(absolutePath)?.Source.Path;

    /// <summary>
    /// Validates that no two configured source paths overlap. Two paths overlap
    /// when one is a prefix of the other and both belong to *different* libraries.
    /// Throws <see cref="InvalidOperationException"/> when an overlap is found
    /// so misconfiguration is loud at startup, not silent at first file.
    /// </summary>
    public static void ValidateNoOverlap(IReadOnlyList<LibraryFolderEntry> libraries)
    {
        ArgumentNullException.ThrowIfNull(libraries);

        // Flatten to (libraryIndex, normalizedPath, originalPath) triples.
        var flat = new List<(int LibraryIndex, string Normalized, string Original)>();
        for (int i = 0; i < libraries.Count; i++)
        {
            foreach (var sourcePath in libraries[i].EffectiveSourcePaths)
            {
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    continue;
                }

                flat.Add((i, NormalizePath(sourcePath), sourcePath));
            }
        }

        for (int i = 0; i < flat.Count; i++)
        {
            for (int j = i + 1; j < flat.Count; j++)
            {
                if (flat[i].LibraryIndex == flat[j].LibraryIndex)
                {
                    continue;
                }

                if (IsUnderPrefix(flat[i].Normalized, flat[j].Normalized) ||
                    IsUnderPrefix(flat[j].Normalized, flat[i].Normalized))
                {
                    throw new InvalidOperationException(
                        $"Library source paths overlap between two libraries: " +
                        $"'{flat[i].Original}' and '{flat[j].Original}'. " +
                        "A single path can belong to only one library. " +
                        "Either remove the overlap or merge the libraries.");
                }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string NormalizePath(string path)
    {
        // Collapse separators, strip trailing slash, fold case for case-insensitive
        // filesystems. We don't call Path.GetFullPath because we don't want
        // disk access during resolve — paths come from config and are assumed
        // already absolute.
        string p = path.Replace('\\', '/').TrimEnd('/');
        return p.ToLowerInvariant();
    }

    private static bool IsUnderPrefix(string normalizedChild, string normalizedPrefix)
    {
        if (normalizedChild.Length < normalizedPrefix.Length)
        {
            return false;
        }

        if (!normalizedChild.StartsWith(normalizedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // Exact match counts as "under".
        if (normalizedChild.Length == normalizedPrefix.Length)
        {
            return true;
        }

        // Otherwise the next character must be a separator so we don't
        // false-match "/Movies" against "/Movies2".
        return normalizedChild[normalizedPrefix.Length] == '/';
    }
}
