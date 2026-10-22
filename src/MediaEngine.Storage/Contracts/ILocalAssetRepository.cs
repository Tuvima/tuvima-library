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

    /// <summary>The items that exist among <paramref name="itemIds"/>, in the order given (unknown ids are left out). The read-only lookup is one query for the whole list.</summary>
    IReadOnlyList<LocalAssetDto> FindMany(IReadOnlyList<Guid> itemIds, CancellationToken ct = default);

    /// <summary>True when the item is a document (PDF, Office or text file) rather than a photo, video or audio.</summary>
    bool IsDocument(Guid itemId, CancellationToken ct = default);

    /// <summary>
    /// The View kind (<c>photos</c>, <c>personal_videos</c> or <c>documents</c>) of an item, or null when the item does not
    /// exist or has none (an audio or other file). The permission checks gate on this, not on the file type.
    /// </summary>
    string? GetContentKind(Guid itemId, CancellationToken ct = default);

    /// <summary>
    /// The profiles that added items in the query's scope, with how many items each added, most first. It reads the same
    /// rows the timeline reads (same scope, visibility and blocked-kind rules); profiles that no longer exist are left out.
    /// </summary>
    IReadOnlyList<LocalAssetAddedByCount> QueryAddedBy(LocalAssetTimelineQuery query, CancellationToken ct = default);

    /// <summary>The View kind of each id that has one; unknown ids and items without a kind are absent. Read in chunks.</summary>
    IReadOnlyDictionary<Guid, string> GetContentKinds(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    /// <summary>The media kind (<c>image</c>, <c>video</c>, <c>document</c>, ...) of each id that exists; unknown ids are absent. Read in chunks, a few queries for any number of ids.</summary>
    IReadOnlyDictionary<Guid, string> GetMediaKinds(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    /// <summary>
    /// The ids, among <paramref name="itemIds"/>, of items that are read-only: at least one of their files comes from a
    /// <c>linked</c> folder, which Tuvima never moves, renames or deletes. Such an item cannot be moved to Trash.
    /// Read in chunks, a few queries for any number of ids.
    /// </summary>
    IReadOnlySet<Guid> GetReadOnlyItemIds(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);

    /// <summary>
    /// Reads at most <paramref name="maxCharacters"/> characters of an item's extracted document text; the second value
    /// says whether more existed. Returns null when no text is stored.
    /// </summary>
    (string Text, bool Cut)? GetDocumentText(Guid itemId, int maxCharacters, CancellationToken ct = default);
    LocalAssetContentLocation? ResolveContent(
        Guid itemId,
        string role = LocalAssetFileRoles.Primary,
        CancellationToken ct = default);

    Task<LocalAssetUpsertResult> UpsertAsync(
        LocalAssetRegistration registration,
        CancellationToken ct = default);
    /// <summary>
    /// Replaces an item's extracted document text and its <c>text_source</c>, then rebuilds the search document.
    /// Passing no text clears it. Prefer this over <see cref="UpsertAsync"/> for text reads.
    /// </summary>
    Task<bool> SetDocumentTextAsync(
        Guid itemId,
        LocalAssetDocumentTextWrite write,
        CancellationToken ct = default);

    /// <summary>
    /// Lists PDFs and photos whose text has not been read (or was read as none) so text recognition can be
    /// queued for them again. Pages by item ID: pass the last ID of the previous page as <paramref name="afterItemId"/>.
    /// </summary>
    Task<IReadOnlyList<LocalAssetOcrCandidate>> GetOcrCandidatesAsync(
        Guid? afterItemId,
        int limit,
        IReadOnlyCollection<string> imageExtensions,
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
    /// <summary>
    /// Replaces an item's tags by name with no viewer in the picture (ingest, imports and tests). Names that do not
    /// exist yet are added to the global tag list with no creator. A person editing tags goes through
    /// <see cref="IViewCatalogueRepository"/> instead, which checks what they may see.
    /// </summary>
    Task ReplaceTagsAsync(
        Guid itemId,
        IReadOnlyCollection<string> tags,
        CancellationToken ct = default);
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
    string? AddedAfterKey = null,
    LocalAssetDocumentFilter? DocumentFilter = null,
    IReadOnlyCollection<string>? ExcludedContentKinds = null,
    IReadOnlyCollection<Guid>? AddedByProfileIds = null,
    LocalAssetVaultScope? Vault = null);

/// <summary>One profile that added items, and how many (the filter chips on the shared galleries).</summary>
public sealed record LocalAssetAddedByCount(Guid ProfileId, string Name, int ItemCount);

/// <summary>
/// Turns a timeline query into a Vault image query (Videos spec D44). Without it, a query never returns a Vault image, whatever
/// libraries it is given; with it, the query returns only Vault images, so the two worlds cannot mix. <see cref="FolderId"/> and
/// <see cref="GalleryId"/> narrow it to one folder gallery or one hand-made Vault gallery; a Smart Gallery rule on the query is
/// compiled with the Vault's own rule limits and tags.
/// </summary>
public sealed record LocalAssetVaultScope(Guid? FolderId = null, Guid? GalleryId = null);

/// <summary>
/// Organising conditions for document listings. Every listed tag must be present. The date range covers the
/// document's own date, or the day it was added when it has none. Documents with no organising details count as
/// filed.
/// </summary>
public sealed record LocalAssetDocumentFilter(
    IReadOnlyList<Guid>? TagIds = null,
    Guid? CorrespondentId = null,
    Guid? DocumentTypeId = null,
    DateOnly? DateFrom = null,
    DateOnly? DateTo = null,
    string? FilingState = null,
    IReadOnlyList<LocalAssetFieldFilter>? Fields = null,
    string? Sort = null,
    int Offset = 0);

public sealed record LocalAssetFieldFilter(Guid FieldId, string Value);

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
    string ScopeKind = LocalAssetScopeKinds.Personal,
    string? ContentKind = null,
    Guid? AddedByProfileId = null);

public sealed record LocalAssetLocationUpdate(
    double? Latitude,
    double? Longitude,
    string? Name = null,
    string? City = null,
    string? Region = null,
    string? Country = null,
    string? CountryCode = null,
    bool ResetToEmbedded = false);

/// <summary>Where an item's document text came from.</summary>
public static class LocalAssetTextSources
{
    public const string Embedded = "embedded";
    public const string Ocr = "ocr";

    /// <summary>Some pages came from the file's own text layer and others were read by OCR.</summary>
    public const string Mixed = "mixed";

    public const string None = "none";
}

public sealed record LocalAssetDocumentTextWrite(
    string? Text,
    string TextSource,
    bool Truncated = false,
    bool Encrypted = false,
    int? PageCount = null,
    double? OcrAverageConfidence = null,
    int? OcrPages = null);

/// <summary>A file whose text could still be recognised.</summary>
public sealed record LocalAssetOcrCandidate(Guid ItemId, string ContentHash, string FilePath);

public static class LocalAssetScopeKinds
{
    public const string Personal = "personal";
    public const string Shared = "shared";

    /// <summary>
    /// An image of a Vault image library (Videos spec D44). Only a registration uses this name; the item is stored with the
    /// shared kind and no Personal Space, owner or View source, and only in a library listed in <c>vault_image_libraries</c>.
    /// </summary>
    public const string Vault = "vault";
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
