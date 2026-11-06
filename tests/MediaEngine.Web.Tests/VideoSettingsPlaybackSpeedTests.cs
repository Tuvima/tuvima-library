using Bunit;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

// Unified player D10: playback speed lives inside the video Settings menu, not as its own button on the control bar.
public sealed class VideoSettingsPlaybackSpeedTests : AsyncBunitContext
{
    public VideoSettingsPlaybackSpeedTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
    }

    [Fact]
    public void SettingsMainPage_ListsPlaybackSpeedWithTheCurrentRateOnTheRight()
    {
        var cut = RenderMenu(1.25d);

        var row = cut.Find("[data-settings-row='speed']");
        Assert.Contains("Playback speed", row.QuerySelector(".video-settings-menu__row-label")!.TextContent);
        Assert.Contains("1.25x", row.QuerySelector(".video-settings-menu__row-value")!.TextContent);
    }

    [Fact]
    public void SettingsMainPage_ShowsOneDotZeroForTheDefaultRate()
    {
        var cut = RenderMenu(1d);

        Assert.Contains("1.0x", cut.Find("[data-settings-row='speed'] .video-settings-menu__row-value").TextContent);
    }

    [Fact]
    public void SettingsMainPage_HidesPlaybackSpeedWhenTheHostDoesNotListen()
    {
        var cut = Render<VideoSettingsMenu>();

        Assert.Empty(cut.FindAll("[data-settings-row='speed']"));
    }

    [Fact]
    public async Task PlaybackSpeedRow_OpensTheSpeedPageWithBackAndCloseAndBackReturnsToSettings()
    {
        var cut = RenderMenu(1.5d);

        await cut.Find("[data-settings-row='speed']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("main", cut.Find(".video-settings-menu").GetAttribute("data-settings-page"));
        Assert.Equal("Playback speed", cut.Find(".video-settings-menu__header h3").TextContent);
        Assert.Equal("1.5x", cut.Find(".video-settings-menu__speed output").TextContent);
        Assert.NotNull(cut.Find(".video-settings-menu__header button[aria-label='Close']"));

        await cut.Find(".video-settings-menu__header button[aria-label='Back']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("Settings", cut.Find(".video-settings-menu__header h3").TextContent);
        Assert.Single(cut.FindAll("[data-settings-row='speed']"));
    }

    [Fact]
    public async Task ResetToOneX_ReportsTheDefaultRateToTheHost()
    {
        var rates = new List<double>();
        var cut = RenderMenu(2d, rates);

        await cut.Find("[data-settings-row='speed']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find(".video-settings-menu__speed button[aria-label='Reset playback speed to 1×']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal([1d], rates);
    }

    [Fact]
    public void ControlBar_NoLongerHasAStandalonePlaybackSpeedButton()
    {
        var host = LocalizedSource.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor"));

        Assert.DoesNotContain("<PlaybackSpeedControl", host, StringComparison.Ordinal);
        Assert.Contains("PlaybackRateChanged=\"SelectPlaybackRateAsync\"", host, StringComparison.Ordinal);
        // The gear shows its on-state when speed is not 1x or captions are on.
        Assert.Contains("Math.Abs(Playback.PlaybackRate - 1d) > 0.001 ? \"is-on\"", host, StringComparison.Ordinal);
    }

    private IRenderedComponent<VideoSettingsMenu> RenderMenu(double rate, List<double>? rates = null) =>
        Render<VideoSettingsMenu>(parameters => parameters
            .Add(component => component.PlaybackRate, rate)
            .Add(component => component.PlaybackRateChanged, EventCallback.Factory.Create<double>(this, value => rates?.Add(value))));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
