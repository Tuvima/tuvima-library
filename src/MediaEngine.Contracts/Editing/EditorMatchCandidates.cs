using System.Globalization;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain;

namespace MediaEngine.Contracts.Editing;

/// <summary>
/// Turns a TheTVDB episode or a MusicBrainz track the person picked in the Match tab into the same candidate a search result is, so
/// Apply match stages it the same way for every kind of item (universal editor v5, 3c). The Engine builds the same candidate when it lines
/// several ticked items up (D36), so a single pick and a group pick can never differ.
/// </summary>
public static class EditorMatchCandidates
{
    private const string TvdbName = "tvdb";
    private const string MusicBrainzName = "musicbrainz";

    /// <summary>The fact names the verdict reads from <see cref="ItemCanonicalRetailCandidateDto.ExtraFields"/>.</summary>
    public static class Extra
    {
        public const string RuntimeMinutes = "runtime";
        public const string EpisodeNumber = "episode_number";
        public const string TrackNumber = "track_number";
        public const string DurationMilliseconds = "duration_ms";
        public const string Artist = "artist";
        public const string Disambiguation = "disambiguation";
    }

    /// <summary>A TheTVDB episode as an episode record: the exact show, season and episode, so it can never be a show-only match.</summary>
    public static ItemCanonicalRetailCandidateDto ForTvEpisode(
        string seriesId, string showName, string episodeId, int seasonNumber, int episodeNumber, string title,
        string? aired, string? overview, string? imageUrl, int? runtimeMinutes)
    {
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Extra.EpisodeNumber] = episodeNumber.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(aired))
        {
            // Facts the Match tab compares but never stages (air date, runtime), kept apart from the fields a save would write.
            extra[MetadataFieldConstants.AirDate] = aired!;
        }

        if (runtimeMinutes is > 0)
        {
            extra[Extra.RuntimeMinutes] = runtimeMinutes.Value.ToString(CultureInfo.InvariantCulture);
        }

        return new ItemCanonicalRetailCandidateDto
        {
            CandidateId = $"{TvdbName}:{episodeId}",
            ProviderId = WellKnownProviders.Tvdb.ToString("D"),
            ProviderName = TvdbName,
            ProviderItemId = episodeId,
            Title = title,
            Year = aired is { Length: >= 4 } ? aired[..4] : null,
            Description = overview,
            CoverUrl = imageUrl,
            IsApplicable = true,
            RequiredFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataFieldConstants.ShowName] = showName,
                [MetadataFieldConstants.SeasonNumber] = seasonNumber.ToString(CultureInfo.InvariantCulture),
                [MetadataFieldConstants.EpisodeNumber] = episodeNumber.ToString(CultureInfo.InvariantCulture),
                [MetadataFieldConstants.EpisodeTitle] = title,
            },
            SuggestedFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExtraFields = extra,
            BridgeIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BridgeIdKeys.TvdbId] = seriesId,
                [BridgeIdKeys.TvdbEpisodeId] = episodeId,
            },
        };
    }

    /// <summary>
    /// A MusicBrainz track of one release as a song record: the exact release and recording, the album and artist the song then belongs
    /// to, and its disc and track number on that release.
    /// </summary>
    public static ItemCanonicalRetailCandidateDto ForMusicTrack(
        string releaseId, string? releaseGroupId, string albumTitle, string? albumArtist, string? year, bool hasCover,
        string? recordingId, string title, string? trackArtist, int disc, int track, double? lengthSeconds, string? disambiguation)
    {
        var artist = trackArtist ?? albumArtist;
        var bridge = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BridgeIdKeys.MusicBrainzReleaseId] = releaseId,
        };
        if (!string.IsNullOrWhiteSpace(releaseGroupId))
        {
            bridge[BridgeIdKeys.MusicBrainzReleaseGroupId] = releaseGroupId!;
        }

        if (!string.IsNullOrWhiteSpace(recordingId))
        {
            bridge[BridgeIdKeys.MusicBrainzRecordingId] = recordingId!;
        }

        var required = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = title,
            [MetadataFieldConstants.Album] = albumTitle,
        };
        if (!string.IsNullOrWhiteSpace(artist))
        {
            required[MetadataFieldConstants.Artist] = artist!;
        }

        var suggested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.TrackNumber] = track.ToString(CultureInfo.InvariantCulture),
            [MetadataFieldConstants.DiscNumber] = disc.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(year))
        {
            suggested[MetadataFieldConstants.Year] = year!;
        }

        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Extra.TrackNumber] = track.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(artist))
        {
            extra[Extra.Artist] = artist!;
        }

        if (lengthSeconds is > 0)
        {
            extra[Extra.DurationMilliseconds] = Math.Round(lengthSeconds.Value * 1000d).ToString("0", CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(disambiguation))
        {
            extra[Extra.Disambiguation] = disambiguation!;
        }

        return new ItemCanonicalRetailCandidateDto
        {
            CandidateId = $"{MusicBrainzName}:{recordingId ?? releaseId}:{disc.ToString(CultureInfo.InvariantCulture)}:{track.ToString(CultureInfo.InvariantCulture)}",
            ProviderId = WellKnownProviders.MusicBrainz.ToString("D"),
            ProviderName = MusicBrainzName,
            ProviderItemId = recordingId ?? releaseId,
            Title = title,
            Year = year,
            CoverUrl = hasCover ? $"https://coverartarchive.org/release/{releaseId}/front-500" : null,
            IsApplicable = true,
            RequiredFields = required,
            SuggestedFields = suggested,
            ExtraFields = extra,
            BridgeIds = bridge,
        };
    }
}
