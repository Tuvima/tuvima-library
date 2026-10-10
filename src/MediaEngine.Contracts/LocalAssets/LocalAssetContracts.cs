using System.Text.Json.Serialization;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Contracts.LocalAssets;

/// <summary>
/// A library-scoped local item. Unlike a catalogue work, this identity is made
/// from files owned or referenced by a personal library and is never enriched
/// through an external identity provider.
/// </summary>
public sealed record LocalAssetDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("library_id")] Guid LibraryId,
    [property: JsonPropertyName("personal_space_id")] Guid? PersonalSpaceId,
    [property: JsonPropertyName("owner_profile_id")] Guid? OwnerProfileId,
    [property: JsonPropertyName("media_kind")] string MediaKind,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("mime_type")] string MimeType,
    [property: JsonPropertyName("captured_at")] DateTimeOffset? CapturedAt,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("width")] int? Width,
    [property: JsonPropertyName("height")] int? Height,
    [property: JsonPropertyName("duration_seconds")] double? DurationSeconds,
    [property: JsonPropertyName("page_count")] int? PageCount,
    [property: JsonPropertyName("device_make")] string? DeviceMake,
    [property: JsonPropertyName("device_model")] string? DeviceModel,
    [property: JsonPropertyName("latitude")] double? Latitude,
    [property: JsonPropertyName("longitude")] double? Longitude,
    [property: JsonPropertyName("location_name")] string? LocationName,
    [property: JsonPropertyName("favorite")] bool Favorite,
    [property: JsonPropertyName("hidden")] bool Hidden,
    [property: JsonPropertyName("archived_at")] DateTimeOffset? ArchivedAt,
    [property: JsonPropertyName("trashed_at")] DateTimeOffset? TrashedAt,
    [property: JsonPropertyName("source_count")] int SourceCount,
    [property: JsonPropertyName("files")] IReadOnlyList<LocalAssetFileDto> Files,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("thumbnail_url")] string ThumbnailUrl,
    [property: JsonPropertyName("content_url")] string ContentUrl,
    [property: JsonPropertyName("scope_kind")] string ScopeKind = "personal",
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("lens_model")] string? LensModel = null,
    [property: JsonPropertyName("exposure_time")] string? ExposureTime = null,
    [property: JsonPropertyName("aperture")] double? Aperture = null,
    [property: JsonPropertyName("iso")] int? Iso = null,
    [property: JsonPropertyName("focal_length_mm")] double? FocalLengthMm = null,
    [property: JsonPropertyName("video_codec")] string? VideoCodec = null,
    [property: JsonPropertyName("frame_rate")] double? FrameRate = null,
    [property: JsonPropertyName("location_city")] string? LocationCity = null,
    [property: JsonPropertyName("location_region")] string? LocationRegion = null,
    [property: JsonPropertyName("location_country")] string? LocationCountry = null,
    [property: JsonPropertyName("location_country_code")] string? LocationCountryCode = null,
    [property: JsonPropertyName("location_source")] string? LocationSource = null,
    [property: JsonPropertyName("location_user_override")] bool LocationUserOverride = false,
    [property: JsonPropertyName("embedded_latitude")] double? EmbeddedLatitude = null,
    [property: JsonPropertyName("embedded_longitude")] double? EmbeddedLongitude = null,
    [property: JsonPropertyName("embedded_captured_at")] DateTimeOffset? EmbeddedCapturedAt = null,
    [property: JsonPropertyName("captured_at_user_override")] bool CapturedAtUserOverride = false,
    [property: JsonPropertyName("people")] IReadOnlyList<string>? People = null);

public sealed record LocalAssetFileDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("derivative_kind")] string? DerivativeKind,
    [property: JsonPropertyName("mime_type")] string MimeType,
    [property: JsonPropertyName("byte_size")] long ByteSize,
    [property: JsonPropertyName("source_count")] int SourceCount,
    [property: JsonPropertyName("file_name")] string? FileName = null);

public sealed record LocalAssetPageDto(
    [property: JsonPropertyName("items")] IReadOnlyList<LocalAssetDto> Items,
    [property: JsonPropertyName("offset")] int Offset,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("has_more")] bool HasMore);

public sealed record ViewAssetTimelinePageDto(
    [property: JsonPropertyName("items")] IReadOnlyList<LocalAssetDto> Items,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("has_more")] bool HasMore);

public sealed record ViewTimelineBucketDto(
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month")] int Month,
    [property: JsonPropertyName("asset_count")] int AssetCount,
    [property: JsonPropertyName("earliest_at")] DateTimeOffset EarliestAt,
    [property: JsonPropertyName("latest_at")] DateTimeOffset LatestAt);

public sealed record ViewTimelineIndexDto(
    [property: JsonPropertyName("buckets")] IReadOnlyList<ViewTimelineBucketDto> Buckets);

