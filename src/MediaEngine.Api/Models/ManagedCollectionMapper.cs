using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Models;

internal static class ManagedCollectionMapper
{
    public static ManagedCollectionDto FromDomain(
        Collection collection,
        int itemCount,
        Profile? activeProfile,
        bool hasCollectionsWrite = false) => new()
        {
            Id = collection.Id,
            Name = collection.DisplayName ?? $"Collection {collection.Id.ToString("N")[..8]}",
            Description = collection.Description,
            IconName = collection.IconName,
            CoverArtworkUrl = string.IsNullOrWhiteSpace(collection.CoverArtworkPath)
            ? null
            : activeProfile is null
                ? $"/collections/{collection.Id}/artwork/poster"
                : $"/collections/{collection.Id}/artwork/poster?profileId={activeProfile.Id:D}",
            BackgroundArtworkUrl = string.IsNullOrWhiteSpace(collection.BackgroundArtworkPath) ? null
            : activeProfile is null ? $"/collections/{collection.Id}/artwork/background" : $"/collections/{collection.Id}/artwork/background?profileId={activeProfile.Id:D}",
            BannerArtworkUrl = null,
            LogoArtworkUrl = string.IsNullOrWhiteSpace(collection.LogoArtworkPath) ? null
            : activeProfile is null ? $"/collections/{collection.Id}/artwork/logo" : $"/collections/{collection.Id}/artwork/logo?profileId={activeProfile.Id:D}",
            CollectionType = collection.CollectionType.ToStorageValue(),
            Scope = collection.Scope.ToStorageValue(),
            ProfileId = collection.ProfileId,
            Visibility = CollectionAccessPolicy.ResolveVisibility(collection),
            MembershipMode = collection.MembershipMode.ToStorageValue(),
            PrimaryArea = collection.PrimaryArea.ToStorageValue(),
            OwnerKind = collection.OwnerKind.ToStorageValue(),
            Audience = collection.Audience.ToStorageValue(),
            SelectedProfileIds = collection.AudienceProfileIds,
            IsEnabled = collection.IsEnabled,
            IsFeatured = collection.IsFeatured,
            MinItems = collection.MinItems,
            RuleJson = collection.RuleJson,
            Resolution = collection.Resolution.ToStorageValue(),
            RuleHash = collection.RuleHash,
            MatchMode = collection.MatchMode.ToStorageValue(),
            SortField = collection.SortField,
            SortDirection = collection.SortDirection.ToStorageValue(),
            SecondarySortField = collection.SecondarySortField,
            SecondarySortDirection = collection.SecondarySortDirection?.ToStorageValue(),
            RefreshSchedule = collection.RefreshSchedule,
            ItemCount = itemCount,
            Status = !collection.IsEnabled ? "Disabled" : itemCount == 0 ? "Empty" : "Active",
            CreatedAt = collection.CreatedAt,
            ModifiedAt = collection.ModifiedAt,
            CanEdit = CollectionAccessPolicy.CanEdit(collection, activeProfile, hasCollectionsWrite),
            CanShare = CollectionAccessPolicy.CanManageSharedCollections(hasCollectionsWrite),
        };

