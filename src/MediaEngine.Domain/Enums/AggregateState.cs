namespace MediaEngine.Domain.Enums;

/// <summary>Lifecycle state for a Work's Wikidata identity.</summary>
public enum WikidataLinkStatus
{
    Pending,
    Confirmed,
    Skipped,
    Missing,
    Manual,
    ProviderOnly,
    AutoAligned,
    UserConfirmed,
    UserReplaced,
    UserRejected,
}

/// <summary>Specificity of a Work's resolved external identity.</summary>
public enum WorkMatchLevel
{
    Work,
    Edition,
    RetailOnly,
    Unlinked,
}

/// <summary>Structural purpose of a Collection.</summary>
public enum CollectionType
{
    Universe,
    Series,
    ContentGroup,
    System,
    Smart,
    Playlist,
    PlaylistFolder,
    Mix,
    Genre,
    Author,
    Collection,
    Custom,

    /// <summary>A parent collection created by a pack (Collections and lists spec D14): it holds no titles itself, its children are ordinary rule collections.</summary>
    Set,
}

/// <summary>Ownership boundary for a Collection.</summary>
public enum CollectionScope
{
    Library,
    User,
}

/// <summary>How membership in a user-authored container is maintained.</summary>
public enum ContainerMembershipMode
{
    Manual,
    Smart,
}

/// <summary>The product surface where a curated Collection primarily appears.</summary>
public enum CollectionPrimaryArea
{
    Read,
    Watch,
    Listen,
    Mixed,
}

/// <summary>The durable owner boundary for a user-authored container.</summary>
public enum ContainerOwnerKind
{
    Profile,
    Library,
}

/// <summary>The audience allowed to discover a user-authored container.</summary>
public enum ContainerAudience
{
    Private,
    SelectedProfiles,
    Everyone,
}

/// <summary>How multiple collection rules are combined.</summary>
public enum CollectionMatchMode
{
    All,
    Any,
}

/// <summary>Direction used to order collection results.</summary>
public enum CollectionSortDirection
{
    Asc,
    Desc,
}

/// <summary>Coverage state for a Collection's universe identity.</summary>
public enum CollectionUniverseStatus
{
    Unknown,
    None,
    Limited,
    Rich,
    Complete,
}