public sealed record ViewResolvedScopeDto(
    [property: JsonPropertyName("kind")] ViewScopeKind Kind,
    [property: JsonPropertyName("profile_id")] Guid? ProfileId,
    [property: JsonPropertyName("was_fallback")] bool WasFallback);

public sealed record ViewScopeOptionDto(
    [property: JsonPropertyName("kind")] ViewScopeKind Kind,
    [property: JsonPropertyName("profile_id")] Guid? ProfileId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("avatar_color")] string? AvatarColor,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl);

public sealed record ViewScopeResolutionDto(
    [property: JsonPropertyName("scope")] ViewResolvedScopeDto Scope,
    [property: JsonPropertyName("available_scopes")] IReadOnlyList<ViewScopeOptionDto> AvailableScopes,
    [property: JsonPropertyName("can_browse_other_people")] bool CanBrowseOtherPeople = false);

public sealed record ViewPreferencesDto(
    [property: JsonPropertyName("profile_id")] Guid ProfileId,
    [property: JsonPropertyName("scope")] ViewScopeKind? Scope,
    [property: JsonPropertyName("scope_profile_id")] Guid? ScopeProfileId,
    [property: JsonPropertyName("timeline_density")] ViewTimelineDensity TimelineDensity,
    [property: JsonPropertyName("viewer_info_open")] bool ViewerInfoOpen,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt);

public sealed record ViewUploadResponseDto(
    [property: JsonPropertyName("item_id")] Guid ItemId,
    [property: JsonPropertyName("item_added")] bool ItemAdded,
    [property: JsonPropertyName("files_added")] int FilesAdded,
    [property: JsonPropertyName("sources_added")] int SourcesAdded);

public sealed record ViewPreferencesRequest(
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("scope_profile_id")] Guid? ScopeProfileId,
    [property: JsonPropertyName("timeline_density")] ViewTimelineDensity TimelineDensity,
    [property: JsonPropertyName("viewer_info_open")] bool ViewerInfoOpen = true);

public sealed record LibraryCapacityLocationDto(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("total_bytes")] long? TotalBytes,
    [property: JsonPropertyName("available_bytes")] long? AvailableBytes,
    [property: JsonPropertyName("is_available")] bool IsAvailable);

public sealed record LibraryCapacityDto(
    [property: JsonPropertyName("area")] string Area,
    [property: JsonPropertyName("locations")] IReadOnlyList<LibraryCapacityLocationDto> Locations,
    [property: JsonPropertyName("captured_at")] DateTimeOffset CapturedAt);

public sealed record UpdateLocalAssetDescriptionRequest(
    [property: JsonPropertyName("description")] string? Description);

public sealed record UpdateLocalAssetTagsRequest(
    [property: JsonPropertyName("tags")] IReadOnlyCollection<string> Tags);

public sealed record UpdateLocalAssetLocationRequest(
    [property: JsonPropertyName("latitude")] double? Latitude,
    [property: JsonPropertyName("longitude")] double? Longitude,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("city")] string? City = null,
    [property: JsonPropertyName("region")] string? Region = null,
    [property: JsonPropertyName("country")] string? Country = null,
    [property: JsonPropertyName("country_code")] string? CountryCode = null,
    [property: JsonPropertyName("reset_to_embedded")] bool ResetToEmbedded = false);

public sealed record UpdateLocalAssetCapturedAtRequest(
    [property: JsonPropertyName("captured_at")] DateTimeOffset? CapturedAt,
    [property: JsonPropertyName("reset_to_embedded")] bool ResetToEmbedded = false);

public sealed record UpdateLocalAssetPeopleRequest(
    [property: JsonPropertyName("people")] IReadOnlyCollection<string> People);

public sealed record ViewGalleryRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] ViewGalleryKind Kind,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("smart_rule_json")] string? SmartRuleJson = null,
    [property: JsonPropertyName("cover_item_id")] Guid? CoverItemId = null,
    [property: JsonPropertyName("sort_order")] int SortOrder = 0,
    [property: JsonPropertyName("soundtrack_playlist_id")] Guid? SoundtrackPlaylistId = null);

public sealed record ViewGalleryListResponse(
    [property: JsonPropertyName("owned")] IReadOnlyList<ViewGalleryDto> Owned,
    [property: JsonPropertyName("shared_with_you")] IReadOnlyList<ViewGalleryDto> SharedWithYou);

public sealed record ViewGalleryDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("owner_profile_id")] Guid OwnerProfileId,
    [property: JsonPropertyName("personal_space_id")] Guid PersonalSpaceId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("kind")] ViewGalleryKind Kind,
    [property: JsonPropertyName("smart_rule_json")] string? SmartRuleJson,
    [property: JsonPropertyName("cover_item_id")] Guid? CoverItemId,
    [property: JsonPropertyName("sort_order")] int SortOrder,
    [property: JsonPropertyName("item_count")] int ItemCount,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("soundtrack_playlist_id")] Guid? SoundtrackPlaylistId = null);

