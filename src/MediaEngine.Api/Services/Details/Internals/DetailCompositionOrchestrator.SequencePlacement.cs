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
using SeriesManifestItemDto = MediaEngine.Domain.Models.SeriesManifestItemDto;
using SeriesManifestViewDto = MediaEngine.Domain.Models.SeriesManifestViewDto;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    private async Task<SequencePlacementViewModel?> BuildSequencePlacementAsync(
        Guid workId,
        LibraryItemDetail detail,
        DetailEntityType entityType,
        string? requestedContainerId,
        string? currentArtworkUrl,
        string currentDisplayTitle,
        CancellationToken ct)
    {
        var labels = ResolveSequenceLabels(entityType);
        var availableContainers = new List<SequenceContainerOptionViewModel>();
        var localContainer = await ResolveLocalSequenceContainerOptionAsync(workId, entityType, detail.MediaType, ct);
        if (localContainer is not null)
        {
            AddSequenceContainerOption(
                availableContainers,
                localContainer.ContainerId,
                localContainer.ContainerTitle,
                localContainer.MediaScope ?? SeriesMediaFilter(entityType, detail.MediaType),
                localContainer.SourceContainerId,
                localContainer.EquivalentContainerIds);
        }

        var allLinkedContainers = await ResolveLinkedManifestSequenceContainerOptionsAsync(workId, entityType, detail.MediaType, ct);
        var wikidataLinkedContainers = allLinkedContainers
            .Where(option => IsWikidataQid(option.SourceContainerId) || IsWikidataQid(option.ContainerId))
            .ToList();
        var linkedContainers = PreferWikidataLinkedSequenceContainers(allLinkedContainers);

        if (wikidataLinkedContainers.Count > 0)
        {
            availableContainers.RemoveAll(option =>
                IsProviderBackedSequenceContainer(option)
                && !wikidataLinkedContainers.Any(wikidata => ShouldMergeSequenceContainerOptions(option, wikidata)));
        }

        foreach (var option in linkedContainers)
        {
            if (IsComicSequenceEntity(entityType)
                && !availableContainers.Any(existing => ShouldMergeSequenceContainerOptions(existing, option)))
            {
                continue;
            }

            AddSequenceContainerOption(
                availableContainers,
                option.ContainerId,
                option.ContainerTitle,
                option.MediaScope ?? SeriesMediaFilter(entityType, detail.MediaType),
                option.SourceContainerId,
                option.EquivalentContainerIds);
        }

        var canonicalContainers = ResolveSequenceContainerOptions(detail, entityType);
        var hasTrustedSequenceContainer = availableContainers.Any(IsLocalOrProviderBackedSequenceContainer)
            || canonicalContainers.Any(option =>
                IsManifestBackedSequenceContainerId(option.ContainerId)
                || IsManifestBackedSequenceContainerId(option.SourceContainerId));
        if (hasTrustedSequenceContainer)
        {
            availableContainers.RemoveAll(IsTitleOnlySequenceContainerOption);
        }

        foreach (var option in canonicalContainers)
        {
            if (IsComicSequenceEntity(entityType) && IsWikidataOnlySequenceContainer(option))
            {
                continue;
            }

            if (linkedContainers.Count > 0
                && !linkedContainers.Any(linked => ShouldMergeSequenceContainerOptions(linked, option)))
            {
                continue;
            }

            if (hasTrustedSequenceContainer && IsTitleOnlySequenceContainerOption(option))
            {
                continue;
            }

            AddSequenceContainerOption(
                availableContainers,
                option.ContainerId,
                option.ContainerTitle,
                option.MediaScope ?? SeriesMediaFilter(entityType, detail.MediaType),
                option.SourceContainerId,
                option.EquivalentContainerIds);
        }

        if (availableContainers.Count == 0)
        {
            return null;
        }

        var hasExplicitSequenceEvidence = linkedContainers.Count > 0
            || (localContainer is not null && IsLocalOrProviderBackedSequenceContainer(localContainer))
            || canonicalContainers.Any(option => IsWikidataQid(NormalizeSequenceContainerId(option.ContainerId)));
        var defaultContainerId = NormalizeSequenceContainerId(GetDetailCanonicalValue(detail, "default_sequence_container_id"));
        var requestedQid = NormalizeSequenceContainerId(requestedContainerId);
        var selectedContainer = availableContainers.FirstOrDefault(option =>
            !string.IsNullOrWhiteSpace(requestedQid)
            && SequenceContainerOptionMatches(option, requestedQid))
            ?? availableContainers.FirstOrDefault(option =>
            !string.IsNullOrWhiteSpace(defaultContainerId)
            && SequenceContainerOptionMatches(option, defaultContainerId))
            ?? availableContainers[0];
        var containerTitle = SeriesDisplayFormatter.NormalizeContainerTitle(selectedContainer.ContainerTitle, isStructuralSeries: true)
            ?? selectedContainer.ContainerTitle;
        var containerId = NormalizeSequenceContainerId(selectedContainer.ContainerId) ?? selectedContainer.ContainerId;
        var sourceContainerId = NormalizeSequenceContainerId(selectedContainer.SourceContainerId) ?? selectedContainer.SourceContainerId;
        var manifestContainerId = IsManifestBackedSequenceContainerId(sourceContainerId)
            ? sourceContainerId
            : containerId;

        using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<SequenceRow>(new CommandDefinition(
            """
            WITH current_lineage AS (
                SELECT COALESCE(current_grandparent.id, current_parent.id, current_work.id) AS RootWorkId,
                       current_work.collection_id AS CollectionId
                FROM works current_work
                LEFT JOIN works current_parent ON current_parent.id = current_work.parent_work_id
                LEFT JOIN works current_grandparent ON current_grandparent.id = current_parent.parent_work_id
                WHERE current_work.id = @workId
            )
            SELECT w.id AS WorkId,
                   ma.id AS AssetId,
                   CAST(COALESCE(
                       (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'issue_title' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'issue_title' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'episode_title' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'episode_title' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'title' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'title' LIMIT 1),
                       'Untitled') AS TEXT) AS Title,
                   CAST(COALESCE(
                       (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key IN ('episode_description', 'episode_overview') LIMIT 1),
                       (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = ma.id AND key IN ('episode_description', 'episode_overview') LIMIT 1),
                       (SELECT NULLIF(CAST(claim_value AS TEXT), '') FROM metadata_claims WHERE entity_id = w.id AND claim_key IN ('episode_description', 'episode_overview') ORDER BY confidence DESC, claimed_at DESC LIMIT 1),
                       (SELECT NULLIF(CAST(claim_value AS TEXT), '') FROM metadata_claims WHERE entity_id = ma.id AND claim_key IN ('episode_description', 'episode_overview') ORDER BY confidence DESC, claimed_at DESC LIMIT 1),
                       (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = w.id AND key IN ('description', 'overview') LIMIT 1),
                       (SELECT NULLIF(CAST(value AS TEXT), '') FROM canonical_values WHERE entity_id = ma.id AND key IN ('description', 'overview') LIMIT 1)) AS TEXT) AS Description,
                   CAST(w.media_type AS TEXT) AS MediaType,
                   CAST(COALESCE(
                       (SELECT claim_value FROM metadata_claims WHERE entity_id = ma.id AND claim_key = 'series_position' AND provider_id = @wikidataProviderId ORDER BY confidence DESC, claimed_at DESC LIMIT 1),
                       (SELECT claim_value FROM metadata_claims WHERE entity_id = w.id AND claim_key = 'series_position' AND provider_id = @wikidataProviderId ORDER BY confidence DESC, claimed_at DESC LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('series_position', 'issue_number') LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('series_position', 'ordinal') LIMIT 1),
                       CASE WHEN w.ordinal_sort IS NOT NULL AND ABS(w.ordinal_sort - ROUND(w.ordinal_sort)) > 0.0001 THEN CAST(w.ordinal_sort AS TEXT) END,
                       CASE WHEN w.ordinal IS NOT NULL THEN CAST(w.ordinal AS TEXT) END) AS TEXT) AS PositionLabel,
                   w.ordinal_sort AS PositionSort,
                   CAST(COALESCE(
                       (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'season_number' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'season_number' LIMIT 1)) AS TEXT) AS SeasonLabel,
                   CAST(COALESCE(
                       (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'episode_number' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'episode_number' LIMIT 1)) AS TEXT) AS EpisodeLabel,
                    episodeStill.id AS EpisodeStillAssetId,
                    episodeStill.width_px AS EpisodeStillWidthPx,
                    episodeStill.height_px AS EpisodeStillHeightPx,
                    CAST(CASE WHEN @useEpisodeArtwork = 1 THEN COALESCE(
                        (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1),
                        (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('episode_still_url', 'episode_still', 'still_url', 'still') LIMIT 1),
                        (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('background_url', 'background') LIMIT 1),
                        (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('background_url', 'background') LIMIT 1),
                        (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('cover_url', 'cover') LIMIT 1),
                        (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('cover_url', 'cover') LIMIT 1))
                    ELSE COALESCE(
                        (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('cover_url', 'cover') LIMIT 1),
                        (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('cover_url', 'cover') LIMIT 1)) END AS TEXT) AS ArtworkUrl,
                    CAST(CASE WHEN @useEpisodeArtwork = 1 THEN COALESCE(
                        (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('background_state', 'hero_state') LIMIT 1),
                        (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('background_state', 'hero_state') LIMIT 1),
                        (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'cover_state' LIMIT 1),
                        (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'cover_state' LIMIT 1))
                    ELSE COALESCE(
                        (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'cover_state' LIMIT 1),
                        (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'cover_state' LIMIT 1)) END AS TEXT) AS ArtworkState,
                   CAST(COALESCE(
                       (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('runtime', 'duration') LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('runtime', 'duration') LIMIT 1)) AS TEXT) AS Duration
                  ,CAST(COALESCE(
                       (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key IN ('publication_date', 'release_date', 'year') LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = w.id AND key IN ('publication_date', 'release_date', 'year') LIMIT 1)) AS TEXT) AS PublicationDate
            FROM works w
            LEFT JOIN works parent ON parent.id = w.parent_work_id
            LEFT JOIN works grandparent ON grandparent.id = parent.parent_work_id
            LEFT JOIN editions e ON e.work_id = w.id
            LEFT JOIN media_assets ma ON ma.edition_id = e.id
            LEFT JOIN entity_assets episodeStill ON episodeStill.id = (
                SELECT id FROM entity_assets
                WHERE @useEpisodeArtwork = 1 AND asset_type = 'EpisodeStill'
                  AND entity_id IN (w.id, e.id, ma.id)
                  AND (local_image_path_s IS NOT NULL OR local_image_path IS NOT NULL)
                ORDER BY is_preferred DESC, is_user_override DESC, updated_at DESC LIMIT 1)
            CROSS JOIN current_lineage current
            WHERE NOT EXISTS (SELECT 1 FROM works child WHERE child.parent_work_id = w.id)
              AND (
                    COALESCE(grandparent.id, parent.id, w.id) = current.RootWorkId
                 OR (current.CollectionId IS NOT NULL AND w.collection_id = current.CollectionId)
                 OR COALESCE(
                       (SELECT value FROM canonical_values WHERE entity_id = ma.id AND key = 'series' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'series' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = COALESCE(grandparent.id, parent.id, w.id) AND key = 'series' LIMIT 1),
                       (SELECT value FROM canonical_values WHERE entity_id = COALESCE(grandparent.id, parent.id, w.id) AND key = 'title' LIMIT 1)
                    ) = @series
              )
              AND (
                    @mediaFilter = 'Other'
                 OR (@mediaFilter = 'Read' AND w.media_type IN ('Books', 'Book', 'Ebook', 'Comics', 'Comic'))
                 OR (@mediaFilter = 'Listen' AND w.media_type IN ('Audiobooks', 'Audiobook', 'Audio'))
                 OR (@mediaFilter = 'Watch' AND w.media_type IN ('Movies', 'Movie', 'TV', 'Television'))
                 OR (@mediaFilter = 'Music' AND w.media_type IN ('Music', 'MusicAlbum'))
              )
            GROUP BY w.id
            """,
            new
            {
                workId,
                series = containerTitle,
                mediaFilter = SeriesMediaFilter(entityType, detail.MediaType),
                wikidataProviderId = WellKnownProviders.Wikidata.ToString(),
                useEpisodeArtwork = entityType == DetailEntityType.TvEpisode ? 1 : 0,
            },
            cancellationToken: ct))).ToList();

        var items = rows.Select(row =>
        {
            var positionLabel = ResolveSequencePositionLabel(entityType, row.PositionLabel, row.EpisodeLabel);
            var positionNumber = TryParseSeriesPosition(positionLabel);
            var positionSort = row.PositionSort ?? TryParseSeriesPositionSort(positionLabel);
            var group = ResolveSequenceGroup(entityType, row.SeasonLabel);
            var artworkKind = entityType == DetailEntityType.TvEpisode ? "background" : "cover";
            return new SequenceItemViewModel
            {
                Id = row.WorkId.ToString("D"),
                EpisodeStillUrl = row.EpisodeStillAssetId is Guid stillId ? $"/stream/artwork/{stillId:D}" : null,
                EpisodeStillWidthPx = row.EpisodeStillWidthPx,
                EpisodeStillHeightPx = row.EpisodeStillHeightPx,
                EntityType = entityType,
                Title = row.WorkId == workId
                    ? currentDisplayTitle
                    : ResolveSequenceItemTitle(entityType, row.Title, containerTitle, positionLabel),
                ArtworkUrl = StringHelpers.FirstNonBlankOr(string.Empty,
                    ResolveCollectionArtworkUrl(row.ArtworkUrl, row.AssetId?.ToString("D"), artworkKind, row.ArtworkState),
                    row.WorkId == workId ? currentArtworkUrl : null),
                Description = row.Description,
                Duration = FormatTrackDuration(row.Duration),
                Route = entityType == DetailEntityType.TvEpisode
                    ? $"/details/work/{row.WorkId:D}?context=watch"
                    : null,
                PublicationDate = row.PublicationDate,
                PositionNumber = positionNumber,
                PositionSort = positionSort,
                PositionLabel = positionLabel ?? positionNumber?.ToString(CultureInfo.InvariantCulture),
                PositionText = FormatSequencePositionText(entityType, positionLabel, positionNumber),
                GroupKey = group.Key,
                GroupTitle = group.Title,
                IsCurrent = row.WorkId == workId,
                IsOwned = true,
                ProgressState = LibraryProgressState.Unknown,
            };
        }).ToList();
        if (!IsComicSequenceEntity(entityType))
        {
            items = await MergeSequenceManifestPlaceholdersAsync(items, manifestContainerId, detail.WikidataQid, workId, entityType, ct);
            items = await ApplyExactManifestPositionsAsync(items, manifestContainerId, entityType, ct);
        }

        if (!items.Any(item => item.IsCurrent))
        {
            var fallbackPositionLabel = entityType == DetailEntityType.TvEpisode
                ? FirstText(detail.EpisodeNumber, GetDetailCanonicalValue(detail, MetadataFieldConstants.EpisodeNumber))
                : detail.SeriesPosition;
            var fallbackPositionNumber = TryParseSeriesPosition(fallbackPositionLabel);
            var fallbackPositionSort = TryParseSeriesPositionSort(fallbackPositionLabel);
            var fallbackGroup = ResolveSequenceGroup(entityType, FirstText(detail.SeasonNumber, GetDetailCanonicalValue(detail, MetadataFieldConstants.SeasonNumber)));
            items.Add(new SequenceItemViewModel
            {
                Id = workId.ToString("D"),
                EntityType = entityType,
                Title = currentDisplayTitle,
                ArtworkUrl = entityType == DetailEntityType.TvEpisode
                    ? StringHelpers.FirstNonBlankOr(string.Empty,
                        GetDetailCanonicalValue(detail, "episode_still_url"),
                        GetDetailCanonicalValue(detail, "episode_still"),
                        detail.BackgroundUrl,
                        detail.CoverUrl)
                    : StringHelpers.FirstNonBlankOr(string.Empty, currentArtworkUrl, detail.CoverUrl),
                Description = detail.Description,
                Duration = FormatTrackDuration(detail.Runtime),
                PublicationDate = StringHelpers.FirstNonBlankOr(string.Empty, detail.ReleaseDate, detail.Year),
                PositionLabel = fallbackPositionLabel,
                PositionNumber = fallbackPositionNumber,
                PositionSort = fallbackPositionSort,
                PositionText = FormatSequencePositionText(entityType, fallbackPositionLabel, fallbackPositionNumber),
                GroupKey = fallbackGroup.Key,
                GroupTitle = fallbackGroup.Title,
                IsCurrent = true,
                IsOwned = true,
            });
        }

        items = DeduplicateManifestMergeItems(items).ToList();
        items = NormalizeSequenceItems(items, entityType);
        items = SortSequenceItems(items);
        var hasPositionEvidence = items.Any(HasSequencePositionEvidence);
        if (IsUnpositionedSelfNamedSequence(
                items,
                containerTitle,
                detail.Title,
                containerId,
                sourceContainerId))
        {
            return null;
        }

        if (items.Count <= 1 && !hasExplicitSequenceEvidence)
        {
            return null;
        }

        if (!hasExplicitSequenceEvidence && !hasPositionEvidence)
        {
            return null;
        }

        var expectedTotalCandidate = await LoadSequenceExpectedTotalAsync(containerId, ct)
            ?? await LoadSequenceExpectedTotalAsync(sourceContainerId, ct);
        var containerMetadata = await LoadSequenceContainerMetadataAsync(containerId, sourceContainerId, ct);
        var containerDescription = containerMetadata?.Description
            ?? GetDetailCanonicalValue(detail, "series_description")
            ?? (entityType is DetailEntityType.BookSeries or DetailEntityType.ComicSeries
                or DetailEntityType.MovieSeries or DetailEntityType.TvShow
                    ? detail.Description
                    : null);
        var currentIndex = Math.Max(0, items.FindIndex(i => i.IsCurrent));
        var current = items[currentIndex];
        var mainSequenceItemCount = items.Count(item =>
            string.IsNullOrWhiteSpace(item.MembershipScope)
            || string.Equals(item.MembershipScope, SeriesMembershipScopeNames.MainSequence, StringComparison.OrdinalIgnoreCase));
        var expectedTotal = expectedTotalCandidate is > 0 && mainSequenceItemCount >= expectedTotalCandidate
            ? expectedTotalCandidate
            : null;
        var groups = BuildSequenceGroups(items, labels.ItemPluralLabel, expectedTotal);
        var currentGroup = groups.FirstOrDefault(group => string.Equals(group.Key, current.GroupKey ?? "all", StringComparison.OrdinalIgnoreCase));
        var totalKnownItems = currentGroup?.TotalKnownItems ?? Math.Max(items.Count, expectedTotal ?? 0);
        var distinctContainers = DeduplicateSequenceContainerOptions(availableContainers);
        return new SequencePlacementViewModel
        {
            ContainerId = containerId,
            SourceContainerId = sourceContainerId,
            ContainerTitle = containerTitle,
            ContainerDescription = containerDescription,
            ContainerWikipediaUrl = containerMetadata?.WikipediaUrl,
            SelectedContainerId = containerId,
            CanChooseContainer = distinctContainers.Count > 1,
            CanSetDefaultContainer = distinctContainers.Count > 1
                && !SequenceContainerOptionMatches(selectedContainer, defaultContainerId),
            AvailableContainers = distinctContainers.Select(option => new SequenceContainerOptionViewModel
            {
                ContainerId = option.ContainerId,
                SourceContainerId = option.SourceContainerId,
                ContainerTitle = SeriesDisplayFormatter.NormalizeContainerTitle(option.ContainerTitle, isStructuralSeries: true)
                    ?? option.ContainerTitle,
                MediaScope = option.MediaScope,
                EquivalentContainerIds = option.EquivalentContainerIds,
                IsSelected = SequenceContainerOptionMatches(option, selectedContainer.ContainerId)
                    || SequenceContainerOptionMatches(option, selectedContainer.SourceContainerId),
                IsDefault = !string.IsNullOrWhiteSpace(defaultContainerId)
                    && SequenceContainerOptionMatches(option, defaultContainerId),
            }).ToList(),
            UniverseId = detail.UniverseSummary?.UniverseQid,
            UniverseTitle = detail.UniverseSummary?.UniverseName,
            ContainerLabel = labels.ContainerLabel,
            ItemLabel = labels.ItemLabel,
            ItemPluralLabel = labels.ItemPluralLabel,
            GroupLabel = groups.Count > 1 ? "Series scope" : labels.GroupLabel,
            CurrentGroupKey = current.GroupKey ?? groups.FirstOrDefault()?.Key,
            PositionNumber = current.PositionNumber,
            PositionSort = current.PositionSort,
            TotalKnownItems = totalKnownItems,
            HasAuthoritativeTotal = expectedTotal.HasValue,
            PositionLabel = current.PositionLabel,
            PositionText = current.PositionText,
            PositionSummary = BuildSequencePositionSummary(entityType, current, containerTitle, labels),
            OrderingType = entityType switch
            {
                DetailEntityType.TvEpisode => SequenceOrderingType.EpisodeNumber,
                DetailEntityType.ComicIssue => SequenceOrderingType.IssueNumber,
                _ => SequenceOrderingType.LibraryOrder,
            },
            PreviousItem = currentIndex > 0 ? items[currentIndex - 1] : null,
            CurrentItem = current,
            NextItem = currentIndex < items.Count - 1 ? items[currentIndex + 1] : null,
            OrderedItems = items,
            Groups = groups,
        };
    }

    private async Task<SequenceContainerMetadataRow?> LoadSequenceContainerMetadataAsync(
        string? containerId,
        string? sourceContainerId,
        CancellationToken ct)
    {
        var localId = Guid.TryParse(containerId, out var parsedContainerId)
            ? parsedContainerId
            : Guid.TryParse(sourceContainerId, out var parsedSourceContainerId)
                ? parsedSourceContainerId
                : (Guid?)null;
        var containerQid = IsWikidataQid(containerId) ? containerId : null;
        var sourceQid = IsWikidataQid(sourceContainerId) ? sourceContainerId : null;

        if (localId is null && containerQid is null && sourceQid is null)
        {
            return null;
        }

        using var conn = _db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<SequenceContainerMetadataDbRow>(new CommandDefinition(
            """
            SELECT CAST(COALESCE(
                       NULLIF(TRIM(c.description), ''),
                       (SELECT NULLIF(TRIM(CAST(cv.value AS TEXT)), '')
                        FROM canonical_values cv
                        WHERE cv.entity_id = c.id
                          AND cv.key IN ('wikipedia_extract', 'description', 'overview')
                        ORDER BY CASE cv.key WHEN 'wikipedia_extract' THEN 0 WHEN 'description' THEN 1 ELSE 2 END
                        LIMIT 1),
                       (SELECT NULLIF(TRIM(ql.description), '')
                        FROM qid_labels ql
                        WHERE ql.qid = c.wikidata_qid
                        LIMIT 1)) AS TEXT) AS Description,
                   CAST((SELECT NULLIF(TRIM(CAST(cv.value AS TEXT)), '')
                    FROM canonical_values cv
                    WHERE cv.entity_id = c.id
                      AND cv.key = 'wikipedia_url'
                    LIMIT 1) AS TEXT) AS WikipediaUrl
            FROM collections c
            WHERE (@localId IS NOT NULL AND c.id = @localId)
               OR (@containerQid IS NOT NULL AND c.wikidata_qid = @containerQid)
               OR (@sourceQid IS NOT NULL AND c.wikidata_qid = @sourceQid)
            ORDER BY CASE WHEN @localId IS NOT NULL AND c.id = @localId THEN 0 ELSE 1 END
            LIMIT 1
            """,
            new { localId, containerQid, sourceQid },
            cancellationToken: ct));

        return row is null
            ? null
            : new SequenceContainerMetadataRow(
                NormalizeSqliteText(row.Description),
                NormalizeSqliteText(row.WikipediaUrl));
    }

    private static string? NormalizeSqliteText(object? value)
    {
        var text = value switch
        {
            null or DBNull => null,
            string stringValue => stringValue,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private async Task<int?> LoadSequenceExpectedTotalAsync(string? containerId, CancellationToken ct)
    {
        var normalized = NormalizeSequenceContainerId(containerId);
        using var conn = _db.CreateConnection();

        if (Guid.TryParse(normalized, out var collectionId))
        {
            var collectionTotal = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
                """
                SELECT CAST(value AS INTEGER)
                FROM canonical_values
                WHERE entity_id = @collectionId
                  AND key = @key
                  AND CAST(value AS INTEGER) > 0
                  AND EXISTS (
                      SELECT 1
                      FROM canonical_values scope
                      WHERE scope.entity_id = @collectionId
                        AND scope.key = @scopeKey
                        AND scope.value = @mainSequenceScope)
                LIMIT 1;
                """,
                new
                {
                    collectionId,
                    key = MetadataFieldConstants.SequenceTotal,
                    scopeKey = MetadataFieldConstants.SequenceTotalScope,
                    mainSequenceScope = SequenceCountScope.MainSequence.ToString(),
                },
                cancellationToken: ct));

            if (collectionTotal is > 0)
            {
                return collectionTotal;
            }
        }

        if (!IsManifestBackedSequenceContainerId(normalized))
        {
            return null;
        }

        return await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            SELECT CAST(COALESCE(
                json_extract(api_metadata_json, '$.expectedTotal'),
                json_extract(api_metadata_json, '$.expected_total')) AS INTEGER)
            FROM series_manifest_hydrations
            WHERE series_qid = @seriesQid
              AND json_extract(api_metadata_json, '$.completeness') = 'Complete'
              AND COALESCE(
                    CAST(json_extract(api_metadata_json, '$.expectedTotal') AS INTEGER),
                    CAST(json_extract(api_metadata_json, '$.expected_total') AS INTEGER),
                    0) > 0
            ORDER BY last_hydrated_at DESC
            LIMIT 1;
            """,
            new { seriesQid = normalized },
            cancellationToken: ct));
    }

    private static int? AuthoritativeManifestTotal(SeriesManifestViewDto? manifest)
    {
        if (manifest?.ExpectedTotal is not > 0
            || string.Equals(manifest.ExpectedTotalSource, "wikidata-manifest-rows", StringComparison.OrdinalIgnoreCase)
            || manifest.ExpectedTotalConfidence is < 0.8)
        {
            return null;
        }

        return manifest.ExpectedTotal;
    }

    private static bool HasSequencePositionEvidence(SequenceItemViewModel item)
        => item.PositionNumber.HasValue
           || !string.IsNullOrWhiteSpace(item.PositionLabel)
           || !string.IsNullOrWhiteSpace(item.PositionText);

    private static bool IsUnpositionedSelfNamedSequence(
        IReadOnlyList<SequenceItemViewModel> items,
        string containerTitle,
        string workTitle,
        string? containerId,
        string? sourceContainerId)
    {
        if (items.Count > 1
            || items.Any(HasSequencePositionEvidence)
            || IsManifestBackedSequenceContainerId(containerId)
            || IsManifestBackedSequenceContainerId(sourceContainerId))
        {
            return false;
        }

        return string.Equals(
            NormalizeSeriesTitle(containerTitle),
            NormalizeSeriesTitle(workTitle),
            StringComparison.OrdinalIgnoreCase);
    }

}
