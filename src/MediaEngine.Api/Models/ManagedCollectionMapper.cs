using MediaEngine.Api.Services.Collections;
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
        bool isAdministrator = false,
        DateOnly? today = null) => new()
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
            MaxItems = collection.MaxItems,
            SeasonFrom = collection.SeasonFrom,
            SeasonTo = collection.SeasonTo,
            HiddenUntil = today is { } day && CollectionOptionsPolicy.HiddenUntil(collection, day) is { } until
                ? until.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                : null,
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
            CanEdit = CollectionAccessPolicy.CanEdit(collection, activeProfile, isAdministrator),
            CanShare = CollectionAccessPolicy.CanManageSharedCollections(isAdministrator),
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
        bool isAdministrator = false,
        string groupType = CollectionGroupTypes.Collection,
        string? universeQid = null,
        CollectionCatalogSourceInfo? sourceInfo = null)
    {
        var baseDto = FromDomain(collection, itemCount, activeProfile, isAdministrator);
        var isGlobal = string.Equals(baseDto.Visibility, CollectionAccessPolicy.SharedVisibility, StringComparison.OrdinalIgnoreCase);
        var canEdit = CollectionAccessPolicy.CanEdit(collection, activeProfile, isAdministrator);
        var canManageGlobal = CollectionAccessPolicy.CanManageSharedCollections(isAdministrator);

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
            MaxItems = baseDto.MaxItems,
            SeasonFrom = baseDto.SeasonFrom,
            SeasonTo = baseDto.SeasonTo,
            HiddenUntil = baseDto.HiddenUntil,
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
            UniverseQid = universeQid,
            FillKind = sourceInfo?.FillKind ?? CollectionFillKinds.Automatic,
            ListSource = sourceInfo?.ListSource,
            SetId = sourceInfo?.SetId,
            SetName = sourceInfo?.SetName,
            ChildCollectionCount = sourceInfo?.ChildCollectionCount ?? 0,
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
    /// <paramref name="verifiedUniverseQid"/> is the QID of a Universe the caller has confirmed the paired profile can
    /// open through <c>/api/v1/universe/{qid}</c>; only then is <c>universe_qid</c> set and the entry's group type
    /// <c>universe</c>. Otherwise <c>universe_qid</c> is null and the group type is the catalogue's own.
    /// </summary>
    public static CollectionManagementCatalogDto ForNativeClient(
        CollectionManagementCatalogDto source,
        string? verifiedUniverseQid = null) =>
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
            MaxItems = source.MaxItems,
            SeasonFrom = source.SeasonFrom,
            SeasonTo = source.SeasonTo,
            HiddenUntil = source.HiddenUntil,
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
            GroupType = verifiedUniverseQid is null ? source.GroupType : CollectionGroupTypes.Universe,
            UniverseQid = verifiedUniverseQid,
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
            FillKind = source.FillKind,
            ListSource = source.ListSource,
            SetId = source.SetId,
            SetName = source.SetName,
            ChildCollectionCount = source.ChildCollectionCount,
        };
}

/// <summary>How a collection fills up and where it came from, worked out once per catalog read for the Collections page.</summary>
/// <param name="FillKind">See <see cref="CollectionFillKinds"/>.</param>
/// <param name="ListSource">The imported list behind a list collection; null for other kinds and for a Kids profile.</param>
/// <param name="SetId">The set a pack's collection sits in.</param>
/// <param name="SetName">That set's name.</param>
/// <param name="ChildCollectionCount">For a set, how many collections it holds.</param>
public sealed record CollectionCatalogSourceInfo(
    string FillKind,
    CollectionCatalogListSourceDto? ListSource = null,
    Guid? SetId = null,
    string? SetName = null,
    int ChildCollectionCount = 0);
