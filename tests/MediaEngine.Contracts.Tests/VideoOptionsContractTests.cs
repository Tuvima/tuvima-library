using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;

namespace MediaEngine.Contracts.Tests;

/// <summary>The wire shapes of "Video quality and devices" (playback-path spec D15), the list every client draws.</summary>
public sealed class VideoOptionsContractTests
{
    [Fact]
    public void TheListRow_UsesSnakeCaseNames_SoEveryClientReadsTheSameShape()
    {
        var row = new VideoOptionDto
        {
            Id = VideoOptionIds.VideoQuality,
            Label = "Video quality",
            Kind = VideoOptionKinds.Choice,
            Scope = VideoOptionScopes.Profile,
            Choices = [new VideoOptionChoiceDto { Value = "Auto", Label = "Automatic" }],
            Value = "Auto",
            DefaultValue = "Auto",
            Help = "x",
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new VideoPlaybackOptionsDto { Options = [row], Learned = ["Learned: x"] }));
        var root = document.RootElement;

        Assert.Equal("Video quality and devices", root.GetProperty("title").GetString());
        Assert.Equal("Learned: x", root.GetProperty("learned")[0].GetString());
        var json = root.GetProperty("options")[0];
        Assert.Equal(["choices", "default_value", "help", "id", "kind", "label", "scope", "value"],
            json.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("video_quality", json.GetProperty("id").GetString());
        Assert.Equal("profile", json.GetProperty("scope").GetString());
        Assert.Equal("Automatic", json.GetProperty("choices")[0].GetProperty("label").GetString());
    }

    [Fact]
    public void TheOptionIds_AreTheAgreedStableNames()
    {
        Assert.Equal(
            ["video_quality", "mobile_data_quality", "convert_only_when_needed", "allow_hdr", "max_resolution", "hardware_decoding", "surround_sound", "device_abilities", "check_again"],
            [VideoOptionIds.VideoQuality, VideoOptionIds.MobileDataQuality, VideoOptionIds.ConvertOnlyWhenNeeded, VideoOptionIds.AllowHdr,
             VideoOptionIds.MaxResolution, VideoOptionIds.HardwareDecoding, VideoOptionIds.SurroundSound, VideoOptionIds.DeviceAbilities, VideoOptionIds.CheckAgain]);
    }

    [Fact]
    public void ADevicesOptions_DefaultToAutomatic_AndRideInTheCapabilitiesRecord()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new ClientCapabilitiesDto()));

        var options = document.RootElement.GetProperty("video_options");
        Assert.Equal("auto", options.GetProperty("max_resolution").GetString());
        Assert.Equal("auto", options.GetProperty("hardware_decoding").GetString());
        Assert.Equal("auto", options.GetProperty("surround_sound").GetString());
    }

    [Fact]
    public void AnOlderReportWithoutVideoOptions_StillReads_AndEverythingIsAutomatic()
    {
        var older = JsonSerializer.Deserialize<ClientCapabilitiesDto>("""{"schema_version":2,"video_codecs":["h264"]}""");

        Assert.Equal("auto", older!.VideoOptions.MaxResolution);
        Assert.Equal("auto", older.VideoOptions.SurroundSound);
    }

    [Fact]
    public void ASaveRequest_NamesTheRowTheValueAndTheBrowser()
    {
        var request = JsonSerializer.Deserialize<SetVideoOptionRequest>("""{"id":"max_resolution","value":"720","browser":"abc"}""");

        Assert.Equal(("max_resolution", "720", "abc"), (request!.Id, request.Value, request.Browser));
    }

    [Fact]
    public void ThePersonsFourChoices_DefaultToAutomaticAndOn()
    {
        var watching = new WatchingSettingsDto();

        Assert.Equal(PlaybackPreferenceValues.Auto, watching.PreferredVideoQuality);
        Assert.Equal(PlaybackPreferenceValues.Auto, watching.MobileDataVideoQuality);
        Assert.True(watching.ConvertOnlyWhenNeeded);
        Assert.True(watching.AllowHdr);
    }
}
