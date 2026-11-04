using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Tests.Support;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class UnifiedPlayerTests
{
    [Theory]
    [InlineData(false, 0d, false, false)] // initial zero before the player reported: sound on
    [InlineData(false, 0d, true, true)]   // the player reported a real zero level
    [InlineData(true, 0.8d, true, true)]  // the person muted it
    [InlineData(false, 0.8d, true, false)]
    public void MuteDisplay_StartsWithSoundOn_AndOnlyShowsMutedAfterMuteOrZero(bool muted, double volume, bool reported, bool expected)
    {
        Assert.Equal(expected, PlaybackMuteDisplay.ShowsMuted(muted, volume, reported));
    }

    [Fact]
    public void NewPlaybackSession_HasNotReportedAVolumeYet()
    {
        var controller = new PlaybackSessionController(null!, EngineApiClientStub.CreateDefault());
        Assert.False(controller.VolumeReported);
    }
}
