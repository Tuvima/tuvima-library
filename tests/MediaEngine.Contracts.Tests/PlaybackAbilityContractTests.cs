using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of measured player abilities and learned limits (playback-path spec D12, D13, D14).</summary>
public sealed class PlaybackAbilityContractTests
{
    [Fact]
    public void CapabilitiesVersionTwo_UsesSnakeCaseNames_ForTheNewFields()
    {
        var capabilities = new ClientCapabilitiesDto
        {
            SchemaVersion = 2,
            VideoCodecs = ["h264", "hevc"],
            HardwareDecodeVideoCodecs = ["hevc"],
            TenBitVideoCodecs = ["hevc"],
            LearnedLimits =
            [
                new LearnedPlaybackLimitDto
                {
                    VideoCodec = "hevc", Container = "mkv", TenBit = true, FailedRung = PlaybackRungs.Direct,
                    Summary = "cannot play HEVC 10-bit MKV as it is", LearnedAt = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero),
                },
            ],
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(capabilities));
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("hevc", root.GetProperty("hardware_decode_video_codecs")[0].GetString());
        Assert.Equal("hevc", root.GetProperty("ten_bit_video_codecs")[0].GetString());
        var limit = root.GetProperty("learned_limits")[0];
        Assert.Equal("hevc", limit.GetProperty("video_codec").GetString());
        Assert.Equal("mkv", limit.GetProperty("container").GetString());
        Assert.True(limit.GetProperty("ten_bit").GetBoolean());
        Assert.Equal("direct", limit.GetProperty("failed_rung").GetString());
        Assert.Equal("cannot play HEVC 10-bit MKV as it is", limit.GetProperty("summary").GetString());
        Assert.True(limit.TryGetProperty("learned_at", out _));
    }

    [Fact]
    public void AVersionOneReport_WithoutTheNewFields_StillReadsAndMeansNothingWasMeasured()
    {
        const string fromAnOlderApp = """
            {"schema_version":1,"containers":["mp4"],"video_codecs":["h264","hevc"],"audio_codecs":["aac"],"subtitle_formats":["vtt"],
             "protocols":["https"],"max_height":2160,"supports_hdr":true}
            """;

        var capabilities = JsonSerializer.Deserialize<ClientCapabilitiesDto>(fromAnOlderApp)!;

        Assert.Equal(1, capabilities.SchemaVersion);
        Assert.Equal(["h264", "hevc"], capabilities.VideoCodecs);
        Assert.Empty(capabilities.HardwareDecodeVideoCodecs);
        Assert.Empty(capabilities.TenBitVideoCodecs);
        Assert.Empty(capabilities.LearnedLimits);
    }

    [Fact]
    public void ABrowserReport_AndAFailureReport_UseSnakeCaseNames()
    {
        var report = new BrowserCapabilitiesReportDto { Browser = "abc123", Capabilities = new ClientCapabilitiesDto { SchemaVersion = 2 } };
        var failure = new PlaybackFailureReportDto { Rung = PlaybackRungs.Repackage, Detail = "The video source failed to load.", Browser = "abc123" };

        using var reportJson = JsonDocument.Parse(JsonSerializer.Serialize(report));
        using var failureJson = JsonDocument.Parse(JsonSerializer.Serialize(failure));

        Assert.Equal("abc123", reportJson.RootElement.GetProperty("browser").GetString());
        Assert.Equal(2, reportJson.RootElement.GetProperty("capabilities").GetProperty("schema_version").GetInt32());
        Assert.Equal("repackage", failureJson.RootElement.GetProperty("rung").GetString());
        Assert.Equal("The video source failed to load.", failureJson.RootElement.GetProperty("detail").GetString());
        Assert.Equal("abc123", failureJson.RootElement.GetProperty("browser").GetString());
    }

    [Fact]
    public void TheManagedDevice_CarriesWhatItReported_AndNothingWhenItNeverDid()
    {
        var device = new ManagedClientDeviceDto { Capabilities = new ClientCapabilitiesDto { SchemaVersion = 2, VideoCodecs = ["h264"] } };

        using var withReport = JsonDocument.Parse(JsonSerializer.Serialize(device));
        var never = JsonSerializer.Deserialize<ManagedClientDeviceDto>("""{"id":"00000000-0000-0000-0000-000000000001"}""")!;

        Assert.Equal("h264", withReport.RootElement.GetProperty("capabilities").GetProperty("video_codecs")[0].GetString());
        Assert.Null(never.Capabilities);
    }
}
