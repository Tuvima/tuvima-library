using Bunit;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Tests.Support;
namespace MediaEngine.Web.Tests;
public sealed class ListenTransportRenderTests : AsyncBunitContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactDockKeepsOnlyItsPrimaryTransport(bool audiobook)
    {
        JSInterop.Mode=JSRuntimeMode.Loose;
        var cut=Render<ListenTransportControls>(p=>p.Add(c=>c.IsAudiobookMode,audiobook).Add(c=>c.Compact,true).Add(c=>c.ShowMusicModeButtons,true).Add(c=>c.PrimaryAppearance,"dock"));
        Assert.Single(cut.FindAll("button"));
        Assert.Equal("Play",cut.Find("button").GetAttribute("aria-label"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothDockModesRenderTheRequestedRingAppearance(bool audiobook)
    {
        JSInterop.Mode=JSRuntimeMode.Loose;
        var cut=Render<ListenTransportControls>(p=>p.Add(c=>c.IsAudiobookMode,audiobook).Add(c=>c.PrimaryAppearance,"dock"));
        Assert.Equal("dock",cut.FindComponent<PlaybackPrimaryButton>().Instance.Appearance);
        Assert.Single(cut.FindAll(".playback-primary-button-shell--appearance-dock"));
    }
}
