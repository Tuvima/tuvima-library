using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Configuration;
using MediaEngine.Providers.Services;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The release and the track the Match tab's music flow suggests, and the track a group Match lines a song up with (universal editor v5,
/// 3c and D36): the album's title and the Library's track count pick the release; the title, the number and the length pick the track; a
/// song is lined up by its own track number, on its own disc when the file names one.
/// </summary>
public sealed class EditorMusicSuggestionTests
{
    private readonly EditorMatchVerdictCalculator _calculator = new(new MatchVerdictSettings());

    private static MusicBrainzReleaseSummary Release(string id, string title, int tracks) => new(id, null, title, "The Tides", "2011", null, "CD", "GB", tracks, false);

    private static MusicBrainzTrackDetail Track(int disc, int track, string title, double? seconds = null, string? note = null) =>
        new($"rec-{disc}-{track}", $"rt-{disc}-{track}", disc, track, title, "The Tides", seconds, note);

    [Fact]
    public void TheReleaseWithTheAlbumsTitleAndTheLibrarysTrackCount_IsSuggested()
    {
        var releases = new[] { Release("a", "Low Water (Deluxe)", 24), Release("b", "Low Water", 11), Release("c", "Low Water", 12) };

        Assert.Equal("b", EditorMusicSuggestion.SuggestRelease(releases, "Low Water", libraryTrackCount: 11));
    }

    [Fact]
    public void WhenTheLibraryHoldsPartOfTheAlbum_AReleaseWithMoreTracksStillFits()
    {
        var releases = new[] { Release("a", "Other Album", 8), Release("b", "Low Water", 11) };

        Assert.Equal("b", EditorMusicSuggestion.SuggestRelease(releases, "Low Water", libraryTrackCount: 6));
    }

    [Fact]
    public void WhenNothingFits_NoReleaseIsSuggested() =>
        Assert.Null(EditorMusicSuggestion.SuggestRelease([Release("a", "Something Else", 3)], "Low Water", libraryTrackCount: 0));

    [Fact]
    public void TheTrackWithTheFilesTitleNumberAndLength_IsPreSelected()
    {
        var tracks = new[] { Track(1, 3, "Opening", 180), Track(2, 3, "Harbor Lights", 222, "2013 remaster"), Track(2, 4, "Low Tide", 241) };
        var evidence = new EditorMatchLocalEvidenceDto { FileTitle = "Harbor Lights", Number = 3, Disc = 2, MeasuredSeconds = 222 };

        var pick = EditorMusicSuggestion.SuggestTrack(tracks, evidence, _calculator);

        Assert.Equal("rec-2-3", pick!.RecordingId);
        Assert.Equal("2013 remaster", pick.Disambiguation);
    }

    [Fact]
    public void ANumberAndLengthThatAgree_AreEnoughWithoutATitle()
    {
        var tracks = new[] { Track(1, 1, "One", 100), Track(1, 2, "Two", 222) };
        var evidence = new EditorMatchLocalEvidenceDto { Number = 2, MeasuredSeconds = 222 };

        Assert.Equal("rec-1-2", EditorMusicSuggestion.SuggestTrack(tracks, evidence, _calculator)!.RecordingId);
    }

    [Fact]
    public void WhenNothingAboutTheFileFits_NoTrackIsPreSelected()
    {
        var tracks = new[] { Track(1, 1, "One", 100) };
        var evidence = new EditorMatchLocalEvidenceDto { FileTitle = "Unrelated", Number = 9, MeasuredSeconds = 500 };

        Assert.Null(EditorMusicSuggestion.SuggestTrack(tracks, evidence, _calculator));
    }

    [Fact]
    public void TwoRecordingsOfOneSong_AreToldApartByTheirNoteInTheRow()
    {
        var tracks = new[] { Track(1, 5, "Harbor Lights", 222, "live at Wembley"), Track(1, 6, "Harbor Lights", 221, "2013 remaster") };

        Assert.Equal(["live at Wembley", "2013 remaster"], tracks.Select(track => track.Disambiguation));
    }

    [Fact]
    public void AGroupLinesASongUpByItsOwnNumber_OnTheFilesDiscWhenItNamesOne()
    {
        var tracks = new[] { Track(1, 3, "Opening"), Track(2, 3, "Harbor Lights"), Track(2, 4, "Low Tide") };

        Assert.Equal("rec-2-3", EditorMusicSuggestion.TrackFor(tracks, 3, disc: 2, fileTitle: null)!.RecordingId);
        Assert.Equal("rec-1-3", EditorMusicSuggestion.TrackFor(tracks, 3, disc: 1, fileTitle: null)!.RecordingId);
    }

    [Fact]
    public void AGroupSongWithNoDisc_TakesTheDiscWhoseTitleAgrees_ElseTheEarliest()
    {
        var tracks = new[] { Track(1, 3, "Opening"), Track(2, 3, "Harbor Lights") };

        Assert.Equal("rec-2-3", EditorMusicSuggestion.TrackFor(tracks, 3, disc: null, fileTitle: "Harbor Lights")!.RecordingId);
        Assert.Equal("rec-1-3", EditorMusicSuggestion.TrackFor(tracks, 3, disc: null, fileTitle: "Something")!.RecordingId);
    }

    [Fact]
    public void AGroupSongWhoseNumberTheReleaseLacks_IsLeftUnplaced() =>
        Assert.Null(EditorMusicSuggestion.TrackFor([Track(1, 1, "One")], 7, disc: null, fileTitle: null));
}
