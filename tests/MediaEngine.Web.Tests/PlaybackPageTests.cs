using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>Playback has two tabs, Quality and Advanced, both opened by address.</summary>
public sealed class PlaybackPageTests : AsyncBunitContext
{
    private TranscodingSettings? _saved;

    public PlaybackPageTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTranscodingSettingsAsync),
                _ => Task.FromResult<TranscodingSettings?>(new TranscodingSettings { MaintenanceWindow = "01:30-05:45" }));
            stub.SetHandler(nameof(IEngineApiClient.SaveTranscodingSettingsAsync), args =>
            {
                _saved = (TranscodingSettings)args![0]!;
                return Task.FromResult<TranscodingSettings?>(_saved);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackDiagnosticsAsync),
                _ => Task.FromResult<PlaybackDiagnosticsDto?>(new PlaybackDiagnosticsDto { FFmpegAvailable = true, FFmpegVersion = "7.1" }));
            stub.SetHandler(nameof(IEngineApiClient.GetEncodeJobsAsync), _ => Task.FromResult(new List<EncodeJobDto>()));
            stub.SetHandler(nameof(IEngineApiClient.GetNetworkSettingsAsync),
                _ => Task.FromResult<MediaEngine.Contracts.Settings.NetworkSettingsDto?>(new MediaEngine.Contracts.Settings.NetworkSettingsDto()));
        });
        Services.AddSingleton(api);
    }

    [Fact]
    public void QualityOpensByAddress_WithoutTheDisabledStreamAllocationSelect()
    {
        var cut = Render<PlaybackDeliverySettingsTab>(p => p.Add(c => c.Subsection, string.Empty));

        cut.WaitForAssertion(() => Assert.Contains("shares your internet connection", cut.Markup));
        Assert.DoesNotContain("Remote stream allocation", cut.Markup);
        Assert.Equal("limits", cut.Find("[data-delivery-section]").GetAttribute("data-delivery-section"));
    }

    [Fact]
    public void AdvancedOpensByAddress_AndShowsFfmpegOnceWithALinkToTools()
    {
        var cut = Render<PlaybackDeliverySettingsTab>(p => p.Add(c => c.Subsection, "advanced"));

        cut.WaitForAssertion(() => Assert.Contains("7.1", cut.Markup));
        Assert.Equal("advanced", cut.Find("[data-delivery-section]").GetAttribute("data-delivery-section"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(cut.Markup, "7\\.1"));
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "/settings/tools");
    }

    [Fact]
    public void MaintenanceWindowIsTwoTimePickers_ThatSaveTheSameStoredValue()
    {
        var cut = Render<EncodeSettingsTab>(p => p.Add(c => c.Section, "optimized-copies"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("input[type='time']").Count));

        var pickers = cut.FindAll("input[type='time']");
        Assert.Equal("01:30", pickers[0].GetAttribute("value"));
        Assert.Equal("05:45", pickers[1].GetAttribute("value"));

        pickers[0].Input("02:00");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Save optimized copy settings")).Click();

        cut.WaitForAssertion(() => Assert.NotNull(_saved));
        Assert.Equal("02:00-05:45", _saved!.MaintenanceWindow);
    }

    [Theory]
    [InlineData("01:00-05:00", "01:00", "05:00")]
    [InlineData(" 22:15 - 03:30 ", "22:15", "03:30")]
    public void MaintenanceWindowSplitsAndJoinsTheStoredString(string stored, string start, string end)
    {
        Assert.Equal((start, end), EncodeSettingsTab.SplitMaintenanceWindow(stored));
        Assert.Equal($"{start}-{end}", EncodeSettingsTab.JoinMaintenanceWindow(start, end, stored));
    }

    [Fact]
    public void MaintenanceWindowKeepsTheCurrentValueWhenAPickerIsCleared() =>
        Assert.Equal("01:00-05:00", EncodeSettingsTab.JoinMaintenanceWindow("", "05:00", "01:00-05:00"));
}
