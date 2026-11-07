using System.Text.Json;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Contracts.Tests;

/// <summary>
/// The wire shapes of the Match tab's TV and music flows (universal editor v5, 3c and D36): the releases and tracks of an album, the group
/// line-up, the picks turned into candidates, and the TV browser's new facts.
/// </summary>
public sealed class EditorMusicMatchContractTests
{
    [Fact]
    public void Releases_UseSnakeCaseNames_AndRoundTrip()
    {
        var id = Guid.NewGuid();
        var releases = new EditorMusicReleasesDto
        {
            EntityId = id,
            Query = "The Tides Low Water",
            Releases =
            [
                new EditorMusicReleaseDto
                {
                    ReleaseId = "r-1", ReleaseGroupId = "rg-1", Title = "Low Water", Artist = "The Tides", Year = "2011", Label = "Harbor Records",
                    Format = "2 × CD", Country = "GB", TrackCount = 24, HasCover = true, IsCurrent = true, IsSuggested = true,
                },
            ],
        };

        var json = JsonSerializer.Serialize(releases);
        var back = JsonSerializer.Deserialize<EditorMusicReleasesDto>(json)!;

        Assert.Contains("\"entity_id\":\"" + id.ToString("D") + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"release_id\":\"r-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"release_group_id\":\"rg-1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"track_count\":24", json, StringComparison.Ordinal);
        Assert.Contains("\"has_cover\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"is_current\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"is_suggested\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"message\"", json, StringComparison.Ordinal);
        Assert.Equal("2 × CD", back.Releases[0].Format);
    }

    [Fact]
    public void ReleaseTracks_UseSnakeCaseNames_AndCarryTheDisambiguation()
    {
        var tracks = new EditorMusicReleaseTracksDto
        {
            EntityId = Guid.NewGuid(),
            Release = new EditorMusicReleaseDto { ReleaseId = "r-1", Title = "Low Water" },
            Discs =
            [
                new EditorMusicDiscDto
                {
                    Number = 2,
                    Tracks = [new EditorMusicTrackDto { RecordingId = "rec-3", ReleaseTrackId = "t-3", Disc = 2, Track = 3, Title = "Harbor Lights", LengthSeconds = 222, LengthText = "3:42", Disambiguation = "2013 remaster", IsSuggested = true }],
                },
            ],
        };

        var json = JsonSerializer.Serialize(tracks);
        var back = JsonSerializer.Deserialize<EditorMusicReleaseTracksDto>(json)!;

        Assert.Contains("\"recording_id\":\"rec-3\"", json, StringComparison.Ordinal);
        Assert.Contains("\"release_track_id\":\"t-3\"", json, StringComparison.Ordinal);
        Assert.Contains("\"length_seconds\":222", json, StringComparison.Ordinal);
        Assert.Contains("\"length_text\":\"3:42\"", json, StringComparison.Ordinal);
        Assert.Contains("\"disambiguation\":\"2013 remaster\"", json, StringComparison.Ordinal);
        Assert.Equal("2013 remaster", back.Discs[0].Tracks[0].Disambiguation);
    }

    [Fact]
    public void GroupMatch_UsesSnakeCaseNames_AndRoundTrips()
    {
        var item = Guid.NewGuid();
        var request = new EditorGroupMatchRequestDto { Kind = EditorGroupMatchKinds.Episodes, EntityIds = [item], SeriesId = "100", SeasonNumber = 2 };
        var answer = new EditorGroupMatchDto
        {
            Kind = EditorGroupMatchKinds.Episodes,
            TargetLabel = "The Last Signal · Season 2",
            Lines =
            [
                new EditorGroupMatchLineDto { EntityId = item, Title = "s02e03.mkv", NumberText = "Episode 3", TargetTitle = "Echoes in the Dark", Verdict = EditorMatchVerdicts.Strong, Fits = true },
                new EditorGroupMatchLineDto { EntityId = Guid.NewGuid(), Title = "extra.mkv", Note = "Its name carries no episode number to line up by." },
            ],
        };

        var requestJson = JsonSerializer.Serialize(request);
        var answerJson = JsonSerializer.Serialize(answer);
        var back = JsonSerializer.Deserialize<EditorGroupMatchDto>(answerJson)!;

        Assert.Equal("{\"kind\":\"episodes\",\"entity_ids\":[\"" + item.ToString("D") + "\"],\"series_id\":\"100\",\"season_number\":2,\"release_id\":null}", requestJson);
        Assert.Contains("\"target_label\":", answerJson, StringComparison.Ordinal);
        Assert.Equal("The Last Signal · Season 2", back.TargetLabel);
        Assert.Contains("\"number_text\":\"Episode 3\"", answerJson, StringComparison.Ordinal);
        Assert.Contains("\"target_title\":\"Echoes in the Dark\"", answerJson, StringComparison.Ordinal);
        Assert.Contains("\"fits\":true", answerJson, StringComparison.Ordinal);
        Assert.Contains("\"fits\":false", answerJson, StringComparison.Ordinal);
        Assert.Equal(2, back.Lines.Count);
        Assert.Equal("Echoes in the Dark", back.Lines[0].TargetTitle);
    }

