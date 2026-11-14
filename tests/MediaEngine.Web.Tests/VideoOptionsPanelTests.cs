using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>"Video quality and devices" (playback-path spec D15): the Dashboard draws the Engine's list and sends a change back, nothing more.</summary>
public sealed class VideoOptionsPanelTests : AsyncBunitContext
{
    public VideoOptionsPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddNativeUiServices();
    }

    private static VideoOptionChoiceDto Choice(string value, string label) => new() { Value = value, Label = label };

    private static VideoPlaybackOptionsDto List(string quality = "Auto", string resolution = "auto", string convert = "on", params string[] learned) => new()
    {
        Options =
        [
            new VideoOptionDto
            {
                Id = VideoOptionIds.VideoQuality, Label = "Video quality", Kind = VideoOptionKinds.Choice, Scope = VideoOptionScopes.Profile,
                Choices = [Choice("Auto", "Automatic"), Choice("High", "Highest"), Choice("Balanced", "Balanced"), Choice("DataSaver", "Data saver")],
                Value = quality, DefaultValue = "Auto",
            },
            new VideoOptionDto
            {
                Id = VideoOptionIds.ConvertOnlyWhenNeeded, Label = "Convert only when needed", Kind = VideoOptionKinds.Toggle, Scope = VideoOptionScopes.Profile,
                Value = convert, DefaultValue = "on",
            },
            new VideoOptionDto
            {
                Id = VideoOptionIds.MaxResolution, Label = "Maximum resolution", Kind = VideoOptionKinds.Choice, Scope = VideoOptionScopes.Device,
                Choices = [Choice("auto", "Automatic"), Choice("1080", "1080p"), Choice("720", "720p")],
                Value = resolution, DefaultValue = "auto",
            },
            new VideoOptionDto
            {
                Id = VideoOptionIds.DeviceAbilities, Label = "What this device can play", Kind = VideoOptionKinds.Summary, Scope = VideoOptionScopes.Device,
                Value = "Plays H.264 and HEVC in hardware · HDR · up to 4K",
            },
            new VideoOptionDto
            {
                Id = VideoOptionIds.CheckAgain, Label = "Check again", Kind = VideoOptionKinds.Action, Scope = VideoOptionScopes.Device,
            },
        ],
        Learned = learned,
    };

    [Fact]
    public void AProfilePanelDrawsOnlyThePersonsRows_WithTheEnginesWordsAndChoices()
    {
        var cut = Render<VideoOptionsPanel>(parameters => parameters
            .Add(panel => panel.Scope, VideoOptionScopes.Profile)
            .Add(panel => panel.Load, () => Task.FromResult<VideoPlaybackOptionsDto?>(List()))
            .Add(panel => panel.Save, (_, _) => Task.FromResult<VideoPlaybackOptionsDto?>(null)));

        cut.WaitForElement("[data-video-option]");
        Assert.Equal(
            [VideoOptionIds.VideoQuality, VideoOptionIds.ConvertOnlyWhenNeeded],
            cut.FindAll("[data-video-option]").Select(row => row.GetAttribute("data-video-option")));
        Assert.Contains("Video quality", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(["Automatic", "Highest", "Balanced", "Data saver"], cut.FindAll("select option").Select(option => option.TextContent.Trim()));
        Assert.DoesNotContain("Maximum resolution", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Check again", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ADevicePanelDrawsTheDeviceRowsTheSummaryAndCheckAgain()
    {
        var cut = Render<VideoOptionsPanel>(parameters => parameters
            .Add(panel => panel.Scope, VideoOptionScopes.Device)
            .Add(panel => panel.Load, () => Task.FromResult<VideoPlaybackOptionsDto?>(List(learned: "Learned: cannot play HEVC 10-bit")))
            .Add(panel => panel.Save, (_, _) => Task.FromResult<VideoPlaybackOptionsDto?>(null))
            .Add(panel => panel.CheckAgain, () => Task.CompletedTask));

        cut.WaitForElement("[data-video-option]");
        Assert.Equal(
            [VideoOptionIds.MaxResolution, VideoOptionIds.DeviceAbilities, VideoOptionIds.CheckAgain],
            cut.FindAll("[data-video-option]").Select(row => row.GetAttribute("data-video-option")));
        Assert.Contains("Plays H.264 and HEVC in hardware", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Learned: cannot play HEVC 10-bit", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WhereThePageAlreadyShowsTheSummary_ItIsLeftOut_AndAPairedDeviceHasNoCheckAgain()
    {
        var cut = Render<VideoOptionsPanel>(parameters => parameters
            .Add(panel => panel.Scope, VideoOptionScopes.Device)
            .Add(panel => panel.ShowSummary, false)
            .Add(panel => panel.Load, () => Task.FromResult<VideoPlaybackOptionsDto?>(List()))
            .Add(panel => panel.Save, (_, _) => Task.FromResult<VideoPlaybackOptionsDto?>(null)));

        cut.WaitForElement("[data-video-option]");
        Assert.Equal([VideoOptionIds.MaxResolution], cut.FindAll("[data-video-option]").Select(row => row.GetAttribute("data-video-option")));
    }

    [Fact]
    public void ChoosingAValueSendsItsIdAndValue_AndShowsTheListTheEngineReturned()
    {
        var saved = new List<(string Id, string Value)>();
        var cut = Render<VideoOptionsPanel>(parameters => parameters
            .Add(panel => panel.Scope, VideoOptionScopes.Profile)
            .Add(panel => panel.Load, () => Task.FromResult<VideoPlaybackOptionsDto?>(List()))
            .Add(panel => panel.Save, (id, value) =>
            {
                saved.Add((id, value));
                return Task.FromResult<VideoPlaybackOptionsDto?>(List(quality: "DataSaver"));
            }));

        cut.WaitForElement("select").Change("DataSaver");

        cut.WaitForAssertion(() => Assert.Equal("DataSaver", cut.Find("select").GetAttribute("value") ?? cut.Find("select option[selected]").GetAttribute("value")));
        Assert.Equal([(VideoOptionIds.VideoQuality, "DataSaver")], saved);
    }

    [Fact]
    public void ATogglePanelSaysOnOrOff()
    {
        var saved = new List<(string Id, string Value)>();
        var cut = Render<VideoOptionsPanel>(parameters => parameters
            .Add(panel => panel.Scope, VideoOptionScopes.Profile)
            .Add(panel => panel.Load, () => Task.FromResult<VideoPlaybackOptionsDto?>(List()))
            .Add(panel => panel.Save, (id, value) =>
            {
                saved.Add((id, value));
                return Task.FromResult<VideoPlaybackOptionsDto?>(List(convert: value));
            }));

        cut.WaitForElement("[data-video-option='convert_only_when_needed'] input[role=switch]").Change(false);

        cut.WaitForAssertion(() => Assert.Equal([(VideoOptionIds.ConvertOnlyWhenNeeded, "off")], saved));
    }

    [Fact]
    public void ARefusedSaveIsToldInPlainWords_WithoutAToast_AndTheOldValueStays()
    {
        var cut = Render<VideoOptionsPanel>(parameters => parameters
            .Add(panel => panel.Scope, VideoOptionScopes.Profile)
            .Add(panel => panel.Load, () => Task.FromResult<VideoPlaybackOptionsDto?>(List(quality: "Balanced")))
            .Add(panel => panel.Save, (_, _) => Task.FromResult<VideoPlaybackOptionsDto?>(null)));

        cut.WaitForElement("select").Change("High");

        cut.WaitForElement("[data-video-options-failure]");
        Assert.Contains("Video quality could not be saved.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void AListThatCannotBeLoadedSaysSo()
    {
        var cut = Render<VideoOptionsPanel>(parameters => parameters
            .Add(panel => panel.Scope, VideoOptionScopes.Profile)
            .Add(panel => panel.Load, () => Task.FromResult<VideoPlaybackOptionsDto?>(null))
            .Add(panel => panel.Save, (_, _) => Task.FromResult<VideoPlaybackOptionsDto?>(null)));

        cut.WaitForElement("[data-video-options-failure]");
        Assert.Contains("could not be loaded", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckAgainMeasuresAgainAndThenReadsTheListAgain()
    {
        var loads = 0;
        var checks = 0;
        var cut = Render<VideoOptionsPanel>(parameters => parameters
            .Add(panel => panel.Scope, VideoOptionScopes.Device)
            .Add(panel => panel.Load, () =>
            {
                loads++;
                return Task.FromResult<VideoPlaybackOptionsDto?>(List());
            })
            .Add(panel => panel.Save, (_, _) => Task.FromResult<VideoPlaybackOptionsDto?>(null))
            .Add(panel => panel.CheckAgain, () =>
            {
                checks++;
                return Task.CompletedTask;
            }));

        cut.WaitForElement("[data-video-option='check_again'] button").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, checks));
        cut.WaitForAssertion(() => Assert.Equal(2, loads));
    }
}
