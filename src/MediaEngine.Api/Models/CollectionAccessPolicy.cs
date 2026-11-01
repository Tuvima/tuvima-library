using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Models;

public static class CollectionAccessPolicy
{
    public const string PrivateVisibility = "private";
    public const string SharedVisibility = "shared";

    /// <summary>What the Engine says when a request would leave a manually curated collection with no items.</summary>
    public const string CuratedCollectionNeedsItemMessage = "A collection needs at least one item. Delete the collection instead.";

    /// <summary>
    /// A manually curated collection: a <c>Custom</c> collection with manual membership whose members are stored with it.
    /// It may never be empty (it is deleted when its last item leaves the library). Playlists,
    /// playlist folders, rule-driven collections and system groupings are not curated collections.
    /// </summary>
    public static bool IsCuratedCollection(Collection collection) =>
        collection.CollectionType == CollectionType.Custom
        && collection.Resolution == CollectionResolution.Materialized
        && collection.MembershipMode == ContainerMembershipMode.Manual;

    public static string NormalizeVisibility(string? visibility) =>
        string.Equals(visibility, SharedVisibility, StringComparison.OrdinalIgnoreCase)
            ? SharedVisibility
            : PrivateVisibility;

    public static string ResolveVisibility(Collection collection) =>
        collection.OwnerKind == ContainerOwnerKind.Library || collection.Audience == ContainerAudience.Everyone
            ? SharedVisibility
            : PrivateVisibility;

    /// <summary>
    /// Only an administrator makes or edits Library collections (everyone sees them) and shares one with selected profiles
    /// ("push"). Every adult profile owns what it makes (Collections and lists spec D19).
    /// </summary>
    public static bool CanManageSharedCollections(bool isAdministrator) =>
        isAdministrator;

    public static bool CanManageCuratedCollections(bool isAdministrator) =>
        isAdministrator;

    /// <summary>
    /// What the person asked for when it goes beyond a collection of their own that only they see: Library ownership, a
    /// shared visibility, or an audience of Selected profiles or Everyone. A non-administrator is refused all of these.
    /// </summary>
    public static bool RequestsSharing(ContainerOwnerKind ownerKind, ContainerAudience audience, string? visibility = null) =>
        ownerKind == ContainerOwnerKind.Library
        || audience != ContainerAudience.Private
        || string.Equals(visibility, SharedVisibility, StringComparison.OrdinalIgnoreCase);

    /// <summary>The refusal a non-administrator gets for sharing choices.</summary>
    public const string SharingNeedsAdministratorMessage =
        "Only an administrator can share a collection with other profiles or make it a Library collection.";

    public static bool CanAccess(Collection collection, Profile? activeProfile)
    {
        if (collection.OwnerKind == ContainerOwnerKind.Library || collection.Audience == ContainerAudience.Everyone)
        {
            return true;
        }

        if (activeProfile is null || collection.Scope != CollectionScope.User)
        {
            return false;
        }

        return collection.ProfileId == activeProfile.Id
            || collection.Audience == ContainerAudience.SelectedProfiles
               && collection.AudienceProfileIds.Contains(activeProfile.Id);
    }

    /// <summary>
    /// Whether the caller may change the collection. An administrator manages Library collections and their own profile's;
    /// anyone else, an adult profile, manages only the collections they own (their own profile, kept to themselves).
    /// </summary>
    public static bool CanEdit(
        Collection collection,
        Profile? activeProfile,
        bool isAdministrator)
    {
        if (collection.OwnerKind == ContainerOwnerKind.Library)
        {
            return CanManageCuratedCollections(isAdministrator);
        }

        if (collection.Scope == CollectionScope.Library)
        {
            return CanManageSharedCollections(isAdministrator);
        }

        return activeProfile is not null
            && collection.Scope == CollectionScope.User
            && collection.ProfileId == activeProfile.Id;
    }

    /// <summary>
    /// A hand-picked collection or playlist the caller may put titles into: the "Add to collection" list. Rule-driven
    /// collections, imports, packs and system groupings fill themselves and are never offered.
    /// </summary>
    public static bool CanFill(Collection collection, Profile? activeProfile, bool isAdministrator) =>
        collection.IsEnabled
        && IsManagedCollectionType(collection.CollectionType)
        && collection.Resolution == CollectionResolution.Materialized
        && CanEdit(collection, activeProfile, isAdministrator);

    public static bool IsManagedCollectionType(CollectionType collectionType) =>
        collectionType is CollectionType.Custom
            or CollectionType.Playlist
            or CollectionType.Smart // Legacy rows remain editable and are normalized by the editor/API.
            or CollectionType.PlaylistFolder;

    public static bool IsManagedCollectionType(string collectionType) =>
        string.Equals(collectionType, "Custom", StringComparison.OrdinalIgnoreCase)
        || string.Equals(collectionType, "Playlist", StringComparison.OrdinalIgnoreCase)
        || string.Equals(collectionType, "Smart", StringComparison.OrdinalIgnoreCase)
        || string.Equals(collectionType, "PlaylistFolder", StringComparison.OrdinalIgnoreCase);

    public static void ApplyOwnership(Collection collection, Guid? activeProfileId)
    {
        if (collection.OwnerKind == ContainerOwnerKind.Library)
        {
            collection.Audience = ContainerAudience.Everyone;
            collection.SetVisibility(CollectionScope.Library, profileId: null);
            return;
        }

        collection.SetVisibility(CollectionScope.User, activeProfileId);
        if (collection.Audience == ContainerAudience.Everyone)
        {
            collection.Audience = ContainerAudience.Private;
        }
    }

    public static void ApplyVisibility(Collection collection, string visibility, Guid? activeProfileId)
    {
        if (string.Equals(visibility, SharedVisibility, StringComparison.OrdinalIgnoreCase))
        {
            collection.SetVisibility(CollectionScope.Library, profileId: null);
            return;
        }

        collection.SetVisibility(CollectionScope.User, activeProfileId);
    }
}