    [Fact]
    public void AMusicTrackPick_IsASongRecordWithTheExactReleaseAndRecording_AndItsDiscAndTrack()
    {
        var candidate = EditorMatchCandidates.ForMusicTrack(
            "r-1", "rg-1", "Low Water", "The Tides", "2011", hasCover: true, "rec-3", "Harbor Lights", null, disc: 2, track: 3, lengthSeconds: 222.4, disambiguation: "2013 remaster");

        Assert.Equal("musicbrainz", candidate.ProviderName);
        Assert.Equal("rec-3", candidate.ProviderItemId);
        Assert.Equal("r-1", candidate.BridgeIds["musicbrainz_release_id"]);
        Assert.Equal("rg-1", candidate.BridgeIds["musicbrainz_release_group_id"]);
        Assert.Equal("rec-3", candidate.BridgeIds["musicbrainz_recording_id"]);
        Assert.Equal("Low Water", candidate.RequiredFields["album"]);
        Assert.Equal("The Tides", candidate.RequiredFields["artist"]);
        Assert.Equal("3", candidate.SuggestedFields["track_number"]);
        Assert.Equal("2", candidate.SuggestedFields["disc_number"]);
        Assert.Equal("222400", candidate.ExtraFields[EditorMatchCandidates.Extra.DurationMilliseconds]);
        Assert.Equal("2013 remaster", candidate.ExtraFields[EditorMatchCandidates.Extra.Disambiguation]);
        Assert.Equal("https://coverartarchive.org/release/r-1/front-500", candidate.CoverUrl);
    }

    [Fact]
    public void AMusicTrackPickOnAReleaseWithNoCover_NamesNoCoverAddress() =>
        Assert.Null(EditorMatchCandidates.ForMusicTrack("r-1", null, "Low Water", null, null, hasCover: false, null, "Song", null, 1, 1, null, null).CoverUrl);

    [Fact]
    public void ATvEpisodePick_CarriesTheExactShowSeasonEpisodeAndRuntime_SoItIsNeverAShowOnlyMatch()
    {
        var candidate = EditorMatchCandidates.ForTvEpisode("100", "The Last Signal", "e23", 2, 3, "Echoes in the Dark", "2021-01-18", "Overview", null, 46);

        Assert.Equal("tvdb", candidate.ProviderName);
        Assert.Equal("e23", candidate.ProviderItemId);
        Assert.Equal("100", candidate.BridgeIds["tvdb_id"]);
        Assert.Equal("e23", candidate.BridgeIds["tvdb_episode_id"]);
        Assert.Equal("The Last Signal", candidate.RequiredFields["show_name"]);
        Assert.Equal("2", candidate.RequiredFields["season_number"]);
        Assert.Equal("3", candidate.RequiredFields["episode_number"]);
        Assert.Equal("46", candidate.ExtraFields["runtime"]);
        Assert.Equal("2021", candidate.Year);
    }

    [Fact]
    public void TheTvBrowsersNewFacts_AreOptional_SoOlderAnswersStillRead()
    {
        var episode = new TvMatchBrowserEpisodeDto("e1", "100", "Show", "s1", 1, 1, "Pilot", null, null, null, string.Empty, false, false, null);
        var seasons = new TvMatchBrowserSeasonsDto("100", "Show", []);

        Assert.Null(episode.RuntimeMinutes);
        Assert.Null(episode.AbsoluteNumber);
        Assert.Null(seasons.Genre);
        Assert.Null(seasons.Overview);
    }
}
