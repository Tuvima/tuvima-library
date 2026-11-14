using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>What the Dashboard's browser measures about itself and how the Devices page words it (playback-path spec D12, D14).</summary>
public sealed class DeviceAbilitySummaryTests
{
    [Fact]
    public void AllCodecsInHardware_ReadsLikeTheMockup()
    {
        var line = DeviceAbilitySummary.Describe(new ClientCapabilitiesDto
        {
            SchemaVersion = 2,
            VideoCodecs = ["h264", "hevc", "av1"],
            HardwareDecodeVideoCodecs = ["h264", "hevc", "av1"],
            SupportsHdr = true,
            MaxHeight = 2160,
        });

        Assert.Equal("Plays H.264, HEVC and AV1 in hardware · HDR · up to 4K", line);
    }

    [Fact]
    public void SomeCodecsInHardware_NamesWhichOnes()
    {
        var line = DeviceAbilitySummary.Describe(new ClientCapabilitiesDto
        {
            SchemaVersion = 2,
            VideoCodecs = ["h264", "vp9"],
            HardwareDecodeVideoCodecs = ["h264"],
            MaxHeight = 1080,
        });

        Assert.Equal("Plays H.264 and VP9 · H.264 in hardware · up to 1080p", line);
    }

    [Fact]
    public void AFixedVersionOneList_ReadsWithoutClaimingHardware()
    {
        var line = DeviceAbilitySummary.Describe(new ClientCapabilitiesDto { VideoCodecs = ["h264", "hevc"] });

        Assert.Equal("Plays H.264 and HEVC", line);
    }

    [Fact]
    public void ADeviceThatNeverReported_HasNoLine()
    {
        Assert.Null(DeviceAbilitySummary.Describe(null));
        Assert.Null(DeviceAbilitySummary.Describe(new ClientCapabilitiesDto()));
    }
}

public sealed class BrowserPlaybackCapabilityReporterTests : BunitContext
{
    private static ClientCapabilitiesDto Measured() => new() { SchemaVersion = 2, VideoCodecs = ["h264"], Containers = ["mp4", "mkv"] };

    private (BrowserPlaybackCapabilityReporter Reporter, List<ClientCapabilitiesDto> Sent, EngineApiClientStub Stub) Create(
        bool accepted = true, ClientCapabilitiesDto? measured = null)
    {
        var module = JSInterop.SetupModule("./js/playback-capabilities.js");
        module.Setup<ClientCapabilitiesDto?>("measure").SetResult(measured);
        var sent = new List<ClientCapabilitiesDto>();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.ReportBrowserPlaybackCapabilitiesAsync), args =>
            {
                sent.Add((ClientCapabilitiesDto)args![0]!);
                return Task.FromResult(accepted);
            });
            stub.SetHandler(nameof(IEngineApiClient.ReportPlaybackFailureAsync), _ => Task.FromResult(true));
        });
        var reporter = new BrowserPlaybackCapabilityReporter(
            Services.GetRequiredService<IJSRuntime>(), api, NullLogger<BrowserPlaybackCapabilityReporter>.Instance);
        return (reporter, sent, (EngineApiClientStub)(object)api);
    }

    [Fact]
    public async Task TheBrowserMeasuresOnce_AndReportsOncePerPerson()
    {
        var (reporter, sent, _) = Create(measured: Measured());
        var anna = Guid.NewGuid();
        var mary = Guid.NewGuid();

        await reporter.EnsureReportedAsync(anna);
        await reporter.EnsureReportedAsync(anna);
        await reporter.EnsureReportedAsync(mary);

        Assert.Equal(2, sent.Count);
        Assert.Contains("mkv", sent[0].Containers);
        Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "measure");
    }

    [Fact]
    public async Task ABrowserThatCannotMeasure_SendsNothing_AndKeepsTheEnginesFixedList()
    {
        var (reporter, sent, _) = Create(measured: null);

        await reporter.EnsureReportedAsync(Guid.NewGuid());

        Assert.Empty(sent);
        Assert.Null(reporter.Report);
    }

    [Fact]
    public async Task AnEngineThatRefusesTheReport_IsTriedAgainNextTime()
    {
        var (reporter, sent, _) = Create(accepted: false, measured: Measured());
        var anna = Guid.NewGuid();

        await reporter.EnsureReportedAsync(anna);
        await reporter.EnsureReportedAsync(anna);

        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task AFailedStart_IsReportedWithTheRungTheManifestChose()
    {
        var (reporter, _, stub) = Create(measured: Measured());
        var asset = Guid.NewGuid();
        var manifest = new PlaybackManifestDto
        {
            AssetId = asset,
            PlaybackPath = new PlaybackPathDto { Rung = PlaybackRungs.Direct },
        };

        Assert.True(await reporter.ReportFailureAsync(manifest, "The video source failed to load."));
        Assert.False(await reporter.ReportFailureAsync(new PlaybackManifestDto { AssetId = asset }, null));

        Assert.Single(stub.Calls, call => call == nameof(IEngineApiClient.ReportPlaybackFailureAsync));
    }
}
