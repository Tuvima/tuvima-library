using System.Text.Json.Nodes;
using MediaEngine.Api.Services.Matching;

namespace MediaEngine.Api.Tests;

public sealed class ParentFirstPairingEngineTests
{
    [Fact]
    public void WrongSpecialsNumber_CanProposeActualEpisodeAcrossSeasonsForReview()
    {
        var asset = new PairingAssetEvidence(Guid.NewGuid(), "Show.S00E01.The Beginning.mkv");
        var preview = ParentFirstPairingEngine.Preview(new PairingRequest(
            PairingMediaKind.TvEpisode, "tvdb", "show-1", [asset],
            [Tv("special", 0, 1, "Behind the Scenes"), Tv("actual", 1, 1, "The Beginning")], true));

        var row = Assert.Single(preview.Rows);
        Assert.Equal("actual", row.Proposed?.Child.ChildId);
        Assert.Equal(PairingBand.Review, row.Band);
        Assert.False(row.CanPreselect);
        Assert.Contains(row.Proposed!.Conflicts, reason => reason.Contains("similarly strong", StringComparison.Ordinal));
    }

    [Fact]
    public void TvAdapter_OrdersSeasonTenNumerically_AndUsesDefaultOrderFields()
    {
        var catalogue = ParentFirstCatalogueAdapters.FromTvdbDefaultEpisodes("show-1", [
            JsonNode.Parse("""{"id": "10", "seasonNumber": 10, "number": 1, "name": "Later"}""")!,
            JsonNode.Parse("""{"id": "2", "seasonNumber": 2, "number": 1, "name": "Earlier"}""")!,
        ]);
        Assert.Equal([2, 10], catalogue.Select(child => child.SeasonNumber!.Value).ToArray());
        var row = Assert.Single(ParentFirstPairingEngine.Preview(new PairingRequest(
            PairingMediaKind.TvEpisode, "tvdb", "show-1",
            [new PairingAssetEvidence(Guid.NewGuid(), "Show.S10E01.mkv")], catalogue, true)).Rows);
        Assert.Equal("10", row.Proposed?.Child.ChildId);
        Assert.True(row.CanPreselect);
    }

    [Fact]
    public void MusicAdapter_PreservesReleaseTrackPositionSeparateFromRepeatedRecording()
    {
        const string releaseId = "11111111-1111-4111-8111-111111111111";
        const string manifest = """
            {"source":"musicbrainz_release","provider_collection_id":"11111111-1111-4111-8111-111111111111","tracks":[
              {"ordinal":1,"disc_number":1,"track_number":1,"title":"Opening","musicbrainz_release_track_id":"22222222-2222-4222-8222-222222222222","musicbrainz_recording_id":"recording-1"},
              {"ordinal":13,"disc_number":2,"track_number":1,"title":"Opening Reprise","musicbrainz_release_track_id":"33333333-3333-4333-8333-333333333333","musicbrainz_recording_id":"recording-1"}
            ]}
            """;
        var catalogue = ParentFirstCatalogueAdapters.FromMusicBrainzReleaseManifest(releaseId, manifest);
        Assert.Equal(2, catalogue.Count);
        Assert.NotEqual(catalogue[0].ChildId, catalogue[1].ChildId);
        Assert.Equal(catalogue[0].RecordingId, catalogue[1].RecordingId);

        var asset = new PairingAssetEvidence(Guid.NewGuid(), "01 - Opening Reprise.flac",
            DiscNumber: 2, TrackNumber: 1);
        var row = Assert.Single(ParentFirstPairingEngine.Preview(new PairingRequest(
            PairingMediaKind.MusicReleaseTrack, "musicbrainz", releaseId, [asset], catalogue, true)).Rows);
        Assert.Equal(catalogue[1].ChildId, row.Proposed?.Child.ChildId);
        Assert.True(row.CanPreselect);
    }

    [Fact]
    public void MusicAdapter_OmitsTracksWithoutProviderTrackMbid()
    {
        const string releaseId = "11111111-1111-4111-8111-111111111111";
        const string manifest = """
            {"source":"musicbrainz_release","provider_collection_id":"11111111-1111-4111-8111-111111111111","tracks":[
              {"ordinal":1,"disc_number":1,"track_number":1,"title":"Known","musicbrainz_release_track_id":"22222222-2222-4222-8222-222222222222"},
              {"ordinal":2,"disc_number":1,"track_number":2,"title":"Unverified"},
              {"ordinal":3,"disc_number":1,"track_number":3,"title":"Invalid","musicbrainz_release_track_id":"not-an-id"}
            ]}
            """;
        var child = Assert.Single(ParentFirstCatalogueAdapters.FromMusicBrainzReleaseManifest(releaseId, manifest));
        Assert.Equal("22222222-2222-4222-8222-222222222222", child.ChildId);
    }

