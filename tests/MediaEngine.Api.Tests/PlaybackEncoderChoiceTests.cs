using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

public sealed class PlaybackEncoderChoiceTests
{
    [Fact]
    public void Automatic_UsesTheGraphicsCardWhenItWorks()
    {
        var choice = AdaptiveHlsService.ChooseEncoder("auto", new HardwareCapabilities { HasNvenc = true, PreferredEncoder = "h264_nvenc" });

        Assert.Equal("h264_nvenc", choice.Encoder);
        Assert.Equal(PlaybackEncodePlaces.GraphicsCard, choice.RanOn);
        Assert.Null(choice.FallbackReason);
    }

    [Fact]
    public void Automatic_FallsBackToTheProcessorAndSaysWhyInPlainWords()
    {
        var hardware = new HardwareCapabilities
        {
            ProbeFailures = new Dictionary<string, string> { ["h264_nvenc"] = "Driver does not support the required nvenc API version. Required: 13.0" },
        };

        var choice = AdaptiveHlsService.ChooseEncoder("auto", hardware);

        Assert.Equal("libx264", choice.Encoder);
        Assert.Equal(PlaybackEncodePlaces.Processor, choice.RanOn);
        Assert.Equal("The graphics driver is too old for this card or this FFmpeg.", choice.FallbackReason);
    }

    [Fact]
    public void Automatic_WithNoCard_SaysNoneWasFound()
    {
        var choice = AdaptiveHlsService.ChooseEncoder("auto", new HardwareCapabilities());

        Assert.Equal("No graphics card encoding was found", choice.FallbackReason);
    }

    [Fact]
    public void ProcessorOnly_IsHonouredAndStated()
    {
        var choice = AdaptiveHlsService.ChooseEncoder("cpu", new HardwareCapabilities { HasNvenc = true, PreferredEncoder = "h264_nvenc" });

        Assert.Equal("libx264", choice.Encoder);
        Assert.Contains("Processor only", choice.FallbackReason);
    }

    [Fact]
    public void OneQualityIsBuiltForAHomePlayer_AndALowerOneOnlyWhenTheConnectionAsks()
    {
        var settings = new AdaptiveHlsSettings();
        var copy = new PlaybackDecision(PlaybackRungs.EncodePicture, "x", false, []);

        var home = AdaptiveHlsService.PickRenditions(settings, 2160, new HlsStartPlan(copy, 1080));
        var remote = AdaptiveHlsService.PickRenditions(settings, 2160, new HlsStartPlan(copy, 720, null, OfferLowerQuality: true));

        Assert.Equal([1080], home.Select(r => r.Height));
        Assert.Equal([720, 480], remote.Select(r => r.Height));
    }

    [Fact]
    public async Task TheEncodeLimit_AppliesAtOnceWhenItIsChanged()
    {
        var limit = 1;
        var gate = new EncodeSlotGate(() => limit);
        await gate.WaitAsync(CancellationToken.None);
        var second = gate.WaitAsync(CancellationToken.None);
        await Task.Delay(700);
        Assert.False(second.IsCompleted);

        limit = 2;
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(second.IsCompletedSuccessfully);
    }
}
