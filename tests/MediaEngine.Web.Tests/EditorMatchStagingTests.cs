using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>
/// What a chosen match stages besides the match itself (universal editor v5, 3c): a song takes its disc and track number from the chosen
/// record, and the move to its new album carries the track number as one change, so the song lands on the right track.
/// </summary>
public sealed class EditorMatchStagingTests
{
    private static readonly Guid SongId = Guid.NewGuid();

    [Fact]
    public void AMoveForASong_CarriesTheChosenTracksNumber_AsOnePlaceToken()
    {
        var pending = new PendingEditList();
        var destination = Guid.NewGuid();

        EditorMatchStaging.StageMove(pending, SongId, new EditorMatchPlacementDto
        {
            DestinationId = destination,
            ParentLabel = "Low Water (Deluxe)",
            CurrentParentLabel = "Low Water",
            Position = 3,
        });

        var move = Assert.Single(pending.Changes, change => change.Kind == EditSessionChangeKinds.Relationship);
        Assert.Equal(destination.ToString("D"), move.TargetId);
        Assert.True(EditSessionPosition.IsPlace(move.Token, out var number));
        Assert.Equal(3, number);
    }

    [Fact]
    public void AMoveForAnEpisode_CarriesNoTrackNumber()
    {
        var pending = new PendingEditList();

        EditorMatchStaging.StageMove(pending, SongId, new EditorMatchPlacementDto
        {
            DestinationId = Guid.NewGuid(),
            ParentLabel = "The Last Signal · Season 2",
            CurrentParentLabel = "Signal Lost · Season 2",
        });

        var move = Assert.Single(pending.Changes, change => change.Kind == EditSessionChangeKinds.Relationship);
        Assert.Null(move.Token);
    }

    [Fact]
    public void AMatchedTrack_SavesItsDiscAndTrackNumber_WithTheRecordingAndRelease()
    {
        var candidate = EditorMatchCandidates.ForMusicTrack(
            "rel-b", "rg-1", "Low Water (Deluxe)", "The Tides", "2013", true, "rec-3", "Harbor Lights", "The Tides", 2, 3, 222, "2013 remaster");
        var subject = new EditorMatchSubject(SongId, "Music", "track", "track", "Harbor Lights", "The Tides", "Low Water", "2011", new Dictionary<string, string>(), []);

        var request = EditorMatchRules.RequestFor(candidate, subject, "track");

        Assert.Equal("3", request.SuggestedFields["track_number"]);
        Assert.Equal("2", request.SuggestedFields["disc_number"]);
        Assert.Equal("rel-b", request.BridgeIds["musicbrainz_release_id"]);
        Assert.Equal("rec-3", request.BridgeIds["musicbrainz_recording_id"]);
    }
}
