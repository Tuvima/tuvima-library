namespace MediaEngine.Storage.Contracts;

/// <summary>
/// Read-only personal-media discovery boundary. Library IDs must come from the
/// trusted View scope resolver; callers cannot discover by supplying arbitrary IDs.
/// </summary>
public interface IViewDiscoveryRepository
{
    ViewPlaceDiscoveryPage QueryPlaces(ViewPlaceDiscoveryQuery query, CancellationToken ct = default);
    ViewAtlasDiscoveryPage QueryAtlas(ViewAtlasDiscoveryQuery query, CancellationToken ct = default) =>
        throw new NotSupportedException("Atlas discovery is not implemented by this repository.");
    ViewPlaceAssetDiscoveryPage QueryPlaceAssets(ViewPlaceAssetDiscoveryQuery query, CancellationToken ct = default) =>
        throw new NotSupportedException("Place Story discovery is not implemented by this repository.");
    ViewPeopleDiscoveryPage QueryPeople(ViewPeopleDiscoveryQuery query, CancellationToken ct = default);
}

public sealed record ViewPlaceDiscoveryQuery(
    IReadOnlyCollection<Guid> AuthorizedLibraryIds,
    int Limit = 50,
    string? Search = null,
    ViewDiscoveryCursor? Cursor = null,
    bool IncludeSharedLibraryAssets = false,
    IReadOnlyCollection<string>? ExcludedContentKinds = null);

public sealed record ViewPeopleDiscoveryQuery(
    IReadOnlyCollection<Guid> AuthorizedLibraryIds,
    int Limit = 100,
    string? Search = null,
    ViewDiscoveryCursor? Cursor = null,
    bool IncludeSharedLibraryAssets = false,
    IReadOnlyCollection<string>? ExcludedContentKinds = null);

public sealed record ViewAtlasDiscoveryQuery(
    IReadOnlyCollection<Guid> AuthorizedLibraryIds,
    string? Search = null,
    int? Year = null,
    string? MediaKind = null,
    int Limit = 50000,
    bool IncludeSharedLibraryAssets = false,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string TimelineResolution = "year",
    bool FavoritesOnly = false,
    IReadOnlyCollection<string>? ExcludedContentKinds = null);

public sealed record ViewPlaceAssetDiscoveryQuery(
    IReadOnlyCollection<Guid> AuthorizedLibraryIds,
    string PlaceKey,
    int Offset = 0,
    int Limit = 250,
    int? Year = null,
    string? MediaKind = null,
    bool IncludeSharedLibraryAssets = false,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    bool FavoritesOnly = false,
    IReadOnlyCollection<string>? ExcludedContentKinds = null);

public sealed record ViewDiscoveryCursor(int AssetCount, string Key);

public sealed record ViewPlaceDiscoveryRow(
    string Key,
    string Name,
    double Latitude,
    double Longitude,
    int AssetCount,
    Guid RepresentativeLibraryId,
    Guid RepresentativeAssetId);

public sealed record ViewAtlasDiscoveryRow(
    string Key,
    string Name,
    double Latitude,
    double Longitude,
    int AssetCount,
    int ImageCount,
    int VideoCount,
    DateTimeOffset EarliestAt,
    DateTimeOffset LatestAt,
    Guid RepresentativeLibraryId,
    Guid RepresentativeAssetId);

public sealed record ViewAtlasTimelineBucketRow(
    DateTimeOffset Start,
    int AssetCount,
    int ImageCount,
    int VideoCount,
    DateTimeOffset? End = null);

public sealed record ViewPersonDiscoveryRow(
    string Key,
    string DisplayName,
    int AssetCount,
    Guid RepresentativeLibraryId,
    Guid RepresentativeAssetId,
    IReadOnlyList<string> AnnotationKinds,
    IReadOnlyList<string> ProvenanceSources,
    bool HasReviewedEvidence);

public sealed record ViewPlaceDiscoveryPage(
    IReadOnlyList<ViewPlaceDiscoveryRow> Items,
    ViewDiscoveryCursor? NextCursor,
    bool HasMore,
    bool HasEligibleData);

public sealed record ViewAtlasDiscoveryPage(
    IReadOnlyList<ViewAtlasDiscoveryRow> Hotspots,
    IReadOnlyList<int> AvailableYears,
    int MappedAssetCount,
    int UnmappedAssetCount,
    bool HasEligibleData,
    IReadOnlyList<ViewAtlasTimelineBucketRow>? Timeline = null,
    DateTimeOffset? EarliestAt = null,
    DateTimeOffset? LatestAt = null,
    int ImageCount = 0,
    int VideoCount = 0);

public sealed record ViewPlaceAssetDiscoveryPage(
    string PlaceName,
    IReadOnlyList<Guid> AssetIds,
    int Total,
    bool HasMore);

public sealed record ViewPeopleDiscoveryPage(
    IReadOnlyList<ViewPersonDiscoveryRow> Items,
    ViewDiscoveryCursor? NextCursor,
    bool HasMore,
    bool HasEligibleData);
