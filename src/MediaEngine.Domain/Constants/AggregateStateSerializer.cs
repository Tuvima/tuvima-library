using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Constants;

/// <summary>
/// Converts typed aggregate state to the stable values stored in SQLite and
/// exposed by existing API contracts.
/// </summary>
public static class AggregateStateSerializer
{
    public static string ToStorageValue(this WikidataLinkStatus value) => value switch
    {
        WikidataLinkStatus.Pending => "pending",
        WikidataLinkStatus.Confirmed => "confirmed",
        WikidataLinkStatus.Skipped => "skipped",
        WikidataLinkStatus.Missing => "missing",
        WikidataLinkStatus.Manual => "manual",
        WikidataLinkStatus.ProviderOnly => "provider_only",
        WikidataLinkStatus.AutoAligned => "auto_aligned",
        WikidataLinkStatus.UserConfirmed => "user_confirmed",
        WikidataLinkStatus.UserReplaced => "user_replaced",
        WikidataLinkStatus.UserRejected => "user_rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static WikidataLinkStatus ParseWikidataLinkStatus(string value) =>
        Normalize(value) switch
        {
            "pending" => WikidataLinkStatus.Pending,
            "confirmed" => WikidataLinkStatus.Confirmed,
            "skipped" => WikidataLinkStatus.Skipped,
            "missing" => WikidataLinkStatus.Missing,
            "manual" => WikidataLinkStatus.Manual,
            "provider_only" => WikidataLinkStatus.ProviderOnly,
            "auto_aligned" => WikidataLinkStatus.AutoAligned,
            "user_confirmed" => WikidataLinkStatus.UserConfirmed,
            "user_replaced" => WikidataLinkStatus.UserReplaced,
            "user_rejected" => WikidataLinkStatus.UserRejected,
            _ => throw Unknown(nameof(WikidataLinkStatus), value),
        };

    public static string ToStorageValue(this WorkMatchLevel value) => value switch
    {
        WorkMatchLevel.Work => "work",
        WorkMatchLevel.Edition => "edition",
        WorkMatchLevel.RetailOnly => "retail_only",
        WorkMatchLevel.Unlinked => "unlinked",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static WorkMatchLevel ParseWorkMatchLevel(string value) =>
        Normalize(value) switch
        {
            "work" => WorkMatchLevel.Work,
            "edition" => WorkMatchLevel.Edition,
            "retail_only" => WorkMatchLevel.RetailOnly,
            "unlinked" => WorkMatchLevel.Unlinked,
            _ => throw Unknown(nameof(WorkMatchLevel), value),
        };

    public static string ToStorageValue(this WorkKind value) => value switch
    {
        WorkKind.Standalone => "standalone",
        WorkKind.Parent => "parent",
        WorkKind.Child => "child",
        WorkKind.Catalog => "catalog",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static WorkKind ParseWorkKind(string value) =>
        Normalize(value) switch
        {
            "standalone" => WorkKind.Standalone,
            "parent" => WorkKind.Parent,
            "child" => WorkKind.Child,
            "catalog" => WorkKind.Catalog,
            _ => throw Unknown(nameof(WorkKind), value),
        };

    public static string ToStorageValue(this OwnershipStatus value) => value switch
    {
        OwnershipStatus.Owned => "Owned",
        OwnershipStatus.Unowned => "Unowned",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToStorageValue(this CollectionType value) => value switch
    {
        CollectionType.Universe => "Universe",
        CollectionType.Series => "Series",
        CollectionType.ContentGroup => "ContentGroup",
        CollectionType.System => "System",
        CollectionType.Smart => "Smart",
        CollectionType.Playlist => "Playlist",
        CollectionType.PlaylistFolder => "PlaylistFolder",
        CollectionType.Mix => "Mix",
        CollectionType.Genre => "Genre",
        CollectionType.Author => "Author",
        CollectionType.Collection => "Collection",
        CollectionType.Custom => "Custom",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static CollectionType ParseCollectionType(string value) =>
        Normalize(value) switch
        {
            "universe" => CollectionType.Universe,
            "series" => CollectionType.Series,
            "contentgroup" => CollectionType.ContentGroup,
            "system" => CollectionType.System,
            "smart" => CollectionType.Smart,
            "playlist" => CollectionType.Playlist,
            "playlistfolder" => CollectionType.PlaylistFolder,
            "mix" => CollectionType.Mix,
            "genre" => CollectionType.Genre,
            "author" => CollectionType.Author,
            "collection" => CollectionType.Collection,
            "custom" => CollectionType.Custom,
            _ => throw Unknown(nameof(CollectionType), value),
        };

    public static string ToStorageValue(this ContainerMembershipMode value) => value.ToString();
    public static ContainerMembershipMode ParseContainerMembershipMode(string? value) =>
        Enum.TryParse<ContainerMembershipMode>(value, true, out var parsed) ? parsed : ContainerMembershipMode.Smart;

    public static string ToStorageValue(this CollectionPrimaryArea value) => value.ToString();
    public static CollectionPrimaryArea ParseCollectionPrimaryArea(string? value) =>
        Enum.TryParse<CollectionPrimaryArea>(value, true, out var parsed) ? parsed : CollectionPrimaryArea.Mixed;

    public static string ToStorageValue(this ContainerOwnerKind value) => value.ToString();
    public static ContainerOwnerKind ParseContainerOwnerKind(string? value) =>
        Enum.TryParse<ContainerOwnerKind>(value, true, out var parsed) ? parsed : ContainerOwnerKind.Library;

    public static string ToStorageValue(this ContainerAudience value) => value.ToString();
    public static ContainerAudience ParseContainerAudience(string? value) =>
        Enum.TryParse<ContainerAudience>(value, true, out var parsed) ? parsed : ContainerAudience.Everyone;

    public static string ToStorageValue(this CollectionScope value) => value switch
    {
        CollectionScope.Library => "library",
        CollectionScope.User => "user",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static CollectionScope ParseCollectionScope(string value) =>
        Normalize(value) switch
        {
            "library" => CollectionScope.Library,
            "user" => CollectionScope.User,
            _ => throw Unknown(nameof(CollectionScope), value),
        };

    public static string ToStorageValue(this CollectionResolution value) => value switch
    {
        CollectionResolution.Query => "query",
        CollectionResolution.Materialized => "materialized",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static CollectionResolution ParseCollectionResolution(string value) =>
        Normalize(value) switch
        {
            "query" => CollectionResolution.Query,
            "materialized" => CollectionResolution.Materialized,
            _ => throw Unknown(nameof(CollectionResolution), value),
        };

    public static string ToStorageValue(this CollectionMatchMode value) => value switch
    {
        CollectionMatchMode.All => "all",
        CollectionMatchMode.Any => "any",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static CollectionMatchMode ParseCollectionMatchMode(string value) =>
        Normalize(value) switch
        {
            "all" => CollectionMatchMode.All,
            "any" => CollectionMatchMode.Any,
            _ => throw Unknown(nameof(CollectionMatchMode), value),
        };

    public static string ToStorageValue(this CollectionSortDirection value) => value switch
    {
        CollectionSortDirection.Asc => "asc",
        CollectionSortDirection.Desc => "desc",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static CollectionSortDirection ParseCollectionSortDirection(string value) =>
        Normalize(value) switch
        {
            "asc" => CollectionSortDirection.Asc,
            "desc" => CollectionSortDirection.Desc,
            _ => throw Unknown(nameof(CollectionSortDirection), value),
        };

    public static string ToStorageValue(this CollectionUniverseStatus value) => value switch
    {
        CollectionUniverseStatus.Unknown => "Unknown",
        CollectionUniverseStatus.None => "None",
        CollectionUniverseStatus.Limited => "Limited",
        CollectionUniverseStatus.Rich => "Rich",
        CollectionUniverseStatus.Complete => "Complete",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static CollectionUniverseStatus ParseCollectionUniverseStatus(string value) =>
        Normalize(value) switch
        {
            "unknown" => CollectionUniverseStatus.Unknown,
            "none" => CollectionUniverseStatus.None,
            "limited" => CollectionUniverseStatus.Limited,
            "rich" => CollectionUniverseStatus.Rich,
            "complete" => CollectionUniverseStatus.Complete,
            _ => throw Unknown(nameof(CollectionUniverseStatus), value),
        };

    private static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant();
    }

    private static InvalidOperationException Unknown(string stateType, string value) =>
        new($"Unsupported persisted {stateType} value '{value}'.");
}
