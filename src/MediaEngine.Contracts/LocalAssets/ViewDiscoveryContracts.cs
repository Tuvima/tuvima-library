using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.LocalAssets;

/// <summary>
/// Truthful availability state for a View discovery surface. Automatic processing is
/// reported separately from indexed evidence so stored user/reviewed data never implies
/// that an AI processor is installed or running.
/// </summary>
public sealed record ViewDiscoveryCapabilityDto(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("has_indexed_data")] bool HasIndexedData,
    [property: JsonPropertyName("automatic_processing_available")] bool AutomaticProcessingAvailable,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("evidence_kinds")] IReadOnlyList<string> EvidenceKinds);

public sealed record ViewPlaceDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("latitude")] double Latitude,
    [property: JsonPropertyName("longitude")] double Longitude,
    [property: JsonPropertyName("asset_count")] int AssetCount,
    [property: JsonPropertyName("representative_library_id")] Guid RepresentativeLibraryId,
    [property: JsonPropertyName("representative_asset_id")] Guid RepresentativeAssetId);

public sealed record ViewPlacesPageDto(
    [property: JsonPropertyName("items")] IReadOnlyList<ViewPlaceDto> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("capability")] ViewDiscoveryCapabilityDto Capability);

/// <summary>
/// An authorization-scoped geographic aggregate used by the View Atlas. A hotspot
/// represents a real stored place group; individual asset coordinates are returned
/// only when the group has naturally resolved to one asset.
/// </summary>
public sealed record ViewAtlasHotspotDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("latitude")] double Latitude,
    [property: JsonPropertyName("longitude")] double Longitude,
    [property: JsonPropertyName("asset_count")] int AssetCount,
    [property: JsonPropertyName("image_count")] int ImageCount,
    [property: JsonPropertyName("video_count")] int VideoCount,
    [property: JsonPropertyName("earliest_at")] DateTimeOffset EarliestAt,
    [property: JsonPropertyName("latest_at")] DateTimeOffset LatestAt,
    [property: JsonPropertyName("representative_library_id")] Guid RepresentativeLibraryId,
    [property: JsonPropertyName("representative_asset_id")] Guid RepresentativeAssetId);

public sealed record ViewAtlasTimelineBucketDto(
    [property: JsonPropertyName("start")] DateTimeOffset Start,
    [property: JsonPropertyName("asset_count")] int AssetCount,
    [property: JsonPropertyName("image_count")] int ImageCount,
    [property: JsonPropertyName("video_count")] int VideoCount,
    [property: JsonPropertyName("end")] DateTimeOffset? End = null);

public sealed record ViewAtlasPageDto(
    [property: JsonPropertyName("hotspots")] IReadOnlyList<ViewAtlasHotspotDto> Hotspots,
    [property: JsonPropertyName("available_years")] IReadOnlyList<int> AvailableYears,
    [property: JsonPropertyName("mapped_asset_count")] int MappedAssetCount,
    [property: JsonPropertyName("unmapped_asset_count")] int UnmappedAssetCount,
    [property: JsonPropertyName("capability")] ViewDiscoveryCapabilityDto Capability,
    [property: JsonPropertyName("timeline")] IReadOnlyList<ViewAtlasTimelineBucketDto>? Timeline = null,
    [property: JsonPropertyName("earliest_at")] DateTimeOffset? EarliestAt = null,
    [property: JsonPropertyName("latest_at")] DateTimeOffset? LatestAt = null,
    [property: JsonPropertyName("image_count")] int ImageCount = 0,
    [property: JsonPropertyName("video_count")] int VideoCount = 0);

public sealed record ViewPlaceMediaPageDto(
    [property: JsonPropertyName("place_key")] string PlaceKey,
    [property: JsonPropertyName("place_name")] string PlaceName,
    [property: JsonPropertyName("items")] IReadOnlyList<LocalAssetDto> Items,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("has_more")] bool HasMore);

public sealed record ViewPersonDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("asset_count")] int AssetCount,
    [property: JsonPropertyName("representative_library_id")] Guid RepresentativeLibraryId,
    [property: JsonPropertyName("representative_asset_id")] Guid RepresentativeAssetId,
    [property: JsonPropertyName("annotation_kinds")] IReadOnlyList<string> AnnotationKinds,
    [property: JsonPropertyName("provenance_sources")] IReadOnlyList<string> ProvenanceSources,
    [property: JsonPropertyName("has_reviewed_evidence")] bool HasReviewedEvidence);

public sealed record ViewPeoplePageDto(
    [property: JsonPropertyName("items")] IReadOnlyList<ViewPersonDto> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("has_more")] bool HasMore,
    [property: JsonPropertyName("capability")] ViewDiscoveryCapabilityDto Capability);

public static class ViewDiscoveryCapabilityStates
{
    public const string Available = "available";
    public const string Empty = "empty";
    public const string NoMatches = "no_matches";
}

/// <summary>One search-bar suggestion: its kind ("person", "place", "year", "camera" or "tag"), the text to search for, and how many photos and videos it matches.</summary>
public sealed record ViewSuggestionDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("count")] int Count);

/// <summary>The suggestions for what the person has typed so far, over only the photos and videos their View scope may see.</summary>
public sealed record ViewSuggestionsDto(
    [property: JsonPropertyName("items")] IReadOnlyList<ViewSuggestionDto> Items);

