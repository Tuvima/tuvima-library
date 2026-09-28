using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Tests;

public sealed class AdaptiveHlsProfileTests
{
    [Theory]
    [InlineData("smpte2084")]
    [InlineData("arib-std-b67")]
    public void HdrIsToneMappedBeforeEightBitOutput(string transfer)
    {
        var filter = AdaptiveHlsService.BuildVideoFilter(720, transfer);
        Assert.Contains("tonemap=", filter);
        Assert.EndsWith("format=yuv420p", filter);
    }

    [Fact]
    public void SdrTenBitCanUseCompatibleEightBitOutput() =>
        Assert.Equal("scale=-2:720,format=yuv420p", AdaptiveHlsService.BuildVideoFilter(720, "bt709"));
    [Fact]
    public void RenditionSelection_NeverUpscalesSource()
    {
        var settings = new AdaptiveHlsSettings();

        var selected = AdaptiveHlsService.SelectRenditions(settings, 720);

        Assert.Equal([720, 480], selected.Select(rendition => rendition.Height));
    }

    [Fact]
    public void RenditionSelection_KeepsSmallSourcePlayable()
    {
        var settings = new AdaptiveHlsSettings();

        var selected = AdaptiveHlsService.SelectRenditions(settings, 360);

        var rendition = Assert.Single(selected);
        Assert.Equal(360, rendition.Height);
    }
}
