using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.Setup;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Setup;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Playback path WP5 (spec D11, section 4): the graphics hardware card on Settings &gt; Playback &gt; Quality (works, failed, none, Test hardware)
/// and the Graphics hardware row in first-run setup, which say the same words and never block Continue.
/// </summary>
public sealed class GraphicsHardwareCardTests : AsyncBunitContext
{
    private GraphicsHardwareDto? _current;
    private GraphicsHardwareDto? _afterTest;
    private int _tests;

    public GraphicsHardwareCardTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackHardwareAsync), _ => Task.FromResult(_current));
            stub.SetHandler(nameof(IEngineApiClient.TestPlaybackHardwareAsync), _ =>
            {
                _tests++;
                return Task.FromResult(_afterTest);
            });
        }));
    }

    private static GraphicsHardwareDto Works(bool processorOnly = false, DateTimeOffset? testedAt = null) => new()
    {
        Outcome = GraphicsHardwareOutcomes.Works,
        CardName = "NVIDIA GeForce RTX 5080",
        Accelerator = "NVIDIA NVENC",
        ProcessorOnlyChosen = processorOnly,
        TestedAt = testedAt,
    };

    private static GraphicsHardwareDto Failed() => new()
    {
        Outcome = GraphicsHardwareOutcomes.Failed,
        CardName = "NVIDIA GeForce RTX 5080",
        Reason = "The graphics driver is too old for this card or this FFmpeg.",
        Detail = "h264_nvenc: Driver does not support the required nvenc API version.",
    };

    // ---- Settings > Playback > Quality ----

    [Fact]
    public void ACardThatWorks_IsNamedWithEncodingWorksAndATestButton()
    {
        _current = Works();

        var cut = Render<GraphicsHardwareCard>();

        Assert.Equal("NVIDIA GeForce RTX 5080 · encoding works", cut.Find("[data-graphics-hardware-headline]").TextContent.Trim());
        Assert.Contains("Working", cut.Markup);
        Assert.Equal("Test hardware", cut.Find("[data-graphics-hardware-test]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-graphics-hardware-details]"));
    }

    [Fact]
    public void AFailedTest_SaysWhyInPlainWordsWhatLibraryDoesInsteadAndKeepsTheEnginesTextBehindShowDetails()
    {
        _current = Failed();

        var cut = Render<GraphicsHardwareCard>();

        Assert.Equal("NVIDIA GeForce RTX 5080 found", cut.Find("[data-graphics-hardware-headline]").TextContent.Trim());
        var lines = cut.FindAll("[data-graphics-hardware-line]").Select(line => line.TextContent.Trim()).ToArray();
        Assert.Equal("Encoding test failed: the graphics driver is too old for this card or this FFmpeg.", lines[0]);
        Assert.Contains("Library keeps playing on your processor", lines[1]);
        Assert.Contains("Using processor", cut.Markup);
        Assert.Equal("/settings/tools", cut.Find("a").GetAttribute("href"));
        Assert.Equal("Test again", cut.Find("[data-graphics-hardware-test]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-graphics-hardware-detail-text]"));

        cut.Find("[data-graphics-hardware-details]").Click();

        Assert.Contains("Driver does not support the required nvenc API version.", cut.Find("[data-graphics-hardware-detail-text]").TextContent);
        Assert.Contains("Hide details", cut.Find("[data-graphics-hardware-details]").TextContent);
    }

    [Fact]
    public void NoGraphicsEncoding_IsCalmAndSaysLibraryUsesTheProcessor()
    {
        _current = new GraphicsHardwareDto { Outcome = GraphicsHardwareOutcomes.None };

        var cut = Render<GraphicsHardwareCard>();

        Assert.Equal("No graphics encoding found. Library uses the processor.", cut.Find("[data-graphics-hardware-headline]").TextContent.Trim());
        Assert.Contains("Processor", cut.Markup);
        Assert.Empty(cut.FindAll("a"));
        Assert.Empty(cut.FindAll("[data-graphics-hardware-details]"));
    }

    [Fact]
    public void ProcessorOnlyChosen_SaysSoBesideAWorkingCard()
    {
        _current = Works(processorOnly: true);

        var cut = Render<GraphicsHardwareCard>();

        Assert.Contains("set to Processor only", cut.Find("[data-graphics-hardware-line]").TextContent);
    }

    [Fact]
    public void TestHardware_AsksTheEngineAndShowsTheNewAnswer()
    {
        _current = new GraphicsHardwareDto { Outcome = GraphicsHardwareOutcomes.None };
        _afterTest = Works(testedAt: DateTimeOffset.UtcNow);
        var cut = Render<GraphicsHardwareCard>();

        cut.Find("[data-graphics-hardware-test]").Click();

        cut.WaitForAssertion(() => Assert.Equal("NVIDIA GeForce RTX 5080 · encoding works", cut.Find("[data-graphics-hardware-headline]").TextContent.Trim()));
        Assert.Equal(1, _tests);
        Assert.Contains("Tested just now", cut.Markup);
    }

    [Fact]
    public void WhenTheTestCannotRun_TheCardSaysSoAndKeepsTheEarlierAnswer()
    {
        _current = Works();
        _afterTest = null;
        var cut = Render<GraphicsHardwareCard>();

        cut.Find("[data-graphics-hardware-test]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-graphics-hardware-error]")));
        Assert.Contains("encoding works", cut.Find("[data-graphics-hardware-headline]").TextContent);
    }

    [Fact]
    public void WhenTheEngineCannotBeReached_TheCardStillOffersTheTestButton()
    {
        _current = null;

        var cut = Render<GraphicsHardwareCard>();

        Assert.Contains("could not be reached", cut.Find("[data-graphics-hardware-headline]").TextContent);
        Assert.Single(cut.FindAll("[data-graphics-hardware-test]"));
    }

    // ---- First-run setup: the Graphics hardware row ----

    [Fact]
    public void SetupRow_ForACardThatWorks_SaysTheSameWordsAsSettings()
    {
        var cut = RenderSetup(Works());

        Assert.Equal("NVIDIA GeForce RTX 5080 · encoding works", cut.Find("[data-tool-id=graphics-hardware] [data-graphics-hardware-line]").TextContent.Trim());
        Assert.Contains("Working", cut.Find("[data-tool-id=graphics-hardware]").TextContent);
    }

    [Fact]
    public void SetupRow_ForNoGraphicsEncoding_SaysLibraryWillUseTheProcessor()
    {
        var cut = RenderSetup(new GraphicsHardwareDto { Outcome = GraphicsHardwareOutcomes.None });

        Assert.Equal("No graphics encoding found · Library will use the processor", cut.Find("[data-tool-id=graphics-hardware] [data-graphics-hardware-line]").TextContent.Trim());
    }

    [Fact]
    public void SetupRow_ForAFailedTest_SaysWhatFailedAndThatTheProcessorWillBeUsed()
    {
        var cut = RenderSetup(Failed());

        Assert.Equal(
            "Found, but the test failed: the graphics driver is too old for this card or this FFmpeg. Library will use the processor.",
            cut.Find("[data-tool-id=graphics-hardware] [data-graphics-hardware-line]").TextContent.Trim());
        Assert.Contains("Using processor", cut.Find("[data-tool-id=graphics-hardware]").TextContent);
    }

    [Fact]
    public void SetupRow_BeforeFFmpegIsReady_WaitsAndNeverBlocksContinue()
    {
        var cut = RenderSetup(null);

        Assert.Equal("Checked automatically once FFmpeg is ready", cut.Find("[data-tool-id=graphics-hardware] [data-graphics-hardware-line]").TextContent.Trim());
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Contains("Continue", StringComparison.Ordinal) && !button.HasAttribute("disabled"));
    }

    [Fact]
    public void SetupRow_ForAFailedTest_StillOffersContinue()
    {
        var cut = RenderSetup(Failed());

        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Contains("Continue", StringComparison.Ordinal) && !button.HasAttribute("disabled"));
    }

    [Fact]
    public void SetupRule_FFmpegCountsAsReadyOnlyWhenItsRowIsReady()
    {
        Assert.True(SetupToolRules.IsFfmpegReady(Tools(Ffmpeg(ToolRequirementStates.Ready))));
        Assert.False(SetupToolRules.IsFfmpegReady(Tools(Ffmpeg(ToolRequirementStates.Downloading))));
        Assert.False(SetupToolRules.IsFfmpegReady(Tools(Ffmpeg(ToolRequirementStates.Missing))));
        Assert.False(SetupToolRules.IsFfmpegReady(null));
    }

    [Theory]
    [InlineData("The graphics driver is too old.", "the graphics driver is too old.")]
    [InlineData("FFmpeg could not use the graphics card.", "FFmpeg could not use the graphics card.")]
    [InlineData("No NVIDIA card could be opened for encoding.", "no NVIDIA card could be opened for encoding.")]
    [InlineData(null, "")]
    public void TheEnginesReason_ReadsAfterAColonWithoutLoweringNamesOrAcronyms(string? reason, string expected)
    {
        Assert.Equal(expected, GraphicsHardwareText.Lowered(reason));
    }

    private IRenderedComponent<SetupToolsStage> RenderSetup(GraphicsHardwareDto? graphics) =>
        Render<SetupToolsStage>(parameters => parameters
            .Add(p => p.Preflight, new SetupPreflightDto(true, false, string.Empty, "win-x64", "X64", "X64", [],
                Tools(Ffmpeg(graphics is null ? ToolRequirementStates.Downloading : ToolRequirementStates.Ready))))
            .Add(p => p.Graphics, graphics));

    private static ToolRequirementsDto Tools(params ToolRequirementDto[] rows) => new(
        ToolInstallKinds.Source,
        DateTimeOffset.UnixEpoch,
        false,
        rows);

    private static ToolRequirementDto Ffmpeg(string state) =>
        new(ToolRequirementIds.Ffmpeg, "FFmpeg", "tool", ToolRequirementLevels.Required, state,
            "Videos, music and audiobooks wait until FFmpeg is available.", null, string.Empty, null, 0, false, false, false, null);
}
