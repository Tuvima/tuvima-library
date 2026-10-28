namespace MediaEngine.Application.Services;

/// <summary>One owned file the viewer may see: the work it belongs to, the library it sits in and when it was presented.</summary>
public readonly record struct ViewerCatalogueFile(Guid WorkId, Guid AssetId, DateTimeOffset CreatedAt, string? LibraryId = null);

/// <summary>
/// What one viewer may see of the shared catalogue, after the access service has applied its rules in order: the Vault and its sections,
/// the Shared Library (areas, hidden libraries, Selected profiles), the content limit, the documents-off setting and personal spaces
/// (Vault sections spec D32). A read that returns people, counts or anything derived from the catalogue takes one of these, so it cannot
/// be written without a viewer, and it never builds one itself: only the access service does (a guardrail test enforces that).
/// </summary>
public sealed class ViewerCatalogue
{
    private readonly HashSet<Guid> _assetIds;
    private readonly HashSet<Guid> _workIds;
    private readonly Dictionary<Guid, Guid> _primaryAssetByWork;
    private readonly HashSet<(Guid WorkId, string Library)> _workLibraries;

    /// <summary>A viewer who may see nothing: no active profile, a denied application, or a request with no usable authority.</summary>
    public static readonly ViewerCatalogue Nothing = new([]);

    /// <summary>Internal on purpose: only the access service (and tests of it) builds a catalogue, so a read cannot invent one.</summary>
    internal ViewerCatalogue(IEnumerable<ViewerCatalogueFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var list = files.ToList();
        _assetIds = list.Select(file => file.AssetId).ToHashSet();
        _workIds = list.Select(file => file.WorkId).ToHashSet();
        _workLibraries = list.Select(file => (file.WorkId, Normalize(file.LibraryId))).ToHashSet();
        // The one file the single-row-per-work list keeps for a work: the earliest presented, then the lowest id.
        _primaryAssetByWork = list
            .GroupBy(file => file.WorkId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(file => file.CreatedAt).ThenBy(file => file.AssetId).First().AssetId);
    }

    /// <summary>Every work with at least one file the viewer may see.</summary>
    public IReadOnlySet<Guid> WorkIds => _workIds;

    /// <summary>True when the viewer may see this file.</summary>
    public bool CanSeeAsset(Guid assetId) => _assetIds.Contains(assetId);

    /// <summary>
    /// True when the viewer may see this work as it sits in this library. A work's other files (an audiobook beside the book) count
    /// when the work is visible in the same library, whichever file the lists chose to show.
    /// </summary>
    public bool CanSeeWorkInLibrary(Guid workId, string? libraryId) => _workLibraries.Contains((workId, Normalize(libraryId)));

    /// <summary>True when the viewer may see a credit on this file: the file itself, or its work in its library.</summary>
    public bool CanSeeCredit(Guid workId, Guid assetId, string? libraryId) =>
        CanSeeAsset(assetId) || CanSeeWorkInLibrary(workId, libraryId);

    private static string Normalize(string? libraryId) => (libraryId ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>The file the plain one-row-per-work list shows for each visible work.</summary>
    public IReadOnlyCollection<Guid> PrimaryAssetIds => _primaryAssetByWork.Values;

    /// <summary>The file the one-row-per-work list shows for one work, or none when the viewer cannot see the work.</summary>
    public IReadOnlyCollection<Guid> PrimaryAssetIdsOf(Guid workId) =>
        _primaryAssetByWork.TryGetValue(workId, out var assetId) ? [assetId] : [];
}