public sealed record ViewGalleryItemDto(
    [property: JsonPropertyName("gallery_id")] Guid GalleryId,
    [property: JsonPropertyName("item_id")] Guid ItemId,
    [property: JsonPropertyName("position")] int Position,
    [property: JsonPropertyName("added_at")] DateTimeOffset AddedAt);

public sealed record ViewGalleryItemPageDto(
    [property: JsonPropertyName("items")] IReadOnlyList<ViewGalleryItemDto> Items,
    [property: JsonPropertyName("next_position")] int? NextPosition,
    [property: JsonPropertyName("next_item_id")] Guid? NextItemId,
    [property: JsonPropertyName("has_more")] bool HasMore);

public sealed record AddViewGalleryItemsResponseDto(
    [property: JsonPropertyName("added")] int Added,
    [property: JsonPropertyName("already_present")] int AlreadyPresent);

public sealed record ViewGalleryShareDto(
    [property: JsonPropertyName("gallery_id")] Guid GalleryId,
    [property: JsonPropertyName("profile_id")] Guid ProfileId,
    [property: JsonPropertyName("permission")] ViewGallerySharePermission Permission,
    [property: JsonPropertyName("shared_at")] DateTimeOffset SharedAt);

/// <summary>
/// Count-free profile identity that an authorized Gallery owner may select as
/// a share recipient. Personal Space, library, asset, and Gallery facts are
/// intentionally absent.
/// </summary>
public sealed record ViewGalleryShareTargetDto(
    [property: JsonPropertyName("profile_id")] Guid ProfileId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("avatar_color")] string AvatarColor,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl);

public sealed record ViewGalleryItemsRequest(
    [property: JsonPropertyName("item_ids")] IReadOnlyCollection<Guid> ItemIds);

public sealed record ViewGalleryPositionRequest(
    [property: JsonPropertyName("position")] int Position);

public sealed record ViewGalleryShareRequest(
    [property: JsonPropertyName("profile_id")] Guid ProfileId,
    [property: JsonPropertyName("permission")] ViewGallerySharePermission Permission);

public sealed record ViewGallerySharesRequest(
    [property: JsonPropertyName("shares")] IReadOnlyCollection<ViewGalleryShareRequest> Shares);

public sealed record ViewItemsRemovedResponse(
    [property: JsonPropertyName("removed")] int Removed);

/// <summary>
/// Administrator-safe review of one profile's View Personal Space. Storage
/// bridge identifiers, source keys, client device identifiers, and file paths
/// are intentionally excluded from this contract.
/// </summary>
public sealed record ViewPersonalSpaceAdminReviewDto(
    [property: JsonPropertyName("profile_id")] Guid ProfileId,
    [property: JsonPropertyName("personal_space")] ViewPersonalSpaceAdminDto? PersonalSpace,
    [property: JsonPropertyName("sources")] IReadOnlyList<ViewSourceAdminDto> Sources,
    [property: JsonPropertyName("devices")] IReadOnlyList<ViewDeviceAdminDto> Devices);

public sealed record ViewPersonalSpaceAdminDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("root_path")] string RootPath,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record ViewSourceAdminDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("storage_mode")] string StorageMode,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("include_subdirectories")] bool IncludeSubdirectories,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("last_activity_at")] DateTimeOffset? LastActivityAt,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("include_in_timeline")] bool IncludeInTimeline = false);

public sealed record CreateViewSourceRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("storage_mode")] string StorageMode,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("include_subdirectories")] bool IncludeSubdirectories = true,
    [property: JsonPropertyName("include_in_timeline")] bool IncludeInTimeline = false);

public sealed record UpdateViewSourceRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("include_in_timeline")] bool IncludeInTimeline = false);

public sealed record ViewDeviceAdminDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("source_id")] Guid? SourceId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("make")] string? Make,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("last_backup_at")] DateTimeOffset? LastBackupAt,
    [property: JsonPropertyName("backup_state")] string BackupState,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

public sealed record LocalAssetScanResultDto(
    [property: JsonPropertyName("library_id")] Guid LibraryId,
    [property: JsonPropertyName("files_seen")] int FilesSeen,
    [property: JsonPropertyName("items_added")] int ItemsAdded,
    [property: JsonPropertyName("files_added")] int FilesAdded,
    [property: JsonPropertyName("sources_added")] int SourcesAdded,
    [property: JsonPropertyName("duplicates_found")] int DuplicatesFound,
    [property: JsonPropertyName("errors")] int Errors);

public sealed record SetLocalAssetFlagRequest(
    [property: JsonPropertyName("value")] bool Value);
