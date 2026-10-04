using System.Globalization;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Display;
using MediaEngine.Storage.Contracts;
using static MediaEngine.Domain.Services.StringHelpers;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class ArtworkLibraryReadService(
    IDatabaseConnection database,
    DisplayWorkProjectionReader workReader,
    DisplayCardBuilder cardBuilder)
{
    private static readonly HashSet<string> SupportedArtworkTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CoverArt", "Headshot", "Banner", "Logo", "NetworkLogo", "StudioLogo",
        "Background", "SeasonPoster", "SeasonThumb", "EpisodeStill", "CharacterPortrait",
    };

    private static readonly HashSet<string> SupportedBrowseModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "titles", "series", "tvshows", "albums",
    };

    public async Task<ArtworkBrowsePageDto> BrowseAsync(
        string? entityKind,
        string? artworkType,
        string? search,
        string? browseAs,
        string? mediaType,
        string? artworkState,
        string? sort,
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalizedKind = entityKind?.Trim().ToLowerInvariant() switch
        {
            "media" or "work" => "media",
            "people" or "person" => "person",
            "universes" or "universe" => "universe",
            _ => null,
        };
        var normalizedBrowse = SupportedBrowseModes.Contains(browseAs?.Trim() ?? string.Empty)
            ? browseAs!.Trim().ToLowerInvariant()
            : "all";
        var normalizedMediaType = NormalizeMediaType(mediaType);
        var normalizedArtworkType = SupportedArtworkTypes.Contains(artworkType?.Trim() ?? string.Empty)
            ? artworkType!.Trim()
            : null;
        var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var normalizedArtworkState = artworkState?.Trim().ToLowerInvariant() switch
        {
            "has" or "missing" => artworkState.Trim().ToLowerInvariant(),
            _ => "all",
        };
        var normalizedSort = sort?.Trim().ToLowerInvariant() switch
        {
            "title-desc" or "year-newest" or "images-most" => sort.Trim().ToLowerInvariant(),
            _ => "title-asc",
        };
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);

        using var connection = database.CreateConnection();
        if (string.Equals(normalizedKind, "universe", StringComparison.OrdinalIgnoreCase))
        {
            var universes = LoadUniverseItems(connection, normalizedSearch, ct)
                .Where(item => MatchesArtworkState(item, normalizedArtworkState));
            var orderedUniverses = OrderItems(universes, normalizedSort)
                .ToList();
            return new ArtworkBrowsePageDto(
                orderedUniverses.Skip(offset).Take(limit).ToList(), offset, limit, orderedUniverses.Count);
        }

        var baseRows = connection.Query<ArtworkLibraryRow>(new CommandDefinition(BaseSql, new
        {
            EntityKind = normalizedKind,
            ArtworkType = normalizedArtworkType,
        }, cancellationToken: ct)).AsList();
        var characterPerformerIds = LoadCharacterPerformerMatches(connection, normalizedSearch, ct);

        var items = baseRows
            .Where(row => MatchesBrowse(row, normalizedBrowse))
            .Where(row => MatchesMediaType(row.MediaType, normalizedMediaType))
            .Where(row => MatchesSearch(row.DisplayTitle, normalizedSearch)
                || (string.Equals(row.EntityType, "Person", StringComparison.OrdinalIgnoreCase)
                    && characterPerformerIds.Contains(row.EntityId)))
            .Select(MapBaseItem)
            .Where(item => MatchesArtworkState(item, normalizedArtworkState))
            .ToList();

        if (!string.Equals(normalizedKind, "person", StringComparison.OrdinalIgnoreCase)
            && normalizedBrowse != "titles"
            && IsStructuralArtworkType(normalizedArtworkType))
        {
            var works = await workReader.LoadAsync(ct);
            var structuralItems = BuildStructuralItems(connection, works, ct)
                .Where(item => MatchesStructuralBrowse(item.GroupKind, normalizedBrowse))
                .Where(item => MatchesMediaType(item.MediaType, normalizedMediaType))
                .Where(item => MatchesSearch(item.DisplayTitle, normalizedSearch));
            items.AddRange(structuralItems);
        }

        var distinct = items
            .GroupBy(item => (item.EntityId, item.EntityType, item.GroupKind), ArtworkItemKeyComparer.Instance)
            .Select(group => group.First())
            .Where(item => MatchesArtworkState(item, normalizedArtworkState));
        var ordered = OrderItems(distinct, normalizedSort).ToList();

        return new ArtworkBrowsePageDto(
            ordered.Skip(offset).Take(limit).ToList(),
            offset,
            limit,
            ordered.Count);
    }

    private static IEnumerable<ArtworkLibraryItemDto> LoadUniverseItems(
        System.Data.IDbConnection connection,
        string? search,
        CancellationToken ct)
    {
        var rows = connection.Query<UniverseArtworkRow>(new CommandDefinition("""
            SELECT collection.id AS EntityId,
                   collection.display_name AS DisplayTitle,
                   collection.description AS Description,
                   collection.cover_artwork_path AS CoverPath,
                   collection.background_artwork_path AS BackgroundPath,
                   collection.logo_artwork_path AS LogoPath,
                   collection.wikidata_qid AS CanonicalId,
                   (SELECT link.artwork_asset_id
                      FROM entity_artwork_links link
                     WHERE link.entity_id=collection.id AND link.entity_type='Collection'
                       AND link.role='Primary'
                     ORDER BY link.is_preferred DESC, link.sort_order, link.created_at LIMIT 1) AS PreferredAssetId,
                   COUNT(DISTINCT item.work_id) AS OwnedWorkCount
            FROM collections collection
            LEFT JOIN collection_items item ON item.collection_id = collection.id
            WHERE collection.collection_type = 'Universe'
              AND (@Search IS NULL OR collection.display_name LIKE '%' || @Search || '%' COLLATE NOCASE)
            GROUP BY collection.id, collection.display_name, collection.description,
                     collection.cover_artwork_path, collection.background_artwork_path, collection.logo_artwork_path;
            """, new { Search = search }, cancellationToken: ct));

        foreach (var row in rows)
        {
            var hasCover = row.PreferredAssetId.HasValue || !string.IsNullOrWhiteSpace(row.CoverPath);
            var hasBackground = !string.IsNullOrWhiteSpace(row.BackgroundPath);
            var hasLogo = !string.IsNullOrWhiteSpace(row.LogoPath);
            var types = new[] { hasCover ? "CoverArt" : null, hasBackground ? "Background" : null, hasLogo ? "Logo" : null }
                .Where(value => value is not null).Cast<string>().ToList();
            yield return new ArtworkLibraryItemDto(
                row.EntityId,
                "Collection",
                row.DisplayTitle,
                null,
                null,
                "Universe",
                row.PreferredAssetId is Guid preferredAssetId
                    ? $"/api/v1/display/artwork/assets/{preferredAssetId:D}/content?size=s"
                    : hasCover ? $"/collections/{row.EntityId:D}/artwork/poster" : null,
                hasCover ? "CoverArt" : null,
                types,
                types.Count,
                row.OwnedWorkCount,
                false)
            {
                IsStructural = true,
                GroupKind = "Universe",
                ResolutionMode = hasCover ? ArtworkResolutionMode.Explicit : ArtworkResolutionMode.None,
                BackgroundImageUrl = hasBackground ? $"/collections/{row.EntityId:D}/artwork/background" : null,
                LogoImageUrl = hasLogo ? $"/collections/{row.EntityId:D}/artwork/logo" : null,
                CanonicalId = row.CanonicalId,
            };
        }
    }

    public IReadOnlyList<ArtworkLibraryItemDto> LoadUniverseHierarchy(
        Guid collectionId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var universeQid = connection.ExecuteScalar<string?>(
            "SELECT wikidata_qid FROM collections WHERE id=@collectionId AND collection_type='Universe' LIMIT 1;",
            new { collectionId });
        if (string.IsNullOrWhiteSpace(universeQid))
        {
            return [];
        }

        var rows = connection.Query<UniverseEntityArtworkRow>(new CommandDefinition("""
            SELECT entity.id AS EntityId,
                   entity.label AS DisplayTitle,
                   entity.description AS Description,
                   entity.entity_sub_type AS EntitySubType,
                   entity.wikidata_qid AS CanonicalId,
                   entity.image_url AS LegacyImageUrl,
                   (SELECT link.artwork_asset_id
                      FROM entity_artwork_links link
                     WHERE link.entity_id=entity.id AND link.entity_type='FictionalEntity'
                       AND link.role IN ('Portrait','Primary')
                     ORDER BY link.is_preferred DESC, link.sort_order, link.created_at LIMIT 1) AS PreferredAssetId,
                   (SELECT COUNT(*) FROM entity_artwork_links link
                     WHERE link.entity_id=entity.id AND link.entity_type='FictionalEntity') AS VariantCount,
                   (SELECT group_concat(DISTINCT link.role) FROM entity_artwork_links link
                     WHERE link.entity_id=entity.id AND link.entity_type='FictionalEntity') AS RolesCsv
              FROM fictional_entities entity
             WHERE entity.fictional_universe_qid=@universeQid
             ORDER BY CASE entity.entity_sub_type
                        WHEN 'Character' THEN 0 WHEN 'Location' THEN 1 WHEN 'Organization' THEN 2
                        WHEN 'Event' THEN 3 ELSE 4 END,
                      entity.label COLLATE NOCASE;
            """, new { universeQid }, cancellationToken: ct)).ToList();

        return rows.Select(row =>
        {
            var primaryRole = string.Equals(row.EntitySubType, "Character", StringComparison.OrdinalIgnoreCase)
                ? "Portrait"
                : "Primary";
            var roles = string.IsNullOrWhiteSpace(row.RolesCsv)
                ? Array.Empty<string>()
                : row.RolesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new ArtworkLibraryItemDto(
                row.EntityId,
                "FictionalEntity",
                row.DisplayTitle,
                null,
                null,
                row.EntitySubType,
                row.PreferredAssetId is Guid assetId
                    ? $"/api/v1/display/artwork/assets/{assetId:D}/content?size=s"
                    : row.LegacyImageUrl,
                primaryRole,
                roles,
                row.VariantCount,
                0,
                false)
            {
                GroupKind = row.EntitySubType,
                ResolutionMode = row.PreferredAssetId.HasValue || !string.IsNullOrWhiteSpace(row.LegacyImageUrl)
                    ? ArtworkResolutionMode.Explicit
                    : ArtworkResolutionMode.None,
                CanonicalId = row.CanonicalId,
            };
        }).ToList();
    }

    private IEnumerable<ArtworkLibraryItemDto> BuildStructuralItems(
        System.Data.IDbConnection connection,
        IReadOnlyList<DisplayWorkRow> works,
        CancellationToken ct)
    {
        var seriesWorks = works
            .Where(work => string.Equals(work.CollectionType, "Series", StringComparison.OrdinalIgnoreCase)
                || string.Equals(work.CollectionGroupByField, "series", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var seriesCards = cardBuilder.BuildCollectionCards(seriesWorks, "view");
        var tvCards = cardBuilder.BuildTvShowCards(works);

        var structuralIds = seriesCards.Select(card => card.Id)
            .Concat(tvCards.Select(card => card.Id))
            .Concat(works.Where(work => IsMediaType(work.MediaType, "Music")).Select(work => work.RootWorkId))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        var managedAssets = LoadManagedAssets(connection, structuralIds, ct);
        var collectionArtwork = LoadCollectionArtwork(connection, seriesCards.Select(card => card.Id).Distinct().ToArray(), ct);

        foreach (var card in seriesCards)
        {
            var groupKind = card.MediaType switch
            {
                "Movie" => "MovieSeries",
                "Book" => "BookSeries",
                "Audiobook" => "AudiobookSeries",
                "Comic" => "ComicSeries",
                _ => "SeriesGroup",
            };
            var mediaType = NormalizeMediaType(card.MediaType);
            var previews = MapPreviews(card.PreviewItems);
            collectionArtwork.TryGetValue(card.Id, out var explicitCollectionArtwork);
            var imageUrl = explicitCollectionArtwork?.PreferredCoverId is Guid preferredCoverId
                ? $"/api/v1/display/artwork/assets/{preferredCoverId:D}/content?size=s"
                : explicitCollectionArtwork?.HasCover == true
                    ? $"/collections/{card.Id:D}/artwork/poster"
                    : null;
            yield return CreateStructuralItem(
                card.Id,
                "Collection",
                card.Title,
                mediaType,
                card.Facts.FirstOrDefault(IsLikelyYear),
                GroupLabel(groupKind),
                imageUrl,
                groupKind,
                previews,
                card.GroupSummary?.OwnedCount ?? card.PreviewTotalCount ?? previews.Count,
                explicitCollectionArtwork?.VariantCount ?? 0,
                explicitCollectionArtwork?.AssetTypes ?? [],
                explicitCollectionArtwork?.HasBackground == true ? $"/collections/{card.Id:D}/artwork/background" : null,
                explicitCollectionArtwork?.HasLogo == true ? $"/collections/{card.Id:D}/artwork/logo" : null);
        }

        foreach (var card in tvCards)
        {
            managedAssets.TryGetValue(card.Id, out var assets);
            var explicitUrl = ExplicitRootImage(card.Id, "tvshow", assets, works
                .Where(work => work.RootWorkId == card.Id)
                .Select(work => FirstNonBlank(work.RootCoverMediumUrl, work.RootCoverUrl, work.RootSquareMediumUrl, work.RootSquareUrl)));
            var previews = MapPreviews(card.PreviewItems);
            yield return CreateStructuralItem(
                card.Id,
                "Work",
                card.Title,
                "TV",
                card.Facts.FirstOrDefault(IsLikelyYear),
                "TV show",
                explicitUrl,
                "TvShow",
                previews,
                card.GroupSummary?.OwnedCount ?? card.PreviewTotalCount ?? previews.Count,
                assets?.VariantCount ?? 0,
                assets?.AssetTypes ?? []);
        }

        foreach (var album in works
                     .Where(work => IsMediaType(work.MediaType, "Music") && work.RootWorkId != Guid.Empty)
                     .GroupBy(work => work.RootWorkId))
        {
            var albumWorks = album
                .OrderBy(work => ParsePosition(work.TrackNumber) ?? double.MaxValue)
                .ThenBy(work => work.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(work => work.CreatedAt)
                .ToList();
            var representative = albumWorks
                .OrderByDescending(work => !string.IsNullOrWhiteSpace(work.RootSquareUrl) || !string.IsNullOrWhiteSpace(work.RootCoverUrl))
                .ThenBy(work => work.CreatedAt)
                .First();
            managedAssets.TryGetValue(album.Key, out var assets);
            var explicitUrl = ExplicitRootImage(album.Key, "musicalbum", assets, albumWorks
                .Select(work => FirstNonBlank(work.RootSquareMediumUrl, work.RootSquareUrl, work.RootCoverMediumUrl, work.RootCoverUrl)));
            var previews = albumWorks
                .Select(MapWorkPreview)
                .Where(preview => preview is not null)
                .Cast<ArtworkLibraryPreviewItemDto>()
                .GroupBy(preview => preview.ImageUrl, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(4)
                .ToList();
            var title = FirstNonBlank(representative.Album, representative.Title) ?? "Music Album";
            yield return CreateStructuralItem(
                album.Key,
                "Work",
                title,
                "Music",
                representative.Year,
                "Music album",
                explicitUrl,
                "MusicAlbum",
                previews,
                albumWorks.Select(work => work.WorkId).Distinct().Count(),
                assets?.VariantCount ?? 0,
                assets?.AssetTypes ?? []);
        }
    }

    private static ArtworkLibraryItemDto CreateStructuralItem(
        Guid entityId,
        string entityType,
        string title,
        string? mediaType,
        string? year,
        string subtitle,
        string? imageUrl,
        string groupKind,
        IReadOnlyList<ArtworkLibraryPreviewItemDto> previews,
        int ownedCount,
        int variantCount,
        IReadOnlyList<string> assetTypes,
        string? backgroundImageUrl = null,
        string? logoImageUrl = null)
    {
        var resolutionMode = !string.IsNullOrWhiteSpace(imageUrl)
            ? ArtworkResolutionMode.Explicit
            : previews.Count > 0
                ? ArtworkResolutionMode.AutomaticGroup
                : ArtworkResolutionMode.None;
        return new ArtworkLibraryItemDto(
            entityId,
            entityType,
            title,
            mediaType,
            year,
            subtitle,
            imageUrl,
            string.IsNullOrWhiteSpace(imageUrl) ? null : "CoverArt",
            assetTypes,
            variantCount,
            ownedCount,
            false)
        {
            IsStructural = true,
            GroupKind = groupKind,
            ResolutionMode = resolutionMode,
            PreviewItems = previews,
            BackgroundImageUrl = backgroundImageUrl,
            LogoImageUrl = logoImageUrl,
        };
    }

    private static ArtworkLibraryItemDto MapBaseItem(ArtworkLibraryRow row)
    {
        var imageUrl = BuildBaseImageUrl(row);
        return new ArtworkLibraryItemDto(
            row.EntityId,
            row.EntityType,
            string.IsNullOrWhiteSpace(row.DisplayTitle) ? "Untitled" : row.DisplayTitle,
            NormalizeMediaType(row.MediaType),
            row.Year,
            row.Subtitle,
            imageUrl,
            row.PrimaryAssetType,
            Split(row.AssetTypesCsv),
            row.VariantCount,
            row.OwnedWorkCount,
            row.UsesLegacyPersonImage)
        {
            ResolutionMode = string.IsNullOrWhiteSpace(imageUrl)
                ? ArtworkResolutionMode.None
                : ArtworkResolutionMode.Explicit,
        };
    }

    private static string? BuildBaseImageUrl(ArtworkLibraryRow row)
    {
        if (string.Equals(row.EntityType, "Person", StringComparison.OrdinalIgnoreCase))
        {
            if (row.PreferredAssetId.HasValue)
            {
                return $"/stream/artwork/{row.PreferredAssetId.Value:D}?size=s";
            }

            var canonicalHeadshot = ApiImageUrls.BuildPersonHeadshotUrl(
                row.EntityId,
                row.LocalHeadshotPath,
                row.RemoteHeadshotUrl);
            if (!string.IsNullOrWhiteSpace(canonicalHeadshot))
            {
                return $"{canonicalHeadshot}?size=s";
            }
        }

        if (row.PreferredAssetId.HasValue)
        {
            return $"/stream/artwork/{row.PreferredAssetId.Value:D}?size=s";
        }

        if (row.UsesLegacyPersonImage)
        {
            var legacyHeadshot = ApiImageUrls.BuildPersonHeadshotUrl(row.EntityId, row.LocalHeadshotPath, row.RemoteHeadshotUrl);
            return string.IsNullOrWhiteSpace(legacyHeadshot) ? null : $"{legacyHeadshot}?size=s";
        }

        return row.HasCanonicalPrimary
            ? $"/stream/entity/work/{row.EntityId:D}/cover?size=s"
            : null;
    }

    private static IReadOnlyList<ArtworkLibraryPreviewItemDto> MapPreviews(
        IReadOnlyList<DisplayCardPreviewItemDto> previews) => previews
        .Where(preview => preview.WorkId.HasValue && !string.IsNullOrWhiteSpace(preview.ImageUrl))
        .Take(4)
        .Select(preview => new ArtworkLibraryPreviewItemDto(
            preview.WorkId!.Value,
            preview.AssetId,
            preview.Title,
            preview.ImageUrl,
            preview.Shape,
            preview.Position,
            NormalizeMediaType(preview.MediaType)))
        .ToList();

    private static ArtworkLibraryPreviewItemDto? MapWorkPreview(DisplayWorkRow work)
    {
        var imageUrl = FirstNonBlank(
            work.SquareMediumUrl,
            work.SquareUrl,
            work.CoverMediumUrl,
            work.CoverUrl);
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        return new ArtworkLibraryPreviewItemDto(
            work.WorkId,
            work.AssetId,
            work.Title,
            imageUrl,
            "Square",
            work.TrackNumber,
            "Music");
    }

    private static string? ExplicitRootImage(
        Guid entityId,
        string routeType,
        ManagedAssetSummary? assets,
        IEnumerable<string?> canonicalUrls)
    {
        if (assets?.PreferredCoverId is Guid preferredId)
        {
            return $"/stream/artwork/{preferredId:D}?size=s";
        }

        return canonicalUrls.Any(url => !string.IsNullOrWhiteSpace(url))
            ? $"/stream/entity/{routeType}/{entityId:D}/cover?size=s"
            : null;
    }

    private static Dictionary<Guid, ManagedAssetSummary> LoadManagedAssets(
        System.Data.IDbConnection connection,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken ct)
    {
        if (entityIds.Count == 0)
        {
            return [];
        }

        return connection.Query<ManagedAssetRow>(new CommandDefinition(
                """
                SELECT entity_id AS EntityId,
                       (SELECT preferred.id FROM entity_assets preferred
                         WHERE preferred.entity_id = entity_assets.entity_id
                           AND preferred.entity_type = 'Work'
                           AND preferred.asset_type = 'CoverArt'
                         ORDER BY preferred.is_preferred DESC,
                                  COALESCE(preferred.updated_at, preferred.created_at) DESC LIMIT 1) AS PreferredCoverId,
                       GROUP_CONCAT(DISTINCT asset_type) AS AssetTypesCsv,
                       COUNT(*) AS VariantCount
                  FROM entity_assets
                 WHERE entity_type = 'Work' AND entity_id IN @EntityIds
                 GROUP BY entity_id;
                """,
                new { EntityIds = entityIds },
                cancellationToken: ct))
            .ToDictionary(
                row => row.EntityId,
                row => new ManagedAssetSummary(row.PreferredCoverId, row.VariantCount, Split(row.AssetTypesCsv)));
    }

    private static Dictionary<Guid, CollectionArtworkSummary> LoadCollectionArtwork(
        System.Data.IDbConnection connection,
        IReadOnlyCollection<Guid> collectionIds,
        CancellationToken ct)
    {
        if (collectionIds.Count == 0)
        {
            return [];
        }

        return connection.Query<CollectionArtworkRow>(new CommandDefinition(
                """
                SELECT id AS EntityId,
                       (SELECT link.artwork_asset_id
                          FROM entity_artwork_links link
                         WHERE link.entity_id=collections.id AND link.entity_type='Collection'
                           AND link.role='Primary'
                         ORDER BY link.is_preferred DESC, link.sort_order, link.created_at LIMIT 1) AS PreferredCoverId,
                       CASE WHEN NULLIF(trim(cover_artwork_path), '') IS NOT NULL THEN 1 ELSE 0 END AS HasCover,
                       CASE WHEN NULLIF(trim(background_artwork_path), '') IS NOT NULL THEN 1 ELSE 0 END AS HasBackground,
                       CASE WHEN NULLIF(trim(logo_artwork_path), '') IS NOT NULL THEN 1 ELSE 0 END AS HasLogo
                  FROM collections
                 WHERE id IN @EntityIds;
                """,
                new { EntityIds = collectionIds },
                cancellationToken: ct))
            .ToDictionary(
                row => row.EntityId,
                row => new CollectionArtworkSummary(
                    row.PreferredCoverId,
                    row.HasCover,
                    row.HasBackground,
                    row.HasLogo,
                    (row.HasCover ? 1 : 0) + (row.HasBackground ? 1 : 0) + (row.HasLogo ? 1 : 0),
                    new[]
                    {
                        row.HasCover ? "CoverArt" : null,
                        row.HasBackground ? "Background" : null,
                        row.HasLogo ? "Logo" : null,
                    }.Where(value => value is not null).Cast<string>().ToList()));
    }

    private static bool MatchesBrowse(ArtworkLibraryRow row, string browseAs) => browseAs switch
    {
        "titles" => row.EntityType == "Work",
        "series" or "tvshows" or "albums" => false,
        _ => true,
    };

    private static bool MatchesStructuralBrowse(string? groupKind, string browseAs) => browseAs switch
    {
        "series" => groupKind is "MovieSeries" or "BookSeries" or "AudiobookSeries" or "ComicSeries" or "SeriesGroup",
        "tvshows" => groupKind == "TvShow",
        "albums" => groupKind == "MusicAlbum",
        "titles" => false,
        _ => true,
    };

    private static bool IsStructuralArtworkType(string? artworkType) => artworkType is null
        or "CoverArt" or "Background" or "Logo";

    private static bool MatchesMediaType(string? value, string? expected) =>
        expected is null || string.Equals(NormalizeMediaType(value), expected, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesSearch(string? value, string? search) =>
        search is null || (value?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    private static IReadOnlySet<Guid> LoadCharacterPerformerMatches(
        System.Data.IDbConnection connection,
        string? search,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return new HashSet<Guid>();
        }

        return connection.Query<Guid>(new CommandDefinition(
                """
                SELECT DISTINCT link.person_id
                  FROM character_performer_links link
                  INNER JOIN fictional_entities entity ON entity.id=link.fictional_entity_id
                  LEFT JOIN fictional_entity_user_overrides user ON user.entity_id=entity.id
                 WHERE COALESCE(NULLIF(user.label, ''), entity.label) LIKE '%' || @Search || '%' COLLATE NOCASE;
                """,
                new { Search = search },
                cancellationToken: ct))
            .ToHashSet();
    }

    private static bool MatchesArtworkState(ArtworkLibraryItemDto item, string state)
    {
        var hasArtwork = item.VariantCount > 0 || item.UsesLegacyPersonImage
            || item.ResolutionMode is ArtworkResolutionMode.Explicit or ArtworkResolutionMode.AutomaticGroup
            || !string.IsNullOrWhiteSpace(item.ImageUrl);
        return state switch { "has" => hasArtwork, "missing" => !hasArtwork, _ => true };
    }

    private static IOrderedEnumerable<ArtworkLibraryItemDto> OrderItems(
        IEnumerable<ArtworkLibraryItemDto> items, string sort) => sort switch
    {
        "title-desc" => items.OrderByDescending(item => item.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.EntityId),
        "year-newest" => items.OrderByDescending(item => item.Year, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.DisplayTitle, StringComparer.OrdinalIgnoreCase),
        "images-most" => items.OrderByDescending(item => item.VariantCount).ThenBy(item => item.DisplayTitle, StringComparer.OrdinalIgnoreCase),
        _ => items.OrderBy(item => item.DisplayTitle, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.EntityId),
    };

    private static bool IsMediaType(string? value, string expected) =>
        string.Equals(NormalizeMediaType(value), NormalizeMediaType(expected), StringComparison.OrdinalIgnoreCase);

    private static string GroupLabel(string groupKind) => groupKind switch
    {
        "MovieSeries" => "Movie series",
        "BookSeries" => "Book series",
        "AudiobookSeries" => "Audiobook series",
        "ComicSeries" => "Comic series / volume",
        _ => "Series / group",
    };

    private static bool IsLikelyYear(string value) =>
        value.Length >= 4 && int.TryParse(value.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
                          && year is >= 1000 and <= 9999;

    private static double? ParsePosition(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string? NormalizeMediaType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" => "Movies",
        "tv" or "television" or "tv shows" => "TV",
        "book" or "books" => "Books",
        "audiobook" or "audiobooks" => "Audiobooks",
        "music" or "album" or "albums" => "Music",
        "comic" or "comics" => "Comics",
        _ => string.IsNullOrWhiteSpace(value) ? null : value.Trim(),
    };

    private static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .ToList();

    private const string BaseSql = """
        WITH work_items AS (
            SELECT
                w.id AS EntityId,
                'Work' AS EntityType,
                COALESCE(
                    (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('title','episode_title','issue_title','album','show_name','series','book_title')
                     ORDER BY CASE key WHEN 'title' THEN 0 WHEN 'episode_title' THEN 1 WHEN 'issue_title' THEN 2 ELSE 3 END LIMIT 1),
                    NULLIF(w.parent_key, ''),
                    'Untitled') AS DisplayTitle,
                w.media_type AS MediaType,
                (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('year','release_year','publication_year')
                 ORDER BY CASE key WHEN 'year' THEN 0 ELSE 1 END LIMIT 1) AS Year,
                NULL AS Subtitle,
                (SELECT preferred.id
                   FROM entity_assets preferred
                  WHERE preferred.entity_id = w.id AND preferred.entity_type = 'Work'
                    AND (@ArtworkType IS NULL OR preferred.asset_type = @ArtworkType)
                  ORDER BY preferred.is_preferred DESC,
                           CASE preferred.asset_type WHEN 'CoverArt' THEN 0 WHEN 'Background' THEN 1 WHEN 'Logo' THEN 2 ELSE 3 END,
                           COALESCE(preferred.updated_at, preferred.created_at) DESC LIMIT 1) AS PreferredAssetId,
                COALESCE(
                    (SELECT preferred.asset_type FROM entity_assets preferred
                      WHERE preferred.entity_id = w.id AND preferred.entity_type = 'Work'
                        AND (@ArtworkType IS NULL OR preferred.asset_type = @ArtworkType)
                      ORDER BY preferred.is_preferred DESC,
                               CASE preferred.asset_type WHEN 'CoverArt' THEN 0 WHEN 'Background' THEN 1 WHEN 'Logo' THEN 2 ELSE 3 END,
                               COALESCE(preferred.updated_at, preferred.created_at) DESC LIMIT 1),
                    CASE WHEN (@ArtworkType IS NULL OR @ArtworkType = 'CoverArt')
                              AND EXISTS (SELECT 1 FROM canonical_values cv WHERE cv.entity_id = w.id
                                          AND cv.key IN ('cover_url','cover','poster_url','poster','square_url','square')
                                          AND NULLIF(trim(cv.value), '') IS NOT NULL)
                         THEN 'CoverArt' END) AS PrimaryAssetType,
                GROUP_CONCAT(DISTINCT ea.asset_type) AS AssetTypesCsv,
                COUNT(ea.id) AS VariantCount,
                1 AS OwnedWorkCount,
                0 AS UsesLegacyPersonImage,
                NULL AS LocalHeadshotPath,
                NULL AS RemoteHeadshotUrl,
                CASE WHEN EXISTS (SELECT 1 FROM canonical_values cv WHERE cv.entity_id = w.id
                                  AND cv.key IN ('cover_url','cover','poster_url','poster','square_url','square')
                                  AND NULLIF(trim(cv.value), '') IS NOT NULL)
                     THEN 1 ELSE 0 END AS HasCanonicalPrimary
            FROM works w
            LEFT JOIN entity_assets ea ON ea.entity_id = w.id AND ea.entity_type = 'Work'
                                      AND (@ArtworkType IS NULL OR ea.asset_type = @ArtworkType)
            WHERE w.ownership = 'Owned' AND w.is_catalog_only = 0 AND w.work_kind != 'parent'
              AND (@EntityKind IS NULL OR @EntityKind = 'media')
            GROUP BY w.id, w.media_type, w.parent_key
            HAVING @ArtworkType IS NULL OR COUNT(ea.id) > 0 OR PrimaryAssetType IS NOT NULL
        ),
        person_items AS (
            SELECT
                p.id AS EntityId,
                'Person' AS EntityType,
                p.name AS DisplayTitle,
                NULL AS MediaType,
                NULL AS Year,
                (SELECT GROUP_CONCAT(role, ', ') FROM (SELECT role FROM person_roles WHERE person_id = p.id ORDER BY role)) AS Subtitle,
                (SELECT preferred.id FROM entity_assets preferred
                  WHERE preferred.entity_id = p.id AND preferred.entity_type = 'Person'
                    AND (@ArtworkType IS NULL OR preferred.asset_type = @ArtworkType)
                  ORDER BY preferred.is_preferred DESC,
                           CASE preferred.asset_type WHEN 'Headshot' THEN 0 WHEN 'Background' THEN 1 WHEN 'Logo' THEN 2 ELSE 3 END,
                           COALESCE(preferred.updated_at, preferred.created_at) DESC LIMIT 1) AS PreferredAssetId,
                COALESCE(
                    (SELECT preferred.asset_type FROM entity_assets preferred
                      WHERE preferred.entity_id = p.id AND preferred.entity_type = 'Person'
                        AND (@ArtworkType IS NULL OR preferred.asset_type = @ArtworkType)
                      ORDER BY preferred.is_preferred DESC, COALESCE(preferred.updated_at, preferred.created_at) DESC LIMIT 1),
                    CASE WHEN (@ArtworkType IS NULL OR @ArtworkType = 'Headshot')
                                   AND (NULLIF(p.local_headshot_path, '') IS NOT NULL OR NULLIF(p.headshot_url, '') IS NOT NULL)
                         THEN 'Headshot' END) AS PrimaryAssetType,
                CASE WHEN COUNT(ea.id) > 0 THEN GROUP_CONCAT(DISTINCT ea.asset_type)
                     WHEN (@ArtworkType IS NULL OR @ArtworkType = 'Headshot')
                          AND (NULLIF(p.local_headshot_path, '') IS NOT NULL OR NULLIF(p.headshot_url, '') IS NOT NULL)
                     THEN 'Headshot' END AS AssetTypesCsv,
                COUNT(ea.id) AS VariantCount,
                (SELECT COUNT(DISTINCT editions.work_id)
                   FROM primary_person_media_credits links
                   INNER JOIN media_assets assets ON assets.id = links.media_asset_id
                   INNER JOIN editions ON editions.id = assets.edition_id
                  WHERE links.person_id = p.id
                    AND assets.status = 'Normal'
                    AND assets.is_orphaned = 0) AS OwnedWorkCount,
                CASE WHEN COUNT(ea.id) = 0 AND (@ArtworkType IS NULL OR @ArtworkType = 'Headshot')
                           AND (NULLIF(p.local_headshot_path, '') IS NOT NULL OR NULLIF(p.headshot_url, '') IS NOT NULL)
                     THEN 1 ELSE 0 END AS UsesLegacyPersonImage,
                p.local_headshot_path AS LocalHeadshotPath,
                p.headshot_url AS RemoteHeadshotUrl,
                0 AS HasCanonicalPrimary
            FROM persons p
            LEFT JOIN entity_assets ea ON ea.entity_id = p.id AND ea.entity_type = 'Person'
                                      AND (@ArtworkType IS NULL OR ea.asset_type = @ArtworkType)
            WHERE (@EntityKind IS NULL OR @EntityKind = 'person')
              AND (@ArtworkType IS NULL OR @ArtworkType = 'Headshot' OR ea.id IS NOT NULL)
              AND EXISTS (
                    SELECT 1
                      FROM primary_person_media_credits primary_credit
                      INNER JOIN media_assets owned_asset ON owned_asset.id = primary_credit.media_asset_id
                     WHERE primary_credit.person_id = p.id
                       AND owned_asset.status = 'Normal'
                       AND owned_asset.is_orphaned = 0)
            GROUP BY p.id, p.name, p.local_headshot_path, p.headshot_url
        )
        SELECT * FROM work_items
        UNION ALL
        SELECT * FROM person_items;
        """;

    private sealed class ArtworkLibraryRow
    {
        public Guid EntityId { get; init; }
        public string EntityType { get; init; } = string.Empty;
        public string DisplayTitle { get; init; } = string.Empty;
        public string? MediaType { get; init; }
        public string? Year { get; init; }
        public string? Subtitle { get; init; }
        public Guid? PreferredAssetId { get; init; }
        public string? PrimaryAssetType { get; init; }
        public string? AssetTypesCsv { get; init; }
        public int VariantCount { get; init; }
        public int OwnedWorkCount { get; init; }
        public bool UsesLegacyPersonImage { get; init; }
        public string? LocalHeadshotPath { get; init; }
        public string? RemoteHeadshotUrl { get; init; }
        public bool HasCanonicalPrimary { get; init; }
    }

    private sealed class ManagedAssetRow
    {
        public Guid EntityId { get; init; }
        public Guid? PreferredCoverId { get; init; }
        public string? AssetTypesCsv { get; init; }
        public int VariantCount { get; init; }
    }

    private sealed class CollectionArtworkRow
    {
        public Guid EntityId { get; init; }
        public Guid? PreferredCoverId { get; init; }
        public bool HasCover { get; init; }
        public bool HasBackground { get; init; }
        public bool HasLogo { get; init; }
    }

    private sealed class UniverseArtworkRow
    {
        public Guid EntityId { get; init; }
        public string DisplayTitle { get; init; } = string.Empty;
        public string? Description { get; init; }
        public string? CoverPath { get; init; }
        public string? BackgroundPath { get; init; }
        public string? LogoPath { get; init; }
        public string? CanonicalId { get; init; }
        public Guid? PreferredAssetId { get; init; }
        public int OwnedWorkCount { get; init; }
    }

    private sealed class UniverseEntityArtworkRow
    {
        public Guid EntityId { get; init; }
        public string DisplayTitle { get; init; } = string.Empty;
        public string? Description { get; init; }
        public string EntitySubType { get; init; } = string.Empty;
        public string? CanonicalId { get; init; }
        public string? LegacyImageUrl { get; init; }
        public Guid? PreferredAssetId { get; init; }
        public int VariantCount { get; init; }
        public string? RolesCsv { get; init; }
    }

    private sealed record ManagedAssetSummary(Guid? PreferredCoverId, int VariantCount, IReadOnlyList<string> AssetTypes);
    private sealed record CollectionArtworkSummary(
        Guid? PreferredCoverId,
        bool HasCover,
        bool HasBackground,
        bool HasLogo,
        int VariantCount,
        IReadOnlyList<string> AssetTypes);

    private sealed class ArtworkItemKeyComparer : IEqualityComparer<(Guid EntityId, string EntityType, string? GroupKind)>
    {
        public static readonly ArtworkItemKeyComparer Instance = new();

        public bool Equals((Guid EntityId, string EntityType, string? GroupKind) x, (Guid EntityId, string EntityType, string? GroupKind) y) =>
            x.EntityId == y.EntityId
            && string.Equals(x.EntityType, y.EntityType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.GroupKind, y.GroupKind, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((Guid EntityId, string EntityType, string? GroupKind) obj) =>
            HashCode.Combine(
                obj.EntityId,
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.EntityType),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.GroupKind ?? string.Empty));
    }
}
