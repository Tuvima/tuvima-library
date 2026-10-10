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
    private async Task<List<SequenceItemViewModel>> MergeSequenceManifestPlaceholdersAsync(
        IReadOnlyList<SequenceItemViewModel> items,
        string? containerId,
        string? currentWorkQid,
        Guid currentWorkId,
        DetailEntityType entityType,
        CancellationToken ct)
    {
        var normalizedContainerId = NormalizeSequenceContainerId(containerId);
        if (string.IsNullOrWhiteSpace(normalizedContainerId))
        {
            return items.ToList();
        }

        var manifestItems = await LoadManifestItemsForSequenceContainerAsync(normalizedContainerId, ct);
        var exactManifestItems = manifestItems
            .Where(item => SequenceContainerIdEquals(item.SeriesQid, normalizedContainerId))
            .ToList();
        if (exactManifestItems.Count > 0)
        {
            manifestItems = exactManifestItems;
        }

        var scopedManifestItems = manifestItems
            .Where(item => IsManifestItemInMediaScope(item, entityType))
            .ToList();
        var connectedManifestItems = BuildConnectedManifestSubset(scopedManifestItems, currentWorkQid);
        if (connectedManifestItems.Count > 1
            && IsWatchEntityType(entityType)
            && !IsParentSequenceContainer(scopedManifestItems, normalizedContainerId))
        {
            scopedManifestItems = connectedManifestItems;
        }

        if (scopedManifestItems.Count > 0)
        {
            return MergeManifestItems(items, scopedManifestItems, currentWorkQid, currentWorkId, entityType);
        }

        return await MergeLegacySequenceMemberPlaceholdersAsync(items, normalizedContainerId, entityType, ct);
    }

    private async Task<List<SequenceItemViewModel>> ApplyExactManifestPositionsAsync(
        List<SequenceItemViewModel> items,
        string? containerId,
        DetailEntityType entityType,
        CancellationToken ct)
    {
        var normalizedContainerId = NormalizeSequenceContainerId(containerId);
        if (items.Count == 0 || !IsManifestBackedSequenceContainerId(normalizedContainerId))
        {
            return items;
        }

        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync(new CommandDefinition(
            """
            SELECT linked_work_id AS LinkedWorkId,
                   item_label AS ItemLabel,
                   raw_ordinal AS RawOrdinal,
                   parsed_ordinal AS ParsedOrdinal,
                   ordinal_scope_qid AS OrdinalScopeQid,
                   sort_order AS SortOrder,
                   membership_scope AS MembershipScope
            FROM series_manifest_items
            WHERE series_qid = @seriesQid
            ORDER BY COALESCE(sort_order, 999999), COALESCE(item_label, item_qid), item_qid;
            """,
            new { seriesQid = normalizedContainerId },
            cancellationToken: ct));

        var updated = items.ToList();
        foreach (var row in rows)
        {
            var rowObject = (object)row;
            var sourcePosition = ToManifestSourcePosition(rowObject, normalizedContainerId!);
            var positionSort = sourcePosition ?? DoubleValue(GetDapperValue(rowObject, "SortOrder"));
            if (!positionSort.HasValue)
            {
                continue;
            }
            var position = ToDisplayPositionNumber(sourcePosition);
            var positionLabel = sourcePosition.HasValue
                ? StringHelpers.FirstNonBlankOr(string.Empty,
                    StringValue(GetDapperValue(rowObject, "RawOrdinal")),
                    FormatSequenceSort(sourcePosition))
                : null;
            var membershipScope = StringHelpers.FirstNonBlankOr(string.Empty,
                StringValue(GetDapperValue(rowObject, "MembershipScope")),
                SeriesMembershipScopeNames.MainSequence)!;
            var group = ManifestScopeGroup(membershipScope);

            var linkedWorkId = StringValue(GetDapperValue(rowObject, "LinkedWorkId"));
            var normalizedTitle = NormalizeSeriesTitle(StringValue(GetDapperValue(rowObject, "ItemLabel")));
            var index = updated.FindIndex(item =>
                (!string.IsNullOrWhiteSpace(linkedWorkId)
                    && string.Equals(item.Id, linkedWorkId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(normalizedTitle)
                    && string.Equals(NormalizeSeriesTitle(item.Title), normalizedTitle, StringComparison.OrdinalIgnoreCase)));
            if (index < 0)
            {
                continue;
            }

            var item = updated[index];
            updated[index] = new SequenceItemViewModel
            {
                Id = item.Id,
                EntityType = item.EntityType,
                Title = item.Title,
                ArtworkUrl = item.ArtworkUrl,
                EpisodeStillUrl = item.EpisodeStillUrl,
                EpisodeStillWidthPx = item.EpisodeStillWidthPx,
                EpisodeStillHeightPx = item.EpisodeStillHeightPx,
                Route = item.Route,
                Description = item.Description,
                Duration = item.Duration,
                PublicationDate = item.PublicationDate,
                PositionNumber = position ?? item.PositionNumber,
                PositionSort = positionSort,
                PositionLabel = sourcePosition.HasValue ? positionLabel : item.PositionLabel,
                PositionText = sourcePosition.HasValue
                    ? FormatSequencePositionText(entityType, positionLabel, position)
                    : item.PositionText,
                GroupKey = group.Key,
                GroupTitle = group.Title,
                MembershipScope = membershipScope,
                IsCurrent = item.IsCurrent,
                IsOwned = item.IsOwned,
                ProgressState = item.ProgressState,
                EpisodeContext = item.EpisodeContext,
                ProgressPercent = item.ProgressPercent,
                PositionSeconds = item.PositionSeconds,
                DurationSeconds = item.DurationSeconds,
                RemainingSeconds = item.RemainingSeconds,
                ProgressLabel = item.ProgressLabel,
                CoveredWith = item.CoveredWith,
            };
        }

        return updated;
    }

    private static double? ToManifestSourcePosition(object row, string seriesQid)
    {
        var ordinalScopeQid = StringValue(GetDapperValue(row, "OrdinalScopeQid"));
        if (!string.IsNullOrWhiteSpace(ordinalScopeQid)
            && !SequenceContainerIdEquals(ordinalScopeQid, seriesQid))
        {
            return null;
        }

        var parsedOrdinal = DoubleValue(GetDapperValue(row, "ParsedOrdinal"));
        if (parsedOrdinal.HasValue)
        {
            return parsedOrdinal;
        }

        var rawOrdinal = StringValue(GetDapperValue(row, "RawOrdinal"));
        var rawPosition = TryParseSeriesPositionSort(rawOrdinal);
        if (rawPosition.HasValue)
        {
            return rawPosition;
        }

        return null;
    }

    private static object? GetDapperValue(object row, string key)
        => row is IDictionary<string, object> values && values.TryGetValue(key, out var value)
            ? value
            : null;

    private static bool IsParentSequenceContainer(IReadOnlyList<SeriesManifestItemRecord> manifestItems, string containerId)
        => manifestItems.Any(item =>
            SequenceContainerIdEquals(item.ParentCollectionQid, containerId)
            && !SequenceContainerIdEquals(item.SeriesQid, containerId));

    private async Task<IReadOnlyList<SeriesManifestItemRecord>> LoadManifestItemsForSequenceContainerAsync(
        string containerId,
        CancellationToken ct)
    {
        using var conn = _db.CreateConnection();
        var exactRows = await conn.QueryAsync<SeriesManifestItemRow>(new CommandDefinition(
            """
            SELECT id AS Id,
                   collection_id AS CollectionId,
                   series_qid AS SeriesQid,
                   item_qid AS ItemQid,
                   item_label AS ItemLabel,
                   item_description AS ItemDescription,
                   media_type AS MediaType,
                   media_kind AS MediaKind,
                   instance_of_qids_json AS InstanceOfQidsJson,
                   raw_ordinal AS RawOrdinal,
                   parsed_ordinal AS ParsedOrdinal,
                   ordinal_scope_qid AS OrdinalScopeQid,
                   sort_order AS SortOrder,
                   publication_date AS PublicationDate,
                   duration AS Duration,
                   previous_qid AS PreviousQid,
                   next_qid AS NextQid,
                   parent_collection_qid AS ParentCollectionQid,
                   parent_collection_label AS ParentCollectionLabel,
                   is_collection AS IsCollection,
                   is_expanded_from_collection AS IsExpandedFromCollection,
                   membership_scope AS MembershipScope,
                   source_properties_json AS SourcePropertiesJson,
                   relationships_json AS RelationshipsJson,
                   order_source AS OrderSource,
                   ownership_state AS OwnershipState,
                   linked_work_id AS LinkedWorkId,
                   last_hydrated_at AS LastHydratedAt,
                   created_at AS CreatedAt,
                   updated_at AS UpdatedAt
            FROM series_manifest_items
            WHERE series_qid = @containerId
            ORDER BY COALESCE(sort_order, 999999), COALESCE(item_label, item_qid), item_qid;
            """,
            new { containerId },
            cancellationToken: ct));
        var exactItems = exactRows.Select(row => row.ToEntity()).ToList();
        if (exactItems.Count > 0)
        {
            return exactItems;
        }

        var seriesItems = await _seriesManifests.GetItemsBySeriesQidAsync(containerId, ct);
        if (seriesItems.Count > 0)
        {
            return seriesItems;
        }

        var rows = await conn.QueryAsync<SeriesManifestItemRow>(new CommandDefinition(
            """
            SELECT id AS Id,
                   collection_id AS CollectionId,
                   series_qid AS SeriesQid,
                   item_qid AS ItemQid,
                   item_label AS ItemLabel,
                   item_description AS ItemDescription,
                   media_type AS MediaType,
                   media_kind AS MediaKind,
                   instance_of_qids_json AS InstanceOfQidsJson,
                   raw_ordinal AS RawOrdinal,
                   parsed_ordinal AS ParsedOrdinal,
                   ordinal_scope_qid AS OrdinalScopeQid,
                   sort_order AS SortOrder,
                   publication_date AS PublicationDate,
                   duration AS Duration,
                   previous_qid AS PreviousQid,
                   next_qid AS NextQid,
                   parent_collection_qid AS ParentCollectionQid,
                   parent_collection_label AS ParentCollectionLabel,
                   is_collection AS IsCollection,
                   is_expanded_from_collection AS IsExpandedFromCollection,
                   membership_scope AS MembershipScope,
                   source_properties_json AS SourcePropertiesJson,
                   relationships_json AS RelationshipsJson,
                   order_source AS OrderSource,
                   ownership_state AS OwnershipState,
                   linked_work_id AS LinkedWorkId,
                   last_hydrated_at AS LastHydratedAt,
                   created_at AS CreatedAt,
                   updated_at AS UpdatedAt
            FROM series_manifest_items
            WHERE parent_collection_qid = @containerId
            ORDER BY COALESCE(sort_order, 999999), COALESCE(item_label, item_qid), item_qid;
            """,
            new { containerId },
            cancellationToken: ct));

        return rows.Select(row => row.ToEntity()).ToList();
    }

    private static List<SeriesManifestItemRecord> BuildConnectedManifestSubset(
        IReadOnlyList<SeriesManifestItemRecord> manifestItems,
        string? currentWorkQid)
    {
        var qid = ExtractQid(currentWorkQid);
        if (string.IsNullOrWhiteSpace(qid))
        {
            return [];
        }

        var byQid = manifestItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ItemQid))
            .GroupBy(item => item.ItemQid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (!byQid.ContainsKey(qid))
        {
            return [];
        }

        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { qid };
        var pending = new Queue<string>();
        pending.Enqueue(qid);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!byQid.TryGetValue(current, out var item))
            {
                continue;
            }

            foreach (var neighbor in new[] { item.PreviousQid, item.NextQid }.Select(ExtractQid).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>())
            {
                if (byQid.ContainsKey(neighbor) && connected.Add(neighbor))
                {
                    pending.Enqueue(neighbor);
                }
            }

            foreach (var inbound in manifestItems.Where(candidate =>
                string.Equals(ExtractQid(candidate.PreviousQid), current, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ExtractQid(candidate.NextQid), current, StringComparison.OrdinalIgnoreCase)))
            {
                if (connected.Add(inbound.ItemQid))
                {
                    pending.Enqueue(inbound.ItemQid);
                }
            }
        }

        return manifestItems
            .Where(item => connected.Contains(item.ItemQid))
            .ToList();
    }

    private static List<SequenceItemViewModel> MergeManifestItems(
        IReadOnlyList<SequenceItemViewModel> items,
        IReadOnlyList<SeriesManifestItemRecord> manifestItems,
        string? currentWorkQid,
        Guid currentWorkId,
        DetailEntityType entityType)
    {
        var merged = items.ToList();
        var currentQid = ExtractQid(currentWorkQid);
        var ownedPositions = BuildOwnedPositionSet(merged);
        var ownedQids = merged
            .Select(item => ExtractQid(item.Id))
            .Where(qid => !string.IsNullOrWhiteSpace(qid))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ownedTitles = merged
            .Where(item => item.IsOwned)
            .Select(item => NormalizeSeriesTitle(item.Title))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var manifestItem in manifestItems)
        {
            var sourcePosition = ManifestSourcePosition(manifestItem);
            var positionSort = ManifestOrderingSort(manifestItem);
            var position = ToDisplayPositionNumber(sourcePosition);
            var positionLabel = sourcePosition.HasValue
                ? StringHelpers.FirstNonBlankOr(string.Empty, manifestItem.RawOrdinal, FormatSequenceSort(sourcePosition))
                : null;
            var isLinkedOwned = manifestItem.LinkedWorkId.HasValue;
            var isCurrentManifestItem = string.Equals(
                manifestItem.ItemQid,
                currentQid,
                StringComparison.OrdinalIgnoreCase);

            if ((isLinkedOwned || isCurrentManifestItem)
                && TryApplyManifestPositionToOwnedItem(
                    merged,
                    manifestItem,
                    positionSort,
                    sourcePosition,
                    currentWorkId,
                    isCurrentManifestItem))
            {
                ownedPositions = BuildOwnedPositionSet(merged);

                continue;
            }

            var normalizedManifestTitle = NormalizeSeriesTitle(manifestItem.ItemLabel);
            if (!string.IsNullOrWhiteSpace(normalizedManifestTitle)
                && ownedTitles.Contains(normalizedManifestTitle)
                && TryApplyManifestPositionToOwnedItemByTitle(merged, normalizedManifestTitle, positionSort, sourcePosition, manifestItem))
            {
                ownedPositions = BuildOwnedPositionSet(merged);

                continue;
            }

            if (!string.IsNullOrWhiteSpace(manifestItem.ItemQid) && ownedQids.Contains(manifestItem.ItemQid))
            {
                continue;
            }

            var positionKey = SequencePositionKey(sourcePosition);
            if (!string.IsNullOrWhiteSpace(positionKey) && ownedPositions.Contains(positionKey))
            {
                continue;
            }

            merged.Add(new SequenceItemViewModel
            {
                Id = $"missing-{manifestItem.ItemQid}",
                EntityType = entityType,
                Title = StringHelpers.FirstNonBlankOr(string.Empty, manifestItem.ItemLabel, manifestItem.ItemQid) ?? "Missing from library",
                Description = manifestItem.ItemDescription,
                Duration = manifestItem.Duration,
                PublicationDate = manifestItem.PublicationDate,
                PositionNumber = position,
                PositionSort = positionSort,
                PositionLabel = positionLabel,
                GroupKey = ManifestScopeGroup(manifestItem.MembershipScope).Key,
                GroupTitle = ManifestScopeGroup(manifestItem.MembershipScope).Title,
                MembershipScope = manifestItem.MembershipScope,
                IsOwned = false,
                ProgressState = LibraryProgressState.Unknown,
            });

            if (!string.IsNullOrWhiteSpace(positionKey))
            {
                ownedPositions.Add(positionKey);
            }
        }

        return DeduplicateManifestMergeItems(merged)
            .OrderBy(item => item.PositionSort ?? item.PositionNumber ?? double.MaxValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double? ManifestSourcePosition(SeriesManifestItemRecord item)
    {
        if (!string.IsNullOrWhiteSpace(item.OrdinalScopeQid)
            && !SequenceContainerIdEquals(item.OrdinalScopeQid, item.SeriesQid))
        {
            return null;
        }

        if (item.ParsedOrdinal.HasValue)
        {
            return item.ParsedOrdinal.Value;
        }

        return TryParseSeriesPositionSort(item.RawOrdinal);
    }

    private static double? ManifestOrderingSort(SeriesManifestItemRecord item)
        => ManifestSourcePosition(item) ?? item.SortOrder;

    private static HashSet<string> BuildOwnedPositionSet(IEnumerable<SequenceItemViewModel> items)
        => items
            .Select(item => SequencePositionKey(item.PositionSort ?? item.PositionNumber))
            .OfType<string>()
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<SequenceItemViewModel> DeduplicateManifestMergeItems(
        IEnumerable<SequenceItemViewModel> items)
    {
        return items
            .GroupBy(BuildManifestMergeKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.IsOwned)
                .ThenByDescending(item => item.IsCurrent)
                .First());
    }

    private static string BuildManifestMergeKey(SequenceItemViewModel item)
    {
        if (item.Id.StartsWith("missing-", StringComparison.OrdinalIgnoreCase))
        {
            return $"qid:{item.Id["missing-".Length..]}";
        }

        var title = NormalizeSeriesTitle(item.Title);
        var positionKey = SequencePositionKey(item.PositionSort ?? item.PositionNumber);
        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(positionKey))
        {
            return $"title-position:{title}:{positionKey}";
        }

        if (Guid.TryParse(item.Id, out var linkedWorkId))
        {
            return $"work:{linkedWorkId:D}";
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            return $"title:{title}";
        }

        return $"id:{item.Id}";
    }

    private static bool TryApplyManifestPositionToOwnedItemByTitle(
        List<SequenceItemViewModel> items,
        string normalizedTitle,
        double? positionSort,
        double? sourcePosition,
        SeriesManifestItemRecord manifestItem)
    {
        var index = items.FindIndex(item =>
            item.IsOwned
            && string.Equals(NormalizeSeriesTitle(item.Title), normalizedTitle, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        var item = items[index];
        var position = ToDisplayPositionNumber(sourcePosition);
        var manifestPositionLabel = sourcePosition.HasValue
            ? StringHelpers.FirstNonBlankOr(string.Empty, manifestItem.RawOrdinal, FormatSequenceSort(sourcePosition))
            : null;
        var group = ManifestScopeGroup(manifestItem.MembershipScope);

        items[index] = new SequenceItemViewModel
        {
            Id = item.Id,
            EntityType = item.EntityType,
            Title = item.Title,
            ArtworkUrl = item.ArtworkUrl,
            EpisodeStillUrl = item.EpisodeStillUrl,
            EpisodeStillWidthPx = item.EpisodeStillWidthPx,
            EpisodeStillHeightPx = item.EpisodeStillHeightPx,
            Route = item.Route,
            Description = item.Description,
            Duration = item.Duration,
            PublicationDate = StringHelpers.FirstNonBlankOr(string.Empty, manifestItem.PublicationDate, item.PublicationDate),
            PositionNumber = position ?? item.PositionNumber,
            PositionSort = positionSort ?? item.PositionSort,
            PositionLabel = manifestPositionLabel ?? item.PositionLabel,
            PositionText = sourcePosition.HasValue ? null : item.PositionText,
            GroupKey = group.Key,
            GroupTitle = group.Title,
            MembershipScope = manifestItem.MembershipScope,
            IsCurrent = item.IsCurrent,
            IsOwned = item.IsOwned,
            ProgressState = item.ProgressState,
            EpisodeContext = item.EpisodeContext,
            ProgressPercent = item.ProgressPercent,
            PositionSeconds = item.PositionSeconds,
            DurationSeconds = item.DurationSeconds,
            RemainingSeconds = item.RemainingSeconds,
            ProgressLabel = item.ProgressLabel,
            CoveredWith = item.CoveredWith,
        };
        return true;
    }

    private static bool TryApplyManifestPositionToOwnedItem(
        List<SequenceItemViewModel> items,
        SeriesManifestItemRecord manifestItem,
        double? positionSort,
        double? sourcePosition,
        Guid currentWorkId,
        bool allowCurrentWorkFallback)
    {
        var index = items.FindIndex(item =>
            (manifestItem.LinkedWorkId.HasValue && string.Equals(item.Id, manifestItem.LinkedWorkId.Value.ToString("D"), StringComparison.OrdinalIgnoreCase))
            || (allowCurrentWorkFallback
                && item.IsCurrent
                && currentWorkId != Guid.Empty
                && string.Equals(item.Id, currentWorkId.ToString("D"), StringComparison.OrdinalIgnoreCase)));
        if (index < 0)
        {
            return false;
        }

        var item = items[index];
        var position = ToDisplayPositionNumber(sourcePosition);
        var manifestPositionLabel = sourcePosition.HasValue
            ? StringHelpers.FirstNonBlankOr(string.Empty, manifestItem.RawOrdinal, FormatSequenceSort(sourcePosition))
            : null;
        var group = ManifestScopeGroup(manifestItem.MembershipScope);

        items[index] = new SequenceItemViewModel
        {
            Id = item.Id,
            EntityType = item.EntityType,
            Title = item.Title,
            ArtworkUrl = item.ArtworkUrl,
            EpisodeStillUrl = item.EpisodeStillUrl,
            EpisodeStillWidthPx = item.EpisodeStillWidthPx,
            EpisodeStillHeightPx = item.EpisodeStillHeightPx,
            Route = item.Route,
            Description = item.Description,
            Duration = item.Duration,
            PublicationDate = StringHelpers.FirstNonBlankOr(string.Empty, manifestItem.PublicationDate, item.PublicationDate),
            PositionNumber = position ?? item.PositionNumber,
            PositionSort = positionSort ?? item.PositionSort,
            PositionLabel = manifestPositionLabel ?? item.PositionLabel,
            PositionText = sourcePosition.HasValue ? null : item.PositionText,
            GroupKey = group.Key,
            GroupTitle = group.Title,
            MembershipScope = manifestItem.MembershipScope,
            IsCurrent = item.IsCurrent,
            IsOwned = item.IsOwned,
            ProgressState = item.ProgressState,
            EpisodeContext = item.EpisodeContext,
            ProgressPercent = item.ProgressPercent,
            PositionSeconds = item.PositionSeconds,
            DurationSeconds = item.DurationSeconds,
            RemainingSeconds = item.RemainingSeconds,
            ProgressLabel = item.ProgressLabel,
            CoveredWith = item.CoveredWith,
        };
        return true;
    }

    private static (string Key, string Title) ManifestScopeGroup(string? membershipScope)
        => membershipScope switch
        {
            SeriesMembershipScopeNames.Supplementary => ("supplementary", "Short Fiction & Extras"),
            SeriesMembershipScopeNames.CollectedContent => ("collected-content", "Collected Content"),
            SeriesMembershipScopeNames.BroaderContext => ("broader-context", "Broader Context"),
            SeriesMembershipScopeNames.Unpositioned => ("unpositioned", "Unnumbered & Extras"),
            _ => ("main-sequence", "Main Series"),
        };

    private static bool IsManifestItemInMediaScope(SeriesManifestItemRecord item, DetailEntityType entityType)
    {
        if (item.IsCollection
            && !(string.Equals(item.MembershipScope, SeriesMembershipScopeNames.MainSequence, StringComparison.OrdinalIgnoreCase)
                && ManifestSourcePosition(item).HasValue))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(item.MediaKind)
            && !string.Equals(item.MediaKind, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return entityType switch
            {
                DetailEntityType.Movie or DetailEntityType.MovieSeries => item.MediaKind.Equals("Film", StringComparison.OrdinalIgnoreCase),
                DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode => item.MediaKind.Equals("Television", StringComparison.OrdinalIgnoreCase),
                DetailEntityType.ComicIssue or DetailEntityType.ComicSeries => item.MediaKind.Equals("Comic", StringComparison.OrdinalIgnoreCase),
                DetailEntityType.Audiobook => item.MediaKind.Equals("Audiobook", StringComparison.OrdinalIgnoreCase)
                    || item.MediaKind.Equals("LiteraryWork", StringComparison.OrdinalIgnoreCase),
                DetailEntityType.Book or DetailEntityType.BookSeries or DetailEntityType.Work => item.MediaKind.Equals("LiteraryWork", StringComparison.OrdinalIgnoreCase),
                DetailEntityType.MusicAlbum => item.MediaKind.Equals("Music", StringComparison.OrdinalIgnoreCase),
                _ => !item.MediaKind.Equals("StageWork", StringComparison.OrdinalIgnoreCase),
            };
        }

        var text = string.Join(' ', new[]
        {
            item.MediaType,
            item.ItemDescription,
            item.ParentCollectionLabel,
            item.SourcePropertiesJson,
            item.RelationshipsJson,
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return entityType switch
        {
            DetailEntityType.Movie or DetailEntityType.MovieSeries => ContainsAny(text, "film", "movie")
                && !ContainsAny(text, "short film", "television", "episode", "video game", "novel", "book", "comic"),
            DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode => ContainsAny(text, "television", "tv series", "episode", "season"),
            DetailEntityType.ComicIssue or DetailEntityType.ComicSeries => ContainsAny(text, "comic", "graphic novel", "manga"),
            DetailEntityType.Audiobook => ContainsAny(text, "audiobook", "audio book", "book", "novel"),
            DetailEntityType.Book or DetailEntityType.BookSeries or DetailEntityType.Work => ContainsAny(text, "book", "novel", "literary", "written work")
                && !ContainsAny(text, "comic", "film", "movie", "television", "video game"),
            DetailEntityType.MusicAlbum => ContainsAny(text, "album", "song", "single", "music"),
            _ => true,
        };
    }

    private static bool IsWatchEntityType(DetailEntityType entityType)
        => entityType is DetailEntityType.Movie
            or DetailEntityType.MovieSeries
            or DetailEntityType.TvShow
            or DetailEntityType.TvSeason
            or DetailEntityType.TvEpisode;

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

}
