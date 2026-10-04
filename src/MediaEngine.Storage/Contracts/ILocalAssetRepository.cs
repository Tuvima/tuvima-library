using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Contracts;

/// <summary>
/// Persistence boundary for provider-isolated personal-library assets.
/// SQLite operations are synchronous; only serialized write-lock acquisition
/// is asynchronous through <see cref="IDatabaseConnection.ExecuteWriteAsync{T}"/>.
/// </summary>
public interface ILocalAssetRepository
{
    LocalAssetPageDto Query(LocalAssetQuery query, CancellationToken ct = default);
    LocalAssetTimelinePage QueryTimeline(LocalAssetTimelineQuery query, CancellationToken ct = default);
    IReadOnlyList<LocalAssetTimelineBucket> QueryTimelineIndex(LocalAssetTimelineQuery query, CancellationToken ct = default);
    LocalAssetDto? Find(Guid itemId, CancellationToken ct = default);
    LocalAssetContentLocation? ResolveContent(
        Guid itemId,
        string role = LocalAssetFileRoles.Primary,
        CancellationToken ct = default);

    Task<LocalAssetUpsertResult> UpsertAsync(
        LocalAssetRegistration registration,
        CancellationToken ct = default);
    Task<bool> SetFlagsAsync(
        Guid itemId,
        bool? favorite,
        bool? hidden,
        CancellationToken ct = default);
    Task<bool> SetLifecycleStateAsync(
        Guid itemId,
        LocalAssetLifecycleState state,
        CancellationToken ct = default);
    Task ReplaceTagsAsync(
        Guid itemId,
        IReadOnlyCollection<string> tags,
        CancellationToken ct = default);
    IReadOnlyList<string> GetTagSuggestions(Guid profileId, string? search, int limit, CancellationToken ct = default);
    Task ReplacePeopleAsync(Guid itemId, IReadOnlyCollection<string> people, CancellationToken ct = default);
    Task<bool> UpdateDescriptionAsync(Guid itemId, string? description, CancellationToken ct = default);
    Task<bool> UpdateCapturedAtAsync(Guid itemId, DateTimeOffset? capturedAt, bool resetToEmbedded, CancellationToken ct = default);
    Task<bool> UpdateLocationAsync(Guid itemId, LocalAssetLocationUpdate update, CancellationToken ct = default);
    Task<Guid> AddAnnotationAsync(
        Guid itemId,
        LocalAssetAnnotation annotation,
        CancellationToken ct = default);
}

public sealed record LocalAssetQuery(
    Guid LibraryId,
    int Offset = 0,
    int Limit = 50,
    string? Search = null,
    IReadOnlyCollection<string>? MediaKinds = null,
    bool FavoritesOnly = false,
    bool IncludeHidden = false,
    bool HiddenOnly = false,
    Guid? GalleryId = null,
    LocalAssetLifecycleFilter Lifecycle = LocalAssetLifecycleFilter.Active);

public sealed record LocalAssetTimelineQuery(
    IReadOnlyCollection<Guid> AuthorizedLibraryIds,
    int Limit = 100,
    DateTimeOffset? BeforeEffectiveAt = null,
    Guid? BeforeItemId = null,
    string? Search = null,
    IReadOnlyCollection<string>? MediaKinds = null,
    bool FavoritesOnly = false,
    bool IncludeHidden = false,
    bool HiddenOnly = false,
    Guid? GalleryId = null,
    LocalAssetLifecycleFilter Lifecycle = LocalAssetLifecycleFilter.Active,
    CollectionRuleDefinition? SmartRule = null,
    bool TimelineEligibleOnly = false,
    bool IncludeSharedLibraryAssets = false,
    DateTimeOffset? AnchorBefore = null,
    bool WithoutLocation = false,
    string? PersonKey = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    bool SortByAddedAt = false,
    DateTimeOffset? AddedBefore = null,
    string? AddedAfterKey = null);