    [Fact]
    public void MusicAdapter_UsesReleaseTrackMbid_AndKeepsOriginalAndDeluxeSeparate()
    {
        const string original = "11111111-1111-4111-8111-111111111111";
        const string deluxe = "22222222-2222-4222-8222-222222222222";
        const string originalTrack = "33333333-3333-4333-8333-333333333333";
        const string deluxeTrack = "44444444-4444-4444-8444-444444444444";
        const string recording = "55555555-5555-4555-8555-555555555555";
        static string Manifest(string release, string track, string recordingId) => $$"""
            {"source":"musicbrainz_release","provider_collection_id":"{{release}}","tracks":[
              {"ordinal":1,"disc_number":1,"track_number":1,"title":"Song",
               "musicbrainz_release_track_id":"{{track}}","musicbrainz_recording_id":"{{recordingId}}"}]}
            """;

        var originalCatalogue = ParentFirstCatalogueAdapters.FromMusicBrainzReleaseManifest(
            original, Manifest(original, originalTrack, recording));
        var deluxeCatalogue = ParentFirstCatalogueAdapters.FromMusicBrainzReleaseManifest(
            deluxe, Manifest(deluxe, deluxeTrack, recording));

        Assert.Equal(originalTrack, Assert.Single(originalCatalogue).ChildId);
        Assert.Equal(deluxeTrack, Assert.Single(deluxeCatalogue).ChildId);
        Assert.Equal(recording, originalCatalogue[0].RecordingId);
        Assert.Equal(recording, deluxeCatalogue[0].RecordingId);
        var preview = ParentFirstPairingEngine.Preview(new PairingRequest(
            PairingMediaKind.MusicReleaseTrack, "musicbrainz", original,
            [new PairingAssetEvidence(Guid.NewGuid(), "01 - Song.flac", DiscNumber: 1, TrackNumber: 1)],
            deluxeCatalogue, CatalogueComplete: false));
        Assert.Null(Assert.Single(preview.Rows).Proposed);
    }

    [Fact]
    public void AmbiguousTitle_DoesNotPreselectEitherChild()
    {
        var asset = new PairingAssetEvidence(Guid.NewGuid(), "unknown.mkv", Title: "Pilot");
        var row = Assert.Single(ParentFirstPairingEngine.Preview(new PairingRequest(
            PairingMediaKind.TvEpisode, "tvdb", "show-1", [asset],
            [Tv("one", 1, 1, "Pilot"), Tv("two", 2, 1, "Pilot")], true)).Rows);
        Assert.Equal(PairingBand.Review, row.Band);
        Assert.False(row.CanPreselect);
        Assert.Single(row.Alternatives);
    }

    [Fact]
    public void CombinedFile_IsExplicitLimitation_AndValidVersionsAreNotCollisions()
    {
        var combined = new PairingAssetEvidence(Guid.NewGuid(), "Show.S01E01E02.mkv");
        var versionA = new PairingAssetEvidence(Guid.NewGuid(), "Show.S01E01.mkv");
        var versionB = new PairingAssetEvidence(Guid.NewGuid(), "Show.S01E01.1080p.mkv");
        var rows = ParentFirstPairingEngine.Preview(new PairingRequest(
            PairingMediaKind.TvEpisode, "tvdb", "show-1", [combined, versionA, versionB],
            [Tv("episode-1", 1, 1, "Pilot")], true)).Rows;
        Assert.Null(rows[0].Proposed);
        Assert.Contains("Combined media", rows[0].Limitation);
        Assert.All(rows.Skip(1), row => Assert.Equal("episode-1", row.Proposed?.Child.ChildId));
        Assert.All(rows.Skip(1), row => Assert.True(row.CanPreselect));
    }

    [Fact]
    public void IncompleteCatalogue_CannotClaimAbsenceOrPreselect()
    {
        var asset = new PairingAssetEvidence(Guid.NewGuid(), "Show.S01E01.mkv");
        var row = Assert.Single(ParentFirstPairingEngine.Preview(new PairingRequest(
            PairingMediaKind.TvEpisode, "tvdb", "show-1", [asset], [], false)).Rows);
        Assert.Equal(PairingBand.Review, row.Band);
        Assert.Contains("incomplete", row.Limitation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThousandFiles_KeepStableRowsAndSelections()
    {
        var catalogue = Enumerable.Range(1, 1000)
            .Select(number => Tv($"episode-{number}", 1, number, $"Episode {number}"))
            .ToArray();
        var assets = Enumerable.Range(1, 1000)
            .Select(number => new PairingAssetEvidence(Guid.NewGuid(), $"Show.S01E{number:D4}.mkv"))
            .ToArray();
        var preview = ParentFirstPairingEngine.Preview(new PairingRequest(
            PairingMediaKind.TvEpisode, "tvdb", "show-1", assets, catalogue, true));

        Assert.Equal(1000, preview.Rows.Count);
        Assert.Equal("episode-1000", preview.Rows[^1].Proposed?.Child.ChildId);
        Assert.All(preview.Rows, row => Assert.True(row.CanPreselect));
    }

    private static PairingCatalogueChild Tv(string id, int season, int episode, string title) =>
        new(id, "show-1", "tvdb", title, SeasonNumber: season, EpisodeNumber: episode);
}