    public static CollectionManagementCatalogDto ToCatalog(
        Collection collection,
        int itemCount,
        Profile? activeProfile,
        CollectionCatalogClassification classification,
        CollectionMediaCounts mediaCounts,
        IReadOnlyList<CollectionArtworkItemDto>? artworkItems = null,
        ArtworkPalette? artworkPalette = null,
        string? displayNameOverride = null,
        CollectionCatalogPersonDto? person = null,
        bool hasCollectionsWrite = false,
        string groupType = CollectionGroupTypes.Collection)
    {
        var baseDto = FromDomain(collection, itemCount, activeProfile, hasCollectionsWrite);
        var isGlobal = string.Equals(baseDto.Visibility, CollectionAccessPolicy.SharedVisibility, StringComparison.OrdinalIgnoreCase);
        var canEdit = CollectionAccessPolicy.CanEdit(collection, activeProfile, hasCollectionsWrite);
        var canManageGlobal = CollectionAccessPolicy.CanManageSharedCollections(hasCollectionsWrite);

        return new CollectionManagementCatalogDto
        {
            Id = baseDto.Id,
            Name = string.IsNullOrWhiteSpace(displayNameOverride) ? baseDto.Name : displayNameOverride,
            Description = baseDto.Description,
            IconName = baseDto.IconName,
            CoverArtworkUrl = baseDto.CoverArtworkUrl,
            BackgroundArtworkUrl = baseDto.BackgroundArtworkUrl,
            BannerArtworkUrl = baseDto.BannerArtworkUrl,
            LogoArtworkUrl = baseDto.LogoArtworkUrl,
            CollectionType = classification.CollectionType,
            Scope = baseDto.Scope,
            ProfileId = baseDto.ProfileId,
            Visibility = baseDto.Visibility,
            MembershipMode = baseDto.MembershipMode,
            PrimaryArea = baseDto.PrimaryArea,
            OwnerKind = baseDto.OwnerKind,
            Audience = baseDto.Audience,
            SelectedProfileIds = baseDto.SelectedProfileIds,
            IsEnabled = baseDto.IsEnabled,
            IsFeatured = baseDto.IsFeatured,
            MinItems = baseDto.MinItems,
            RuleJson = baseDto.RuleJson,
            Resolution = baseDto.Resolution,
            RuleHash = baseDto.RuleHash,
            MatchMode = baseDto.MatchMode,
            SortField = baseDto.SortField,
            SortDirection = baseDto.SortDirection,
            SecondarySortField = baseDto.SecondarySortField,
            SecondarySortDirection = baseDto.SecondarySortDirection,
            RefreshSchedule = baseDto.RefreshSchedule,
            ItemCount = baseDto.ItemCount,
            Status = baseDto.Status,
            CreatedAt = baseDto.CreatedAt,
            ModifiedAt = baseDto.ModifiedAt,
            CanEdit = canEdit,
            CanShare = canManageGlobal,
            Family = classification.Family,
            SystemKey = classification.SystemKey,
            GroupType = groupType,
            PrimaryLane = classification.PrimaryLaneOverride ?? mediaCounts.PrimaryLane,
            IsGlobal = isGlobal,
            IsSystem = classification.IsSystem,
            IsCrossMedia = classification.PrimaryLaneOverride is null && mediaCounts.IsCrossMedia,
            WatchCount = mediaCounts.WatchCount,
            ListenCount = mediaCounts.ListenCount,
            ReadCount = mediaCounts.ReadCount,
            OtherCount = mediaCounts.OtherCount,
            MovieCount = mediaCounts.MovieCount,
            TvCount = mediaCounts.TvCount,
            BookCount = mediaCounts.BookCount,
            ComicCount = mediaCounts.ComicCount,
            MusicCount = mediaCounts.MusicCount,
            AudiobookCount = mediaCounts.AudiobookCount,
            EarliestYear = mediaCounts.EarliestYear,
            LatestYear = mediaCounts.LatestYear,
            CanDelete = canEdit && !classification.IsSystem && CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType),
            CanRename = canEdit && !classification.IsSystem,
            CanToggleGlobal = canManageGlobal && !classification.IsSystem && CollectionAccessPolicy.IsManagedCollectionType(collection.CollectionType),
            ArtworkItems = artworkItems ?? [],
            ArtworkPalette = artworkPalette ?? ArtworkPalette.TuvimaDefault(),
            Person = person,
        };
    }

    /// <summary>
    /// Copies a catalogue entry for a paired app. Apps only read: the authoring fields (rule definition, who a list
    /// is shared with, edit rights) are left out, and the collection's own uploaded artwork is not offered because its
    /// route is not part of the app surface. Member artwork in <c>artwork_items</c> is unchanged.
    /// </summary>
    public static CollectionManagementCatalogDto ForNativeClient(CollectionManagementCatalogDto source) =>
        new()
        {
            Id = source.Id,
            Name = source.Name,
            Description = source.Description,
            IconName = source.IconName,
            CoverArtworkUrl = null,
            BackgroundArtworkUrl = null,
            BannerArtworkUrl = null,
            LogoArtworkUrl = null,
            CollectionType = source.CollectionType,
            Scope = source.Scope,
            ProfileId = null,
            Visibility = source.Visibility,
            MembershipMode = source.MembershipMode,
            PrimaryArea = source.PrimaryArea,
            OwnerKind = source.OwnerKind,
            Audience = source.Audience,
            SelectedProfileIds = [],
            IsEnabled = source.IsEnabled,
            IsFeatured = source.IsFeatured,
            MinItems = source.MinItems,
            RuleJson = null,
            Resolution = source.Resolution,
            RuleHash = null,
            MatchMode = source.MatchMode,
            SortField = source.SortField,
            SortDirection = source.SortDirection,
            SecondarySortField = source.SecondarySortField,
            SecondarySortDirection = source.SecondarySortDirection,
            RefreshSchedule = null,
            ItemCount = source.ItemCount,
            Status = source.Status,
            CreatedAt = source.CreatedAt,
            ModifiedAt = source.ModifiedAt,
            CanEdit = false,
            CanShare = false,
            Family = source.Family,
            SystemKey = source.SystemKey,
            GroupType = source.GroupType,
            PrimaryLane = source.PrimaryLane,
            IsGlobal = source.IsGlobal,
            IsSystem = source.IsSystem,
            IsCrossMedia = source.IsCrossMedia,
            WatchCount = source.WatchCount,
            ListenCount = source.ListenCount,
            ReadCount = source.ReadCount,
            OtherCount = source.OtherCount,
            MovieCount = source.MovieCount,
            TvCount = source.TvCount,
            BookCount = source.BookCount,
            ComicCount = source.ComicCount,
            MusicCount = source.MusicCount,
            AudiobookCount = source.AudiobookCount,
            EarliestYear = source.EarliestYear,
            LatestYear = source.LatestYear,
            CanDelete = false,
            CanRename = false,
            CanToggleGlobal = false,
            ArtworkItems = source.ArtworkItems,
            ArtworkPalette = source.ArtworkPalette,
            Person = source.Person,
        };
}
