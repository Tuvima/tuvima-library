using MediaEngine.Contracts.Search;
using MediaEngine.Web.Components.MediaEditor;

namespace MediaEngine.Web.Tests;

public sealed class MediaEditorPairingTargetSelectorTests
{
    [Fact]
    public void TvTargetAcceptsOnlyNumericTheTvdbShowId()
    {
        Assert.Equal("426321", MediaEditorPairingTargetSelector.GetTargetId(
            new SearchRetailCandidateDto { ProviderName = "TheTVDB", ProviderItemId = "426321" }, "TV"));
        Assert.Null(MediaEditorPairingTargetSelector.GetTargetId(
            new SearchRetailCandidateDto { ProviderName = "TMDB", ProviderItemId = "426321" }, "TV"));
        Assert.Null(MediaEditorPairingTargetSelector.GetTargetId(
            new SearchRetailCandidateDto { ProviderName = "TheTVDB", ProviderItemId = "S01E01" }, "TV"));
    }

    [Fact]
    public void MusicTargetPrefersScopedReleaseIdAndRejectsOtherProviders()
    {
        var releaseId = Guid.NewGuid();
        var recordingId = Guid.NewGuid();
        var candidate = new SearchRetailCandidateDto
        {
            ProviderName = "MusicBrainz",
            ProviderItemId = recordingId.ToString("D"),
            ExtraFields = new Dictionary<string, string> { ["musicbrainz_release_id"] = releaseId.ToString("D") },
        };

        Assert.Equal(releaseId.ToString("D"), MediaEditorPairingTargetSelector.GetTargetId(candidate, "Music"));
        candidate.ProviderName = "Other";
        Assert.Null(MediaEditorPairingTargetSelector.GetTargetId(candidate, "Music"));
        candidate.ProviderName = "MusicBrainz";
        candidate.ExtraFields = new Dictionary<string, string>();
        Assert.Null(MediaEditorPairingTargetSelector.GetTargetId(candidate, "Music"));
    }

    [Theory]
    [InlineData("426321", "TV", true)]
    [InlineData("S01E01", "TV", false)]
    [InlineData("00000000-0000-0000-0000-000000000001", "Music", true)]
    [InlineData("426321", "Music", false)]
    public void ManualTargetValidationMatchesProviderScope(string id, string mediaType, bool expected)
    {
        Assert.Equal(expected, MediaEditorPairingTargetSelector.IsValidTargetId(id, mediaType));
    }
}
