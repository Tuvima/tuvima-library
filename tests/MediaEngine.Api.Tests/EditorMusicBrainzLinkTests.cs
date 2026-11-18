using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Tests;

public sealed class EditorMusicBrainzLinkTests
{
    private const string Recording = "11111111-1111-4111-8111-111111111111";
    private const string Release = "22222222-2222-4222-8222-222222222222";
    private const string Group = "33333333-3333-4333-8333-333333333333";

    private static ItemCanonicalRetailCandidateDto Candidate(string? itemId, params (string Key, string Value)[] ids)
    {
        var candidate = new ItemCanonicalRetailCandidateDto { ProviderName = "musicbrainz", ProviderItemId = itemId };
        foreach (var (key, value) in ids)
        {
            candidate.BridgeIds[key] = value;
        }

        return candidate;
    }

    [Fact]
    public void ASong_LinksToItsRecording_NotToAReleaseGroupPage()
    {
        var link = EditorMatchSearchService.MusicBrainzUrl(
            Candidate(Release, (BridgeIdKeys.MusicBrainzRecordingId, Recording), (BridgeIdKeys.MusicBrainzReleaseId, Release)), EditorKinds.Song);

        Assert.Equal($"https://musicbrainz.org/recording/{Recording}", link);
    }

    [Fact]
    public void AnAlbum_LinksToItsReleaseGroup_ElseItsRelease()
    {
        Assert.Equal(
            $"https://musicbrainz.org/release-group/{Group}",
            EditorMatchSearchService.MusicBrainzUrl(Candidate(Release, (BridgeIdKeys.MusicBrainzReleaseGroupId, Group)), EditorKinds.Album));
        Assert.Equal($"https://musicbrainz.org/release/{Release}", EditorMatchSearchService.MusicBrainzUrl(Candidate(Release), EditorKinds.Album));
    }

    [Fact]
    public void AnIdThatIsNotAMusicBrainzId_GivesNoLink()
    {
        Assert.Null(EditorMatchSearchService.MusicBrainzUrl(Candidate("12345"), EditorKinds.Song));
    }
}
