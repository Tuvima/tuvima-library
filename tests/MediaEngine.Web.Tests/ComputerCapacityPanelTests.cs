using System.Globalization;
using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Review mock-ups WP1 (spec D1): the pinned Computer capacity panel on Settings &gt; Tools. A score gauge out of 100, the graphics hardware
/// line, when the test last ran, three numbers and Run again; nothing on a phone's reduced Settings; failures stay on the panel (no toasts).
/// </summary>
public sealed class ComputerCapacityPanelTests : AsyncBunitContext
{
    private static readonly DateTimeOffset TestedAt = new(2026, 11, 17, 21, 12, 0, TimeSpan.Zero);

    private PlaybackCapacityDto? _stored;
    private Func<Task<PlaybackCapacityDto?>> _onRun = () => Task.FromResult<PlaybackCapacityDto?>(null);
    private int _runs;
    private readonly DeviceContextService _device;

    public ComputerCapacityPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackCapacityAsync), _ => Task.FromResult(_stored));
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackHardwareAsync), _ => Task.FromResult<GraphicsHardwareDto?>(new GraphicsHardwareDto
            {
                Outcome = GraphicsHardwareOutcomes.Works,
                CardName = "NVIDIA GeForce RTX 5080",
                Accelerator = "NVIDIA NVENC",
            }));
            stub.SetHandler(nameof(IEngineApiClient.TestPlaybackCapacityAsync), _ =>
            {
                _runs++;
                return _onRun();
            });
            stub.SetHandler(nameof(IEngineApiClient.GetToolRequirementsAsync), _ => Task.FromResult<ToolRequirementsDto?>(Tools(
                new ToolRequirementDto("ffmpeg", "FFmpeg", "tool", ToolRequirementLevels.Required, ToolRequirementStates.Ready, "Video.", null, string.Empty,
                    null, 0, false, false, false, null))));
        });
        Services.AddSingleton(api);
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton(serviceProvider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(serviceProvider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
        _device = new DeviceContextService(api);
        Services.AddSingleton(_device);
    }

    private static PlaybackCapacityDto Measured(int score = 87, int streams = 4, int fourK = 2, double speed = 12) => new()
    {
        Measured = true,
        Score = score,
        StreamsSupported = streams,
        FourKStreamsSupported = fourK,
        SpeedFactor = speed,
        RanOn = PlaybackEncodePlaces.GraphicsCard,
        CardName = "NVIDIA GeForce RTX 5080",
        TestedAt = TestedAt,
    };

    private static ToolRequirementsDto Tools(params ToolRequirementDto[] rows) =>
        new(ToolInstallKinds.Source, DateTimeOffset.UnixEpoch, false, rows);

    [Fact]
    public void Panel_ShowsTheGauge_TheHardwareLine_TheDate_AndTheThreeNumbers()
    {
        _stored = Measured();

        var cut = Render<ComputerCapacityPanel>();

        cut.WaitForAssertion(() => Assert.Equal("87", cut.Find("[data-testid=capacity-score]").TextContent.Trim()));
        Assert.Equal("87 100", cut.Find("[data-testid=capacity-gauge-fill]").GetAttribute("stroke-dasharray"));
        Assert.Equal("Score 87 out of 100", cut.Find("[data-testid=capacity-gauge]").GetAttribute("aria-label"));
        Assert.Equal("Computer capacity", cut.Find("#computer-capacity-title").TextContent.Trim());
        Assert.Equal("NVIDIA GeForce RTX 5080 · encoding works", cut.Find("[data-testid=capacity-hardware]").TextContent.Trim());
        Assert.Equal(
            $"Last tested {TestedAt.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture)}",
            cut.Find("[data-testid=capacity-tested]").TextContent.Trim());
        Assert.Equal("Video streams at once4", Compact(cut.Find("[data-testid=capacity-streams]").TextContent));
        Assert.Equal("4K conversions at once2", Compact(cut.Find("[data-testid=capacity-four-k]").TextContent));
        Assert.Equal("Faster than real time12×", Compact(cut.Find("[data-testid=capacity-speed]").TextContent));
        Assert.Equal("Run again", cut.Find("[data-testid=capacity-run]").TextContent.Trim());
    }

    [Fact]
    public void Panel_BeforeAnyTest_SaysNotTestedYet_WithDashesAndARunTestButton()
    {
        _stored = new PlaybackCapacityDto { Measured = false };

        var cut = Render<ComputerCapacityPanel>();

        cut.WaitForAssertion(() => Assert.Equal("Not tested yet", cut.Find("[data-testid=capacity-tested]").TextContent.Trim()));
        Assert.Equal("–", cut.Find("[data-testid=capacity-score]").TextContent.Trim());
        Assert.Equal("0 100", cut.Find("[data-testid=capacity-gauge-fill]").GetAttribute("stroke-dasharray"));
        Assert.Equal("Video streams at once–", Compact(cut.Find("[data-testid=capacity-streams]").TextContent));
        Assert.Equal("Run test", cut.Find("[data-testid=capacity-run]").TextContent.Trim());
    }

    [Fact]
    public void RunAgain_ShowsATestingStateWhileItRuns_ThenReplacesTheNumbers()
    {
        _stored = Measured(score: 40, streams: 2, fourK: 0, speed: 3.5);
        var gate = new TaskCompletionSource<PlaybackCapacityDto?>();
        _onRun = () => gate.Task;
        var cut = Render<ComputerCapacityPanel>();
        cut.WaitForAssertion(() => Assert.Equal("40", cut.Find("[data-testid=capacity-score]").TextContent.Trim()));
        Assert.Equal("Faster than real time3.5×", Compact(cut.Find("[data-testid=capacity-speed]").TextContent));

        cut.Find("[data-testid=capacity-run]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=capacity-running]")));
        Assert.Equal(1, _runs);
        Assert.Contains("Testing", cut.Find("[data-testid=capacity-run]").TextContent, StringComparison.Ordinal);

        gate.SetResult(Measured(score: 93, streams: 6, fourK: 3, speed: 14));

        cut.WaitForAssertion(() => Assert.Equal("93", cut.Find("[data-testid=capacity-score]").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-testid=capacity-running]"));
        Assert.Empty(cut.FindAll("[data-testid=capacity-error]"));
        Assert.Equal("Video streams at once6", Compact(cut.Find("[data-testid=capacity-streams]").TextContent));
        Assert.Equal("4K conversions at once3", Compact(cut.Find("[data-testid=capacity-four-k]").TextContent));
        Assert.Equal("Faster than real time14×", Compact(cut.Find("[data-testid=capacity-speed]").TextContent));
    }

    [Fact]
    public void RunAgain_WhenTheEngineCannotBeReached_SaysSoOnThePanel_AndKeepsTheOldNumbers()
    {
        _stored = Measured(score: 55);
        _onRun = () => Task.FromResult<PlaybackCapacityDto?>(null);
        var cut = Render<ComputerCapacityPanel>();
        cut.WaitForAssertion(() => Assert.Equal("55", cut.Find("[data-testid=capacity-score]").TextContent.Trim()));

        cut.Find("[data-testid=capacity-run]").Click();

        cut.WaitForAssertion(() => Assert.Contains("could not run", cut.Find("[data-testid=capacity-error]").TextContent, StringComparison.Ordinal));
        Assert.Equal("55", cut.Find("[data-testid=capacity-score]").TextContent.Trim());
    }

    [Fact]
    public void RunAgain_WhenTheTestCannotMeasure_ShowsTheEnginesReason()
    {
        _stored = Measured();
        _onRun = () => Task.FromResult<PlaybackCapacityDto?>(new PlaybackCapacityDto
        {
            Measured = false,
            Reason = "The video tool is not ready yet, so nothing could be measured.",
        });
        var cut = Render<ComputerCapacityPanel>();
        cut.WaitForAssertion(() => Assert.Equal("87", cut.Find("[data-testid=capacity-score]").TextContent.Trim()));

        cut.Find("[data-testid=capacity-run]").Click();

        cut.WaitForAssertion(() => Assert.Contains("video tool is not ready", cut.Find("[data-testid=capacity-error]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Panel_OnAPhone_DrawsTheSamePanelAsADesktop()
    {
        _stored = Measured();
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", Touch: true));

        var cut = Render<ComputerCapacityPanel>();

        cut.WaitForAssertion(() => Assert.Equal("87", cut.Find("[data-testid=capacity-score]").TextContent.Trim()));
        Assert.NotEmpty(cut.FindAll("[data-testid=computer-capacity]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=capacity-run]"));
    }

    [Fact]
    public void ToolsPage_PinsThePanelAboveTheToolList_AndTheListHoldsOnlyTools()
    {
        _stored = Measured();

        var cut = Render<ToolsRequirementsTab>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=tools-list]")));
        var markup = cut.Markup;
        Assert.True(markup.IndexOf("data-testid=\"computer-capacity\"", StringComparison.Ordinal)
                    < markup.IndexOf("data-testid=\"tools-list\"", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid=tools-list] [data-tool-id=video-capacity]"));
        Assert.Single(cut.FindAll("[data-testid=tools-list] [data-tool-id]"));
    }

    private static string Compact(string text) => string.Concat(text.Where(character => !char.IsWhiteSpace(character)))
        .Replace("Videostreamsatonce", "Video streams at once", StringComparison.Ordinal)
        .Replace("4Kconversionsatonce", "4K conversions at once", StringComparison.Ordinal)
        .Replace("Fasterthanrealtime", "Faster than real time", StringComparison.Ordinal);
}
