namespace MediaEngine.Domain.PersonalMedia;

public enum ViewScopeKind
{
    Shared,
    Mine,
    Profile,
}

public enum ViewTimelineDensity
{
    Compact,
    Comfortable,
    Relaxed,
}

public sealed record ViewProfilePolicy(
    Guid ProfileId,
    bool ViewEnabled,
    bool AccessSharedLibrary,
    bool SubmitToSharedLibrary,
    bool ShareGalleries,
    DateTimeOffset? UpdatedAt)
{
    /// <summary>
    /// What a profile gets before anyone changes it: everyone in a household can open and contribute to the
    /// household's Shared Library. A child (restricted) profile cannot send items until a household administrator
    /// allows it. Reviewing contributions is not a per-profile setting; household administrators do it.
    /// </summary>
    public static ViewProfilePolicy Default(Guid profileId, bool restricted = false, bool inHousehold = true) =>
        new(profileId, true, inHousehold, inHousehold && !restricted, false, null);
}

public sealed record ViewProfilePreferences(
    Guid ProfileId,
    ViewScopeKind? LastScopeKind,
    Guid? LastScopeProfileId,
    ViewTimelineDensity TimelineDensity,
    DateTimeOffset? UpdatedAt,
    bool ViewerInfoOpen = true)
{
    public static ViewProfilePreferences Default(Guid profileId) =>
        new(profileId, null, null, ViewTimelineDensity.Comfortable, null, true);
}

public sealed record ViewPersonalSpace(
    Guid Id,
    Guid OwnerProfileId,
    Guid LibraryId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string StorageLabel = "");

public enum ViewSourceType
{
    Folder,
    BrowserUpload,
    DeviceImport,
    MobileBackup,
    Network,
    Other,
}

public enum ViewSourceStorageMode
{
    Managed,
    Linked,
}

public sealed record ViewSource(
    Guid Id,
    Guid PersonalSpaceId,
    ViewSourceType SourceType,
    string Name,
    string? SourceKey,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ViewSourceStorageMode StorageMode = ViewSourceStorageMode.Managed,
    string? RelativePath = null,
    string? ExternalPath = null,
    bool IncludeSubdirectories = true,
    bool Enabled = true,
    bool IncludeInTimeline = false);

public enum ViewDeviceBackupState
{
    Unknown,
    Idle,
    BackingUp,
    Complete,
    Error,
}

public sealed record ViewDevice(
    Guid Id,
    Guid PersonalSpaceId,
    Guid? SourceId,
    string ClientDeviceId,
    string Name,
    string? Make,
    string? Model,
    DateTimeOffset? LastBackupAt,
    ViewDeviceBackupState BackupState,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public enum ViewGalleryKind
{
    Manual,
    Smart,
}

public enum ViewGallerySharePermission
{
    View,
    Contribute,
}

public sealed record ViewGallery(
    Guid Id,
    Guid OwnerProfileId,
    Guid PersonalSpaceId,
    string Name,
    string? Description,
    ViewGalleryKind Kind,
    string? SmartRuleJson,
    Guid? CoverItemId,
    int SortOrder,
    int ItemCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? SoundtrackPlaylistId = null);

public sealed record CreateViewGalleryCommand(
    Guid OwnerProfileId,
    Guid PersonalSpaceId,
    string Name,
    ViewGalleryKind Kind,
    string? Description = null,
    string? SmartRuleJson = null,
    Guid? CoverItemId = null,
    int SortOrder = 0,
    Guid? SoundtrackPlaylistId = null);

public sealed record UpdateViewGalleryCommand(
    Guid GalleryId,
    string Name,
    string? Description,
    ViewGalleryKind Kind,
    string? SmartRuleJson,
    Guid? CoverItemId,
    int SortOrder,
    Guid? SoundtrackPlaylistId = null);

public sealed record ViewGalleryItem(
    Guid GalleryId,
    Guid ItemId,
    int Position,
    DateTimeOffset AddedAt);

public sealed record ViewGalleryItemPage(
    IReadOnlyList<ViewGalleryItem> Items,
    int? NextPosition,
    Guid? NextItemId,
    bool HasMore);

public sealed record AddViewGalleryItemsResult(int Added, int AlreadyPresent);

public sealed record ViewGalleryShare(
    Guid GalleryId,
    Guid ProfileId,
    ViewGallerySharePermission Permission,
    DateTimeOffset SharedAt);
