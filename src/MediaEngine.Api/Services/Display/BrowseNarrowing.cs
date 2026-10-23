using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Display;

/// <summary>
/// The one rule for narrowing a catalogue read to a single library and to the Unmatched filter (Library setup spec D54, D64), shared by
/// the browse read and the group reads behind the browse tabs. It looks only at (work, library) rows the read service has already
/// authorized (grants, lane feature, content limits, Vault), so a library the profile cannot see is simply an empty result and nothing
/// says whether it exists.
/// </summary>
internal static class BrowseNarrowing
{
    /// <summary>
    /// The works that have a file the profile may see in the library asked for (any library when none is) and, when asked, a file that is
    /// Unmatched (open or kept, Library setup spec D64, D65) in that same library. Only the works in view are looked up.
    /// </summary>
    public static async Task<HashSet<Guid>> WorksInAsync(
        IDisplayProjectionReadService read,
        IUnmatchedAssetRepository? unmatched,
        Guid? libraryId,
        bool unmatchedOnly,
        CancellationToken ct)
    {
        var library = libraryId?.ToString("D");
        // The Unmatched filter is the one place Unmatched works are still in the rows; every other read leaves them out.
        var rows = unmatchedOnly
            ? await read.LoadWorkLibraryRowsIncludingUnmatchedAsync(ct).ConfigureAwait(false)
            : await read.LoadWorkLibraryRowsAsync(ct).ConfigureAwait(false);
        var pairs = rows
            .Where(row => library is null || string.Equals(row.LibraryId, library, StringComparison.OrdinalIgnoreCase))
            .Select(row => (row.WorkId, Library: (row.LibraryId ?? string.Empty).ToUpperInvariant()))
            .Distinct()
            .ToList();
        if (!unmatchedOnly)
        {
            return pairs.Select(pair => pair.WorkId).ToHashSet();
        }

        if (unmatched is null || pairs.Count == 0)
        {
            return [];
        }

        var withUnmatched = (await unmatched.ListWorkLibrariesAsync(pairs.Select(pair => pair.WorkId).Distinct().ToList(), ct).ConfigureAwait(false))
            .Select(file => (file.WorkId, Library: file.LibraryId))
            .ToHashSet();
        return pairs.Where(withUnmatched.Contains).Select(pair => pair.WorkId).ToHashSet();
    }

    /// <summary>
    /// The works the request may see, narrowed by the library and the Unmatched filter when either is asked for; the plain visible set
    /// when neither is. For the group reads, which start from the visible work ids.
    /// </summary>
    public static async Task<IReadOnlySet<Guid>> VisibleWorkIdsAsync(
        IDisplayProjectionReadService read,
        IUnmatchedAssetRepository? unmatched,
        Guid? libraryId,
        bool unmatchedOnly,
        CancellationToken ct)
    {
        var visible = unmatchedOnly
            ? await read.LoadVisibleWorkIdsIncludingUnmatchedAsync(ct).ConfigureAwait(false)
            : await read.LoadVisibleWorkIdsAsync(ct).ConfigureAwait(false);
        if (libraryId is null && !unmatchedOnly)
        {
            return visible;
        }

        var qualifying = await WorksInAsync(read, unmatched, libraryId, unmatchedOnly, ct).ConfigureAwait(false);
        return visible.Where(qualifying.Contains).ToHashSet();
    }
}
