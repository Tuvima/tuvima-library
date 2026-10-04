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
using MediaEngine.Contracts.Display;
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
    private static string ResolveSequenceItemTitle(DetailEntityType entityType, string title, string containerTitle, string? positionLabel)
    {
        if (entityType == DetailEntityType.ComicIssue && IsGeneratedComicIssueTitle(title, containerTitle, positionLabel))
        {
            return StringHelpers.FirstNonBlankOr(string.Empty, FormatIssue(positionLabel), title);
        }

        return title;
    }

    private static string NormalizeOrdinalTitle(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? BuildSubtitle(
        LibraryItemDetail detail,
        DetailEntityType entityType,
        IReadOnlyDictionary<string, string> values,
        MultiFormatState state)
    {
        if (state == (MultiFormatState)(-1))
        {
            return "Book + Audiobook • Separate Progress";
        }

        return entityType switch
        {
            DetailEntityType.Book => detail.Author,
            DetailEntityType.Audiobook => StringHelpers.FirstNonBlankOr(string.Empty, detail.Narrator, detail.Author),
            DetailEntityType.Movie => StringHelpers.FirstNonBlankOr(string.Empty, detail.Director, GetValue(values, "studio"), detail.Year, "Movie"),
            DetailEntityType.Work when detail.MediaType.Contains("music", StringComparison.OrdinalIgnoreCase)
                => string.Join(" · ", new[] { detail.Artist, GetValue(values, "album") }.Where(s => !string.IsNullOrWhiteSpace(s))),
            DetailEntityType.ComicIssue => string.Join(" - ", new[] { detail.Series, FormatIssue(detail.SeriesPosition), StringHelpers.FirstNonBlankOr(string.Empty, detail.Writer, detail.Illustrator, detail.Author) }.Where(s => !string.IsNullOrWhiteSpace(s))),
            DetailEntityType.TvEpisode => string.Join(" • ", new[] { detail.ShowName, FormatSeasonEpisode(detail.SeasonNumber, detail.EpisodeNumber) }.Where(s => !string.IsNullOrWhiteSpace(s))),
            _ => FormatEntityType(entityType),
        };
    }

    private static IReadOnlyList<RelationshipGroup> BuildRelationshipStrip(LibraryItemDetail detail, SequencePlacementViewModel? sequence)
    {
        var groups = new List<RelationshipGroup>();
        if (sequence is not null)
        {
            groups.Add(new RelationshipGroup
            {
                Title = sequence.ContainerLabel,
                Items = [new RelatedEntityChip
                {
                    Id = sequence.ContainerId,
                    EntityType = RelatedEntityType.Series,
                    Label = sequence.ContainerTitle,
                    Route = BuildSequenceContainerRoute(sequence),
                }],
            });
        }

        var universeQid = ExtractQid(detail.UniverseSummary?.UniverseQid);
        if (!string.IsNullOrWhiteSpace(detail.UniverseSummary?.UniverseName))
        {
            groups.Add(new RelationshipGroup
            {
                Title = "Universe",
                Items = [new RelatedEntityChip
                {
                    Id = universeQid ?? detail.UniverseSummary.UniverseName!,
                    EntityType = RelatedEntityType.Universe,
                    Label = detail.UniverseSummary.UniverseName!,
                    Route = BuildUniverseExploreRoute(universeQid),
                }],
            });
        }

        return groups;
    }

    private static string? BuildSequenceContainerRoute(SequencePlacementViewModel sequence)
    {
        if (!Guid.TryParse(sequence.ContainerId, out var id))
        {
            return null;
        }

        var entityType = sequence.CurrentItem.EntityType switch
        {
            DetailEntityType.Movie => DetailEntityType.MovieSeries,
            DetailEntityType.TvEpisode or DetailEntityType.TvSeason => DetailEntityType.TvShow,
            DetailEntityType.ComicIssue => DetailEntityType.ComicSeries,
            DetailEntityType.Audiobook => DetailEntityType.Collection,
            DetailEntityType.Book or DetailEntityType.Work => DetailEntityType.BookSeries,
            _ => (DetailEntityType?)null,
        };

        return entityType is null
            ? null
            : $"/details/{ToDetailRouteEntityType(entityType.Value)}/{id:D}?context={DetailContextKey(entityType.Value)}";
    }

    private static string? BuildUniverseExploreRoute(string? qid)
    {
        var normalizedQid = ExtractQid(qid);
        return IsWikidataQid(normalizedQid) ? $"/universe/{normalizedQid}/explore" : null;
    }

    private static string ToDetailRouteEntityType(DetailEntityType entityType)
        => entityType.ToString().Replace("Tv", "tv-", StringComparison.Ordinal).ToLowerInvariant();

    private static string DetailContextKey(DetailEntityType entityType) => entityType switch
    {
        DetailEntityType.Movie or DetailEntityType.MovieSeries or DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode => "watch",
        DetailEntityType.MusicAlbum or DetailEntityType.Audiobook => "listen",
        DetailEntityType.Book or DetailEntityType.BookSeries or DetailEntityType.ComicIssue or DetailEntityType.ComicSeries or DetailEntityType.Work => "read",
        _ => "default",
    };

    private static bool HasUniverseRelationship(IReadOnlyList<RelationshipGroup> relationships) =>
        relationships.Any(group =>
            string.Equals(group.Title, "Universe", StringComparison.OrdinalIgnoreCase) ||
            group.Items.Any(item => item.EntityType == RelatedEntityType.Universe));

    private static HeroBrandViewModel? BuildHeroBrand(DetailEntityType entityType, string? label, string? imageUrl)
    {
        if (entityType is not (DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        return new HeroBrandViewModel
        {
            Label = string.IsNullOrWhiteSpace(label) ? null : label,
            ImageUrl = string.IsNullOrWhiteSpace(imageUrl) ? null : imageUrl,
        };
    }

    private static IReadOnlyList<CollectionWorkSummary> MergeCollectionManifestPlaceholders(
        DetailEntityType entityType,
        IReadOnlyList<CollectionWorkSummary> works,
        SeriesManifestViewDto? manifest)
    {
        if (manifest?.Items.Count is not > 0 || entityType is not (DetailEntityType.BookSeries or DetailEntityType.ComicSeries or DetailEntityType.MovieSeries or DetailEntityType.TvShow))
        {
            return works;
        }

        var byId = works
            .Where(work => Guid.TryParse(work.Id, out _))
            .ToDictionary(work => work.Id, StringComparer.OrdinalIgnoreCase);
        var byTitle = works
            .GroupBy(work => NormalizeSeriesTitle(work.Title), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(group => group.Key!, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CollectionWorkSummary>();

        foreach (var item in manifest.Items.OrderBy(ManifestItemSortOrder).ThenBy(item => item.ItemLabel ?? item.ItemQid, StringComparer.OrdinalIgnoreCase))
        {
            var linkedId = item.LinkedWorkId?.ToString("D");
            if (!string.IsNullOrWhiteSpace(linkedId) && byId.TryGetValue(linkedId, out var linkedWork))
            {
                if (consumed.Add(linkedWork.Id))
                {
                    result.Add(ApplyManifestPlacement(linkedWork, item));
                }

                continue;
            }

            var titleKey = NormalizeSeriesTitle(item.ItemLabel);
            if (entityType != DetailEntityType.TvShow && !string.IsNullOrWhiteSpace(titleKey) && byTitle.TryGetValue(titleKey, out var titledWork) && consumed.Add(titledWork.Id))
            {
                result.Add(ApplyManifestPlacement(titledWork, item));
                continue;
            }

            result.Add(CreateMissingManifestWork(entityType, item));
        }

        result.AddRange(works.Where(work => consumed.Add(work.Id)));
        return result;
    }

    private static IReadOnlyList<CollectionWorkSummary> MergeMusicAlbumManifestTracks(
        IReadOnlyList<CollectionWorkSummary> ownedTracks,
        IReadOnlyDictionary<string, string> canonicalValues,
        string? albumCover)
    {
        var childEntitiesJson = GetValue(canonicalValues, MetadataFieldConstants.ChildEntitiesJson);
        if (string.IsNullOrWhiteSpace(childEntitiesJson))
        {
            return SortMusicAlbumTracks(ownedTracks);
        }

        try
        {
            using var document = JsonDocument.Parse(childEntitiesJson);
            if (!document.RootElement.TryGetProperty("tracks", out var trackArray)
                || trackArray.ValueKind != JsonValueKind.Array)
            {
                return SortMusicAlbumTracks(ownedTracks);
            }

            var manifestTracks = SelectMusicAlbumManifestTracks(
                trackArray,
                canonicalValues,
                ownedTracks.Select(track => track.DiscNumber));
            var remainingOwned = ownedTracks.ToList();
            var merged = new List<CollectionWorkSummary>();
            var manifestIndex = 0;
            foreach (var element in manifestTracks)
            {
                manifestIndex++;
                var title = ReadDetailJsonString(element, "title", "name");
                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var trackNumber = ReadDetailJsonInt(element, "track_number", "trackNumber", "number");
                var discNumber = ReadDetailJsonInt(element, "disc_number", "discNumber") ?? 1;
                var ordinal = ReadDetailJsonInt(element, "ordinal", "position") ?? trackNumber ?? manifestIndex;
                var normalizedTitle = NormalizeDetailTrackTitle(title);
                var match = remainingOwned
                    .Where(work => string.Equals(
                        NormalizeDetailTrackTitle(work.Title),
                        normalizedTitle,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(work => work.DiscNumber.HasValue && work.DiscNumber != discNumber)
                    .ThenBy(work => TryParseInt(work.TrackNumber).HasValue
                        && trackNumber.HasValue
                        && TryParseInt(work.TrackNumber) != trackNumber)
                    .FirstOrDefault();

                if (match is not null)
                {
                    remainingOwned.Remove(match);
                    merged.Add(match with
                    {
                        Ordinal = match.Ordinal ?? ordinal,
                        TrackNumber = StringHelpers.FirstNonBlankOr(string.Empty,
                            match.TrackNumber,
                            trackNumber?.ToString(CultureInfo.InvariantCulture)),
                        DiscNumber = match.DiscNumber ?? discNumber,
                        Duration = StringHelpers.FirstNonBlankOr(string.Empty, match.Duration, FormatManifestTrackDuration(element)),
                    });
                    continue;
                }

                var missingId = $"missing-track-{discNumber}-{trackNumber ?? ordinal}-{manifestIndex}";
                merged.Add(new CollectionWorkSummary(
                    missingId,
                    "Music",
                    ordinal,
                    title.Trim(),
                    null,
                    null,
                    null,
                    (trackNumber ?? ordinal).ToString(CultureInfo.InvariantCulture),
                    discNumber,
                    FormatManifestTrackDuration(element),
                    StringHelpers.FirstNonBlankOr(string.Empty,
                        ReadDetailJsonString(element, "release_date", "releaseDate", "year"),
                        GetValue(canonicalValues, "release_date"),
                        GetValue(canonicalValues, "release_year"),
                        GetValue(canonicalValues, "year")),
                    FormatContributorList(StringHelpers.FirstNonBlankOr(string.Empty,
                        ReadDetailJsonString(element, "artist", "artist_name", "artistName"),
                        GetValue(canonicalValues, "artist"),
                        GetValue(canonicalValues, "album_artist"))),
                    false,
                    null,
                    null,
                    false,
                    "Missing",
                    true,
                    albumCover,
                    null,
                    null));
            }

            merged.AddRange(remainingOwned);
            return SortMusicAlbumTracks(merged);
        }
        catch (JsonException)
        {
            // Provider manifests are best-effort enrichment; malformed JSON degrades to owned tracks.
            return SortMusicAlbumTracks(ownedTracks);
        }
    }

    internal static IReadOnlyList<JsonElement> SelectMusicAlbumManifestTracks(
        JsonElement trackArray,
        IReadOnlyDictionary<string, string> canonicalValues,
        IEnumerable<int?> ownedDiscNumbers)
    {
        var tracks = trackArray.EnumerateArray().ToList();
        var canonicalDiscCount = TryParseInt(GetValue(canonicalValues, MetadataFieldConstants.DiscCount));
        var ownedDiscs = ownedDiscNumbers
            .Where(number => number.HasValue)
            .Select(number => number!.Value)
            .Distinct()
            .ToList();
        var canonicalDiscNumber = TryParseInt(GetValue(canonicalValues, MetadataFieldConstants.DiscNumber))
            ?? (ownedDiscs.Count == 1 ? ownedDiscs[0] : null);

        // Retail providers sometimes resolve one tagged album to a much larger box set.
        // When that manifest spans more than three discs, the file's canonical disc
        // identifies the album-sized slice that should appear in this detail surface.
        // Ordinary one-, two-, and three-disc albums retain their complete manifests.
        if (canonicalDiscCount is <= 3 || canonicalDiscNumber is null)
        {
            return tracks;
        }

        var scopedTracks = tracks
            .Where(track => ReadDetailJsonInt(track, "disc_number", "discNumber") == canonicalDiscNumber)
            .ToList();

        return scopedTracks.Count > 0 ? scopedTracks : tracks;
    }

    private static IReadOnlyList<CollectionWorkSummary> SortMusicAlbumTracks(
        IEnumerable<CollectionWorkSummary> tracks)
        => tracks
            .OrderBy(track => track.DiscNumber ?? 1)
            .ThenBy(track => TryParseInt(track.TrackNumber) ?? track.Ordinal ?? int.MaxValue)
            .ThenBy(track => track.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string NormalizeDetailTrackTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().ToLowerInvariant();
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"\s*[\(\[\{].*?[\)\]\}]\s*",
            " ");
        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"\b(remaster(ed)?|remix|mono|stereo|explicit|clean|single version|album version)\b",
            " ");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"[^\p{L}\p{Nd}]+", " ");
        return string.Join(' ', normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? FormatManifestTrackDuration(JsonElement element)
    {
        var seconds = ReadDetailJsonDouble(element, "duration_seconds", "durationSeconds");
        if (seconds is not > 0)
        {
            var milliseconds = ReadDetailJsonDouble(
                element,
                "duration_ms",
                "durationMillis",
                "trackTimeMillis");
            seconds = milliseconds is > 0 ? milliseconds.Value / 1000d : null;
        }

        if (seconds is > 0)
        {
            var rounded = (int)Math.Round(seconds.Value, MidpointRounding.AwayFromZero);
            var span = TimeSpan.FromSeconds(rounded);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes}:{span.Seconds:00}";
        }

        return ReadDetailJsonString(element, "duration", "runtime");
    }

    private static string? ReadDetailJsonString(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static int? ReadDetailJsonInt(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.String
                && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static double? ReadDetailJsonDouble(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.String
                && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static double ManifestItemSortOrder(SeriesManifestItemDto item) =>
        item.SortOrder ?? item.ParsedOrdinal ?? double.MaxValue;

    private static CollectionWorkSummary ApplyManifestPlacement(CollectionWorkSummary work, SeriesManifestItemDto item)
    {
        var sequenceSort = item.ParsedOrdinal ?? item.SortOrder;
        return work with
        {
            SequenceSort = sequenceSort,
            SequenceLabel = StringHelpers.FirstNonBlankOr(string.Empty, item.RawOrdinal, sequenceSort.HasValue ? FormatSequenceSort(sequenceSort) : null),
            MembershipScope = item.MembershipScope,
            Year = StringHelpers.FirstNonBlankOr(string.Empty, work.Year, item.PublicationDate),
        };
    }

    private static CollectionWorkSummary CreateMissingManifestWork(DetailEntityType entityType, SeriesManifestItemDto item)
    {
        var qid = NormalizeQid(item.ItemQid) ?? item.ItemQid;
        var season = entityType == DetailEntityType.TvShow
            ? ProviderManifestSeasonNumber(item.SeriesQid)
            : null;
        return new CollectionWorkSummary(
            $"missing-{qid}",
            ManifestPlaceholderMediaType(entityType, item.MediaType),
            item.SortOrder is { } sortOrder ? (int)Math.Round(sortOrder, MidpointRounding.AwayFromZero) : null,
            StringHelpers.FirstNonBlankOr(string.Empty, item.ItemLabel, item.ItemQid, "Missing from library"),
            entityType == DetailEntityType.TvShow ? null : item.ItemDescription,
            season,
            entityType == DetailEntityType.TvShow ? item.RawOrdinal : null,
            null,
            null,
            item.Duration,
            item.PublicationDate,
            null,
            false,
            null,
            null,
            false,
            "Missing",
            true,
            null,
            null,
            null)
        {
            SequenceSort = item.ParsedOrdinal ?? item.SortOrder,
            SequenceLabel = StringHelpers.FirstNonBlankOr(string.Empty, item.RawOrdinal, FormatSequenceSort(item.ParsedOrdinal ?? item.SortOrder)),
            MembershipScope = item.MembershipScope,
        };
    }

    private static string ManifestPlaceholderMediaType(DetailEntityType entityType, string? mediaType) =>
        StringHelpers.FirstNonBlankOr(string.Empty, mediaType, entityType switch
        {
            DetailEntityType.ComicSeries => "Comic",
            DetailEntityType.MovieSeries => "Movie",
            DetailEntityType.BookSeries => "Book",
            _ => "Unknown",
        });

    private static string? PublicationYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var year = value.Trim();
        return year.Length >= 4 && year.Take(4).All(char.IsDigit) ? year[..4] : year;
    }

    private static SequencePlacementViewModel? BuildCollectionSequencePlacement(
        Guid collectionId,
        DetailEntityType entityType,
        string containerTitle,
        string? sourceContainerId,
        string? containerDescription,
        IReadOnlyList<CollectionWorkSummary> works,
        int? expectedTotal,
        IReadOnlyDictionary<string, int>? authoritativeTotalsByContainer,
        IReadOnlyDictionary<string, SeasonArtworkPresentation>? seasonArtwork,
        Guid? currentWorkId = null,
        Guid? showRootId = null)
    {
        if (entityType is not (DetailEntityType.TvShow
            or DetailEntityType.MovieSeries
            or DetailEntityType.BookSeries
            or DetailEntityType.ComicSeries))
        {
            return null;
        }

        var orderedWorks = entityType == DetailEntityType.TvShow
            ? DeduplicateTvEpisodeSummaries(works)
                .Where(work => expectedTotal is > 0 || work.IsOwned)
                .ToList()
            : works
                .OrderBy(work => work.SequenceSort ?? work.Ordinal ?? double.MaxValue)
                .ThenBy(work => work.Year, StringComparer.OrdinalIgnoreCase)
                .ThenBy(work => work.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        if (orderedWorks.Count == 0)
        {
            return null;
        }

        var labels = ResolveSequenceLabels(entityType);
        var items = orderedWorks.Select(work =>
        {
            var itemType = InferMediaItemEntityType(work);
            var positionLabel = entityType == DetailEntityType.TvShow
                ? StringHelpers.FirstNonBlankOr(string.Empty, work.Episode, work.SequenceLabel, work.Ordinal?.ToString(CultureInfo.InvariantCulture))
                : StringHelpers.FirstNonBlankOr(string.Empty, work.SequenceLabel, work.Ordinal?.ToString(CultureInfo.InvariantCulture));
            var positionSort = work.SequenceSort ?? TryParseSeriesPositionSort(positionLabel) ?? work.Ordinal;
            var positionNumber = ToDisplayPositionNumber(positionSort) ?? TryParseInt(positionLabel);
            var season = entityType == DetailEntityType.TvShow
                ? StringHelpers.FirstNonBlankOr(string.Empty, NormalizeEpisodeKey(work.Season), "unknown")
                : null;
            var scopeGroup = ManifestScopeGroup(work.MembershipScope);
            var groupKey = season is null ? scopeGroup.Key : $"season-{season}";
            var groupTitle = season is null ? scopeGroup.Title : SeasonDisplayTitle(season);

            return new SequenceItemViewModel
            {
                Id = work.Id,
                EntityType = itemType,
                Title = work.Title,
                Description = entityType == DetailEntityType.TvShow && !work.IsOwned ? null : work.Description,
                Duration = FormatTrackDuration(work.Duration),
                ArtworkUrl = entityType == DetailEntityType.TvShow && !work.IsOwned ? null : entityType == DetailEntityType.TvShow
                    ? work.BackgroundUrl
                    : work.ArtworkUrl,
                Route = work.IsOwned && entityType == DetailEntityType.TvShow && showRootId.HasValue && Guid.TryParse(work.Id, out var episodeRouteId)
                    ? TvEpisodeDetailRoute.Build(showRootId.Value, episodeRouteId) : work.IsOwned ? BuildWorkRoute(work) : null,
                EpisodeContext = work.IsOwned && entityType == DetailEntityType.TvShow && showRootId.HasValue
                    && Guid.TryParse(work.Id, out var episodeWorkId) && Guid.TryParse(work.AssetId, out var episodeAssetId)
                    ? new DisplayEpisodeContextDto(showRootId.Value, episodeWorkId, episodeAssetId, containerTitle, work.Title,
                        TryParseInt(work.Season), TryParseInt(work.Episode), DisplayCardBuilder.StateFor(work.ProgressPercent),
                        work.PositionSeconds is >= 0 ? work.PositionSeconds : null, work.DurationSeconds is > 0 ? work.DurationSeconds : null)
                    : null,
                PublicationDate = work.Year,
                PositionNumber = positionNumber,
                PositionSort = positionSort,
                PositionLabel = positionLabel,
                PositionText = entityType == DetailEntityType.TvShow && !string.IsNullOrWhiteSpace(positionLabel)
                    ? $"E{NormalizeSequenceOrdinal(positionLabel)}"
                    : positionLabel,
                GroupKey = groupKey,
                GroupTitle = groupTitle,
                MembershipScope = StringHelpers.FirstNonBlankOr(string.Empty, work.MembershipScope, SeriesMembershipScopeNames.MainSequence),
                IsCurrent = currentWorkId.HasValue
                    && string.Equals(work.Id, currentWorkId.Value.ToString("D"), StringComparison.OrdinalIgnoreCase),
                IsOwned = work.IsOwned,
                ProgressState = ResolveLibraryProgressState(work),
                ProgressPercent = work.IsOwned && work.MediaType != "Music" ? work.ProgressPercent : null,
                PositionSeconds = work.PositionSeconds is >= 0 ? work.PositionSeconds : null,
                DurationSeconds = work.DurationSeconds is > 0 ? work.DurationSeconds : null,
                RemainingSeconds = work.PositionSeconds is >= 0 && work.DurationSeconds is > 0 ? Math.Max(0, work.DurationSeconds.Value - work.PositionSeconds.Value) : null,
                ProgressLabel = SequenceProgressLabel(work),
            };
        }).ToList();

        var authoritativeTotalsBySeason = (authoritativeTotalsByContainer
                ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
            .Select(entry => (Season: ProviderManifestSeasonNumber(entry.Key), entry.Value))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Season))
            .GroupBy(entry => entry.Season!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(entry => entry.Value), StringComparer.OrdinalIgnoreCase);
        var groups = entityType == DetailEntityType.TvShow
            ? items
                .GroupBy(item => item.GroupKey ?? "season-1")
                .OrderBy(group => SeasonSortOrder(group.Key))
                .Select(group =>
                {
                    var seasonKey = group.Key.Replace("season-", string.Empty, StringComparison.OrdinalIgnoreCase);
                    SeasonArtworkPresentation? presentation = null;
                    seasonArtwork?.TryGetValue(seasonKey, out presentation);
                    return new SequenceGroupViewModel
                    {
                    OwnedCount = group.Where(item => item.IsOwned).Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    CompletedCount = group.Where(item => item.IsOwned && item.ProgressState == LibraryProgressState.Completed).Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    Key = group.Key,
                    Title = group.First().GroupTitle ?? "Season 1",
                    EntityId = presentation?.EntityId,
                    ArtworkSmallUrl = presentation?.ArtworkSmallUrl,
                    ArtworkUrl = presentation?.ArtworkUrl,
                    BackgroundArtworkUrl = presentation?.BackgroundArtworkUrl,
                    TotalKnownItems = authoritativeTotalsBySeason.TryGetValue(
                        seasonKey,
                        out var groupTotal)
                            ? groupTotal
                            : group.Count(),
                    HasAuthoritativeTotal = authoritativeTotalsBySeason.ContainsKey(
                        seasonKey),
                    Items = group.OrderBy(item => item.PositionSort ?? double.MaxValue).ToList(),
                    };
                })
                .ToList()
            : [];
        var initialGroup = groups.FirstOrDefault(group => !string.Equals(group.Key, "season-0", StringComparison.OrdinalIgnoreCase))
            ?? groups.FirstOrDefault();
        var selectedItem = currentWorkId.HasValue
            ? items.FirstOrDefault(item => item.IsCurrent)
            : null;
        var selectedGroup = selectedItem is null
            ? initialGroup
            : groups.FirstOrDefault(group => string.Equals(group.Key, selectedItem.GroupKey, StringComparison.OrdinalIgnoreCase));
        var representative = selectedItem
            ?? selectedGroup?.Items.FirstOrDefault(item => item.IsOwned)
            ?? initialGroup?.Items.FirstOrDefault()
            ?? items.FirstOrDefault(item => item.IsOwned)
            ?? items[0];
        var normalizedSourceId = NormalizeSequenceContainerId(sourceContainerId);
        var containerId = collectionId.ToString("D");
        var totalKnownItems = entityType == DetailEntityType.TvShow
            ? items.Count
            : Math.Max(items.Count, expectedTotal ?? 0);

        return new SequencePlacementViewModel
        {
            ContainerId = containerId,
            SourceContainerId = normalizedSourceId,
            ContainerTitle = containerTitle,
            ContainerDescription = containerDescription,
            SelectedContainerId = containerId,
            AvailableContainers =
            [
                new SequenceContainerOptionViewModel
                {
                    ContainerId = containerId,
                    SourceContainerId = normalizedSourceId,
                    ContainerTitle = containerTitle,
                    IsSelected = true,
                    IsDefault = true,
                    MediaScope = SeriesMediaFilter(entityType, entityType == DetailEntityType.TvShow ? "TV" : entityType.ToString()),
                    EquivalentContainerIds = string.IsNullOrWhiteSpace(normalizedSourceId) ? [] : [normalizedSourceId],
                }
            ],
            ContainerLabel = labels.ContainerLabel,
            ItemLabel = labels.ItemLabel,
            ItemPluralLabel = labels.ItemPluralLabel,
            GroupLabel = labels.GroupLabel,
            CurrentGroupKey = selectedGroup?.Key ?? initialGroup?.Key,
            TotalKnownItems = totalKnownItems,
            HasAuthoritativeTotal = entityType == DetailEntityType.TvShow
                ? selectedGroup?.HasAuthoritativeTotal == true
                : expectedTotal is > 0,
            OrderingType = entityType switch
            {
                DetailEntityType.TvShow => SequenceOrderingType.EpisodeNumber,
                DetailEntityType.ComicSeries => SequenceOrderingType.IssueNumber,
                DetailEntityType.MovieSeries => SequenceOrderingType.ReleaseOrder,
                _ => SequenceOrderingType.PublicationOrder,
            },
            CurrentItem = representative,
            OrderedItems = items,
            Groups = groups,
        };
    }

    private static string? SequenceProgressLabel(CollectionWorkSummary work)
    {
        if (!work.IsOwned || work.MediaType == "Music" || work.ProgressPercent is not > 0) return null;
        if (work.ProgressPercent >= 99.5) return DisplayMediaRules.NormalizeDisplayKind(work.MediaType) switch
        {
            "Book" or "Comic" => "Read", "TV" or "Movie" => "Watched", _ => "Finished",
        };
        var percent = $"{work.ProgressPercent:F0}%";
        return work.PositionSeconds is >= 0 && work.DurationSeconds is > 0
            ? $"{percent} · {Math.Ceiling(Math.Max(0, work.DurationSeconds.Value - work.PositionSeconds.Value) / 60):F0} min remaining"
            : percent;
    }

    private static LibraryProgressState ResolveLibraryProgressState(CollectionWorkSummary work)
    {
        if (!work.IsOwned)
        {
            return LibraryProgressState.Missing;
        }

        return work.ProgressPercent switch
        {
            >= 99.5 => LibraryProgressState.Completed,
            > 0 => LibraryProgressState.InProgress,
            _ => LibraryProgressState.Unstarted,
        };
    }

    private static string SeasonDisplayTitle(string season)
        => season == "unknown" ? "Unassigned" : season == "0" ? "Specials" : $"Season {season}";

    private static string? ProviderManifestSeasonNumber(string? containerId)
    {
        const string marker = ":season:";
        var index = containerId?.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        return index < 0 ? null : containerId![(index + marker.Length)..];
    }

    private static int SeasonSortOrder(string groupKey)
    {
        var season = TryParseInt(groupKey.Replace("season-", string.Empty, StringComparison.OrdinalIgnoreCase));
        return season == 0 ? int.MaxValue : season ?? int.MaxValue - 1;
    }

    private static IReadOnlyList<MediaGroupingViewModel> BuildCollectionMediaGroups(
        DetailEntityType entityType,
        IReadOnlyList<CollectionWorkSummary> works,
        IReadOnlySet<Guid> favoriteWorkIds,
        int? expectedTotal)
    {
        return
        [
            ApplyMediaGroupCompletion(new MediaGroupingViewModel
            {
                Key = entityType switch
                {
                    DetailEntityType.MusicAlbum => "tracks",
                    DetailEntityType.MovieSeries => "films",
                    DetailEntityType.BookSeries => "books",
                    DetailEntityType.ComicSeries => "issues",
                    _ => "items",
                },
                Title = entityType switch
                {
                    DetailEntityType.MusicAlbum => "Tracks",
                    DetailEntityType.MovieSeries => "Films",
                    DetailEntityType.BookSeries => "Books",
                    DetailEntityType.ComicSeries => "Issues",
                    _ => "Items",
                },
                Items = (entityType == DetailEntityType.MusicAlbum
                        ? SortMusicAlbumTracks(works)
                        : works)
                    .Select(work => ToMediaItem(work, favoriteWorkIds))
                    .ToList(),
                TotalCount = expectedTotal is > 0 ? expectedTotal.Value : 0,
            })
        ];
    }

    private static MediaGroupingViewModel ApplyMediaGroupCompletion(MediaGroupingViewModel group)
    {
        var total = Math.Max(group.Items.Count, group.TotalCount);
        var owned = group.Items.Count(item => item.IsOwned);
        var missing = Math.Max(0, total - owned);
        return new MediaGroupingViewModel
        {
            Key = group.Key,
            Title = group.Title,
            Items = group.Items,
            OwnedCount = owned,
            TotalCount = total,
            MissingCount = missing,
            CompletionPercent = total == 0 ? 0 : owned * 100.0 / total,
            InitiallyCollapsed = total > 0 && owned == 0,
        };
    }

    private static IReadOnlyList<CollectionWorkSummary> DeduplicateTvEpisodeSummaries(IReadOnlyList<CollectionWorkSummary> works)
    {
        return works
            .GroupBy(BuildTvEpisodeDeduplicationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(work => work.IsOwned)
                .ThenByDescending(work => !string.IsNullOrWhiteSpace(work.BackgroundUrl))
                .ThenByDescending(work => !string.IsNullOrWhiteSpace(work.ArtworkUrl))
                .ThenBy(work => work.Ordinal ?? int.MaxValue)
                .First())
            .OrderBy(work => TryParseInt(work.Season) ?? int.MaxValue)
            .ThenBy(work => TryParseInt(work.Episode) ?? work.Ordinal ?? int.MaxValue)
            .ThenBy(work => work.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildTvEpisodeDeduplicationKey(CollectionWorkSummary work)
    {
        var season = NormalizeEpisodeKey(work.Season);
        var episode = NormalizeEpisodeKey(work.Episode);

        if (!string.IsNullOrWhiteSpace(season) || !string.IsNullOrWhiteSpace(episode))
        {
            return !string.IsNullOrWhiteSpace(season) && !string.IsNullOrWhiteSpace(episode) ? $"{season}:{episode}" : $"work:{work.Id}";
        }

        return $"work:{work.Id}";
    }

    private static string NormalizeEpisodeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().TrimStart('0');
        return normalized.Length == 0 ? "0" : normalized;
    }

    private static string NormalizeTextKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

    private static MediaGroupingItemViewModel ToMediaItem(CollectionWorkSummary work, IReadOnlySet<Guid> favoriteWorkIds)
    {
        var entityType = InferMediaItemEntityType(work);
        return new MediaGroupingItemViewModel
        {
            Id = work.Id,
            EntityType = entityType,
            Title = work.Title,
            Subtitle = work.MediaType.Contains("music", StringComparison.OrdinalIgnoreCase)
                ? StringHelpers.FirstNonBlankOr(string.Empty, work.Artist, work.Year, FormatTrackDuration(work.Duration))
                : StringHelpers.FirstNonBlankOr(string.Empty, FormatSeasonEpisode(work.Season, work.Episode), work.Year, FormatTrackDuration(work.Duration)),
            Description = work.Description,
            ArtworkUrl = entityType switch
            {
                DetailEntityType.TvEpisode => StringHelpers.FirstNonBlankOr(string.Empty, work.BackgroundUrl, work.ArtworkUrl),
                DetailEntityType.TvShow => work.ArtworkUrl,
                _ => StringHelpers.FirstNonBlankOr(string.Empty, work.ArtworkUrl, work.BackgroundUrl),
            },
            TrackNumber = work.TrackNumber,
            Duration = FormatTrackDuration(work.Duration),
            Artist = work.Artist,
            AssetId = work.AssetId,
            Lane = work.MediaType switch
            {
                "Books" or "Comics" => "read",
                "Movies" or "TV" => "watch",
                "Music" or "Audiobooks" => "listen",
                _ => null,
            },
            IsExplicit = work.IsExplicit,
            Quality = work.Quality,
            ProgressPercent = work.ProgressPercent,
            Metadata = BuildEpisodeMetadata(FormatTrackDuration(work.Duration), work.Year),
            Actions = work.IsOwned
                ? [new DetailAction
                {
                    Key = entityType == DetailEntityType.TvEpisode ? "play" : "open",
                    Label = entityType == DetailEntityType.TvEpisode ? "Play" : "Open",
                    Icon = entityType == DetailEntityType.TvEpisode ? "play_arrow" : "open_in_new",
                    Route = BuildWorkRoute(work),
                }]
                : [],
            IsOwned = work.IsOwned,
            IsFavorite = Guid.TryParse(work.Id, out var workId) && favoriteWorkIds.Contains(workId),
            ProgressState = ResolveLibraryProgressState(work),
        };
    }

    private static IReadOnlyList<MetadataPill> BuildEpisodeMetadata(string? duration, string? year)
    {
        var values = new List<MetadataPill>();
        if (!string.IsNullOrWhiteSpace(duration))
        {
            values.Add(new MetadataPill { Label = duration, Kind = "duration" });
        }

        if (!string.IsNullOrWhiteSpace(year))
        {
            values.Add(new MetadataPill { Label = year, Kind = "year" });
        }

        return values;
    }

    private static DetailEntityType InferMediaItemEntityType(CollectionWorkSummary work)
    {
        if (work.DetailRoute?.StartsWith("/details/tvshow/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return DetailEntityType.TvShow;
        }

        if (work.DetailRoute?.StartsWith("/details/comicseries/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return DetailEntityType.ComicSeries;
        }

        if (work.DetailRoute?.StartsWith("/details/bookseries/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return DetailEntityType.BookSeries;
        }

        if (work.DetailRoute?.StartsWith("/details/movieseries/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return DetailEntityType.MovieSeries;
        }

        if (work.DetailRoute?.StartsWith("/details/collection/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return DetailEntityType.Collection;
        }

        return InferMediaItemEntityType(work.MediaType, work.Episode);
    }

    private static DetailEntityType InferMediaItemEntityType(string mediaType, string? episode)
    {
        if (!string.IsNullOrWhiteSpace(episode) || mediaType.Contains("TV", StringComparison.OrdinalIgnoreCase))
        {
            return DetailEntityType.TvEpisode;
        }

        if (mediaType.Contains("movie", StringComparison.OrdinalIgnoreCase))
        {
            return DetailEntityType.Movie;
        }

        if (mediaType.Contains("music", StringComparison.OrdinalIgnoreCase))
        {
            return DetailEntityType.Work;
        }

        if (mediaType.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            return DetailEntityType.Audiobook;
        }

        if (mediaType.Contains("comic", StringComparison.OrdinalIgnoreCase))
        {
            return DetailEntityType.ComicIssue;
        }

        return DetailEntityType.Book;
    }

    private static string BuildWorkRoute(CollectionWorkSummary work)
    {
        if (!string.IsNullOrWhiteSpace(work.DetailRoute))
        {
            return work.DetailRoute;
        }

        return InferMediaItemEntityType(work) switch
        {
            DetailEntityType.Movie or DetailEntityType.TvEpisode => $"/details/work/{work.Id}?context=watch",
            DetailEntityType.Audiobook => $"/details/work/{work.Id}?context=listen",
            DetailEntityType.Work when work.MediaType.Contains("music", StringComparison.OrdinalIgnoreCase)
                => $"/listen/music?browse=songs&track={work.Id:D}",
            DetailEntityType.ComicIssue => $"/details/work/{work.Id}?context=comics",
            _ => $"/details/work/{work.Id}?context=read",
        };
    }

    private static IReadOnlyList<MetadataPill> BuildCollectionMetadata(
        DetailEntityType entityType,
        IReadOnlyList<CollectionWorkSummary> works,
        IReadOnlyDictionary<string, string> values,
        CollectionWorkSummary? tvPlaybackEpisode = null,
        IReadOnlyDictionary<string, string>? tvPlaybackValues = null)
    {
        if (entityType == DetailEntityType.TvShow)
        {
            var pills = new List<MetadataPill>();
            var firstOwnedYear = works
                .Where(work => work.IsOwned && !string.IsNullOrWhiteSpace(work.Year))
                .Select(work => ReleaseYear(work.Year))
                .Where(year => !string.IsNullOrWhiteSpace(year))
                .OrderBy(year => year, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            var ownedEpisodeCount = works.Count(work => work.IsOwned && InferMediaItemEntityType(work) == DetailEntityType.TvEpisode);

            tvPlaybackValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddPlain(pills, StringHelpers.FirstNonBlankOr(string.Empty,
                GetValue(tvPlaybackValues, "content_rating"),
                GetValue(tvPlaybackValues, "certification"),
                GetValue(values, "content_rating"),
                GetValue(values, "certification")), "content_rating");
            AddPlain(pills, StringHelpers.FirstNonBlankOr(string.Empty,
                ReleaseYear(tvPlaybackEpisode?.Year),
                ReleaseYear(GetValue(tvPlaybackValues, "release_date")),
                GetValue(tvPlaybackValues, MetadataFieldConstants.Year),
                GetValue(values, "start_year"),
                ReleaseYear(GetValue(values, "first_air_date")),
                GetValue(values, MetadataFieldConstants.Year),
                GetValue(values, "release_year"),
                firstOwnedYear), "year");
            AddPlain(pills, ownedEpisodeCount > 0
                ? $"{ownedEpisodeCount.ToString(CultureInfo.InvariantCulture)} {(ownedEpisodeCount == 1 ? "episode" : "episodes")}"
                : null, "episode_count");
            AddPlain(pills, StringHelpers.FirstNonBlankOr(string.Empty,
                FormatTrackDuration(StringHelpers.FirstNonBlankOr(string.Empty,
                    tvPlaybackEpisode?.Duration,
                    GetValue(tvPlaybackValues, MetadataFieldConstants.Runtime),
                    GetValue(tvPlaybackValues, "duration"))),
                FormatSecondsDuration(ParseDurationSeconds(StringHelpers.FirstNonBlankOr(string.Empty,
                    GetValue(tvPlaybackValues, "duration_sec"),
                    GetValue(tvPlaybackValues, "duration_seconds"))))), "duration");
            AddPlain(pills, StringHelpers.FirstNonBlankOr(string.Empty,
                tvPlaybackEpisode?.Quality,
                GetValue(tvPlaybackValues, "quality"),
                GetValue(tvPlaybackValues, "video_quality"),
                GetValue(tvPlaybackValues, "resolution"),
                GetValue(values, "quality"),
                GetValue(values, "video_quality"),
                GetValue(values, "resolution"),
                GetValue(values, "video_resolution_label"),
                works.Where(work => work.IsOwned).Select(work => work.Quality).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))), "quality");
            AddPlain(pills, FormatRating(StringHelpers.FirstNonBlankOr(string.Empty,
                GetValue(tvPlaybackValues, MetadataFieldConstants.Rating),
                GetValue(values, MetadataFieldConstants.Rating))), "rating");

            var playbackGenres = StringHelpers.FirstNonBlankOr(string.Empty,
                GetValue(tvPlaybackValues, MetadataFieldConstants.Genre),
                GetValue(values, MetadataFieldConstants.Genre));
            foreach (var genre in SplitMetadataValues(playbackGenres).Take(2))
            {
                pills.Add(new MetadataPill
                {
                    Label = genre,
                    Kind = "genre",
                    Route = $"/search?genre={Uri.EscapeDataString(genre)}",
                    Tooltip = $"Browse {genre}",
                });
            }

            return pills
                .Where(value => !string.IsNullOrWhiteSpace(value.Label))
                .DistinctBy(value => $"{value.Kind}:{value.Label}", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (entityType == DetailEntityType.MusicAlbum)
        {
            var pills = new List<MetadataPill>();
            AddPlain(pills, FormatEntityType(entityType), "type");
            AddPlain(pills, StringHelpers.FirstNonBlankOr(string.Empty, GetValue(values, "year"), GetValue(values, "release_year"), works.Select(w => w.Year).FirstOrDefault(y => !string.IsNullOrWhiteSpace(y))), "year");
            AddPlain(pills, FormatCountLabel(works.Count.ToString(CultureInfo.InvariantCulture), "track"), "track_count");
            AddPlain(pills, FormatAlbumDuration(works), "duration");
            AddPlain(pills, GetValue(values, "genre"), "genre");
            AddPlain(pills, StringHelpers.FirstNonBlankOr(string.Empty, GetValue(values, "quality"), GetValue(values, "audio_quality"), works.Select(w => w.Quality).FirstOrDefault(q => !string.IsNullOrWhiteSpace(q))), "quality");
            return pills
                .Where(value => !string.IsNullOrWhiteSpace(value.Label))
                .DistinctBy(value => $"{value.Kind}:{value.Label}", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (entityType == DetailEntityType.Collection)
        {
            return BuildStandardCollectionMetadata(works);
        }

        if (entityType is DetailEntityType.BookSeries or DetailEntityType.ComicSeries or DetailEntityType.MovieSeries)
        {
            return [new MetadataPill { Label = OwnedCollectionCountLabel(entityType, works), Kind = "count" }];
        }

        return [new MetadataPill { Label = FormatEntityType(entityType), Kind = "type" }, new MetadataPill { Label = OwnedCollectionCountLabel(entityType, works), Kind = "count" }];
    }

    private static IReadOnlyList<MetadataPill> BuildStandardCollectionMetadata(
        IReadOnlyList<CollectionWorkSummary> works)
    {
        var ownedWorks = works
            .Where(work => work.IsOwned)
            .DistinctBy(work => $"{InferMediaItemEntityType(work)}:{work.Id}", StringComparer.OrdinalIgnoreCase)
            .ToList();
        var years = ownedWorks
            .Select(work => ReleaseYear(work.Year))
            .Where(year => int.TryParse(year, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(year => int.Parse(year!, CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .ToList();
        var metadata = new List<MetadataPill>();
        var yearLabel = years.Count switch
        {
            0 => null,
            1 => years[0].ToString(CultureInfo.InvariantCulture),
            _ => $"{years[0].ToString(CultureInfo.InvariantCulture)}–{years[^1].ToString(CultureInfo.InvariantCulture)}",
        };
        AddPlain(metadata, yearLabel, "year");
        AddPlain(
            metadata,
            $"{ownedWorks.Count.ToString(CultureInfo.InvariantCulture)} {(ownedWorks.Count == 1 ? "item" : "items")}",
            "item_count");

        var laneCounts = ownedWorks
            .GroupBy(work => DetailLane(InferMediaItemEntityType(work)), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key is "read" or "watch" or "listen")
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        AddLaneCount("read", "Read");
        AddLaneCount("watch", "Watch");
        AddLaneCount("listen", "Listen");
        return metadata;

        void AddLaneCount(string lane, string label)
        {
            if (!laneCounts.TryGetValue(lane, out var count) || count <= 0)
            {
                return;
            }

            metadata.Add(new MetadataPill
            {
                Label = count.ToString(CultureInfo.InvariantCulture),
                Kind = $"{lane}_count",
                Tooltip = $"{count.ToString(CultureInfo.InvariantCulture)} {label.ToLowerInvariant()} {(count == 1 ? "item" : "items")}",
            });
        }
    }

    private static IReadOnlyList<DetailAction> BuildCollectionActions(
        Guid id,
        DetailEntityType entityType,
        DetailPresentationContext context,
        ProgressViewModel? heroProgress,
        IReadOnlyList<CollectionWorkSummary> works)
        => entityType switch
        {
            DetailEntityType.TvShow => BuildTvShowWatchActions(works, heroProgress),
            DetailEntityType.MusicAlbum => BuildMusicAlbumActions(),
            DetailEntityType.Collection or DetailEntityType.BookSeries or DetailEntityType.ComicSeries or DetailEntityType.MovieSeries
                => [new DetailAction
                {
                    Key = "shuffle-collection",
                    Label = "Shuffle",
                    Icon = "shuffle",
                    Tooltip = "Open a random owned item",
                    IsPrimary = true,
                }],
            _ => [],
        };

    private static bool IsStructuralContainer(DetailEntityType entityType)
        => entityType is DetailEntityType.Collection
            or DetailEntityType.BookSeries
            or DetailEntityType.ComicSeries
            or DetailEntityType.MovieSeries;

    private static IReadOnlyList<DetailAction> BuildTvShowWatchActions(
        IReadOnlyList<CollectionWorkSummary> works,
        ProgressViewModel? heroProgress)
    {
        var episode = SelectActiveTvEpisode(works) ?? SelectFirstOwnedTvEpisode(works);
        if (episode is null || !Guid.TryParse(episode.Id, out var episodeId))
        {
            return BuildWatchActions(null, heroProgress);
        }

        if (works.Where(work => work.IsOwned).All(work => work.ProgressPercent >= 99.5))
        {
            return [new DetailAction { Key = "watch", Label = $"Rewatch {FormatSeasonEpisode(episode.Season ?? "?", episode.Episode ?? "?")}",
                Route = $"/watch/player/{episodeId:D}?restart=true", Icon = "play_arrow" }];
        }

        return BuildWatchActions(
            $"/watch/player/{episodeId:D}",
            heroProgress,
            FormatSeasonEpisode(episode.Season ?? "?", episode.Episode ?? "?"));
    }

    private static TvEpisodePlaybackContext ResolveTvContext(IReadOnlyList<CollectionWorkSummary> works)
        => TvEpisodeContextResolver.Resolve(works.Where(work => work.IsOwned)
            .Select(work => new TvEpisodePlaybackCandidate(work.Id, TryParseInt(work.Season), TryParseInt(work.Episode),
                work.ProgressPercent ?? 0, work.LastAccessed)));

    private static CollectionWorkSummary? SelectInProgressTvEpisode(IReadOnlyList<CollectionWorkSummary> works)
    {
        var context = ResolveTvContext(works);
        return context.Reason == TvEpisodeSelectionReason.Resume ? works.FirstOrDefault(w => w.Id == context.Target?.Id) : null;
    }
    private static CollectionWorkSummary? SelectActiveTvEpisode(IReadOnlyList<CollectionWorkSummary> works)
    {
        var context = ResolveTvContext(works);
        return context.UsesEpisodeArtwork ? works.FirstOrDefault(w => w.Id == context.Target?.Id) : null;
    }
    private static CollectionWorkSummary? SelectFirstOwnedTvEpisode(IReadOnlyList<CollectionWorkSummary> works)
        => works.FirstOrDefault(w => w.Id == ResolveTvContext(works).Target?.Id);

    private async Task<IReadOnlyList<CreditGroupViewModel>> BuildCollectionCreditsAsync(
        Guid collectionId,
        Guid? rootWorkId,
        IReadOnlyList<CollectionWorkSummary> works,
        DetailEntityType entityType,
        IReadOnlyDictionary<string, string> canonicalValues,
        CancellationToken ct)
    {
        if (entityType != DetailEntityType.TvShow)
        {
            var contributorValues = BuildCollectionContributorValues(entityType, canonicalValues, works);
            var collectionCredits = await BuildCollectionTextCreditsAsync(collectionId, entityType, contributorValues, ct);
            rootWorkId ??= works
                .Where(work => work.IsOwned)
                .Select(work => Guid.TryParse(work.Id, out var parsed) ? parsed : (Guid?)null)
                .FirstOrDefault(id => id.HasValue);
            if (collectionCredits.Count > 0 || !rootWorkId.HasValue)
            {
                return collectionCredits;
            }

            var rootDetail = await _libraryItems.GetDetailAsync(rootWorkId.Value, ct);
            if (rootDetail is null)
            {
                return collectionCredits;
            }

            var rootEntityType = InferWorkEntityType(rootDetail.MediaType, rootDetail);
            var rootValues = await LoadWorkCanonicalMapAsync(rootWorkId.Value, rootDetail, ct);
            var rootContributors = await BuildWorkContributorsAsync(rootWorkId.Value, rootDetail, rootEntityType, ct);
            return await BuildContributorGroupsAsync(
                rootWorkId.Value,
                rootDetail,
                rootEntityType,
                rootContributors.CastCredits,
                rootValues,
                ct);
        }

        var textCredits = await BuildCollectionTextCreditsAsync(collectionId, entityType, canonicalValues, ct);
        rootWorkId ??= works
            .Select(work => Guid.TryParse(work.Id, out var parsed) ? parsed : (Guid?)null)
            .FirstOrDefault(id => id.HasValue);

        if (!rootWorkId.HasValue)
        {
            return textCredits;
        }

        var cast = await _personCredits.BuildForWorkAsync(rootWorkId.Value, ct);
        if (cast.Count == 0)
        {
            return textCredits;
        }

        var credits = cast.Select((credit, index) => new EntityCreditViewModel
        {
            EntityId = BuildPersonCreditEntityId(credit.PersonId, credit.WikidataQid, credit.Name),
            EntityType = RelatedEntityType.Person,
            DisplayName = credit.Name,
            ImageUrl = credit.HeadshotUrl,
            FallbackInitials = Initials(credit.Name),
            PrimaryRole = "Actor",
            CharacterName = credit.Characters.FirstOrDefault()?.CharacterName,
            CharacterEntityId = credit.Characters.FirstOrDefault()?.FictionalEntityId.ToString("D"),
            CharacterImageUrl = credit.Characters.FirstOrDefault()?.PortraitUrl,
            SortOrder = index,
            IsPrimary = index < 8,
            IsCanonical = !string.IsNullOrWhiteSpace(credit.WikidataQid),
        }).ToList();

        return ApplyContributorGroupPresentation(
            entityType,
            textCredits.Concat(SplitCastGroups(credits)).ToList());
    }

    private static IReadOnlyDictionary<string, string> BuildCollectionContributorValues(
        DetailEntityType entityType,
        IReadOnlyDictionary<string, string> canonicalValues,
        IReadOnlyList<CollectionWorkSummary> works)
    {
        if (entityType != DetailEntityType.MusicAlbum
            || !string.IsNullOrWhiteSpace(GetValue(canonicalValues, MetadataFieldConstants.Artist)))
        {
            return canonicalValues;
        }

        var artists = works
            .SelectMany(work => SplitMetadataValues(work.Artist))
            .Where(artist => !string.IsNullOrWhiteSpace(artist))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artists.Count == 0)
        {
            return canonicalValues;
        }

        var values = canonicalValues.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        values[MetadataFieldConstants.Artist] = string.Join(", ", artists);
        return values;
    }

    private async Task<IReadOnlyList<CreditGroupViewModel>> BuildCollectionTextCreditsAsync(
        Guid collectionId,
        DetailEntityType entityType,
        IReadOnlyDictionary<string, string> canonicalValues,
        CancellationToken ct)
    {
        var groups = new List<CreditGroupViewModel>();

        async Task AddTextCreditAsync(string title, CreditGroupType type, string role, string canonicalArrayKey)
        {
            var entries = await LoadCollectionContributorEntriesAsync(
                collectionId,
                canonicalArrayKey,
                GetValue(canonicalValues, canonicalArrayKey),
                canonicalValues,
                ct);
            if (entries.Count == 0)
            {
                return;
            }

            var credits = new List<EntityCreditViewModel>();
            foreach (var entry in entries.Take(24))
            {
                var qid = NormalizeQid(entry.Qid);
                var person = string.IsNullOrWhiteSpace(qid) ? null : await _persons.FindByQidAsync(qid, ct);
                person ??= await _persons.FindByNameAsync(entry.Name, ct);
                var imageUrl = person is null
                    ? entries.Count == 1
                        ? StringHelpers.FirstNonBlankOr(string.Empty,
                            GetValue(canonicalValues, $"{canonicalArrayKey}_headshot_url"),
                            GetValue(canonicalValues, $"{canonicalArrayKey}_image_url"),
                            GetValue(canonicalValues, $"{canonicalArrayKey}_profile_url"),
                            GetValue(canonicalValues, $"{canonicalArrayKey}_photo_url"),
                            GetValue(canonicalValues, "headshot_url"))
                        : string.Empty
                    : ApiImageUrls.BuildPersonHeadshotUrl(person.Id, person.LocalHeadshotPath, person.HeadshotUrl);

                credits.Add(new EntityCreditViewModel
                {
                    EntityId = BuildPersonCreditEntityId(person?.Id, qid ?? person?.WikidataQid, entry.Name),
                    EntityType = RelatedEntityType.Person,
                    DisplayName = person?.Name ?? entry.Name,
                    ImageUrl = imageUrl,
                    FallbackInitials = Initials(person?.Name ?? entry.Name),
                    PrimaryRole = role,
                    SortOrder = entry.SortOrder,
                    IsPrimary = entry.SortOrder == 0,
                    IsCanonical = !string.IsNullOrWhiteSpace(qid ?? person?.WikidataQid),
                });
            }

            groups.Add(new CreditGroupViewModel
            {
                Title = title,
                GroupType = type,
                Credits = credits,
            });
        }

        switch (entityType)
        {
            case DetailEntityType.TvShow:
                await AddTextCreditAsync("Directors", CreditGroupType.Directors, "Director", "director");
                break;
            case DetailEntityType.MusicAlbum:
                await AddTextCreditAsync("Artists", CreditGroupType.PrimaryArtists, "Artist", "artist");
                await AddTextCreditAsync("Performers", CreditGroupType.FeaturedArtists, "Performer", "performer");
                await AddTextCreditAsync("Composers", CreditGroupType.MusicCredits, "Composer", "composer");
                break;
            case DetailEntityType.BookSeries:
                await AddTextCreditAsync("Authors", CreditGroupType.Authors, "Author", "author");
                break;
            case DetailEntityType.MovieSeries:
                await AddTextCreditAsync("Directors", CreditGroupType.Directors, "Director", "director");
                await AddTextCreditAsync("Writers", CreditGroupType.Writers, "Writer", "screenwriter");
                break;
            case DetailEntityType.ComicSeries:
                await AddTextCreditAsync("Writers", CreditGroupType.Writers, "Writer", "screenwriter");
                await AddTextCreditAsync("Artists", CreditGroupType.Illustrators, "Artist", "illustrator");
                break;
        }

        return ApplyContributorGroupPresentation(entityType, groups);
    }

}