public sealed record LocalAssetTimelineCursor(DateTimeOffset EffectiveAt, Guid ItemId);

public sealed record LocalAssetTimelinePage(
    IReadOnlyList<LocalAssetDto> Items,
    LocalAssetTimelineCursor? NextCursor,
    bool HasMore);

public sealed record LocalAssetTimelineBucket(
    int Year,
    int Month,
    int AssetCount,
    DateTimeOffset EarliestAt,
    DateTimeOffset LatestAt);

public enum LocalAssetLifecycleFilter
{
    Active,
    Archived,
    Trashed,
    All,
}

public enum LocalAssetLifecycleState
{
    Active,
    Archived,
    Trashed,
}

public sealed record LocalAssetRegistration(
    Guid LibraryId,
    Guid? PersonalSpaceId,
    Guid? OwnerProfileId,
    string MediaKind,
    string? Title,
    DateTimeOffset? CapturedAt,
    IReadOnlyList<LocalAssetFileRegistration> Files,
    int? Width = null,
    int? Height = null,
    double? DurationSeconds = null,
    int? PageCount = null,
    string? DeviceMake = null,
    string? DeviceModel = null,
    double? Latitude = null,
    double? Longitude = null,
    string? LocationName = null,
    string? DocumentText = null,
    string? MetadataJson = null,
    string? LensModel = null,
    string? ExposureTime = null,
    double? Aperture = null,
    int? Iso = null,
    double? FocalLengthMm = null,
    string? VideoCodec = null,
    double? FrameRate = null,
    IReadOnlyCollection<string>? Tags = null,
    Guid? ExistingItemId = null,
    string ScopeKind = LocalAssetScopeKinds.Personal);

public sealed record LocalAssetLocationUpdate(
    double? Latitude,
    double? Longitude,
    string? Name = null,
    string? City = null,
    string? Region = null,
    string? Country = null,
    string? CountryCode = null,
    bool ResetToEmbedded = false);

public static class LocalAssetScopeKinds
{
    public const string Personal = "personal";
    public const string Shared = "shared";
}

public sealed record LocalAssetFileRegistration(
    string FilePath,
    string ContentHash,
    string FileName,
    string MimeType,
    long ByteSize,
    DateTimeOffset ModifiedAt,
    string Role = LocalAssetFileRoles.Primary,
    string? DerivativeKind = null,
    Guid? SourceId = null,
    Guid? DeviceId = null);

public sealed record LocalAssetUpsertResult(
    Guid ItemId,
    bool ItemAdded,
    int FilesAdded,
    int SourcesAdded);

public sealed record LocalAssetContentLocation(
    Guid ItemId,
    Guid FileId,
    Guid LibraryId,
    Guid? OwnerProfileId,
    Guid? SourceId,
    Guid? DeviceId,
    string FilePath,
    string MimeType,
    long ByteSize,
    string ContentHash,
    string Role,
    string? DerivativeKind);

public sealed record LocalAssetAnnotation(
    string Kind,
    string Value,
    string Source,
    double? Confidence = null,
    string? ModelName = null,
    string? ModelVersion = null,
    string? ProvenanceJson = null,
    DateTimeOffset? ReviewedAt = null);

public static class LocalAssetMediaKinds
{
    public const string Image = "image";
    public const string Video = "video";
    public const string Document = "document";
    public const string Audio = "audio";
    public const string Other = "other";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Image,
        Video,
        Document,
        Audio,
        Other,
    };
}

public static class LocalAssetFileRoles
{
    public const string Primary = "primary";
    public const string Original = "original";
    public const string LivePhotoVideo = "live_photo_video";
    public const string Raw = "raw";
    public const string Jpeg = "jpeg";
    public const string Sidecar = "sidecar";
    public const string AudioCompanion = "audio_companion";
    public const string Derivative = "derivative";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Primary,
        Original,
        LivePhotoVideo,
        Raw,
        Jpeg,
        Sidecar,
        AudioCompanion,
        Derivative,
    };
}
