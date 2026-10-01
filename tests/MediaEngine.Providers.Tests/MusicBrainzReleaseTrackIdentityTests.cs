using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Tests;

public sealed class MusicBrainzReleaseTrackIdentityTests
{
    [Fact]
    public void ExactReleaseManifest_PreservesTrackMbidSeparateFromRepeatedRecording()
    {
        const string releaseId = "11111111-1111-4111-8111-111111111111";
        const string recordingId = "22222222-2222-4222-8222-222222222222";
        const string firstTrackId = "33333333-3333-4333-8333-333333333333";
        const string secondTrackId = "44444444-4444-4444-8444-444444444444";
        var release = JsonNode.Parse("""
            {
              "id":"11111111-1111-4111-8111-111111111111", "title":"Album",
              "media":[{"position":1,"tracks":[
                {"id":"33333333-3333-4333-8333-333333333333","position":1,"title":"Song","recording":{"id":"22222222-2222-4222-8222-222222222222"}},
                {"id":"44444444-4444-4444-8444-444444444444","position":2,"title":"Song (reprise)","recording":{"id":"22222222-2222-4222-8222-222222222222"}}
              ]}]
            }
            """);

        var result = MusicBrainzReleaseClient.BuildRelease(release, releaseId);
        Assert.NotNull(result);
        using var manifest = JsonDocument.Parse(result.ManifestJson);
        var tracks = manifest.RootElement.GetProperty("tracks").EnumerateArray().ToArray();
        Assert.Equal(firstTrackId, tracks[0].GetProperty("musicbrainz_release_track_id").GetString());
        Assert.Equal(secondTrackId, tracks[1].GetProperty("musicbrainz_release_track_id").GetString());
        Assert.All(tracks, track => Assert.Equal(recordingId,
            track.GetProperty("musicbrainz_recording_id").GetString()));
    }
}
