using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using static MediaEngine.Api.Services.Details.Internals.DetailPresentationPolicy;
using static MediaEngine.Api.Services.Details.Internals.DetailViewModelBuilder;
using SeriesManifestItemDto = MediaEngine.Domain.Models.SeriesManifestItemDto;
using SeriesManifestViewDto = MediaEngine.Domain.Models.SeriesManifestViewDto;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    private async Task<DetailPageViewModel?> BuildCollectionAsync(
        Guid collectionId,
        DetailEntityType entityType,
        DetailPresentationContext context,
        bool isAdminView,
        DetailActionAuthorizationContext actionAuthorization,
        IReadOnlySet<Guid> favoriteWorkIds,
        CancellationToken ct,
        Guid? currentWorkId = null,
        Guid? profileId = null,
        string? selectedContainerId = null,
        IReadOnlyList<DisplayWorkRow>? authorizedWorks = null)
    {
        using var conn = _db.CreateConnection();
        var rawRow = await conn.QueryFirstOrDefaultAsync(new CommandDefinition(
            """
            SELECT c.id AS Id,
                   c.display_name AS DisplayName,
                   c.collection_type AS CollectionType,
                   c.wikidata_qid AS WikidataQid,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = c.id AND key IN ('description', 'overview') AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS Description,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = c.id AND key = 'tagline' AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS Tagline,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = c.id AND key IN ('cover_url', 'cover', 'poster_url', 'poster') AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS CoverUrl,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = c.id AND key IN ('background_url', 'background', 'hero_url', 'hero') AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS BackgroundUrl,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = c.id AND key IN ('banner_url', 'banner', 'hero_url', 'hero') AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS BannerUrl,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = c.id AND key IN ('logo_url', 'logo') AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS LogoUrl,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = c.id AND key IN ('network', 'studio', 'broadcaster') AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS HeroBrandLabel,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = c.id AND key IN ('network_logo_url', 'network_logo', 'studio_logo_url', 'broadcaster_logo_url') AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS HeroBrandImageUrl
            FROM collections c
            WHERE c.id = @collectionId
            LIMIT 1;
            """,
            new { collectionId = GuidSql.ToBlob(collectionId) },
            cancellationToken: ct));

        var hasCollectionRow = rawRow is not null;
        var musicAlbumGroup = !hasCollectionRow && entityType == DetailEntityType.MusicAlbum
            ? await LoadMusicAlbumSystemViewGroupAsync(collectionId, ct)
            : null;
        var audiobookSeriesGroup = !hasCollectionRow && entityType == DetailEntityType.Collection
            ? await LoadAudiobookSeriesSystemViewGroupAsync(collectionId, ct)
            : null;
        var row = hasCollectionRow
            ? new CollectionDetailRow(
                Guid.Parse(StringValue(rawRow!.Id) ?? collectionId.ToString("D")),
                StringValue(rawRow.DisplayName),
                StringValue(rawRow.CollectionType),
                StringValue(rawRow.WikidataQid),
                StringValue(rawRow.Description),
                StringValue(rawRow.Tagline),
                StringValue(rawRow.CoverUrl),
                StringValue(rawRow.BackgroundUrl),
                StringValue(rawRow.BannerUrl),
                StringValue(rawRow.LogoUrl),
                StringValue(rawRow.HeroBrandLabel),
                StringValue(rawRow.HeroBrandImageUrl))
            : entityType == DetailEntityType.TvShow
                ? await LoadTvShowRootDetailRowAsync(collectionId, ct)
                : musicAlbumGroup is not null
                    ? new CollectionDetailRow(
                        collectionId,
                        musicAlbumGroup.DisplayName,
                        null,
                        musicAlbumGroup.WikidataQid,
                        musicAlbumGroup.Description,
                        musicAlbumGroup.Tagline,
                        musicAlbumGroup.CoverUrl,
                        null,
                        null,
                        null,
                        null,
                        null)
                    : audiobookSeriesGroup is not null
                        ? new CollectionDetailRow(
                            collectionId,
                            audiobookSeriesGroup.DisplayName,
                            null,
                            audiobookSeriesGroup.WikidataQid,
                            audiobookSeriesGroup.Description,
                            audiobookSeriesGroup.Tagline,
                            audiobookSeriesGroup.CoverUrl,
                            null,
                            null,
                            null,
                            null,
                            null)
                    : null;

        if (row is null)
        {
            return null;
        }

        var collectionValues = hasCollectionRow
            ? await LoadCanonicalMapAsync(collectionId, ct)
            : BuildSystemViewCanonicalMap(audiobookSeriesGroup);
        var rootWorkId = hasCollectionRow
            ? await LoadCollectionRootWorkIdAsync(
                collectionId,
                requireRootWithChildren: entityType is DetailEntityType.TvShow or DetailEntityType.MusicAlbum or DetailEntityType.MovieSeries or DetailEntityType.BookSeries or DetailEntityType.ComicSeries,
                ct)
            : entityType is DetailEntityType.TvShow or DetailEntityType.MusicAlbum
                ? collectionId
                : audiobookSeriesGroup?.RootWorkId;
        IReadOnlyList<CollectionItemDto> resolvedCollectionItems = [];
        if (hasCollectionRow
            && entityType == DetailEntityType.Collection
            && _collectionCatalog is not null)
        {
            Profile? activeProfile = profileId.HasValue && _profiles is not null
                ? await _profiles.GetByIdAsync(profileId.Value, ct)
                : null;
            var resolvedItems = await _collectionCatalog.GetItemsAsync(
                collectionId,
                activeProfile,
                int.MaxValue,
                ct);
            resolvedCollectionItems = resolvedItems.Found && !resolvedItems.Forbidden
                ? resolvedItems.Items
                : [];
        }

        var ownedWorks = musicAlbumGroup is not null
            ? await LoadMusicAlbumSystemViewWorksAsync(musicAlbumGroup, ct)
            : audiobookSeriesGroup is not null
                ? await LoadAudiobookSeriesSystemViewWorksAsync(audiobookSeriesGroup, ct)
                : await LoadCollectionWorksAsync(
                    collectionId,
                    rootWorkId,
                    ct,
                    resolvedCollectionItems.Select(item => item.WorkId).Distinct().ToList(),
                    profileId,
                    authorizedWorks?.Select(work => work.AssetId).Distinct().ToList());
        if (entityType == DetailEntityType.Collection && resolvedCollectionItems.Count > 0)
        {
            ownedWorks = NormalizeStandardCollectionWorks(ownedWorks, resolvedCollectionItems);
        }
        IReadOnlySet<Guid>? authorizedDisplayWorkIds = null;
        if (authorizedWorks is not null && _collectionCatalog is not null)
        {
            authorizedDisplayWorkIds = (await _collectionCatalog.GetDisplayWorkIdsAsync(
                    authorizedWorks.Select(work => work.WorkId),
                    ct).ConfigureAwait(false))
                .ToHashSet();
        }
        ownedWorks = FilterAuthorizedCollectionWorks(
            ownedWorks,
            authorizedWorks,
            authorizedDisplayWorkIds);
        if (authorizedWorks is not null && ownedWorks.Count == 0)
        {
            return null;
        }
        if (!hasCollectionRow
            && (entityType is DetailEntityType.TvShow or DetailEntityType.MusicAlbum
                || audiobookSeriesGroup is not null)
            && ownedWorks.Count == 0)
        {
            return null;
        }
        var rootValues = rootWorkId.HasValue
            ? await LoadCanonicalMapAsync(rootWorkId.Value, ct)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var displayOverrides = rootWorkId.HasValue
            ? await LoadWorkDisplayOverridesAsync(rootWorkId.Value, ct)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (hasCollectionRow)
        {
            using var overridesConnection = _db.CreateConnection();
            var collectionOverrides = ParseDisplayOverrides(await overridesConnection.ExecuteScalarAsync<string?>(
                new CommandDefinition("SELECT display_overrides_json FROM collections WHERE id = @collectionId;",
                    new { collectionId }, cancellationToken: ct)));
            foreach (var (key, value) in collectionOverrides)
            {
                displayOverrides[key] = value;
            }
        }
        var values = MergeCanonicalMaps(collectionValues, rootValues);
        foreach (var (key, value) in displayOverrides.Where(pair => DetailDisplayOverrideCatalog.IsAllowed(pair.Key)))
        {
            values[key] = value;
        }
        var displayDescription = ResolveDisplayOverride(displayOverrides, "description");
        var works = entityType == DetailEntityType.MusicAlbum
            ? MergeMusicAlbumManifestTracks(ownedWorks, values, row.CoverUrl)
            : ownedWorks;

        if (entityType == DetailEntityType.TvShow)
        {
            works = DeduplicateTvEpisodeSummaries(works.Where(work => work.IsOwned).ToList());
            using var stillConnection = _db.CreateConnection();
            var ownedIds = works.Select(work => GuidSql.ToBlob(Guid.Parse(work.Id))).ToArray();
            var actualStills = (await stillConnection.QueryAsync<SequenceRow>(new CommandDefinition("""
                SELECT w.id AS WorkId, art.id AS EpisodeStillAssetId,
                       art.width_px AS EpisodeStillWidthPx, art.height_px AS EpisodeStillHeightPx
                FROM works w LEFT JOIN entity_assets art ON art.id = (
                    SELECT ea.id FROM entity_assets ea
                    WHERE ea.asset_type = 'EpisodeStill'
                      AND (ea.entity_id = w.id OR ea.entity_id IN (SELECT e.id FROM editions e WHERE e.work_id = w.id)
                           OR ea.entity_id IN (SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = w.id))
                      AND (ea.local_image_path_s IS NOT NULL OR ea.local_image_path IS NOT NULL)
                    ORDER BY ea.is_preferred DESC, ea.is_user_override DESC, ea.updated_at DESC LIMIT 1)
                WHERE w.id IN @ownedIds
                """, new { ownedIds }, cancellationToken: ct))).ToDictionary(row => row.WorkId);
            var episodes = new List<CollectionWorkSummary>();
            foreach (var episode in works)
            {
                var episodeId = Guid.Parse(episode.Id);
                var episodeValues = await LoadWorkAndAssetCanonicalMapAsync(episodeId, ct);
                var overrides = await LoadWorkDisplayOverridesAsync(episodeId, ct);
                episodes.Add(episode with
                {
                    Description = FirstText(ResolveDisplayOverride(overrides, "description"),
                        GetValue(episodeValues, MetadataFieldConstants.EpisodeDescription)),
                    BackgroundUrl = GetValue(episodeValues, "episode_still_url"),
                    EpisodeStillUrl = actualStills.GetValueOrDefault(episodeId)?.EpisodeStillAssetId is Guid stillId ? $"/stream/artwork/{stillId:D}" : null,
                    EpisodeStillWidthPx = actualStills.GetValueOrDefault(episodeId)?.EpisodeStillWidthPx,
                    EpisodeStillHeightPx = actualStills.GetValueOrDefault(episodeId)?.EpisodeStillHeightPx,
                    Year = FirstText(GetValue(episodeValues, "air_date"), GetValue(episodeValues, "release_date")),
                });
            }
            works = episodes;
        }

        var tvInProgressEpisode = entityType == DetailEntityType.TvShow
            ? SelectActiveTvEpisode(works)
            : null;
        var tvPlaybackEpisode = tvInProgressEpisode ?? (entityType == DetailEntityType.TvShow
            ? SelectFirstOwnedTvEpisode(works)
            : null);
        var tvPlaybackEpisodeId = tvPlaybackEpisode is not null && Guid.TryParse(tvPlaybackEpisode.Id, out var parsedEpisodeId)
            ? parsedEpisodeId
            : (Guid?)null;
        var tvPlaybackValues = tvPlaybackEpisodeId.HasValue
            ? await LoadWorkAndAssetCanonicalMapAsync(tvPlaybackEpisodeId.Value, ct)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var relatedArt = works
            .SelectMany(w => new[] { w.BackgroundUrl, w.ArtworkUrl })
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        var longDescription = displayDescription ?? (entityType == DetailEntityType.TvShow
            ? GetValue(values, MetadataFieldConstants.ShortDescription)
            : FirstText(GetValue(values, MetadataFieldConstants.Description),
                GetValue(values, "overview"), GetValue(values, "plot_summary"), row.Description));
        var heroSummary = entityType == DetailEntityType.TvShow ? longDescription : BuildHeroSummary(values);
        // Episode artwork must never stand in for show artwork. An unenriched TV show
        // deliberately falls back to its own cover (or the generated placeholder).
        var allowChildArtworkFallback = entityType != DetailEntityType.TvShow;
        var fallbackBackdrop = allowChildArtworkFallback
            ? works.Select(w => w.BackgroundUrl).FirstOrDefault(url => !string.IsNullOrWhiteSpace(url))
            : null;
        var fallbackCover = allowChildArtworkFallback
            ? works.Select(w => w.ArtworkUrl).FirstOrDefault(url => !string.IsNullOrWhiteSpace(url))
            : null;
        var collectionBackdrop = IsStructuralContainer(entityType)
            ? null
            : StringHelpers.FirstNonBlankOr(string.Empty,
                tvInProgressEpisode?.BackgroundUrl,
                row.BackgroundUrl,
                GetValue(values, "background_url"),
                GetValue(values, "background"),
                GetValue(values, "hero_url"),
                GetValue(values, "hero"),
                fallbackBackdrop);
        var collectionBanner = StringHelpers.FirstNonBlankOr(string.Empty,
            row.BannerUrl,
            GetValue(values, "banner_url"),
            GetValue(values, "banner"));
        var shelfCoverId = IsStructuralContainer(entityType) && rootWorkId.HasValue
            ? await conn.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
                """
                SELECT id FROM entity_assets
                WHERE entity_id = @rootWorkId AND entity_type = 'Work' AND asset_type = 'CoverArt'
                  AND is_user_override = 1
                  AND COALESCE(NULLIF(local_image_path_m, ''), NULLIF(local_image_path, '')) IS NOT NULL
                ORDER BY is_preferred DESC, COALESCE(updated_at, created_at) DESC LIMIT 1;
                """, new { rootWorkId }, cancellationToken: ct))
            : null;
        var collectionCover = IsStructuralContainer(entityType)
            ? shelfCoverId.HasValue ? $"/stream/artwork/{shelfCoverId.Value:D}" : rootWorkId.HasValue ? null : row.CoverUrl
            : StringHelpers.FirstNonBlankOr(string.Empty,
                row.CoverUrl,
                GetValue(values, "cover_url"),
                GetValue(values, "cover"),
                GetValue(values, "poster_url"),
                GetValue(values, "poster"),
                fallbackCover);
        if (tvInProgressEpisode is not null)
        {
            collectionBackdrop = tvInProgressEpisode.BackgroundUrl;
            collectionBanner = null;
            collectionCover = tvInProgressEpisode.BackgroundUrl;
        }
        var collectionLogo = StringHelpers.FirstNonBlankOr(string.Empty, row.LogoUrl, GetValue(values, "logo_url"), GetValue(values, "logo"));
        IReadOnlyList<CreditGroupViewModel> contributorGroups = IsStructuralContainer(entityType)
            ? []
            : await BuildCollectionCreditsAsync(collectionId, rootWorkId, works, entityType, values, ct);
        var fullContributorGroups = entityType == DetailEntityType.TvShow
            ? await BuildTvCreditsAsync(rootWorkId ?? collectionId, ct) : contributorGroups;
        if (entityType == DetailEntityType.TvShow)
        {
            contributorGroups = tvInProgressEpisode is not null
                ? await BuildTvCreditsAsync(Guid.Parse(tvInProgressEpisode.Id), ct) : fullContributorGroups;
        }

        var musicAlbumCompanion = entityType == DetailEntityType.MusicAlbum
            ? await BuildMusicAlbumCompanionAsync(
                rootWorkId ?? collectionId,
                musicAlbumGroup?.RootWorkId,
                contributorGroups,
                ct)
            : null;
        var characterGroups = IsStructuralContainer(entityType)
            ? []
            : await BuildCollectionCharactersAsync(collectionId, row.WikidataQid, authorizedWorks, ct);
        var heroProgress = BuildCollectionHeroProgress(entityType, works);
        var manifest = await ResolveCollectionSequenceManifestAsync(
            collectionId,
            works,
            selectedContainerId,
            ct);
        var displayWorks = MergeCollectionManifestPlaceholders(entityType, works, manifest);
        var expectedTotal = AuthoritativeManifestTotal(manifest);
        var artwork = BuildArtwork(
            entityType,
            collectionBackdrop,
            collectionBanner,
            collectionCover,
            collectionCover,
            null,
            values,
            allowChildArtworkFallback ? relatedArt : [],
            0,
            null,
            collectionLogo);
        var relationshipOwner = rootWorkId.HasValue ? await _libraryItems.GetDetailAsync(rootWorkId.Value, ct) : null;
        var relationships = relationshipOwner is not null ? BuildRelationshipStrip(relationshipOwner, null) : [];
        var collectionTitle = ResolveDisplayOverride(displayOverrides, "title") ?? ResolveCollectionTitle(entityType, row.DisplayName, rootValues, values);
        var sequenceCollectionId = manifest?.CollectionId ?? collectionId;
        var seasonArtwork = entityType == DetailEntityType.TvShow
            ? await LoadTvSeasonArtworkAsync(rootWorkId ?? collectionId, ct)
            : null;
        var sameFileLinks = entityType == DetailEntityType.TvShow
            ? await LoadSameFileLinksAsync(
                displayWorks
                    .Where(work => work.IsOwned && Guid.TryParse(work.Id, out _))
                    .Select(work => new SameFileCandidate(Guid.Parse(work.Id), TryParseInt(work.Episode)?.ToString(CultureInfo.InvariantCulture) ?? work.Episode, work.Title))
                    .ToList(),
                ct)
            : null;
        var sequencePlacement = BuildCollectionSequencePlacement(
            sequenceCollectionId,
            entityType,
            entityType == DetailEntityType.TvShow ? collectionTitle : StringHelpers.FirstNonBlankOr(collectionTitle, manifest?.SeriesLabel, collectionTitle)!,
            StringHelpers.FirstNonBlank(manifest?.SeriesQid, row.WikidataQid),
            entityType == DetailEntityType.TvShow ? heroSummary : longDescription,
            displayWorks,
            expectedTotal,
            manifest?.AuthoritativeTotalsByContainer,
            seasonArtwork,
            currentWorkId ?? tvPlaybackEpisodeId,
            entityType == DetailEntityType.TvShow ? rootWorkId ?? collectionId : null,
            sameFileLinks);
        var mediaGroups = entityType == DetailEntityType.TvShow
            ? []
            : BuildCollectionMediaGroups(entityType, displayWorks, favoriteWorkIds, expectedTotal);
        var isAutomaticCollection = hasCollectionRow && entityType == DetailEntityType.Collection
            && !CollectionAccessPolicy.IsManagedCollectionType(row.CollectionType ?? string.Empty);
        var canEdit = (hasCollectionRow && entityType is DetailEntityType.Collection or DetailEntityType.BookSeries or DetailEntityType.ComicSeries or DetailEntityType.MovieSeries)
                      || ((IsCanonicalContainerEntity(entityType) || audiobookSeriesGroup is not null) && rootWorkId.HasValue)
                      || (isAdminView
                          && string.Equals(row.CollectionType, CollectionTypeNames.Custom, StringComparison.OrdinalIgnoreCase));

        return new DetailPageViewModel
        {
            Id = collectionId.ToString("D"),
            EntityType = entityType,
            PresentationContext = context,
            EditorTarget = canEdit
                ? isAutomaticCollection
                    ? new DetailEditorTarget { EntityId = collectionId.ToString("D"), EntityKind = "StructuralCollection", ContainerMode = "Collection", InitialTab = "details" }
                    : audiobookSeriesGroup is not null && rootWorkId.HasValue
                    ? BuildCanonicalSystemViewEditorTarget(rootWorkId.Value)
                    : BuildCollectionEditorTarget(collectionId, entityType, rootWorkId)
                : null,
            Title = collectionTitle,
            Subtitle = entityType == DetailEntityType.TvShow ? ResolveTvContext(works) switch
            {
                { Reason: TvEpisodeSelectionReason.AllOwnedCompleted } => "All owned episodes watched",
                { Reason: TvEpisodeSelectionReason.RemainingOwned } => "Earlier owned episodes remain unwatched",
                { HasGap: true } => "Next available episode - intervening episodes are not in the library",
                _ => BuildCollectionSubtitle(entityType, displayWorks, values),
            } : BuildCollectionSubtitle(entityType, displayWorks, values),
            Tagline = entityType == DetailEntityType.TvShow
                ? tvInProgressEpisode?.Description ?? heroSummary
                : heroSummary,
            UsesEpisodeArtwork = tvInProgressEpisode is not null,
            Description = longDescription,
            DescriptionAttribution = displayDescription is not null ? BuildLocalDescriptionAttribution() : entityType == DetailEntityType.TvShow
                ? string.Equals(GetValue(values, MetadataFieldConstants.IdentityProvider), "tvdb", StringComparison.OrdinalIgnoreCase)
                    ? new DescriptionAttributionViewModel { SourceName = "TheTVDB", SourceTitle = "Series synopsis", SourceUrl = GetValue(values, BridgeIdKeys.TvdbId) is { } tvdbId ? $"https://thetvdb.com/series/{tvdbId}" : "https://thetvdb.com", LicenseName = "TheTVDB terms", LicenseUrl = "https://thetvdb.com/tos" }
                    : new DescriptionAttributionViewModel { SourceName = "TMDB", SourceTitle = "Series synopsis", SourceUrl = GetValue(values, "tmdb_id") is { } tmdbId ? $"https://www.themoviedb.org/tv/{tmdbId}" : null }
                : BuildWikipediaDescriptionAttribution(longDescription, GetValue(values, "wikipedia_url")),
            SourceLinks = BuildExternalSourceLinks(row.WikidataQid, GetValue(values, "wikipedia_url"), null, values),
            Facts = BuildCollectionFacts(entityType, displayWorks, values, contributorGroups, row.WikidataQid),
            Artwork = artwork,
            HeroBrand = BuildHeroBrand(
                entityType,
                StringHelpers.FirstNonBlankOr(string.Empty, row.HeroBrandLabel, GetValue(values, "network"), GetValue(values, "studio"), GetValue(values, "broadcaster")),
                StringHelpers.FirstNonBlankOr(string.Empty, row.HeroBrandImageUrl, GetValue(values, "network_logo_url"), GetValue(values, "network_logo"), GetValue(values, "studio_logo_url"), GetValue(values, "broadcaster_logo_url"))),
            Progress = heroProgress,
            Metadata = BuildCollectionMetadata(entityType, displayWorks, values, tvPlaybackEpisode, tvPlaybackValues),
            PrimaryActions = BuildCollectionActions(collectionId, entityType, context, heroProgress, displayWorks),
            SecondaryActions = BuildSecondaryActions(rootWorkId ?? collectionId, entityType, favoriteWorkIds.Contains(rootWorkId ?? collectionId)),
            OverflowActions = BuildOverflowActions(collectionId, entityType, actionAuthorization),
            SequencePlacement = sequencePlacement,
            ContributorGroups = contributorGroups,
            FullContributorGroups = fullContributorGroups,
            PreviewContributors = BuildPreviewContributors(entityType, contributorGroups),
            CharacterGroups = characterGroups,
            PreviewCharacters = characterGroups.SelectMany(g => g.Characters).Take(12).ToList(),
            RelationshipStrip = relationships,
            Tabs = BuildTabs(entityType, context, isAdminView, hasUniverse: HasUniverseRelationship(relationships)),
            MediaGroups = mediaGroups,
            PrimaryModule = BuildPrimaryModule(entityType, sequencePlacement, mediaGroups),
            MusicAlbumCompanion = musicAlbumCompanion,
            IdentityStatus = ResolveIdentityStatus(row.WikidataQid, null, null),
            LibraryStatus = LibraryStatus.Owned,
            IsAdminView = isAdminView,
        };
    }

    private static IReadOnlyList<CollectionWorkSummary> FilterAuthorizedCollectionWorks(
        IReadOnlyList<CollectionWorkSummary> works,
        IReadOnlyList<DisplayWorkRow>? authorizedWorks,
        IReadOnlySet<Guid>? authorizedDisplayWorkIds = null)
    {
        if (authorizedWorks is null)
        {
            return works;
        }

        var visibleByWork = authorizedWorks
            .GroupBy(work => work.WorkId)
            .ToDictionary(group => group.Key, group => group.First());
        return works
            .Select(work =>
            {
                if (!Guid.TryParse(work.Id, out var workId))
                {
                    return null;
                }

                if (visibleByWork.TryGetValue(workId, out var visible))
                {
                    return work with
                    {
                        AssetId = visible.AssetId.ToString("D"),
                        ArtworkUrl = FirstText(visible.CoverUrl, visible.SquareUrl),
                        BackgroundUrl = FirstText(visible.BackgroundUrl, visible.BannerUrl),
                        HasAsset = true,
                        IsCatalogOnly = false,
                        Ownership = "Owned",
                    };
                }

                // Collection presentation collapses episode, issue, and track
                // identities to their show, series, or album root. Accept that
                // root only when it was derived from the caller's authorized
                // work set; the rule/detail authorization has already proven an
                // accessible underlying asset.
                return authorizedDisplayWorkIds?.Contains(workId) == true
                    ? work with
                    {
                        HasAsset = true,
                        IsCatalogOnly = false,
                        Ownership = "Owned",
                    }
                    : null;
            })
            .Where(work => work is not null)
            .Cast<CollectionWorkSummary>()
            .ToList();
    }

    private async Task<SeriesManifestViewDto?> ResolveCollectionSequenceManifestAsync(
        Guid collectionId,
        IReadOnlyList<CollectionWorkSummary> works,
        string? selectedContainerId,
        CancellationToken ct)
    {
        var direct = await _seriesManifests.GetViewByCollectionIdAsync(collectionId, ct);
        var normalizedSelection = NormalizeSequenceContainerId(selectedContainerId);
        if (direct is not null
            && (string.IsNullOrWhiteSpace(normalizedSelection)
                || SequenceContainerIdEquals(normalizedSelection, direct.SeriesQid)
                || SequenceContainerIdEquals(normalizedSelection, direct.CollectionId.ToString("D"))))
        {
            return direct;
        }

        var workIds = works
            .Select(work => Guid.TryParse(work.Id, out var workId) ? workId : (Guid?)null)
            .Where(workId => workId.HasValue)
            .Select(workId => GuidSql.ToBlob(workId!.Value))
            .ToArray();
        if (workIds.Length == 0)
        {
            return direct;
        }

        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync(new CommandDefinition(
            """
            SELECT h.collection_id AS CollectionId,
                   h.series_qid AS SeriesQid,
                   COUNT(DISTINCT CASE
                       WHEN member.linked_work_id IN @workIds THEN hex(member.linked_work_id)
                       ELSE NULL
                   END) AS OwnedOverlap,
                   SUM(CASE WHEN member.ownership_state = 'Missing' THEN 1 ELSE 0 END) AS MissingCount
            FROM series_manifest_hydrations h
            JOIN series_manifest_items member ON member.series_qid = h.series_qid
            WHERE EXISTS (
                SELECT 1
                FROM series_manifest_items linked
                WHERE linked.series_qid = h.series_qid
                  AND linked.linked_work_id IN @workIds
            )
            GROUP BY h.collection_id, h.series_qid
            ORDER BY CASE
                         WHEN @selectedContainerId IS NOT NULL
                          AND (h.series_qid = @selectedContainerId
                               OR lower(hex(h.collection_id)) = replace(lower(@selectedContainerId), '-', ''))
                         THEN 0 ELSE 1
                     END,
                     OwnedOverlap DESC,
                     MissingCount DESC,
                     h.series_qid;
            """,
            new
            {
                workIds,
                selectedContainerId = normalizedSelection,
            },
            cancellationToken: ct));

        var selected = rows.FirstOrDefault();
        string? selectedCollectionId = selected is null
            ? null
            : StringValue((object?)selected.CollectionId);
        return Guid.TryParse(selectedCollectionId, out Guid manifestCollectionId)
            ? await _seriesManifests.GetViewByCollectionIdAsync(manifestCollectionId, ct)
            : direct;
    }

    private static IReadOnlyList<CollectionWorkSummary> NormalizeStandardCollectionWorks(
        IReadOnlyList<CollectionWorkSummary> loadedWorks,
        IReadOnlyList<CollectionItemDto> resolvedItems)
    {
        var loadedById = loadedWorks
            .Where(work => Guid.TryParse(work.Id, out _))
            .GroupBy(work => Guid.Parse(work.Id))
            .ToDictionary(group => group.Key, group => group.First());

        return resolvedItems
            .DistinctBy(item => item.WorkId)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(item =>
            {
                if (loadedById.TryGetValue(item.WorkId, out var loaded))
                {
                    return loaded with
                    {
                        MediaType = item.MediaType,
                        Title = StringHelpers.FirstNonBlankOr("Untitled", item.Title, loaded.Title),
                        HasAsset = true,
                        Ownership = "Owned",
                        IsCatalogOnly = false,
                        ArtworkUrl = StringHelpers.FirstNonBlankOr(string.Empty, item.CoverUrl, loaded.ArtworkUrl),
                        BackgroundUrl = null,
                        DetailRoute = item.DetailRoute,
                    };
                }

                return new CollectionWorkSummary(
                    item.WorkId.ToString("D"),
                    item.MediaType,
                    item.SortOrder,
                    StringHelpers.FirstNonBlankOr("Untitled", item.Title),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    item.Creator,
                    false,
                    null,
                    null,
                    true,
                    "Owned",
                    false,
                    item.CoverUrl,
                    null,
                    null)
                {
                    DetailRoute = item.DetailRoute,
                };
            })
            .ToList();
    }

    private async Task<ContentGroupDto?> LoadAudiobookSeriesSystemViewGroupAsync(
        Guid systemViewGroupId,
        CancellationToken ct)
    {
        if (_collectionBrowse is null)
        {
            return null;
        }

        var groups = await _collectionBrowse
            .GetSystemViewGroupsAsync("Audiobooks", "series", ct)
            .ConfigureAwait(false);
        return groups.FirstOrDefault(group =>
            SystemViewGroupIdentity.CreateId(group, "Audiobooks", "series") == systemViewGroupId);
    }

    private async Task<IReadOnlyList<CollectionWorkSummary>> LoadAudiobookSeriesSystemViewWorksAsync(
        ContentGroupDto group,
        CancellationToken ct)
    {
        if (_collectionBrowse is null)
        {
            return [];
        }

        var rows = await _collectionBrowse.GetSystemViewDetailWorksAsync(
            "series",
            group.DisplayName,
            "Audiobooks",
            group.Creator,
            ct, rootWorkId: group.RootWorkId);
        var previewsByWorkId = group.PreviewItems
            .GroupBy(item => item.WorkId)
            .ToDictionary(items => items.Key, items => items.First());

        return rows.Select((row, index) =>
        {
            var preview = previewsByWorkId.GetValueOrDefault(row.WorkId);
            var seriesPosition = StringHelpers.FirstNonBlankOr(string.Empty, row.SeriesIndex);
            var durationSource = StringHelpers.FirstNonBlankOr(
                string.Empty,
                row.DurationSecondsValue,
                row.Duration,
                row.Runtime);
            var duration = FormatSecondsDuration(ParseDurationSeconds(durationSource))
                ?? FormatTrackDuration(durationSource);
            var assetId = row.AssetId?.ToString("D");
            var artworkUrl = row.AssetId.HasValue
                ? $"/stream/{row.AssetId.Value:D}/cover"
                : preview?.ImageUrl;

            return new CollectionWorkSummary(
                row.WorkId.ToString("D"),
                "Audiobooks",
                TryParseInt(seriesPosition),
                StringHelpers.FirstNonBlankOr(
                    string.Empty,
                    row.Title,
                    preview?.Title,
                    $"Audiobook {index + 1}"),
                null,
                null,
                null,
                null,
                null,
                duration,
                StringHelpers.FirstNonBlankOr(string.Empty, row.ReleaseYear, row.YearValue),
                row.Author,
                false,
                null,
                null,
                row.AssetId.HasValue,
                "Owned",
                false,
                artworkUrl,
                row.AssetId.HasValue ? $"/stream/{row.AssetId.Value:D}/background" : null,
                assetId)
            {
                SequenceLabel = string.IsNullOrWhiteSpace(seriesPosition) ? null : seriesPosition,
                SequenceSort = TryParseSeriesPositionSort(seriesPosition),
            };
        })
        .OrderBy(work => work.SequenceSort ?? double.MaxValue)
        .ThenBy(work => work.Title, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    private static Dictionary<string, string> BuildSystemViewCanonicalMap(ContentGroupDto? group)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (group is null)
        {
            return values;
        }

        Add(MetadataFieldConstants.Series, group.DisplayName);
        Add(MetadataFieldConstants.Title, group.DisplayName);
        Add(MetadataFieldConstants.Author, group.Creator);
        Add(MetadataFieldConstants.Year, group.Year);
        return values;

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[key] = value.Trim();
            }
        }
    }

    private static DetailEditorTarget BuildCanonicalSystemViewEditorTarget(Guid rootWorkId)
        => new()
        {
            EntityId = rootWorkId.ToString("D"),
            EntityKind = "StructuralCollection",
            ContainerMode = "Canonical",
            InitialTab = "details",
        };

    private async Task<ContentGroupDto?> LoadMusicAlbumSystemViewGroupAsync(
        Guid rootWorkId,
        CancellationToken ct)
    {
        if (_collectionBrowse is null)
        {
            return null;
        }

        // Same-named album roots share one group, so any member root id resolves to it.
        return await _collectionBrowse
            .GetMusicAlbumGroupForRootAsync(rootWorkId, ct)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CollectionWorkSummary>> LoadMusicAlbumSystemViewWorksAsync(
        ContentGroupDto group,
        CancellationToken ct)
    {
        if (_collectionBrowse is null)
        {
            return [];
        }

        var rows = await _collectionBrowse.GetSystemViewDetailWorksAsync(
            "album",
            group.DisplayName,
            "Music",
            group.Creator,
            ct);
        return SortMusicAlbumTracks(rows.Select((row, index) =>
        {
            var durationSource = StringHelpers.FirstNonBlankOr(string.Empty, row.DurationSecondsValue, row.Duration, row.Runtime);
            var duration = FormatSecondsDuration(ParseDurationSeconds(durationSource))
                ?? FormatTrackDuration(durationSource);
            var assetId = row.AssetId?.ToString("D");
            return new CollectionWorkSummary(
                row.WorkId.ToString("D"),
                "Music",
                ResolveOwnedTrackOrdinal(row.TrackNumber, index),
                StringHelpers.FirstNonBlankOr(string.Empty, row.EpisodeTitle, row.Title, $"Track {index + 1}"),
                null,
                null,
                null,
                row.TrackNumber,
                TryParseInt(row.DiscNumber),
                duration,
                StringHelpers.FirstNonBlankOr(string.Empty, row.ReleaseYear, row.YearValue),
                FormatContributorList(StringHelpers.FirstNonBlankOr(string.Empty, row.Artist, row.Author)),
                false,
                null,
                null,
                row.AssetId.HasValue,
                "Owned",
                false,
                row.AssetId.HasValue ? $"/stream/{row.AssetId.Value:D}/cover" : row.Cover,
                row.Background,
                assetId);
        }));
    }

    private async Task<MusicAlbumCompanionViewModel> BuildMusicAlbumCompanionAsync(
        Guid currentAlbumRootWorkId,
        Guid? currentAlbumGroupRootWorkId,
        IReadOnlyList<CreditGroupViewModel> contributorGroups,
        CancellationToken ct)
    {
        var primaryArtist = contributorGroups
            .Where(group => group.GroupType == CreditGroupType.PrimaryArtists)
            .SelectMany(group => group.Credits)
            .OrderByDescending(credit => credit.IsPrimary)
            .ThenBy(credit => credit.SortOrder)
            .FirstOrDefault();

        var primaryArtistId = Guid.Empty;
        var hasResolvedArtist = primaryArtist is not null
            && Guid.TryParse(primaryArtist.EntityId, out primaryArtistId);
        var companion = new MusicAlbumCompanionViewModel
        {
            PrimaryArtistId = hasResolvedArtist ? primaryArtistId.ToString("D") : null,
            PrimaryArtistName = primaryArtist?.DisplayName,
            PrimaryArtistRoute = hasResolvedArtist
                ? $"/details/person/{primaryArtistId:D}?context=listen"
                : null,
        };

        if (!hasResolvedArtist || _collectionBrowse is null)
        {
            return companion;
        }

        var ownedAlbumRootIds = await LoadOwnedMusicAlbumRootIdsForArtistAsync(primaryArtistId, ct);
        ownedAlbumRootIds.Remove(currentAlbumRootWorkId);
        if (currentAlbumGroupRootWorkId.HasValue)
        {
            // The current album's group may be represented by a same-named sibling root.
            ownedAlbumRootIds.Remove(currentAlbumGroupRootWorkId.Value);
        }

        if (ownedAlbumRootIds.Count == 0)
        {
            return companion;
        }

        var albumGroups = await _collectionBrowse
            .GetSystemViewGroupsAsync("Music", "album", ct)
            .ConfigureAwait(false);
        var moreByAlbums = albumGroups
            .Where(group => group.RootWorkId.HasValue
                && ownedAlbumRootIds.Contains(group.RootWorkId.Value))
            .GroupBy(group => group.RootWorkId!.Value)
            .Select(group => group
                .OrderByDescending(item => !string.IsNullOrWhiteSpace(item.CoverUrl))
                .ThenByDescending(item => item.WorkCount)
                .First())
            .OrderByDescending(group => TryParseInt(StringHelpers.FirstNonBlankOr(string.Empty,
                group.Year,
                group.LatestYear?.ToString(CultureInfo.InvariantCulture))))
            .ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rootWorkId = group.RootWorkId!.Value;
                var artworkUrl = StringHelpers.FirstNonBlankOr(string.Empty,
                    group.CoverUrl,
                    group.PreviewItems.FirstOrDefault()?.ImageUrl);
                var year = StringHelpers.FirstNonBlankOr(string.Empty,
                    group.Year,
                    group.EarliestYear?.ToString(CultureInfo.InvariantCulture));
                return new MusicAlbumPreviewViewModel
                {
                    Id = rootWorkId.ToString("D"),
                    Title = group.DisplayName,
                    Year = string.IsNullOrWhiteSpace(year) ? null : year,
                    ArtworkUrl = string.IsNullOrWhiteSpace(artworkUrl) ? null : artworkUrl,
                    Route = $"/details/musicalbum/{rootWorkId:D}?context=listen",
                };
            })
            .ToList();

        return new MusicAlbumCompanionViewModel
        {
            PrimaryArtistId = companion.PrimaryArtistId,
            PrimaryArtistName = companion.PrimaryArtistName,
            PrimaryArtistRoute = companion.PrimaryArtistRoute,
            MoreByAlbums = moreByAlbums,
        };
    }

    private async Task<HashSet<Guid>> LoadOwnedMusicAlbumRootIdsForArtistAsync(
        Guid personId,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<Guid>(new CommandDefinition(
            """
            SELECT DISTINCT COALESCE(p.id, w.id)
            FROM primary_person_media_credits credit
            INNER JOIN media_assets ma ON ma.id = credit.media_asset_id
            INNER JOIN editions e ON e.id = ma.edition_id
            INNER JOIN works w ON w.id = e.work_id
            LEFT JOIN works p ON p.id = w.parent_work_id
            WHERE credit.person_id = @personId
              AND credit.credit_key = 'artist'
              AND LOWER(w.media_type) = 'music';
            """,
            new { personId = GuidSql.ToBlob(personId) },
            cancellationToken: ct));

        return rows.ToHashSet();
    }

    private static int ResolveOwnedTrackOrdinal(string? trackNumber, int zeroBasedIndex)
    {
        var parsedTrackNumber = TryParseInt(trackNumber);
        return parsedTrackNumber.HasValue ? parsedTrackNumber.Value : zeroBasedIndex + 1;
    }

    private async Task<IReadOnlyDictionary<string, SeasonArtworkPresentation>> LoadTvSeasonArtworkAsync(
        Guid rootWorkId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var seasons = conn.Query<SeasonWorkArtworkRow>(
            """
            SELECT season.id AS WorkId,
                   season.ordinal AS Ordinal,
                   (SELECT NULLIF(CAST(value AS TEXT), '')
                    FROM canonical_values
                    WHERE entity_id = season.id AND key = 'season_number'
                    LIMIT 1) AS SeasonNumber
            FROM works season
            WHERE season.parent_work_id = @rootWorkId
              AND season.work_kind = 'parent'
              AND COALESCE(season.is_catalog_only, 0) = 0
            ORDER BY COALESCE(season.ordinal_sort, season.ordinal, 2147483647), season.id;
            """,
            new { rootWorkId = GuidSql.ToBlob(rootWorkId) })
            .ToList();
        if (seasons.Count == 0)
        {
            return new Dictionary<string, SeasonArtworkPresentation>(StringComparer.OrdinalIgnoreCase);
        }

        var entityIds = seasons.Select(season => season.WorkId.ToString("D")).ToList();
        var assets = await _entityAssets.GetPreferredByEntitiesAsync(entityIds, ct);
        var assetsByEntity = assets
            .Where(asset => asset.AssetTypeValue is "SeasonPoster" or "SeasonThumb")
            .GroupBy(asset => asset.EntityId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(asset => asset.AssetTypeValue, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, SeasonArtworkPresentation>(StringComparer.OrdinalIgnoreCase);
        foreach (var season in seasons)
        {
            var rawSeasonNumber = StringHelpers.FirstNonBlank(
                season.SeasonNumber,
                season.Ordinal?.ToString(CultureInfo.InvariantCulture));
            var seasonKey = NormalizeEpisodeKey(rawSeasonNumber);
            if (string.IsNullOrWhiteSpace(seasonKey))
            {
                continue;
            }

            assetsByEntity.TryGetValue(season.WorkId.ToString("D"), out var seasonAssets);
            EntityAsset? poster = null;
            EntityAsset? thumb = null;
            seasonAssets?.TryGetValue("SeasonPoster", out poster);
            seasonAssets?.TryGetValue("SeasonThumb", out thumb);
            var posterSmall = poster is null ? null : $"/stream/artwork/{poster.Id:D}?size=s";
            var posterMedium = poster is null ? null : $"/stream/artwork/{poster.Id:D}?size=m";
            var backgroundMedium = thumb is null
                ? posterMedium
                : $"/stream/artwork/{thumb.Id:D}?size=m";

            result[seasonKey] = new SeasonArtworkPresentation(
                season.WorkId.ToString("D"),
                posterSmall,
                posterMedium,
                backgroundMedium);
        }

        return result;
    }

    private async Task<CollectionDetailRow?> LoadTvShowRootDetailRowAsync(Guid rootWorkId, CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var rawRow = await conn.QueryFirstOrDefaultAsync(new CommandDefinition(
            """
            SELECT w.id AS Id,
                   COALESCE(
                       (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key = 'show_name' LIMIT 1),
                       (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key = 'title' LIMIT 1),
                       'TV Show') AS DisplayName,
                   COALESCE(NULLIF(w.wikidata_qid, ''), (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key = 'wikidata_qid' LIMIT 1)) AS WikidataQid,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key IN ('description', 'overview') AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS Description,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key = 'tagline' AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS Tagline,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key IN ('cover_url', 'cover', 'poster_url', 'poster') AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS CoverUrl,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key IN ('background_url', 'background', 'hero_url', 'hero') AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS BackgroundUrl,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key IN ('banner_url', 'banner', 'hero_url', 'hero') AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS BannerUrl,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key IN ('logo_url', 'logo') AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS LogoUrl,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key IN ('network', 'studio', 'broadcaster') AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS HeroBrandLabel,
                   (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key IN ('network_logo_url', 'network_logo', 'studio_logo_url', 'broadcaster_logo_url') AND NULLIF(CAST(value AS TEXT), '') IS NOT NULL LIMIT 1) AS HeroBrandImageUrl
            FROM works w
            WHERE w.id = @rootWorkId
              AND (
                  LOWER(w.media_type) IN ('tv', 'television', 'tv show', 'tv shows')
                  OR EXISTS (
                      SELECT 1
                      FROM works child
                      WHERE child.parent_work_id = w.id
                        AND LOWER(child.media_type) IN ('tv', 'television', 'tv show', 'tv shows')
                      LIMIT 1
                  )
                  OR EXISTS (
                      SELECT 1
                      FROM works season
                      INNER JOIN works episode ON episode.parent_work_id = season.id
                      WHERE season.parent_work_id = w.id
                        AND LOWER(episode.media_type) IN ('tv', 'television', 'tv show', 'tv shows')
                      LIMIT 1
                  )
              )
            LIMIT 1;
            """,
            new { rootWorkId = GuidSql.ToBlob(rootWorkId) },
            cancellationToken: ct));

        return rawRow is null
            ? null
            : new CollectionDetailRow(
                Guid.Parse(StringValue(rawRow.Id) ?? rootWorkId.ToString("D")),
                StringValue(rawRow.DisplayName),
                null,
                StringValue(rawRow.WikidataQid),
                StringValue(rawRow.Description),
                StringValue(rawRow.Tagline),
                StringValue(rawRow.CoverUrl),
                StringValue(rawRow.BackgroundUrl),
                StringValue(rawRow.BannerUrl),
                StringValue(rawRow.LogoUrl),
                StringValue(rawRow.HeroBrandLabel),
                StringValue(rawRow.HeroBrandImageUrl));
    }

}
