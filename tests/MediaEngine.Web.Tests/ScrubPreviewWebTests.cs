using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Unified player v2 D9a: the seek rail's scrub preview tile and the Settings switch for generating the previews.</summary>
public sealed class ScrubPreviewWebTests : AsyncBunitContext
{
    private static readonly Guid Asset = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly ScrubPreviewIndexDto Index = new()
    {
        IntervalSeconds = 10,
        TileWidth = 320,
        TileHeight = 180,
        Columns = 10,
        Rows = 10,
        FrameCount = 215,
        DurationSeconds = 2150,
        Sheets = ["sheet-000.jpg", "sheet-001.jpg", "sheet-002.jpg"],
    };

    private ScrubPreviewStatusDto _status = new() { Enabled = true, BytesUsed = 4_404_019, FfmpegAvailable = true };
    private readonly List<bool> _switchCalls = [];
    private bool _saveFails;

    public ScrubPreviewWebTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(new PlaybackTransientToolCoordinator());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetScrubPreviewStatusAsync), _ => Task.FromResult<ScrubPreviewStatusDto?>(_status));
            stub.SetHandler(nameof(IEngineApiClient.SetScrubPreviewsEnabledAsync), args =>
            {
                _switchCalls.Add((bool)args![0]!);
                if (_saveFails)
                {
                    return Task.FromResult<ScrubPreviewStatusDto?>(null);
                }

                _status = _status with { Enabled = (bool)args[0]!, BytesUsed = (bool)args[0]! ? _status.BytesUsed : 0 };
                return Task.FromResult<ScrubPreviewStatusDto?>(_status);
            });
        }));
        Render<AppPopoverHost>();
    }

    [Fact]
    public void SeekRailOfAVideoWithAPreviewIndexCarriesTheTileAndTheIndex()
    {
        var address = PlaybackSeekRail.PreviewIndexAddress($"/stream/{Asset}/scrub/index.json");

        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 2150).Add(c => c.Surface, "video")
            .Add(c => c.PreviewIndexUrl, address).Add(c => c.Preview, Index));

        var rail = cut.Find("[data-playback-seek-rail]");
        Assert.Equal($"/engine-scrub/{Asset}/index.json", rail.GetAttribute("data-scrub-index-url"));
        Assert.Contains("\"intervalSeconds\":10", rail.GetAttribute("data-scrub-index"));
        Assert.Contains("\"sheets\":[\"sheet-000.jpg\"", rail.GetAttribute("data-scrub-index"));
        Assert.NotNull(cut.Find("[data-scrub-tile]"));
    }

    [Fact]
    public void SeekRailReadsTheIndexItselfWhenThePageSuppliesOnlyTheAddress()
    {
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 2150).Add(c => c.PreviewIndexUrl, $"/engine-scrub/{Asset}/index.json"));

        var rail = cut.Find("[data-playback-seek-rail]");
        Assert.Equal($"/engine-scrub/{Asset}/index.json", rail.GetAttribute("data-scrub-index-url"));
        Assert.Null(rail.GetAttribute("data-scrub-index"));
        Assert.NotNull(cut.Find("[data-scrub-tile]"));
    }

    [Fact]
    public void SeekRailOfAVideoWithoutAPreviewIndexShowsOnlyTheTimestamp()
    {
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 2150).Add(c => c.Surface, "video"));

        Assert.Empty(cut.FindAll("[data-scrub-tile]"));
        Assert.Null(cut.Find("[data-playback-seek-rail]").GetAttribute("data-scrub-index-url"));
        Assert.NotNull(cut.Find("[data-seek-time]"));
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("dock")]
    [InlineData("phone")]
    public void SeekRailOfAudioOrASmallSurfaceNeverShowsAPreviewTile(string surface)
    {
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 2150).Add(c => c.Surface, surface)
            .Add(c => c.PreviewIndexUrl, $"/engine-scrub/{Asset}/index.json").Add(c => c.Preview, Index));

        Assert.Empty(cut.FindAll("[data-scrub-tile]"));
        Assert.Null(cut.Find("[data-playback-seek-rail]").GetAttribute("data-scrub-index-url"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/stream/not-a-guid/scrub/index.json")]
    [InlineData("/stream/11111111-1111-1111-1111-111111111111/scrub/sheet-000.jpg")]
    [InlineData("/somewhere/else.json")]
    public void PreviewIndexAddressOfAnythingButTheEngineIndexIsNone(string? engineUrl)
    {
        Assert.Null(PlaybackSeekRail.PreviewIndexAddress(engineUrl));
    }

    [Fact]
    public void SettingsCardShowsTheSwitchOnWithTheSpaceUsed()
    {
        var cut = Render<ScrubPreviewSettingsCard>();

        cut.WaitForAssertion(() => Assert.Contains("Generate scrub previews", cut.Markup));
        Assert.Contains("Uses 4.2 MB", cut.Markup);
        Assert.True(cut.Find("[role=switch]").HasAttribute("checked"));
    }

    [Fact]
    public void SettingsCardSaysWhenFfmpegIsMissingSoNothingCanBeMade()
    {
        _status = _status with { FfmpegAvailable = false, BytesUsed = 0 };

        var cut = Render<ScrubPreviewSettingsCard>();

        cut.WaitForAssertion(() => Assert.Contains("FFmpeg is not available", cut.Markup));
    }

    [Fact]
    public async Task SettingsCardTurnsPreviewsOffAndShowsTheSpaceFreed()
    {
        var cut = Render<ScrubPreviewSettingsCard>();
        cut.WaitForAssertion(() => Assert.Contains("Uses 4.2 MB", cut.Markup));

        await cut.Find("[role=switch]").ChangeAsync(new ChangeEventArgs { Value = false });

        cut.WaitForAssertion(() => Assert.Contains("Uses 0 B", cut.Markup));
        Assert.Equal([false], _switchCalls);
        Assert.False(cut.Find("[role=switch]").HasAttribute("checked"));
    }

    [Fact]
    public async Task SettingsCardKeepsTheSwitchWhereItWasWhenTheEngineRefusesTheChange()
    {
        _saveFails = true;
        var cut = Render<ScrubPreviewSettingsCard>();
        cut.WaitForAssertion(() => Assert.Contains("Uses 4.2 MB", cut.Markup));

        await cut.Find("[role=switch]").ChangeAsync(new ChangeEventArgs { Value = false });

        cut.WaitForAssertion(() => Assert.Equal([false], _switchCalls));
        Assert.True(cut.Find("[role=switch]").HasAttribute("checked"));
        Assert.Contains("Uses 4.2 MB", cut.Markup);
    }

    [Fact]
    public void SettingsCardSaysSoWhenTheEngineDoesNotAnswer()
    {
        _status = null!;

        var cut = Render<ScrubPreviewSettingsCard>();

        cut.WaitForAssertion(() => Assert.Contains("Scrub preview settings are unavailable", cut.Markup));
        Assert.Empty(cut.FindAll("[role=switch]"));
    }
}
