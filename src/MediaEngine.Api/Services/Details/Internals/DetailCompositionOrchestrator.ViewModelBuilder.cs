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
    private static DetailFactsViewModel BuildWorkFacts(
        LibraryItemDetail detail,
        DetailEntityType entityType,
        IReadOnlyDictionary<string, string> canonicalValues,
        IReadOnlyList<CreditGroupViewModel> contributorGroups)
    {
        var identifiers = BuildIdentifierFacts(canonicalValues, detail.BridgeIds, detail.WikidataQid);
        var artists = MergeNames(
            CreditNames(contributorGroups, CreditGroupType.PrimaryArtists),
            SplitMetadataValues(detail.Artist),
            SplitMetadataValues(GetValue(canonicalValues, MetadataFieldConstants.Artist)));
        var albumArtists = MergeNames(
            SplitMetadataValues(GetValue(canonicalValues, "album_artist")),
            SplitMetadataValues(GetValue(canonicalValues, MetadataFieldConstants.Author)));
        string? Canonical(string key) => GetValue(canonicalValues, key);

        return new DetailFactsViewModel
        {
            MediaKind = FormatEntityType(entityType),
            Year = StringHelpers.FirstNonBlankOr(
                string.Empty,
                detail.Year,
                MediaDateSemantics.ResolveOriginalYear(detail.MediaType, Canonical)),
            ReleaseDate = StringHelpers.FirstNonBlankOr(
                string.Empty,
                detail.ReleaseDate,
                MediaDateSemantics.ResolveOriginalDate(detail.MediaType, Canonical)),
            Rating = StringHelpers.FirstNonBlankOr(string.Empty, FormatRating(detail.Rating), detail.Rating, GetValue(canonicalValues, MetadataFieldConstants.Rating)),
            ContentRating = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, "content_rating"), GetValue(canonicalValues, "certification")),
            Runtime = FormatRuntime(detail.Runtime),
            Duration = StringHelpers.FirstNonBlankOr(string.Empty, FormatRuntime(detail.Runtime), FormatRuntime(GetValue(canonicalValues, MetadataFieldConstants.DurationField)), GetValue(canonicalValues, MetadataFieldConstants.DurationField)),
            Language = StringHelpers.FirstNonBlankOr(string.Empty, detail.Language, GetValue(canonicalValues, MetadataFieldConstants.Language), GetValue(canonicalValues, MetadataFieldConstants.OriginalLanguage)),
            Genres = SplitMetadataValues(StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, MetadataFieldConstants.Genre), detail.Genre)).ToList(),
            Identifiers = identifiers,

            Authors = MergeNames(CreditNames(contributorGroups, CreditGroupType.Authors), SplitMetadataValues(detail.Author)),
            Artists = artists,
            AlbumArtists = albumArtists,
            Actors = MergeNames(CreditNames(contributorGroups, CreditGroupType.Cast), SplitMetadataValues(detail.Cast)),
            Directors = MergeNames(CreditNames(contributorGroups, CreditGroupType.Directors), SplitMetadataValues(detail.Director)),
            Writers = MergeNames(CreditNames(contributorGroups, CreditGroupType.Writers), SplitMetadataValues(detail.Writer)),
            Composers = MergeNames(CreditNames(contributorGroups, CreditGroupType.MusicCredits), SplitMetadataValues(detail.Composer)),
            Narrators = MergeNames(CreditNames(contributorGroups, CreditGroupType.Narrators), SplitMetadataValues(detail.Narrator)),
            Illustrators = MergeNames(CreditNames(contributorGroups, CreditGroupType.Illustrators), SplitMetadataValues(detail.Illustrator)),
            Producers = MergeNames(CreditNames(contributorGroups, CreditGroupType.Producers), SplitMetadataValues(GetValue(canonicalValues, "producer"))),

            ShowName = StringHelpers.FirstNonBlankOr(string.Empty, detail.ShowName, GetValue(canonicalValues, MetadataFieldConstants.ShowName), GetValue(canonicalValues, MetadataFieldConstants.Series)),
            SeasonNumber = StringHelpers.FirstNonBlankOr(string.Empty, detail.SeasonNumber, GetValue(canonicalValues, MetadataFieldConstants.SeasonNumber), GetValue(canonicalValues, "season")),
            EpisodeNumber = StringHelpers.FirstNonBlankOr(string.Empty, detail.EpisodeNumber, GetValue(canonicalValues, MetadataFieldConstants.EpisodeNumber), GetValue(canonicalValues, "episode")),
            EpisodeTitle = StringHelpers.FirstNonBlankOr(string.Empty, detail.EpisodeTitle, GetValue(canonicalValues, MetadataFieldConstants.EpisodeTitle)),
            Network = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, MetadataFieldConstants.Network), GetValue(canonicalValues, "broadcaster")),
            SeasonCount = GetValue(canonicalValues, MetadataFieldConstants.SeasonCount),
            EpisodeCount = GetValue(canonicalValues, MetadataFieldConstants.EpisodeCount),

            Album = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, MetadataFieldConstants.Album), detail.Series),
            AlbumArtist = StringHelpers.FirstNonBlankOr(string.Empty, albumArtists.FirstOrDefault(), detail.Artist),
            TrackNumber = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, MetadataFieldConstants.TrackNumber), detail.SeriesPosition),
            TrackCount = GetValue(canonicalValues, MetadataFieldConstants.TrackCount),
            DiscNumber = GetValue(canonicalValues, MetadataFieldConstants.DiscNumber),
            DiscCount = GetValue(canonicalValues, MetadataFieldConstants.DiscCount),
            Isrc = GetValue(canonicalValues, "isrc"),
            Label = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, "label"), GetValue(canonicalValues, "record_label")),
            IsExplicit = ParseNullableBool(GetValue(canonicalValues, "explicit"), GetValue(canonicalValues, "is_explicit")),

            Series = StringHelpers.FirstNonBlankOr(string.Empty, detail.Series, GetValue(canonicalValues, MetadataFieldConstants.Series)),
            SeriesPosition = StringHelpers.FirstNonBlankOr(string.Empty, detail.SeriesPosition, GetValue(canonicalValues, MetadataFieldConstants.SeriesPosition)),
            IssueNumber = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, MetadataFieldConstants.IssueNumber), detail.SeriesPosition),
            IssueTitle = GetValue(canonicalValues, MetadataFieldConstants.IssueTitle),
            Publisher = GetValue(canonicalValues, MetadataFieldConstants.PublisherField),
            PageCount = GetValue(canonicalValues, MetadataFieldConstants.PageCount),
        };
    }

    private static DetailFactsViewModel BuildCollectionFacts(
        DetailEntityType entityType,
        IReadOnlyList<CollectionWorkSummary> works,
        IReadOnlyDictionary<string, string> canonicalValues,
        IReadOnlyList<CreditGroupViewModel> contributorGroups,
        string? wikidataQid)
    {
        var identifiers = BuildIdentifierFacts(canonicalValues, null, wikidataQid);
        var genres = SplitMetadataValues(GetValue(canonicalValues, MetadataFieldConstants.Genre)).ToList();
        var years = works.Select(work => work.Year).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        var suppressCollectionCredits = IsStructuralContainer(entityType);
        var artists = suppressCollectionCredits
            ? []
            : MergeNames(
                CreditNames(contributorGroups, CreditGroupType.PrimaryArtists),
                works
                    .SelectMany(work => SplitMetadataValues(work.Artist))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                SplitMetadataValues(GetValue(canonicalValues, MetadataFieldConstants.Artist)));
        var albumArtists = suppressCollectionCredits
            ? []
            : MergeNames(
                SplitMetadataValues(GetValue(canonicalValues, "album_artist")),
                SplitMetadataValues(GetValue(canonicalValues, MetadataFieldConstants.Author)),
                artists.Take(1));
        var seasonCount = StringHelpers.FirstNonBlankOr(string.Empty,
            GetValue(canonicalValues, MetadataFieldConstants.SeasonCount),
            entityType is DetailEntityType.TvShow
                ? works.Select(work => work.Season).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString(CultureInfo.InvariantCulture)
                : null);
        string? Canonical(string key) => GetValue(canonicalValues, key);
        var explicitOriginalYear = MediaDateSemantics.ResolveExplicitOriginalYear(entityType.ToString(), Canonical);
        var canonicalOriginalYear = MediaDateSemantics.ResolveOriginalYear(entityType.ToString(), Canonical);
        var displayedYear = entityType is DetailEntityType.MusicAlbum && explicitOriginalYear is null
            ? years.FirstOrDefault()
            : explicitOriginalYear ?? canonicalOriginalYear ?? years.FirstOrDefault();
        var displayedReleaseDate = entityType is DetailEntityType.MusicAlbum && explicitOriginalYear is null
            ? displayedYear
            : MediaDateSemantics.ResolveOriginalDate(entityType.ToString(), Canonical);

        return new DetailFactsViewModel
        {
            MediaKind = FormatEntityType(entityType),
            Year = displayedYear ?? string.Empty,
            ReleaseDate = displayedReleaseDate ?? string.Empty,
            Rating = StringHelpers.FirstNonBlankOr(string.Empty, FormatRating(GetValue(canonicalValues, MetadataFieldConstants.Rating)), GetValue(canonicalValues, MetadataFieldConstants.Rating)),
            ContentRating = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, "content_rating"), GetValue(canonicalValues, "certification")),
            Runtime = FormatRuntime(GetValue(canonicalValues, MetadataFieldConstants.Runtime)),
            Duration = StringHelpers.FirstNonBlankOr(string.Empty, FormatRuntime(GetValue(canonicalValues, MetadataFieldConstants.DurationField)), FormatCollectionDuration(works)),
            Language = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, MetadataFieldConstants.Language), GetValue(canonicalValues, MetadataFieldConstants.OriginalLanguage)),
            Genres = genres,
            Identifiers = identifiers,

            Authors = suppressCollectionCredits
                ? []
                : MergeNames(CreditNames(contributorGroups, CreditGroupType.Authors), SplitMetadataValues(GetValue(canonicalValues, MetadataFieldConstants.Author))),
            Artists = artists,
            AlbumArtists = albumArtists,
            Actors = suppressCollectionCredits ? [] : CreditNames(contributorGroups, CreditGroupType.Cast),
            Directors = suppressCollectionCredits ? [] : CreditNames(contributorGroups, CreditGroupType.Directors),
            Writers = suppressCollectionCredits ? [] : CreditNames(contributorGroups, CreditGroupType.Writers),
            Composers = suppressCollectionCredits ? [] : CreditNames(contributorGroups, CreditGroupType.MusicCredits),
            Narrators = suppressCollectionCredits ? [] : CreditNames(contributorGroups, CreditGroupType.Narrators),
            Illustrators = suppressCollectionCredits ? [] : CreditNames(contributorGroups, CreditGroupType.Illustrators),
            Producers = suppressCollectionCredits
                ? []
                : MergeNames(CreditNames(contributorGroups, CreditGroupType.Producers), SplitMetadataValues(GetValue(canonicalValues, "producer"))),

            ShowName = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, MetadataFieldConstants.ShowName), GetValue(canonicalValues, MetadataFieldConstants.Title)),
            Network = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, MetadataFieldConstants.Network), GetValue(canonicalValues, "broadcaster")),
            SeasonCount = seasonCount,
            EpisodeCount = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, MetadataFieldConstants.EpisodeCount), entityType is DetailEntityType.TvShow ? works.Count.ToString(CultureInfo.InvariantCulture) : null),

            Album = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, MetadataFieldConstants.Album), GetValue(canonicalValues, MetadataFieldConstants.Title)),
            AlbumArtist = albumArtists.FirstOrDefault(),
            TrackCount = entityType is DetailEntityType.MusicAlbum
                ? works.Count.ToString(CultureInfo.InvariantCulture)
                : GetValue(canonicalValues, MetadataFieldConstants.TrackCount),
            DiscCount = GetValue(canonicalValues, MetadataFieldConstants.DiscCount),
            Isrc = GetValue(canonicalValues, "isrc"),
            Label = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, "label"), GetValue(canonicalValues, "record_label")),
            IsExplicit = ParseNullableBool(GetValue(canonicalValues, "explicit"), GetValue(canonicalValues, "is_explicit")),

            Series = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, MetadataFieldConstants.Series), GetValue(canonicalValues, MetadataFieldConstants.Title)),
            SeriesPosition = GetValue(canonicalValues, MetadataFieldConstants.SeriesPosition),
            Publisher = GetValue(canonicalValues, MetadataFieldConstants.PublisherField),
            PageCount = GetValue(canonicalValues, MetadataFieldConstants.PageCount),
        };
    }

    private static DetailFactsViewModel BuildPersonFacts(Person person, IReadOnlyList<string> displayRoles)
        => new()
        {
            MediaKind = person.IsGroup ? "Group" : "Person",
            Identifiers = BuildIdentifierFacts(new Dictionary<string, string>(), null, person.WikidataQid),
            Artists = displayRoles.Any(role => role.Contains("Artist", StringComparison.OrdinalIgnoreCase) || role.Contains("Performer", StringComparison.OrdinalIgnoreCase))
                ? [person.Name]
                : [],
            Authors = displayRoles.Any(role => role.Contains("Author", StringComparison.OrdinalIgnoreCase)) ? [person.Name] : [],
            Actors = displayRoles.Any(role => role.Contains("Actor", StringComparison.OrdinalIgnoreCase)) ? [person.Name] : [],
            Directors = displayRoles.Any(role => role.Contains("Director", StringComparison.OrdinalIgnoreCase)) ? [person.Name] : [],
            Writers = displayRoles.Any(role => role.Contains("Writer", StringComparison.OrdinalIgnoreCase)) ? [person.Name] : [],
            Composers = displayRoles.Any(role => role.Contains("Composer", StringComparison.OrdinalIgnoreCase)) ? [person.Name] : [],
            Narrators = displayRoles.Any(role => role.Contains("Narrator", StringComparison.OrdinalIgnoreCase)) ? [person.Name] : [],
            Illustrators = displayRoles.Any(role => role.Contains("Illustrator", StringComparison.OrdinalIgnoreCase)) ? [person.Name] : [],
            Producers = displayRoles.Any(role => role.Contains("Producer", StringComparison.OrdinalIgnoreCase)) ? [person.Name] : [],
        };

    private static IReadOnlyDictionary<string, string> BuildIdentifierFacts(
        IReadOnlyDictionary<string, string> canonicalValues,
        IReadOnlyDictionary<string, string>? bridgeIds,
        string? wikidataQid)
    {
        var identifiers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifier(identifiers, BridgeIdKeys.WikidataQid, wikidataQid);
        foreach (var key in DetailIdentifierKeys)
        {
            if (bridgeIds is not null && bridgeIds.TryGetValue(key, out var bridgeValue))
            {
                AddIdentifier(identifiers, key, bridgeValue);
            }

            AddIdentifier(identifiers, key, GetValue(canonicalValues, key));
        }

        return identifiers;
    }

    private static readonly string[] DetailIdentifierKeys =
    [
        BridgeIdKeys.WikidataQid,
        BridgeIdKeys.TmdbId,
        BridgeIdKeys.ImdbId,
        BridgeIdKeys.AppleMusicId,
        BridgeIdKeys.AppleMusicCollectionId,
        BridgeIdKeys.AppleArtistId,
        BridgeIdKeys.MusicBrainzId,
        BridgeIdKeys.MusicBrainzRecordingId,
        BridgeIdKeys.MusicBrainzReleaseGroupId,
        "musicbrainz_release_id",
        "musicbrainz_artist_id",
        "isrc",
        BridgeIdKeys.Isbn,
        BridgeIdKeys.Asin,
        BridgeIdKeys.ComicVineId,
        BridgeIdKeys.ComicVineVolumeId,
    ];

    private static void AddIdentifier(IDictionary<string, string> identifiers, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        identifiers.TryAdd(key, value.Trim());
    }

    private static IReadOnlyList<string> CreditNames(
        IReadOnlyList<CreditGroupViewModel> groups,
        CreditGroupType type)
        => groups
            .Where(group => group.GroupType == type)
            .SelectMany(group => group.Credits)
            .Select(credit => credit.DisplayName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyList<string> MergeNames(params IEnumerable<string>[] sources)
        => sources
            .SelectMany(source => source)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool? ParseNullableBool(params string?[] values)
    {
        foreach (var value in values)
        {
            if (bool.TryParse(value, out var parsed))
            {
                return parsed;
            }

            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "explicit", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "clean", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return null;
    }

    private static string? ResolveDisplayOverride(
        IReadOnlyDictionary<string, string> overrides,
        string key) =>
        overrides.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static DescriptionAttributionViewModel BuildLocalDescriptionAttribution() => new()
    {
        SourceName = "Tuvima Library",
        SourceTitle = "local customization",
        LicenseName = "Local library value",
        IsModifiedOrSummarized = true,
        Notice = "This description was customized for this Tuvima Library.",
    };

    private static string? ReleaseYear(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Length < 4 ? null : value[..4];

    private static string? FormatCollectionDuration(IReadOnlyList<CollectionWorkSummary> works)
    {
        var seconds = works
            .Select(work => ParseDurationSeconds(work.Duration))
            .Where(value => value.HasValue)
            .Sum(value => value!.Value);

        return seconds > 0 ? FormatSecondsDuration(seconds) : null;
    }

    private static double? ParseDurationSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds;
        }

        return null;
    }

    private static IReadOnlyList<MetadataPill> BuildMetadataPills(
        LibraryItemDetail detail,
        DetailEntityType entityType,
        IReadOnlyDictionary<string, string> canonicalValues,
        IReadOnlyList<OwnedFormatViewModel> formats)
    {
        var pills = new List<MetadataPill>();
        AddPlain(pills, StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, "content_rating"), GetValue(canonicalValues, "certification")), "content_rating");
        AddPlain(pills, FormatRating(StringHelpers.FirstNonBlankOr(
            string.Empty,
            detail.Rating,
            GetValue(canonicalValues, MetadataFieldConstants.Rating))), "rating");

        foreach (var genre in SplitMetadataValues(StringHelpers.FirstNonBlankOr(string.Empty,
                     GetValue(canonicalValues, MetadataFieldConstants.Genre),
                     detail.Genre)).Take(12))
        {
            pills.Add(new MetadataPill
            {
                Label = genre,
                Kind = "genre",
                Route = $"/search?genre={Uri.EscapeDataString(genre)}",
                Tooltip = $"Browse {genre}",
            });
        }

        foreach (var tag in LibraryTagCatalog.ParseDisplayValue(GetValue(canonicalValues, MetadataFieldConstants.CustomTags)))
        {
            pills.Add(new MetadataPill { Label = tag, Kind = "custom_tag" });
        }

        AddPlain(pills, FormatEntityType(entityType), "type");
        AddPlain(pills, detail.Year, "year");
        AddPlain(pills, FormatRuntime(detail.Runtime), "duration");
        AddPlain(pills, FormatCountLabel(GetValue(canonicalValues, "page_count"), "page"), "page_count");
        AddPlain(pills, FormatCountLabel(GetValue(canonicalValues, "track_count"), "track"), "track_count");
        AddPlain(pills, FormatCountLabel(GetValue(canonicalValues, "season_count"), "season"), "season_count");
        AddPlain(pills, FormatCountLabel(GetValue(canonicalValues, "episode_count"), "episode"), "episode_count");
        AddPlain(pills, ResolveWatchQualityLabel(canonicalValues, detail.PlaybackSummary), "quality");
        if (HasSubtitles(canonicalValues, detail.PlaybackSummary))
        {
            AddPlain(pills, "CC", "subtitles");
        }

        AddPlain(pills, detail.Language, "audio");
        if (HasReadListenCompanion(entityType, formats))
        {
            AddPlain(pills, BuildReadListenAvailabilityLabel(entityType, formats), "sync");
        }

        return pills
            .Where(value => !string.IsNullOrWhiteSpace(value.Label))
            .DistinctBy(value => value.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ProgressViewModel? BuildFormatProgress(double? progressPct)
    {
        if (progressPct is not > 0)
        {
            return null;
        }

        var percent = Math.Clamp(progressPct.Value, 0, 100);
        return new ProgressViewModel
        {
            Percent = percent,
            Label = $"{Math.Max(1, percent):F0}%",
        };
    }

    private static ProgressViewModel? BuildHeroProgress(
        DetailEntityType entityType,
        string? runtime,
        IReadOnlyList<OwnedFormatViewModel> formats)
    {
        if (!IsWatchEntity(entityType)
            && entityType is not (DetailEntityType.Audiobook or DetailEntityType.Book or DetailEntityType.ComicIssue or DetailEntityType.Work))
        {
            return null;
        }

        var progressFormats = entityType switch
        {
            DetailEntityType.Audiobook => formats.Where(format => format.FormatType == MediaFormatType.Audiobook).ToList(),
            DetailEntityType.Book or DetailEntityType.ComicIssue or DetailEntityType.Work => formats
                .Where(format => format.FormatType is MediaFormatType.Ebook
                    or MediaFormatType.Paperback
                    or MediaFormatType.Hardcover
                    or MediaFormatType.ComicIssue
                    or MediaFormatType.ComicVolume)
                .ToList(),
            _ => formats,
        };
        var progress = progressFormats
            .Select(format => format.Progress)
            .Where(value => value?.Percent is > 0 and < 99.5)
            .OrderByDescending(value => value!.Percent)
            .FirstOrDefault();
        if (progress is null)
        {
            return null;
        }

        var percent = Math.Clamp(progress.Percent, 0, 100);
        var runtimeSource = StringHelpers.FirstNonBlankOr(string.Empty, progressFormats.Select(format => format.Runtime).Prepend(runtime).ToArray());
        return new ProgressViewModel
        {
            Percent = percent,
            Kind = entityType switch
            {
                DetailEntityType.Audiobook => DetailProgressKind.Listening,
                DetailEntityType.Book or DetailEntityType.ComicIssue or DetailEntityType.Work => DetailProgressKind.Reading,
                _ => DetailProgressKind.Watching,
            },
            Label = entityType switch
            {
                DetailEntityType.Audiobook => BuildListenHeroProgressLabel(percent, runtimeSource),
                DetailEntityType.Book or DetailEntityType.ComicIssue or DetailEntityType.Work => BuildReadHeroProgressLabel(percent),
                _ => BuildHeroProgressLabel(percent, runtimeSource),
            },
        };
    }

    private static ProgressViewModel? BuildAudiobookHeroProgress(
        DetailEntityType entityType,
        string? runtime,
        IReadOnlyList<MediaGroupingViewModel> mediaGroups)
    {
        if (entityType is not DetailEntityType.Audiobook)
        {
            return null;
        }

        var trackGroup = mediaGroups
            .FirstOrDefault(group => string.Equals(group.Key, "tracks", StringComparison.OrdinalIgnoreCase));
        var tracks = trackGroup?.Items ?? [];
        if (tracks.Count == 0)
        {
            return null;
        }

        var current = tracks
            .Where(item => item.ResumePositionSeconds is > 0)
            .OrderByDescending(item => item.ResumePositionSeconds)
            .FirstOrDefault()
            ?? tracks
                .Where(item => item.ProgressPercent is > 0 and < 99.5)
                .OrderByDescending(item => item.ProgressPercent)
                .FirstOrDefault();
        if (current is null)
        {
            return null;
        }

        var totalSeconds = tracks
            .Select(item => item.EndSeconds ?? item.DurationSeconds ?? 0)
            .Where(seconds => seconds > 0)
            .DefaultIfEmpty()
            .Max();
        var percent = current.ResumePositionSeconds is > 0 && totalSeconds > 0
            ? current.ResumePositionSeconds.Value / totalSeconds * 100
            : current.ProgressPercent ?? 0;
        if (percent is <= 0 or >= 99.5)
        {
            return null;
        }

        var runtimeSource = StringHelpers.FirstNonBlankOr(string.Empty, FormatSecondsDuration(totalSeconds > 0 ? totalSeconds : null), runtime);
        var clampedPercent = Math.Clamp(percent, 0, 100);
        var roundedPercent = Math.Clamp((int)Math.Round(clampedPercent, MidpointRounding.AwayFromZero), 1, 99);
        var timeLeft = FormatTimeLeft(runtimeSource, clampedPercent);
        var currentPosition = 0;
        for (var i = 0; i < tracks.Count; i++)
        {
            if (ReferenceEquals(tracks[i], current))
            {
                currentPosition = i + 1;
                break;
            }
        }

        var hasVisibleTracks = tracks.Count > 1
            && string.Equals(trackGroup?.Title, "Tracks", StringComparison.OrdinalIgnoreCase)
            && currentPosition > 0;
        var tracksRemaining = hasVisibleTracks
            ? Math.Max(0, tracks.Count - currentPosition)
            : 0;

        return new ProgressViewModel
        {
            Percent = clampedPercent,
            Kind = DetailProgressKind.Listening,
            Label = BuildListenHeroProgressLabel(clampedPercent, runtimeSource),
            ContextLabel = hasVisibleTracks ? $"{current.Title} of {tracks.Count}" : null,
            PercentLabel = $"{roundedPercent}%",
            RemainingLabel = string.IsNullOrWhiteSpace(timeLeft) ? null : $"{timeLeft} left",
            SecondaryLabel = hasVisibleTracks
                ? tracksRemaining == 1 ? "1 track remaining" : $"{tracksRemaining} tracks remaining"
                : null,
        };
    }

    private static ProgressViewModel? BuildCollectionHeroProgress(
        DetailEntityType entityType,
        IReadOnlyList<CollectionWorkSummary> works)
    {
        if (!IsWatchEntity(entityType))
        {
            return null;
        }

        var item = entityType == DetailEntityType.TvShow
            ? SelectInProgressTvEpisode(works)
            : works
                .Where(work => work.IsOwned && work.ProgressPercent is > 0 and < 99.5)
                .OrderByDescending(work => work.ProgressPercent)
                .FirstOrDefault();
        if (item is null || item.ProgressPercent is null)
        {
            return null;
        }

        var percent = Math.Clamp(item.ProgressPercent.Value, 0, 100);
        return new ProgressViewModel
        {
            Percent = percent,
            Kind = DetailProgressKind.Watching,
            Label = entityType is DetailEntityType.TvShow or DetailEntityType.TvSeason
                ? BuildTvHeroProgressLabel(percent, item.PositionSeconds, item.DurationSeconds)
                : BuildHeroProgressLabel(percent, item.Duration),
        };
    }

    private static string BuildHeroProgressLabel(double percent, string? runtime)
    {
        var rounded = Math.Clamp((int)Math.Round(percent, MidpointRounding.AwayFromZero), 1, 99);
        var timeLeft = FormatTimeLeft(runtime, percent);
        return string.IsNullOrWhiteSpace(timeLeft)
            ? $"Continue watching · {rounded}% watched"
            : $"Continue watching · {rounded}% watched · {timeLeft} left";
    }

    private static string BuildListenHeroProgressLabel(double percent, string? runtime)
    {
        var rounded = Math.Clamp((int)Math.Round(percent, MidpointRounding.AwayFromZero), 1, 99);
        var timeLeft = FormatTimeLeft(runtime, percent);
        return string.IsNullOrWhiteSpace(timeLeft)
            ? $"Continue listening - {rounded}% listened"
            : $"Continue listening - {rounded}% listened - {timeLeft} left";
    }

    private static string BuildTvHeroProgressLabel(double percent, double? positionSeconds, double? durationSeconds)
    {
        var rounded = Math.Clamp((int)Math.Round(percent, MidpointRounding.AwayFromZero), 1, 99);
        var timeLeft = positionSeconds is { } position && durationSeconds is { } duration
            && double.IsFinite(position) && double.IsFinite(duration) && position >= 0 && duration > 0
            ? FormatRemainingTime(Math.Max(0, duration - position))
            : null;
        return timeLeft is null
            ? $"Continue watching · {rounded}% watched"
            : $"Continue watching · {rounded}% watched · {timeLeft} left";
    }

    private static string BuildReadHeroProgressLabel(double percent)
    {
        var rounded = Math.Clamp((int)Math.Round(percent, MidpointRounding.AwayFromZero), 1, 99);
        return $"Continue reading · {rounded}% complete";
    }

    private static bool IsWatchEntity(DetailEntityType entityType)
        => entityType is DetailEntityType.Movie or DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode;

    private static bool HasAudiobookProgress(IReadOnlyList<OwnedFormatViewModel> formats)
        => formats.Any(format =>
            format.FormatType == MediaFormatType.Audiobook
            && format.Progress?.Percent is > 0 and < 99.5);

    private static IReadOnlyList<DetailAction> BuildPrimaryActions(
        Guid id,
        DetailEntityType entityType,
        DetailPresentationContext context,
        IReadOnlyList<OwnedFormatViewModel> formats,
        ProgressViewModel? heroProgress,
        string? episodePosition)
    {
        return entityType switch
        {
            DetailEntityType.Movie => BuildWatchActions($"/watch/player/{id}", heroProgress),
            DetailEntityType.TvEpisode => BuildWatchActions($"/watch/player/{id}", heroProgress, episodePosition),
            DetailEntityType.TvShow or DetailEntityType.TvSeason => BuildWatchActions(null, heroProgress),
            DetailEntityType.Book or DetailEntityType.ComicIssue => [new DetailAction { Key = "read", Label = heroProgress is null ? "Read" : "Continue Reading", Icon = "menu_book", IsPrimary = true }],
            DetailEntityType.Audiobook => [new DetailAction { Key = "listen", Label = heroProgress is null ? "Listen" : "Continue Listening", Icon = "headphones", IsPrimary = true }],
            DetailEntityType.Work when formats.Any(f => f.FormatType == MediaFormatType.Ebook) => [new DetailAction { Key = "read", Label = heroProgress is null ? "Read" : "Continue Reading", Icon = "menu_book", IsPrimary = true }],
            DetailEntityType.Work when formats.Any(f => f.FormatType == MediaFormatType.Audiobook) => [new DetailAction { Key = "listen", Label = HasAudiobookProgress(formats) ? "Continue Listening" : "Listen", Icon = "headphones", IsPrimary = true }],
            DetailEntityType.MusicAlbum => BuildMusicAlbumActions(),
            _ => [new DetailAction { Key = "open", Label = "Open", Icon = "open_in_new", IsPrimary = true }],
        };
    }

    private static IReadOnlyList<DetailAction> BuildMusicAlbumActions() =>
    [
        new DetailAction
        {
            Key = "play-album",
            Label = "Play",
            Icon = "play_arrow",
            IsPrimary = true,
        },
        new DetailAction
        {
            Key = "shuffle",
            Label = "Shuffle",
            Icon = "shuffle",
            Tooltip = "Shuffle album",
            IsPrimary = true,
        },
    ];

    private static IReadOnlyList<DetailAction> BuildSecondaryActions(Guid id, DetailEntityType entityType, bool isFavorite, IReadOnlyList<OwnedFormatViewModel>? formats = null)
    {
        var actions = new List<DetailAction>();
        var hasReadListenCompanion = HasReadListenCompanion(entityType, formats ?? []);

        if (CanFavoriteEntity(entityType))
        {
            actions.Add(BuildMyListAction(isFavorite));
            actions.Add(BuildReactionAction());
        }

        if (hasReadListenCompanion)
        {
            actions.Add(new DetailAction
            {
                Key = "read-listen",
                Label = "Read + Listen",
                Subtitle = "Continue seamlessly between reading and listening",
                Icon = "read_listen",
                Tooltip = "Unified Read + Listen is waiting on sync enablement",
                IsDisabled = true,
                IsStub = true,
                DisplayStyle = "premium",
            });
        }

        return actions;
    }

    private static DetailAction BuildMyListAction(bool isSelected)
        => new()
        {
            Key = "my-list",
            Label = isSelected ? "In My List" : "Add to My List",
            Icon = isSelected ? "check_circle" : "add",
            Tooltip = isSelected ? "Remove from My List" : "Add to My List",
            DisplayStyle = "icon-circle",
            IsSelected = isSelected,
        };

    private static DetailAction BuildReactionAction()
        => new()
        {
            Key = "reaction-menu",
            Label = "Rate",
            Icon = "thumb_up",
            Tooltip = "Rate this title",
            DisplayStyle = "icon-circle",
            Children =
            [
                new DetailAction { Key = "reaction-dislike", Label = "Not for me", Icon = "thumb_down" },
                new DetailAction { Key = "reaction-like", Label = "I like this", Icon = "thumb_up" },
            ],
        };

    private static IReadOnlyList<DetailAction> BuildWatchActions(string? route, ProgressViewModel? progress, string? episodePosition = null)
    {
        var verb = progress is null ? "Watch" : "Resume";
        var watch = new DetailAction
        {
            Key = "watch",
            Label = string.IsNullOrWhiteSpace(episodePosition) ? verb : $"{verb} {episodePosition}",
            Icon = "play_arrow",
            Route = route,
            IsPrimary = true,
        };

        return [watch];
    }

    private static bool HasReadListenCompanion(DetailEntityType entityType, IReadOnlyList<OwnedFormatViewModel> formats)
        => entityType is DetailEntityType.Book or DetailEntityType.Audiobook or DetailEntityType.Work
           && formats.Any(f => f.FormatType == MediaFormatType.Ebook)
           && formats.Any(f => f.FormatType == MediaFormatType.Audiobook);

    private static bool IsReadableEntity(DetailEntityType entityType)
        => entityType is DetailEntityType.Book or DetailEntityType.ComicIssue or DetailEntityType.Audiobook or DetailEntityType.Work;

    private static bool CanFavoriteEntity(DetailEntityType entityType)
        => IsReadableEntity(entityType)
           || IsWatchEntity(entityType)
           || entityType is DetailEntityType.MusicAlbum
               or DetailEntityType.TvShow
               or DetailEntityType.Collection;

    private static string BuildReadListenAvailabilityLabel(DetailEntityType entityType, IReadOnlyList<OwnedFormatViewModel> formats)
    {
        if (entityType == DetailEntityType.Audiobook)
        {
            return "Ebook available";
        }

        var audiobook = formats.FirstOrDefault(f => f.FormatType == MediaFormatType.Audiobook);
        var runtime = FormatRuntime(audiobook?.Runtime);
        return string.IsNullOrWhiteSpace(runtime)
            ? "Audiobook available"
            : $"Audiobook available · {runtime}";
    }

    private static DetailEditorTarget BuildCollectionEditorTarget(
        Guid collectionId,
        DetailEntityType entityType,
        Guid? rootWorkId)
    {
        if (entityType is DetailEntityType.BookSeries or DetailEntityType.ComicSeries or DetailEntityType.MovieSeries)
        {
            return new DetailEditorTarget
            {
                EntityId = collectionId.ToString("D"),
                EntityKind = "StructuralCollection",
                ContainerMode = "Collection",
                InitialTab = "details",
            };
        }
        if (IsCanonicalContainerEntity(entityType) && rootWorkId.HasValue)
        {
            return new DetailEditorTarget
            {
                EntityId = rootWorkId.Value.ToString("D"),
                EntityKind = entityType is DetailEntityType.BookSeries or DetailEntityType.ComicSeries or DetailEntityType.MovieSeries
                    ? "StructuralCollection"
                    : "Work",
                ContainerMode = "Canonical",
                InitialTab = entityType switch
                {
                    DetailEntityType.TvShow or DetailEntityType.TvSeason => "episodes",
                    DetailEntityType.MusicAlbum => "tracks",
                    _ => "details",
                },
            };
        }

        return new DetailEditorTarget
        {
            EntityId = collectionId.ToString("D"),
            EntityKind = "Collection",
            ContainerMode = "Curated",
            InitialTab = "media",
        };
    }

    private static bool IsCanonicalContainerEntity(DetailEntityType entityType) =>
        entityType is DetailEntityType.TvShow
            or DetailEntityType.TvSeason
            or DetailEntityType.MusicAlbum
            or DetailEntityType.BookSeries
            or DetailEntityType.ComicSeries
            or DetailEntityType.MovieSeries;

    private static IReadOnlyList<DetailAction> BuildOverflowActions(
        Guid id,
        DetailEntityType entityType,
        DetailActionAuthorizationContext authorization,
        bool updatingDetails = false)
    {
        _ = id;
        _ = entityType;
        DetailAction[] candidates =
        [
            new() { Key = "edit", Label = "Edit details", Icon = "edit", IsDisabled = updatingDetails,
                Tooltip = updatingDetails ? "Updating details. Editing is available when ingestion finishes." : null },
        ];
        return candidates.Where(action => authorization.Allows(action.Key)).ToList();
    }

    private static IReadOnlyList<DetailTab> BuildTabs(
        DetailEntityType entityType,
        DetailPresentationContext context,
        bool isAdminView,
        bool hasSeries = false,
        bool hasUniverse = false,
        bool hasChapters = true)
    {
        _ = hasChapters;
        string[] keys = entityType switch
        {
            DetailEntityType.TvShow => hasUniverse ? ["overview", "cast", "universe", "related", "details"] : ["overview", "cast", "related", "details"],
            DetailEntityType.TvSeason when hasUniverse => ["overview", "cast", "universe", "related", "details"],
            DetailEntityType.TvSeason => ["overview", "cast", "related", "details"],
            DetailEntityType.Movie when hasUniverse => ["overview", "cast", "universe", "related", "details"],
            DetailEntityType.Movie => ["overview", "cast", "related", "details"],
            DetailEntityType.MovieSeries when hasUniverse => ["overview", "universe", "related", "details"],
            DetailEntityType.MovieSeries => ["overview", "related", "details"],
            DetailEntityType.TvEpisode when hasUniverse => ["overview", "cast", "characters", "universe", "related", "details"],
            DetailEntityType.TvEpisode => ["overview", "cast", "characters", "related", "details"],
            DetailEntityType.Book when hasUniverse => ["overview", "credits", "universe", "related", "details"],
            DetailEntityType.Book => ["overview", "credits", "related", "details"],
            DetailEntityType.Audiobook => ["overview", "tracks", "details"],
            DetailEntityType.BookSeries when hasUniverse => ["overview", "universe", "related", "details"],
            DetailEntityType.BookSeries => ["overview", "related", "details"],
            DetailEntityType.Work when hasUniverse => ["overview", "credits", "universe", "related", "details"],
            DetailEntityType.Work => ["overview", "credits", "related", "details"],
            DetailEntityType.ComicIssue when hasUniverse => ["overview", "credits", "universe", "related", "details"],
            DetailEntityType.ComicIssue => ["overview", "credits", "related", "details"],
            DetailEntityType.ComicSeries when hasUniverse => ["overview", "universe", "related", "details"],
            DetailEntityType.ComicSeries => ["overview", "related", "details"],
            DetailEntityType.MusicAlbum => ["overview", "details"],
            DetailEntityType.Person => ["overview"],
            DetailEntityType.Collection => ["overview", "details"],
            DetailEntityType.Character when hasUniverse => ["overview", "portrayals", "relationships", "universe", "details"],
            DetailEntityType.Character => ["overview", "portrayals", "relationships", "details"],
            DetailEntityType.Universe => ["overview", "characters", "people", "relationships", "details"],
            _ when hasUniverse => ["overview", "people", "characters", "universe", "details"],
            _ => ["overview", "people", "characters", "details"],
        };

        var tabs = keys.Select(key => new DetailTab { Key = key, Label = ToTabLabel(key, entityType) }).ToList();
        if (isAdminView)
        {
            tabs.Add(new DetailTab { Key = "registry", Label = "Registry", IsAdminOnly = true });
        }

        return tabs;
    }

    private static DetailPrimaryModuleViewModel BuildPrimaryModule(
        DetailEntityType entityType,
        SequencePlacementViewModel? sequencePlacement,
        IReadOnlyList<MediaGroupingViewModel> mediaGroups)
    {
        var kind = entityType switch
        {
            DetailEntityType.MusicAlbum when mediaGroups.Any(group =>
                string.Equals(group.Key, "tracks", StringComparison.OrdinalIgnoreCase))
                => DetailPrimaryModuleKind.Tracks,
            DetailEntityType.Audiobook when sequencePlacement is null && mediaGroups.Any(group =>
                string.Equals(group.Key, "tracks", StringComparison.OrdinalIgnoreCase))
                => DetailPrimaryModuleKind.Tracks,
            DetailEntityType.TvShow or DetailEntityType.TvSeason or DetailEntityType.TvEpisode when sequencePlacement is not null
                => DetailPrimaryModuleKind.Episodes,
            DetailEntityType.Person when mediaGroups.Count > 0
                => DetailPrimaryModuleKind.Works,
            DetailEntityType.Character when mediaGroups.Count > 0 => DetailPrimaryModuleKind.Appearances,
            DetailEntityType.Collection when mediaGroups.Count > 0 => DetailPrimaryModuleKind.CollectionItems,
            DetailEntityType.Universe when mediaGroups.Count > 0 => DetailPrimaryModuleKind.CollectionItems,
            _ when sequencePlacement is not null => DetailPrimaryModuleKind.Sequence,
            _ => DetailPrimaryModuleKind.None,
        };

        var title = kind switch
        {
            DetailPrimaryModuleKind.Tracks => "Tracks",
            DetailPrimaryModuleKind.Chapters => "Chapters",
            DetailPrimaryModuleKind.Episodes => "Episodes",
            DetailPrimaryModuleKind.Works => "Works in Your Library",
            DetailPrimaryModuleKind.CollectionItems => "Items",
            DetailPrimaryModuleKind.Appearances => "Appearances",
            DetailPrimaryModuleKind.Sequence => sequencePlacement?.ItemPluralLabel ?? "Series",
            _ => string.Empty,
        };

        var groupKeys = kind switch
        {
            DetailPrimaryModuleKind.Tracks => ["tracks"],
            _ => mediaGroups.Select(group => group.Key).ToList(),
        };

        return new DetailPrimaryModuleViewModel
        {
            Kind = kind,
            Title = title,
            GroupKeys = groupKeys,
            SupportsLaneFilter = kind is DetailPrimaryModuleKind.Works or DetailPrimaryModuleKind.CollectionItems,
            SupportsRoleFilter = kind == DetailPrimaryModuleKind.Works,
        };
    }

    private static void AddPlain(List<MetadataPill> values, string? label, string kind)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            values.Add(new MetadataPill { Label = label, Kind = kind });
        }
    }

    private static string? FormatCountLabel(string? value, string singular)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return trimmed;
        }

        var label = count == 1 ? singular : singular + "s";
        return $"{count.ToString(CultureInfo.InvariantCulture)} {label}";
    }

    private static string? FormatRating(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        var trimmed = rating.Trim();
        return double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : trimmed;
    }

    private static string? ResolveWatchQualityLabel(
        IReadOnlyDictionary<string, string> canonicalValues,
        PlaybackTechnicalSummary? playbackSummary)
    {
        var explicitQuality = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(canonicalValues, "quality"), GetValue(canonicalValues, "video_quality"));
        if (!string.IsNullOrWhiteSpace(explicitQuality))
        {
            return NormalizeWatchQualityLabel(explicitQuality);
        }

        return NormalizeWatchQualityLabel(playbackSummary?.VideoResolutionLabel);
    }

    private static string? NormalizeWatchQualityLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Equals("2160p", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("UHD", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Ultra HD", StringComparison.OrdinalIgnoreCase)
            ? "4K"
            : normalized;
    }

    private static bool HasSubtitles(
        IReadOnlyDictionary<string, string> canonicalValues,
        PlaybackTechnicalSummary? playbackSummary)
        => !string.IsNullOrWhiteSpace(GetValue(canonicalValues, "subtitle_languages"))
            || !string.IsNullOrWhiteSpace(GetValue(canonicalValues, "subtitles"))
            || !string.IsNullOrWhiteSpace(playbackSummary?.SubtitleSummary)
            || playbackSummary?.SubtitleLanguages.Count > 0;

    private static string? FormatRuntime(string? runtime)
    {
        if (string.IsNullOrWhiteSpace(runtime))
        {
            return null;
        }

        var trimmed = runtime.Trim();
        if (!double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes))
        {
            return trimmed;
        }

        if (minutes <= 0)
        {
            return null;
        }

        var totalMinutes = (int)Math.Round(minutes, MidpointRounding.AwayFromZero);
        var hours = totalMinutes / 60;
        var remainingMinutes = totalMinutes % 60;

        return hours > 0
            ? remainingMinutes > 0 ? $"{hours}h {remainingMinutes}m" : $"{hours}h"
            : $"{totalMinutes}m";
    }

    private static string? FormatTimeLeft(string? runtime, double progressPercent)
    {
        var totalSeconds = TryParseDurationSeconds(runtime);
        if (totalSeconds is null or <= 0)
        {
            return null;
        }

        var remainingSeconds = totalSeconds.Value * (100d - Math.Clamp(progressPercent, 0, 100)) / 100d;
        return FormatRemainingTime(remainingSeconds);
    }

    private static string? FormatRemainingTime(double remainingSeconds)
    {
        if (remainingSeconds <= 60)
        {
            return null;
        }

        var remainingMinutes = (int)Math.Ceiling(remainingSeconds / 60d);
        var hours = remainingMinutes / 60;
        var minutes = remainingMinutes % 60;
        return hours > 0
            ? minutes > 0 ? $"{hours}h {minutes:D2}m" : $"{hours}h"
            : $"{remainingMinutes}m";
    }

    private static int? TryParseDurationSeconds(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
        {
            return null;
        }

        var trimmed = duration.Trim();
        if (trimmed.Contains(':', StringComparison.Ordinal))
        {
            return TryParseClockDurationSeconds(trimmed);
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
        {
            return null;
        }

        return (int)Math.Round(minutes * 60d, MidpointRounding.AwayFromZero);
    }

    private static int? TryParseAudioDurationSeconds(string? durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(durationSeconds))
        {
            return null;
        }

        if (!double.TryParse(durationSeconds.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || seconds <= 0)
        {
            return null;
        }

        return (int)Math.Round(seconds >= 60000 ? seconds / 1000d : seconds, MidpointRounding.AwayFromZero);
    }

    private static string? FormatTrackDuration(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration))
        {
            return null;
        }

        var trimmed = duration.Trim();
        if (trimmed.Contains(':', StringComparison.Ordinal))
        {
            return trimmed;
        }

        return FormatRuntime(trimmed);
    }

    private static string? FormatSecondsDuration(double? seconds)
    {
        if (!seconds.HasValue || seconds.Value <= 0)
        {
            return null;
        }

        var totalSeconds = (int)Math.Round(seconds.Value, MidpointRounding.AwayFromZero);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var remainingSeconds = totalSeconds % 60;

        return hours > 0
            ? $"{hours}:{minutes:D2}:{remainingSeconds:D2}"
            : $"{minutes}:{remainingSeconds:D2}";
    }

    private static double? CalculateChapterProgress(double? resumeSeconds, double startSeconds, double? endSeconds)
    {
        if (!resumeSeconds.HasValue || !endSeconds.HasValue || endSeconds.Value <= startSeconds)
        {
            return null;
        }

        if (resumeSeconds.Value >= endSeconds.Value)
        {
            return 100;
        }

        if (resumeSeconds.Value <= startSeconds)
        {
            return null;
        }

        return Math.Clamp((resumeSeconds.Value - startSeconds) / (endSeconds.Value - startSeconds) * 100d, 0d, 100d);
    }

    private static string? FormatAlbumDuration(IReadOnlyList<CollectionWorkSummary> works)
    {
        var seconds = works
            .Select(work => TryParseClockDurationSeconds(work.Duration))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        if (seconds.Count == 0)
        {
            return null;
        }

        var totalSeconds = seconds.Sum();
        var totalMinutes = (int)Math.Round(totalSeconds / 60d, MidpointRounding.AwayFromZero);
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        return hours > 0
            ? minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h"
            : $"{totalMinutes}m";
    }

    private static int? TryParseClockDurationSeconds(string? duration)
    {
        if (string.IsNullOrWhiteSpace(duration) || !duration.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var parts = duration.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3)
        {
            return null;
        }

        var total = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            total = (total * 60) + value;
        }

        return total;
    }

    private static bool IsTruthy(string? value)
        => value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "explicit";

    private static IEnumerable<string> SplitMetadataValues(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var part in value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                yield return part;
            }
        }
    }

    private static string? FormatContributorList(string? value)
    {
        var contributors = SplitMetadataValues(value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return contributors.Count == 0 ? null : string.Join(", ", contributors);
    }

    private static IReadOnlyList<DetailAction> BuildFormatActions(Guid workId, MediaFormatType format)
        => format switch
        {
            MediaFormatType.Ebook => [new DetailAction { Key = "read", Label = "Read", Icon = "menu_book" }],
            MediaFormatType.Audiobook => [new DetailAction { Key = "listen", Label = "Listen", Icon = "headphones" }],
            MediaFormatType.Movie => [new DetailAction { Key = "play", Label = "Play", Icon = "play_arrow" }],
            _ => [new DetailAction { Key = "open", Label = "Open", Icon = "open_in_new" }],
        };

    private static ReadingListeningSyncCapabilityViewModel? BuildSyncCapability(Guid workId, IReadOnlyList<OwnedFormatViewModel> formats, MultiFormatState state)
    {
        if (state == MultiFormatState.SingleFormat)
        {
            return null;
        }

        var ebook = formats.FirstOrDefault(f => f.FormatType == MediaFormatType.Ebook);
        var audio = formats.FirstOrDefault(f => f.FormatType == MediaFormatType.Audiobook);
        if (ebook is null || audio is null)
        {
            return new ReadingListeningSyncCapabilityViewModel
            {
                State = SyncCapabilityState.NotApplicable,
                Reason = "Read + Listen Sync only applies when both ebook and audiobook formats are owned.",
            };
        }

        // Cross-format position alignment remains a post-beta capability. Do not
        // expose disabled actions until an alignment can be previewed and saved.
        return null;
    }

    private static DetailEntityType InferWorkEntityType(string mediaType, LibraryItemDetail detail)
    {
        if (!string.IsNullOrWhiteSpace(detail.EpisodeNumber) || mediaType.Equals("TV", StringComparison.OrdinalIgnoreCase))
        {
            return DetailEntityType.TvEpisode;
        }

        if (mediaType.Contains("movie", StringComparison.OrdinalIgnoreCase))
        {
            return DetailEntityType.Movie;
        }

        if (mediaType.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            return DetailEntityType.Audiobook;
        }

        if (mediaType.Contains("comic", StringComparison.OrdinalIgnoreCase) || mediaType.Equals("Cbz", StringComparison.OrdinalIgnoreCase))
        {
            return DetailEntityType.ComicIssue;
        }

        if (mediaType.Contains("music", StringComparison.OrdinalIgnoreCase))
        {
            return DetailEntityType.Work;
        }

        return DetailEntityType.Book;
    }

    private static DetailEntityType InferCollectionEntityType(IReadOnlyList<CollectionWorkSummary> works)
    {
        var mediaTypes = works.Select(w => w.MediaType).ToList();
        if (mediaTypes.Any(m => m.Contains("TV", StringComparison.OrdinalIgnoreCase)) || works.Any(w => !string.IsNullOrWhiteSpace(w.Season)))
        {
            return DetailEntityType.TvShow;
        }

        if (mediaTypes.Any(m => m.Contains("movie", StringComparison.OrdinalIgnoreCase)))
        {
            return DetailEntityType.MovieSeries;
        }

        if (mediaTypes.Any(m => m.Contains("music", StringComparison.OrdinalIgnoreCase)))
        {
            return DetailEntityType.MusicAlbum;
        }

        if (mediaTypes.Any(m => m.Contains("comic", StringComparison.OrdinalIgnoreCase)))
        {
            return DetailEntityType.ComicSeries;
        }

        return DetailEntityType.Collection;
    }

    private static MediaFormatType ToFormatType(string mediaType, string? formatLabel)
    {
        var value = $"{mediaType} {formatLabel}".ToLowerInvariant();
        if (value.Contains("audio"))
        {
            return MediaFormatType.Audiobook;
        }

        if (value.Contains("epub") || value.Contains("ebook") || value.Contains("book"))
        {
            return MediaFormatType.Ebook;
        }

        if (value.Contains("comic") || value.Contains("cbz"))
        {
            return MediaFormatType.ComicIssue;
        }

        if (value.Contains("movie") || value.Contains("video"))
        {
            return MediaFormatType.Movie;
        }

        if (value.Contains("music") || value.Contains("album"))
        {
            return MediaFormatType.MusicAlbum;
        }

        if (value.Contains("tv"))
        {
            return MediaFormatType.TvSeries;
        }

        return MediaFormatType.Ebook;
    }

    private static string ToFormatDisplay(string mediaType, string? formatLabel)
    {
        if (!string.IsNullOrWhiteSpace(formatLabel))
        {
            return formatLabel;
        }

        return ToFormatType(mediaType, formatLabel) switch
        {
            MediaFormatType.Audiobook => "Audiobook",
            MediaFormatType.Ebook => "Ebook",
            MediaFormatType.ComicIssue => "Comic Issue",
            MediaFormatType.Movie => "Movie",
            MediaFormatType.MusicAlbum => "Music Album",
            MediaFormatType.TvSeries => "TV",
            _ => mediaType,
        };
    }

    private static string ResolveWorkDisplayTitle(
        string? displayTitle,
        LibraryItemDetail detail,
        IReadOnlyDictionary<string, string> values,
        DetailEntityType entityType)
    {
        if (entityType == DetailEntityType.TvEpisode)
        {
            return StringHelpers.FirstNonBlankOr(string.Empty, displayTitle, detail.EpisodeTitle, GetValue(values, MetadataFieldConstants.EpisodeTitle), detail.Title, detail.FileName, "Untitled");
        }

        if (entityType == DetailEntityType.ComicIssue)
        {
            var issueTitle = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(values, MetadataFieldConstants.IssueTitle), displayTitle);
            if (!string.IsNullOrWhiteSpace(issueTitle)
                && !IsGeneratedComicIssueTitle(issueTitle, detail, values))
            {
                return StringHelpers.FirstNonBlankOr(string.Empty, issueTitle, detail.Title, detail.FileName, "Untitled");
            }

            if (!IsGeneratedComicIssueTitle(detail.Title, detail, values))
            {
                return StringHelpers.FirstNonBlankOr(string.Empty, detail.Title, detail.FileName, "Untitled");
            }

            var issueNumber = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(values, MetadataFieldConstants.IssueNumber), detail.SeriesPosition, GetValue(values, MetadataFieldConstants.SeriesPosition));
            return StringHelpers.FirstNonBlankOr(string.Empty, FormatIssue(issueNumber), detail.FileName, "Untitled");
        }

        return StringHelpers.FirstNonBlankOr(
            string.Empty,
            displayTitle,
            GetValue(values, MetadataFieldConstants.Title),
            detail.Title,
            detail.EpisodeTitle,
            detail.FileName,
            "Untitled");
    }

    private static bool IsGeneratedComicIssueTitle(string? title, LibraryItemDetail detail, IReadOnlyDictionary<string, string> values)
    {
        var series = StringHelpers.FirstNonBlankOr(string.Empty, detail.Series, GetValue(values, MetadataFieldConstants.Series));
        var issueNumber = StringHelpers.FirstNonBlankOr(string.Empty, GetValue(values, "issue_number"), detail.SeriesPosition, GetValue(values, MetadataFieldConstants.SeriesPosition));
        return IsGeneratedComicIssueTitle(title, series, issueNumber);
    }

    private static bool IsGeneratedComicIssueTitle(string? title, string? series, string? issueNumber)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(series) || string.IsNullOrWhiteSpace(issueNumber))
        {
            return false;
        }

        var normalizedTitle = NormalizeOrdinalTitle(title);
        var normalizedSeries = NormalizeOrdinalTitle(series);
        var normalizedIssue = NormalizeOrdinalTitle(issueNumber);
        return normalizedTitle == normalizedSeries
            || normalizedTitle == $"{normalizedSeries}{normalizedIssue}"
            || normalizedTitle == $"{normalizedSeries}issue{normalizedIssue}"
            || normalizedTitle == $"{normalizedSeries}no{normalizedIssue}"
            || (normalizedTitle.StartsWith(normalizedSeries, StringComparison.OrdinalIgnoreCase)
                && normalizedTitle.EndsWith(normalizedIssue, StringComparison.OrdinalIgnoreCase));
    }

}
