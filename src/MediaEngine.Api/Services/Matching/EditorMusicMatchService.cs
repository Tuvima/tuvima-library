using System.Globalization;
using MediaEngine.Api.Services.Canonical;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Services.Matching;

/// <summary>
/// The Match tab's music flow (universal editor v5, 3c "Music, step by step"): the releases of an album with the one that fits the file
/// suggested and the current one marked, and a release's discs and tracks with the track that fits the file pre-selected and each
/// recording's disambiguation. Reads only: nothing is written and nothing is applied here; Apply match stages the chosen track.
/// </summary>
public sealed class EditorMusicMatchService(
    MusicBrainzReleaseClient musicBrainz,
    EditorMatchLocalEvidenceReader reader,
    IEditorMatchKindResolver kinds,
    IMetadataEditorRepository launches,
    ICanonicalValueRepository canonicals,
    IMediaEditorOwnedChildReadService ownedChildren,
    EditorMatchVerdictCalculator calculator,
    MusicMatchSettings settings,
    IHttpClientFactory httpFactory,
    IMemoryCache previews)
{
    private const string ProviderKey = "musicbrainz";

    /// <summary>The releases of the album the words name; null when no such music item is in the Library.</summary>
    public async Task<EditorMusicReleasesDto?> ReleasesAsync(Guid entityId, string? artist, string? album, string? song, CancellationToken ct = default)
    {
        var item = await ReadItemAsync(entityId, ct).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var query = string.Join(" ", new[] { artist, album ?? song }.Where(part => !string.IsNullOrWhiteSpace(part))).Trim();
        if (query.Length == 0)
        {
            return new EditorMusicReleasesDto { EntityId = entityId, MessageCode = EditorMatchMessageCodes.EnterWords };
        }

        var found = await musicBrainz.SearchReleasesAsync(artist, album, song, settings.MaxReleases, ct).ConfigureAwait(false);
        if (found is null)
        {
            return new EditorMusicReleasesDto { EntityId = entityId, Query = query, MessageCode = EditorMatchMessageCodes.ProviderSilent };
        }

        if (found.Count == 0)
        {
            return new EditorMusicReleasesDto { EntityId = entityId, Query = query, MessageCode = EditorMatchMessageCodes.NothingFound };
        }

        // Releases that tie on title and track count are told apart by the song's length against the tracks of a release already read
        // (kept by the client): no extra request is made for it.
        var wanted = string.IsNullOrWhiteSpace(album) ? item.AlbumTitle : album;
        var measured = item.Read.Evidence.MeasuredSeconds;
        var suggested = EditorMusicSuggestion.SuggestRelease(
            found, wanted, item.LibraryTrackCount, settings,
            measured is > 0 && item.Kind == EditorKinds.Song ? release => HasTrackOfLength(release, measured) : null);
        var covers = await Task.WhenAll(found.Select(release => CoverAsync(release, ct))).ConfigureAwait(false);
        var releases = found
            .Select((release, index) => ToDto(release, item, suggested == release.ReleaseId, covers[index]))
            .ToList();
        return new EditorMusicReleasesDto { EntityId = entityId, Query = query, Releases = releases };
    }

    private bool HasTrackOfLength(MusicBrainzReleaseSummary release, double? measured) =>
        musicBrainz.KeptReleaseDetail(release.ReleaseId) is { } detail
        && detail.Discs.SelectMany(disc => disc.Tracks).Any(track => calculator.TrackLength(measured, track.LengthSeconds).State == EditorMatchEvidenceStates.Agrees);

    // A small cover for the row, made here (bounded, allow-listed) so the Dashboard never contacts the Cover Art Archive itself.
    private Task<string?> CoverAsync(MusicBrainzReleaseSummary release, CancellationToken ct) =>
        release.HasFrontCover
            ? RetailCandidateArtworkPreview.LoadAsync(ProviderKey, $"https://coverartarchive.org/release/{release.ReleaseId}/front-250", httpFactory, previews, ct)
            : Task.FromResult<string?>(null);

    /// <summary>A release opened with the track that fits the file marked; null when no such music item is in the Library or the release cannot be read.</summary>
    public async Task<EditorMusicReleaseTracksDto?> TracksAsync(Guid entityId, string releaseId, CancellationToken ct = default)
    {
        var item = await ReadItemAsync(entityId, ct).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var detail = await musicBrainz.FetchReleaseDetailAsync(releaseId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return null;
        }

        var tracks = detail.Discs.SelectMany(disc => disc.Tracks).ToList();
        var pick = item.Kind == EditorKinds.Song
            ? EditorMusicSuggestion.SuggestTrack(tracks, item.Read.Evidence, calculator, settings)
            : null;
        return new EditorMusicReleaseTracksDto
        {
            EntityId = entityId,
            Release = ToDto(detail.Summary, item, false),
            Discs = [.. detail.Discs.Select(disc => new EditorMusicDiscDto
            {
                Number = disc.Number,
                Title = disc.Title,
                Tracks = [.. disc.Tracks.Select(track => ToDto(track, ReferenceEquals(track, pick)))],
            })],
        };
    }

    private sealed record Item(
        string Kind, EditorMatchLocalRead Read, string? CurrentReleaseId, string? AlbumTitle, int LibraryTrackCount);

    private async Task<Item?> ReadItemAsync(Guid entityId, CancellationToken ct)
    {
        var kind = await kinds.ResolveAsync(entityId, ct).ConfigureAwait(false);
        if (kind is not (EditorKinds.Song or EditorKinds.Album))
        {
            return null;
        }

        var read = await reader.ReadAsync(entityId, kind, ct).ConfigureAwait(false);
        var launch = await launches.ResolveEditorLaunchAsync(entityId, ct).ConfigureAwait(false);
        if (read is null || launch is null)
        {
            return null;
        }

        var albumId = launch.RootWorkId != Guid.Empty ? launch.RootWorkId : launch.WorkId;
        var own = Latest(await canonicals.GetByEntityAsync(launch.WorkId, ct).ConfigureAwait(false));
        var album = albumId == launch.WorkId ? own : Latest(await canonicals.GetByEntityAsync(albumId, ct).ConfigureAwait(false));
        var children = await ownedChildren.SearchAsync(albumId, null, 1, 1, null, null, null, null, null, ct).ConfigureAwait(false);
        return new Item(
            kind,
            read,
            Value(own, BridgeIdKeys.MusicBrainzReleaseId) ?? Value(album, BridgeIdKeys.MusicBrainzReleaseId),
            Value(album, MetadataFieldConstants.Album) ?? Value(album, MetadataFieldConstants.Title) ?? read.Fields.GetValueOrDefault(EditorMatchFieldIds.Album).Value,
            children?.TotalCount ?? 0);
    }

    private static EditorMusicReleaseDto ToDto(MusicBrainzReleaseSummary release, Item item, bool suggested, string? image = null) => new()
    {
        ImageUrl = image,
        ReleaseId = release.ReleaseId,
        ReleaseGroupId = release.ReleaseGroupId,
        Title = release.Title,
        Artist = release.Artist,
        Year = release.Year,
        Label = release.Label,
        Format = release.Format,
        Country = release.Country,
        TrackCount = release.TrackCount,
        HasCover = release.HasFrontCover,
        SourceUrl = OutsideLinks.MusicBrainz("release", release.ReleaseId),
        IsCurrent = string.Equals(release.ReleaseId, item.CurrentReleaseId, StringComparison.OrdinalIgnoreCase),
        IsSuggested = suggested,
    };

    private static EditorMusicTrackDto ToDto(MusicBrainzTrackDetail track, bool suggested) => new()
    {
        RecordingId = track.RecordingId,
        ReleaseTrackId = track.ReleaseTrackId,
        Disc = track.Disc,
        Track = track.Track,
        Title = track.Title,
        Artist = track.Artist,
        LengthSeconds = track.LengthSeconds,
        LengthText = EditorMatchVerdictCalculator.ClockText(track.LengthSeconds),
        Disambiguation = track.Disambiguation,
        IsSuggested = suggested,
    };

    private static IReadOnlyDictionary<string, string> Latest(IEnumerable<MediaEngine.Domain.Entities.CanonicalValue> values) =>
        values.Where(field => !string.IsNullOrWhiteSpace(field.Key))
            .GroupBy(field => field.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.LastScoredAt).First().Value, StringComparer.OrdinalIgnoreCase);

    private static string? Value(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

/// <summary>The rules that pick the release and the track to suggest, kept apart from the reads so they can be tested directly.</summary>
public static class EditorMusicSuggestion
{
    /// <summary>
    /// The release whose title is the album's and whose track count is the count the Library holds (or one above it, when the Library holds
    /// part of the album), by the points in <c>music_match</c>; null when none fits at all. Releases that tie on points are told apart by
    /// <paramref name="lengthFits"/> (a release already read that holds a track of the song's length wins); a tie that still stands keeps the
    /// provider's own order.
    /// </summary>
    public static string? SuggestRelease(
        IReadOnlyList<MusicBrainzReleaseSummary> releases, string? albumTitle, int libraryTrackCount, MusicMatchSettings settings,
        Func<MusicBrainzReleaseSummary, bool>? lengthFits = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var scored = releases.Select(release => (Release: release, Score: ScoreRelease(release, albumTitle, libraryTrackCount, settings))).ToList();
        var bestScore = scored.Count == 0 ? 0 : scored.Max(item => item.Score);
        if (bestScore <= 0)
        {
            return null;
        }

        var tied = scored.Where(item => item.Score == bestScore).Select(item => item.Release).ToList();
        var byLength = lengthFits is null ? null : tied.FirstOrDefault(lengthFits);
        return (byLength ?? tied[0]).ReleaseId;
    }

    private static int ScoreRelease(MusicBrainzReleaseSummary release, string? albumTitle, int libraryTrackCount, MusicMatchSettings settings)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(albumTitle) && EditorMatchVerdictCalculator.SameTitle(albumTitle, release.Title))
        {
            score += settings.ReleaseTitlePoints;
        }

        if (libraryTrackCount > 0 && release.TrackCount == libraryTrackCount)
        {
            score += settings.ReleaseTrackCountExactPoints;
        }
        else if (libraryTrackCount > 0 && release.TrackCount > libraryTrackCount)
        {
            score += settings.ReleaseTrackCountMorePoints;
        }

        return score;
    }

    /// <summary>
    /// The track of a release a file lines up with by its own number: the track with that number on the file's disc, or, when the file names
    /// no disc, on whichever disc has one (the one whose title agrees when several do, else the earliest). Null when the release has no such track.
    /// </summary>
    public static MusicBrainzTrackDetail? TrackFor(IReadOnlyList<MusicBrainzTrackDetail> tracks, int number, int? disc, string? fileTitle)
    {
        var numbered = tracks.Where(track => track.Track == number && (disc is null || track.Disc == disc)).OrderBy(track => track.Disc).ToList();
        return numbered.Count <= 1
            ? numbered.FirstOrDefault()
            : numbered.FirstOrDefault(track => !string.IsNullOrWhiteSpace(fileTitle) && EditorMatchVerdictCalculator.SameTitle(fileTitle, track.Title)) ?? numbered[0];
    }

    /// <summary>
    /// The track of a release whose title, number (with its disc) and length fit the file, by the points in <c>music_match</c>; null when
    /// nothing earns the minimum. A title that agrees is enough; a number and a length that agree are enough without one.
    /// </summary>
    public static MusicBrainzTrackDetail? SuggestTrack(
        IReadOnlyList<MusicBrainzTrackDetail> tracks, EditorMatchLocalEvidenceDto evidence, EditorMatchVerdictCalculator calculator, MusicMatchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MusicBrainzTrackDetail? best = null;
        var bestScore = 0;
        foreach (var track in tracks)
        {
            var score = 0;
            if (!string.IsNullOrWhiteSpace(evidence.FileTitle) && EditorMatchVerdictCalculator.SameTitle(evidence.FileTitle, track.Title))
            {
                score += settings.TrackTitlePoints;
            }

            if (evidence.Number is { } number && number == track.Track)
            {
                score += evidence.Disc is { } disc && disc != track.Disc ? 0 : settings.TrackNumberPoints;
            }

            if (calculator.TrackLength(evidence.MeasuredSeconds, track.LengthSeconds).State == EditorMatchEvidenceStates.Agrees)
            {
                score += settings.TrackLengthPoints;
            }

            if (score > bestScore)
            {
                best = track;
                bestScore = score;
            }
        }

        return bestScore >= settings.TrackMinPoints ? best : null;
    }
}
