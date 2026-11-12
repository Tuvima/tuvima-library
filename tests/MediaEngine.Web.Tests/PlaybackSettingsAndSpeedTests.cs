using System.Security.Claims;
using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Unified player v2 D14, D15 and acceptance A8: the Speed panel (presets, slider, reset) and the Playback settings panel (skip lengths saved on the
/// profile, Reset playback preferences), against the real components and, for the saved preferences, the real video host and session controller.
/// </summary>
public sealed class PlaybackSettingsAndSpeedTests : AsyncBunitContext
{
    private sealed class Preferences(UserPlaybackSettingsDto settings) : IUserPlaybackPreferencesAccessor
    {
        private UserPlaybackSettingsDto _current = settings;
        public Guid? ActiveProfileId { get; } = settings.ProfileId;
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(_current);
        public void UpdateCache(UserPlaybackSettingsDto next) => _current = next;
        public void Invalidate() { }
    }

    private sealed class ProfileAuthentication(Guid profileId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("tuvima:active_profile_id", profileId.ToString("D"))], "test"))));
    }

    public PlaybackSettingsAndSpeedTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
    }

    // Services are added before the first component renders, so a test that needs the real session controller adds its own coordinator and host.
    private void PrepareStandalone()
    {
        Services.AddSingleton(new PlaybackTransientToolCoordinator());
        Render<AppPopoverHost>();
    }

    private async Task OpenAsync(IRenderedComponent<PlaybackPanel> panel) => await panel.InvokeAsync(() => panel.Instance.OpenAsync(true));

    // ---- The Speed panel (D15, A8) ----

    private async Task<IRenderedComponent<PlaybackSpeedPanel>> OpenSpeedPanelAsync(double rate, List<double>? changes = null)
    {
        PrepareStandalone();
        var cut = Render<PlaybackSpeedPanel>(parameters => parameters
            .Add(component => component.Rate, rate)
            .Add(component => component.TriggerText, PlaybackSpeedChoices.Format(rate))
            .Add(component => component.RateChanged, EventCallback.Factory.Create<double>(this, value => changes?.Add(value))));
        await OpenAsync(cut.FindComponent<PlaybackPanel>());
        cut.WaitForElement("[data-playback-speed-view]");
        return cut;
    }

    private static string[] TickedPresets(IRenderedComponent<PlaybackSpeedPanel> cut) =>
        cut.FindAll("[data-speed-preset][aria-checked='true']").Select(preset => preset.GetAttribute("data-speed-preset")!).ToArray();

    [Theory]
    [InlineData(0.75d, "0.75×")]
    [InlineData(1d, "1×")]
    [InlineData(1.25d, "1.25×")]
    [InlineData(1.5d, "1.5×")]
    [InlineData(2d, "2×")]
    public async Task SpeedPanel_TicksAPresetOnlyWhenTheSpeedEqualsIt(double rate, string expected)
    {
        var cut = await OpenSpeedPanelAsync(rate);

        Assert.Equal([expected], TickedPresets(cut));
        Assert.Equal(expected, cut.Find("[data-speed-value]").TextContent);
        Assert.Equal("Current speed", cut.Find(".psv__caption").TextContent);
    }

    [Theory]
    [InlineData(1.2345d)]
    [InlineData(1.3d)]
    [InlineData(2.8d)]
    [InlineData(0.5d)]
    public async Task SpeedPanel_ACustomSpeedTicksNoPreset(double rate)
    {
        var cut = await OpenSpeedPanelAsync(rate);

        Assert.Empty(TickedPresets(cut));
        Assert.Equal(5, cut.FindAll("[data-speed-preset]").Count);
    }

    [Fact]
    public async Task SpeedPanel_OffersTheFivePresetsInOrderAndASliderFromHalfToTripleSpeedInFiveHundredthsSteps()
    {
        var cut = await OpenSpeedPanelAsync(1d);

        Assert.Equal(["0.75×", "1×", "1.25×", "1.5×", "2×"], cut.FindAll("[data-speed-preset]").Select(preset => preset.GetAttribute("data-speed-preset")));
        var slider = cut.Find("input[type=range]");
        Assert.Equal("0.5", slider.GetAttribute("min"));
        Assert.Equal("3", slider.GetAttribute("max"));
        Assert.Equal("0.05", slider.GetAttribute("step"));
        Assert.Equal(["0.5×", "3×"], cut.FindAll(".psv__range span").Select(label => label.TextContent));
        Assert.Contains("Adjust in steps of 0.05×", cut.Markup);
    }

    [Fact]
    public async Task SpeedPanel_HasNoCustomSpeedRowAnywhere()
    {
        var cut = await OpenSpeedPanelAsync(1.3d);

        Assert.DoesNotContain("Custom speed", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SpeedPanel_ChoosingAPresetAppliesAtOnceAndKeepsThePanelOpen()
    {
        var changes = new List<double>();
        var cut = await OpenSpeedPanelAsync(1d, changes);

        await cut.Find("[data-speed-preset='1.5×']").ClickAsync();

        Assert.Equal([1.5d], changes);
        Assert.NotNull(cut.Find("[data-playback-speed-view]"));
    }

    [Fact]
    public async Task SpeedPanel_MovingTheSliderAppliesAtOnceInFiveHundredthsAndThePresetFollowsOnceTheHostReportsTheSpeed()
    {
        var changes = new List<double>();
        var cut = await OpenSpeedPanelAsync(1d, changes);

        await cut.Find("input[type=range]").InputAsync(new ChangeEventArgs { Value = "1.27" });

        Assert.Equal(1.25d, changes[^1]);
        cut.Render(parameters => parameters.Add(component => component.Rate, 1.25d));
        Assert.Equal(["1.25×"], TickedPresets(cut));
        Assert.Equal("1.25", cut.Find("input[type=range]").GetAttribute("value"));
        Assert.Equal("1.25×", cut.Find("[data-speed-value]").TextContent);
    }

    [Fact]
    public async Task SpeedPanel_APresetAndTheSliderAlwaysAgree()
    {
        var cut = await OpenSpeedPanelAsync(1d);

        foreach (var preset in PlaybackSpeedChoices.Presets)
        {
            cut.Render(parameters => parameters.Add(component => component.Rate, preset));
            Assert.Equal([PlaybackSpeedChoices.Format(preset)], TickedPresets(cut));
            Assert.Equal(preset, double.Parse(cut.Find("input[type=range]").GetAttribute("value")!, System.Globalization.CultureInfo.InvariantCulture));
        }

        cut.Render(parameters => parameters.Add(component => component.Rate, 1.8d));
        Assert.Empty(TickedPresets(cut));
        Assert.Equal("1.8", cut.Find("input[type=range]").GetAttribute("value"));
    }

    [Fact]
    public async Task SpeedPanel_ResetToOneXReportsNormalSpeed()
    {
        var changes = new List<double>();
        var cut = await OpenSpeedPanelAsync(2.4d, changes);

        Assert.Equal("Reset to 1×", cut.Find("[data-speed-reset]").TextContent.Trim());
        await cut.Find("[data-speed-reset]").ClickAsync();

        Assert.Equal([1d], changes);
    }

    [Fact]
    public async Task SpeedPanel_TheAudiobookChipOpensTheSpeedPanelDirectlyWithItsValueInTheName()
    {
        PrepareStandalone();
        var cut = Render<PlaybackSpeedPanel>(parameters => parameters
            .Add(component => component.Rate, 1.25d)
            .Add(component => component.TriggerText, "1.25×"));

        var chip = cut.Find("button.playback-popover-trigger");
        Assert.Equal("1.25×", chip.TextContent.Trim());
        Assert.Equal("Playback speed 1.25×", chip.GetAttribute("aria-label"));
        await OpenAsync(cut.FindComponent<PlaybackPanel>());

        cut.WaitForElement("[data-playback-speed-view]");
        // One step: the speed view itself, not a settings list with a Playback speed row to press first.
        Assert.Empty(cut.FindAll("[data-psp-row]"));
        Assert.Equal("Playback speed", cut.Find(".playback-panel__title").TextContent.Trim());
    }

    // ---- Playback settings (D14) ----

    private IRenderedComponent<PlaybackSettingsPanel> RenderSettings(
        PlaybackBarMedia media, double rate = 1d, int skipBack = 10, int skipForward = 30,
        List<PlaybackSkipChange>? skips = null, List<double>? rates = null, Action? reset = null, bool pictureInPicture = false, PlaybackManifestDto? manifest = null,
        int? audioTrackIndex = null, PlaybackInformationSubtitle? subtitle = null)
    {
        return Render<PlaybackSettingsPanel>(parameters => parameters
            .Add(component => component.AudioTrackIndex, audioTrackIndex)
            .Add(component => component.Subtitle, subtitle)
            .Add(component => component.Media, media)
            .Add(component => component.Rate, rate)
            .Add(component => component.RateChanged, EventCallback.Factory.Create<double>(this, value => rates?.Add(value)))
            .Add(component => component.SkipBackSeconds, skipBack)
            .Add(component => component.SkipForwardSeconds, skipForward)
            .Add(component => component.SkipChanged, EventCallback.Factory.Create<PlaybackSkipChange>(this, change => skips?.Add(change)))
            .Add(component => component.CanPictureInPicture, pictureInPicture)
            .Add(component => component.OnPictureInPicture, EventCallback.Factory.Create(this, () => { }))
            .Add(component => component.Manifest, manifest)
            .Add(component => component.OnReset, EventCallback.Factory.Create(this, () => reset?.Invoke())));
    }

    private async Task<IRenderedComponent<PlaybackSettingsPanel>> OpenSettingsAsync(PlaybackBarMedia media,
        double rate = 1d, int skipBack = 10, int skipForward = 30, List<PlaybackSkipChange>? skips = null, List<double>? rates = null, Action? reset = null,
        bool pictureInPicture = false, PlaybackManifestDto? manifest = null, int? audioTrackIndex = null, PlaybackInformationSubtitle? subtitle = null)
    {
        PrepareStandalone();
        var cut = RenderSettings(media, rate, skipBack, skipForward, skips, rates, reset, pictureInPicture, manifest, audioTrackIndex, subtitle);
        await OpenAsync(cut.FindComponent<PlaybackPanel>());
        cut.WaitForElement("[data-playback-settings]");
        return cut;
    }

    [Fact]
    public async Task PlaybackSettings_ForVideoListsSpeedSkipsAndInformationInTheMockupOrderAndNothingElse()
    {
        var cut = await OpenSettingsAsync(PlaybackBarMedia.Movie, rate: 1.25d);

        Assert.Equal(["speed", "skip-back", "skip-forward", "information"], cut.FindAll("[data-psp-row]").Select(row => row.GetAttribute("data-psp-row")));
        Assert.Equal("1.25×", cut.Find("[data-psp-value='speed']").TextContent.Trim());
        Assert.Equal("Reset playback preferences", cut.Find("[data-psp-reset]").TextContent.Trim());
        Assert.Contains("Video, audio and connection details", cut.Find("[data-psp-row='information']").TextContent);
        Assert.DoesNotContain("Audio & subtitles", cut.Markup);
    }

    [Fact]
    public async Task PlaybackSettings_ShowsNormalSpeedAsOneXNormal()
    {
        var cut = await OpenSettingsAsync(PlaybackBarMedia.Tv, rate: 1d);

        Assert.Equal("1× · Normal", cut.Find("[data-psp-value='speed']").TextContent.Trim());
    }

    [Fact]
    public async Task PlaybackSettings_PictureInPictureRowAppearsForVideoWhenTheBrowserAllowsIt()
    {
        var cut = await OpenSettingsAsync(PlaybackBarMedia.Movie, pictureInPicture: true);

        Assert.Equal(["speed", "skip-back", "skip-forward", "picture-in-picture", "information"], cut.FindAll("[data-psp-row]").Select(row => row.GetAttribute("data-psp-row")));
    }

    [Fact]
    public async Task PlaybackSettings_PictureInPictureRowIsHiddenWhenTheBrowserDoesNotAllowIt()
    {
        var cut = await OpenSettingsAsync(PlaybackBarMedia.Movie, pictureInPicture: false);

        Assert.DoesNotContain("picture-in-picture", cut.FindAll("[data-psp-row]").Select(row => row.GetAttribute("data-psp-row")));
    }

    [Fact]
    public async Task PlaybackSettings_MusicHasNeitherSpeedNorSkipRows()
    {
        var cut = await OpenSettingsAsync(PlaybackBarMedia.Music);

        Assert.Equal(["information"], cut.FindAll("[data-psp-row]").Select(row => row.GetAttribute("data-psp-row")));
    }

    [Fact]
    public async Task PlaybackSettings_SkipBackOffersFiveTenFifteenAndThirtySecondsAndSkipForwardTenThirtySixtyAndNinety()
    {
        var cut = await OpenSettingsAsync(PlaybackBarMedia.Movie);

        Assert.Equal(["5", "10", "15", "30"], cut.FindAll("[data-psp-row='skip-back'] option").Select(option => option.GetAttribute("value")));
        Assert.Equal(["10", "30", "60", "90"], cut.FindAll("[data-psp-row='skip-forward'] option").Select(option => option.GetAttribute("value")));
        Assert.Equal("10 seconds", cut.Find("[data-psp-row='skip-back'] .psp__value").TextContent.Trim());
        Assert.Equal("30 seconds", cut.Find("[data-psp-row='skip-forward'] .psp__value").TextContent.Trim());
    }

    [Fact]
    public async Task PlaybackSettings_ChoosingASkipLengthReportsTheDirectionAndSeconds()
    {
        var skips = new List<PlaybackSkipChange>();
        var cut = await OpenSettingsAsync(PlaybackBarMedia.Movie, skips: skips);

        await cut.Find("[data-psp-row='skip-back'] select").ChangeAsync(new ChangeEventArgs { Value = "15" });
        await cut.Find("[data-psp-row='skip-forward'] select").ChangeAsync(new ChangeEventArgs { Value = "60" });

        Assert.Equal([new PlaybackSkipChange(PlaybackSkipDirection.Back, 15), new PlaybackSkipChange(PlaybackSkipDirection.Forward, 60)], skips);
    }

    [Fact]
    public async Task PlaybackSettings_ThePlaybackSpeedRowOpensTheSpeedViewInsideThePanelAndBackReturns()
    {
        var rates = new List<double>();
        var cut = await OpenSettingsAsync(PlaybackBarMedia.Movie, rates: rates);

        await cut.Find("[data-psp-row='speed']").ClickAsync();

        cut.WaitForElement("[data-playback-speed-view]");
        Assert.Equal("Playback speed", cut.Find(".playback-panel__title").TextContent.Trim());
        await cut.Find("[data-speed-preset='2×']").ClickAsync();
        Assert.Equal([2d], rates);
        await cut.Find(".playback-panel__back").ClickAsync();
        Assert.NotEmpty(cut.FindAll("[data-psp-row='speed']"));
        Assert.Equal("Playback settings", cut.Find(".playback-panel__title").TextContent.Trim());
    }

    [Fact]
    public async Task PlaybackSettings_PlaybackInformationShowsTheFilesTechnicalDetails()
    {
        var manifest = new PlaybackManifestDto
        {
            AssetId = Guid.NewGuid(),
            MediaType = "Movie",
            DirectPlaySupported = true,
            Technical = new PlaybackTechnicalInfoDto { Container = "mkv", VideoCodec = "hevc", AudioCodec = "eac3" },
        };
        var cut = await OpenSettingsAsync(PlaybackBarMedia.Movie, manifest: manifest);

        await cut.Find("[data-psp-row='information']").ClickAsync();

        cut.WaitForElement("[data-playback-information]");
        Assert.Equal("Playback information", cut.Find(".playback-panel__title").TextContent.Trim());
        Assert.Contains("MKV", cut.Find("[data-playback-information]").TextContent);
        Assert.Contains("Playing the file as it is", cut.Find("[data-playback-information]").TextContent);
    }

    // ---- Playback information (video-player follow-ups D13 to D15) ----

    private static PlaybackManifestDto FourKHdrConvertingVideo() => new()
    {
        AssetId = Guid.NewGuid(),
        MediaType = "Movie",
        SourceExtension = ".mkv",
        RecommendedDelivery = PlaybackDeliveryModes.Hls,
        ConversionReason = "This browser cannot play HEVC.",
        SourceBitrateKbps = 41200,
        Connection = new() { ConnectionPath = PlaybackConnectionPaths.Local, EstimatedBandwidthMbps = 85 },
        Technical = new()
        {
            Container = "mkv",
            FileName = "Dune Part One (2021).mkv",
            FileSizeBytes = 4_800_000_000,
            VideoCodec = "hevc",
            VideoProfile = "Main 10",
            Width = 3840,
            Height = 2160,
            FrameRate = 23.976,
            ColorFormat = "HDR10",
            AudioCodec = "eac3",
            SampleRateHz = 48000,
            AudioBitrateKbps = 640,
            Channels = 6,
        },
        AudioTracks =
        [
            new() { Index = 0, Language = "en", Codec = "eac3", Channels = 6, IsDefault = true },
            new() { Index = 1, Language = "fr", Codec = "ac3", Channels = 2 },
        ],
    };

    private static List<string> InfoLines(IRenderedComponent<PlaybackSettingsPanel> cut) => cut.FindAll("[data-info-section]")
        .SelectMany(section => new[] { section.QuerySelector("h3")!.TextContent.Trim() }.Concat(section.QuerySelectorAll("[data-info-row]")
            .Select(row => $"{row.QuerySelector("dt")!.TextContent.Trim()}: {row.QuerySelector("dd")!.TextContent.Trim()}")))
        .ToList();

    private async Task<IRenderedComponent<PlaybackSettingsPanel>> OpenInformationAsync(PlaybackBarMedia media, PlaybackManifestDto? manifest,
        int? audioTrackIndex = null, PlaybackInformationSubtitle? subtitle = null)
    {
        var cut = await OpenSettingsAsync(media, manifest: manifest, audioTrackIndex: audioTrackIndex, subtitle: subtitle);
        await cut.Find("[data-psp-row='information']").ClickAsync();
        cut.WaitForElement("[data-playback-information]");
        return cut;
    }

    [Fact]
    public async Task PlaybackInformation_ForA4kHdrConvertingVideoListsEverySectionWithItsLines()
    {
        var module = JSInterop.SetupModule("./js/playback-information.js");
        module.Setup<string?>("clientName").SetResult("Edge on Windows");
        module.Setup<int?>("bufferAhead", PlaybackRemoteController.VideoElementId).SetResult(12);

        var cut = await OpenInformationAsync(PlaybackBarMedia.Movie, FourKHdrConvertingVideo(), subtitle: new("English", "subdl"));

        cut.WaitForAssertion(() => Assert.Equal(
        [
            "File", "Name: Dune Part One (2021).mkv", "Format: MKV · 4.5 GB", "Overall bitrate: 41,200 kb/s",
            "Video", "Picture: 4K (3840 × 2160) · HEVC Main 10", "Frame rate: 23.976 fps", "Color: HDR10",
            "Audio", "Track: English · EAC3 · 5.1", "Quality: 48 kHz · 640 kb/s",
            "Subtitles", "Showing: English · from SubDL",
            "How it is playing", "Delivery: Converting for this browser", "Why: This browser cannot play HEVC.", "Connection: Home network · 85 Mb/s",
            "Buffer: 12 s ahead", "Playing on: Edge on Windows",
        ], InfoLines(cut)));
    }

    [Fact]
    public async Task PlaybackInformation_TheAudioTrackInUseIsTheOneTheCallerNames()
    {
        var cut = await OpenInformationAsync(PlaybackBarMedia.Movie, FourKHdrConvertingVideo(), audioTrackIndex: 1);

        Assert.Contains("Track: French · AC3 · 2.0", InfoLines(cut));
    }

    [Fact]
    public async Task PlaybackInformation_SubtitlesSayOffWhenNoneIsShowing()
    {
        var cut = await OpenInformationAsync(PlaybackBarMedia.Movie, FourKHdrConvertingVideo(), subtitle: new(null));

        Assert.Contains("Showing: Off", InfoLines(cut));
    }

    [Theory]
    [InlineData(null, "in the file")]
    [InlineData("embedded", "in the file")]
    [InlineData("sidecar", "a file beside it")]
    [InlineData("subdl", "SubDL")]
    [InlineData("user", "your file")]
    public async Task PlaybackInformation_SubtitlesNameWhereTheShowingTrackCameFrom(string? provider, string expected)
    {
        var cut = await OpenInformationAsync(PlaybackBarMedia.Movie, FourKHdrConvertingVideo(), subtitle: new("Spanish", provider));

        Assert.Contains($"Showing: Spanish · from {expected}", InfoLines(cut));
    }

    [Fact]
    public async Task PlaybackInformation_ForMusicListsFileAudioAndHowItIsPlayingOnly()
    {
        var manifest = new PlaybackManifestDto
        {
            AssetId = Guid.NewGuid(),
            MediaType = "Music",
            SourceExtension = ".flac",
            SourceBitrateKbps = 1021,
            Technical = new()
            {
                Container = "flac",
                FileName = "01 Opening.flac",
                FileSizeBytes = 31_000_000,
                AudioCodec = "flac",
                SampleRateHz = 44100,
                BitDepth = 16,
                Channels = 2,
                VideoCodec = "mjpeg",
                Width = 600,
                Height = 600,
                FrameRate = 1,
                ColorFormat = "SDR"
            },
            AudioTracks = [new() { Index = 0, Language = "en", Codec = "flac", Channels = 2, IsDefault = true }],
            Connection = new() { ConnectionPath = PlaybackConnectionPaths.RemoteDirect },
        };

        var cut = await OpenInformationAsync(PlaybackBarMedia.Music, manifest, subtitle: new("English", "subdl"));

        Assert.Equal(["file", "audio", "playing"], cut.FindAll("[data-info-section]").Select(section => section.GetAttribute("data-info-section")));
        Assert.Contains("Format: FLAC · 30 MB", InfoLines(cut));
        Assert.Contains("Quality: 44.1 kHz · 16-bit", InfoLines(cut));
        Assert.Contains("Connection: Remote", InfoLines(cut));
        Assert.DoesNotContain("Picture", cut.Markup);
        Assert.DoesNotContain("Subtitles", cut.Markup);
    }

    [Fact]
    public async Task PlaybackInformation_ALineWithNothingToSayAndASectionWithNoLinesAreLeftOut()
    {
        var manifest = new PlaybackManifestDto
        {
            AssetId = Guid.NewGuid(),
            MediaType = "Movie",
            Technical = new() { Container = "  ", FileName = "   ", VideoCodec = "", ColorFormat = null, AudioCodec = null },
            Connection = new() { ConnectionPath = PlaybackConnectionPaths.Local },
        };

        var cut = await OpenInformationAsync(PlaybackBarMedia.Movie, manifest);

        Assert.Equal(["How it is playing", "Delivery: Playing the file as it is", "Connection: Home network"], InfoLines(cut));
        Assert.Empty(cut.FindAll("[data-info-section='file']"));
        Assert.Empty(cut.FindAll("[data-info-section='video']"));
        Assert.Empty(cut.FindAll("[data-info-section='audio']"));
    }

    [Fact]
    public async Task PlaybackInformation_TheFolderNeverAppearsEvenIfOneArrivesWithTheName()
    {
        var manifest = FourKHdrConvertingVideo() with { Technical = FourKHdrConvertingVideo().Technical with { FileName = @"Z:\Movies\Dune (2021)\Dune.mkv" } };

        var cut = await OpenInformationAsync(PlaybackBarMedia.Movie, manifest);

        Assert.Contains("Name: Dune.mkv", InfoLines(cut));
        Assert.DoesNotContain("Movies", cut.Find("[data-playback-information]").TextContent);
        Assert.DoesNotContain(@"\", cut.Find("[data-playback-information]").TextContent);
    }

    [Fact]
    public async Task PlaybackInformation_CopyDetailsPutsExactlyTheRenderedLinesOnTheClipboardAndSaysCopiedInPlace()
    {
        var module = JSInterop.SetupModule("./js/playback-information.js");
        module.Setup<string?>("clientName").SetResult("Edge on Windows");
        module.Setup<int?>("bufferAhead", PlaybackRemoteController.VideoElementId).SetResult(12);
        var clipboard = JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true);
        clipboard.SetVoidResult();
        var cut = await OpenInformationAsync(PlaybackBarMedia.Movie, FourKHdrConvertingVideo(), subtitle: new("English", "subdl"));
        cut.WaitForAssertion(() => Assert.Contains("Playing on: Edge on Windows", InfoLines(cut)));

        Assert.Equal("Copy details", cut.Find("[data-pi-copy]").TextContent.Trim());
        // The button holds "Copied" for two seconds inside its click handler, so the click is not awaited to the end.
        cut.Find("[data-pi-copy]").Click();

        var copied = Assert.IsType<string>(Assert.Single(clipboard.Invocations).Arguments[0]);
        var shown = cut.FindAll("[data-info-section]").Select(section =>
            string.Join("\n", new[] { section.QuerySelector("h3")!.TextContent.Trim() }.Concat(section.QuerySelectorAll("[data-info-row]")
                .Select(row => $"{row.QuerySelector("dt")!.TextContent.Trim()}: {row.QuerySelector("dd")!.TextContent.Trim()}"))));
        Assert.Equal(string.Join("\n\n", shown), copied);
        Assert.StartsWith("File\nName: Dune Part One (2021).mkv\n", copied);
        cut.WaitForAssertion(() => Assert.Equal("Copied", cut.Find("[data-pi-copy]").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[role='alert'], .app-toast"));
    }

    [Fact]
    public async Task PlaybackInformation_CopyDetailsSaysCouldNotCopyWhenTheBrowserRefuses()
    {
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetException(new JSException("Document is not focused."));
        var cut = await OpenInformationAsync(PlaybackBarMedia.Movie, FourKHdrConvertingVideo());

        cut.Find("[data-pi-copy]").Click();

        cut.WaitForAssertion(() => Assert.Equal("Could not copy", cut.Find("[data-pi-copy]").TextContent.Trim()));
    }

    // D15: the panel showed nothing on the real library. A music track (or audiobook) queued with a direct stream address is started without ever asking the
    // Engine for a manifest, so the session holds none and Playback information had nothing to draw. A video asks only after its picture has opened. The view
    // now asks for the manifest itself while it is open.
    private PlaybackSessionController ArrangePlayingWithoutManifest(PlaybackManifestDto? engineManifest, TaskCompletionSource<PlaybackManifestDto?>? pending = null)
    {
        var asset = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var preferences = new Preferences(UserPlaybackSettingsDto.CreateDefaults(profile));
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetPlaybackManifestAsync),
            _ => pending is null ? Task.FromResult(engineManifest) : pending.Task));
        var playback = new PlaybackSessionController(null!, api, preferences: preferences);
        playback.RestoreState(new()
        {
            Queue = [new() { WorkId = Guid.NewGuid(), AssetId = asset, Title = "Opening", MediaType = "Music", StreamUrl = $"/media/assets/{asset:D}/stream" }],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Music,
        });
        Assert.Null(playback.CurrentManifest);
        Services.AddSingleton(playback);
        PrepareStandalone();
        return playback;
    }

    [Fact]
    public async Task PlaybackInformation_WhenThePlayerHoldsNoManifestForWhatIsPlayingTheViewAsksForItInsteadOfStayingBlank()
    {
        var playback = ArrangePlayingWithoutManifest(new PlaybackManifestDto
        {
            AssetId = Guid.NewGuid(),
            MediaType = "Music",
            Technical = new() { Container = "mp3", FileName = "Opening.mp3", AudioCodec = "mp3" },
        });
        var cut = RenderSettings(PlaybackBarMedia.Music, manifest: playback.CurrentManifest);
        await OpenAsync(cut.FindComponent<PlaybackPanel>());
        cut.WaitForElement("[data-playback-settings]");

        await cut.Find("[data-psp-row='information']").ClickAsync();

        cut.WaitForAssertion(() => Assert.Contains("Name: Opening.mp3", InfoLines(cut)));
        Assert.Empty(cut.FindAll("[data-info-empty]"));
        Assert.Null(playback.CurrentManifest);
    }

    [Fact]
    public async Task PlaybackInformation_SaysLoadingDetailsWhileTheEngineIsAnsweringAndThenShowsTheLines()
    {
        var pending = new TaskCompletionSource<PlaybackManifestDto?>();
        var playback = ArrangePlayingWithoutManifest(null, pending);
        var cut = RenderSettings(PlaybackBarMedia.Music, manifest: playback.CurrentManifest);
        await OpenAsync(cut.FindComponent<PlaybackPanel>());
        cut.WaitForElement("[data-playback-settings]");

        await cut.Find("[data-psp-row='information']").ClickAsync();

        Assert.Equal("Loading details…", cut.Find("[data-info-empty='loading']").TextContent.Trim());
        pending.SetResult(new PlaybackManifestDto { AssetId = Guid.NewGuid(), MediaType = "Music", Technical = new() { Container = "mp3" } });
        cut.WaitForAssertion(() => Assert.Contains("Format: MP3", InfoLines(cut)));
    }

    [Fact]
    public async Task PlaybackInformation_SaysNoDetailsAreAvailableOnlyWhenTheEngineHasNone()
    {
        var playback = ArrangePlayingWithoutManifest(null);
        var cut = RenderSettings(PlaybackBarMedia.Music, manifest: playback.CurrentManifest);
        await OpenAsync(cut.FindComponent<PlaybackPanel>());
        cut.WaitForElement("[data-playback-settings]");

        await cut.Find("[data-psp-row='information']").ClickAsync();

        cut.WaitForAssertion(() => Assert.Equal("No details are available for this file.", cut.Find("[data-info-empty='none']").TextContent.Trim()));
        Assert.True(cut.Find("[data-pi-copy]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task SessionController_LoadCurrentManifestAsksForTheManifestOfWhatIsPlayingAndKeepsTheOneItAlreadyHolds()
    {
        var calls = new List<Guid>();
        var asset = Guid.NewGuid();
        var held = new PlaybackManifestDto { AssetId = asset, MediaType = "Movie" };
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetPlaybackManifestAsync), args =>
        {
            calls.Add((Guid)args![0]!);
            return Task.FromResult<PlaybackManifestDto?>(new PlaybackManifestDto { AssetId = (Guid)args[0]!, MediaType = "Music" });
        }));
        var playback = new PlaybackSessionController(null!, api);
        playback.RestoreState(new() { Queue = [new() { WorkId = Guid.NewGuid(), AssetId = asset, Title = "Opening", MediaType = "Music" }], CurrentIndex = 0 });

        var fetched = await playback.LoadCurrentManifestAsync();

        Assert.Equal(asset, fetched?.AssetId);
        Assert.Equal([asset], calls);
        Assert.Null(playback.CurrentManifest);

        playback.RestoreState(new() { Queue = [new() { WorkId = Guid.NewGuid(), AssetId = asset, Title = "Dune", MediaType = "Movie", Manifest = held }], CurrentIndex = 0 });
        Assert.Same(held, await playback.LoadCurrentManifestAsync());
        Assert.Single(calls);
    }

    [Fact]
    public async Task PlaybackSettings_ResetPlaybackPreferencesTellsTheHost()
    {
        var resets = 0;
        var cut = await OpenSettingsAsync(PlaybackBarMedia.Tv, reset: () => resets++);

        await cut.Find("[data-psp-reset]").ClickAsync();

        Assert.Equal(1, resets);
    }

    [Fact]
    public async Task PlaybackSettings_LeavesAHookForExtraRowsSuchAsPlayOnAnotherDevice()
    {
        PrepareStandalone();
        var cut = Render<PlaybackSettingsPanel>(parameters => parameters
            .Add(component => component.Media, PlaybackBarMedia.Movie)
            .Add(component => component.ExtraRows, (RenderFragment)(builder =>
            {
                builder.OpenElement(0, "div");
                builder.AddAttribute(1, "data-extra-row", "true");
                builder.CloseElement();
            })));
        await OpenAsync(cut.FindComponent<PlaybackPanel>());

        cut.WaitForElement("[data-extra-row]");
    }

    [Fact]
    public async Task PlaybackSettings_WhileSpeedIsPushedInsideThePanelAnUpdatedRateMovesTheTickAndTheSlider()
    {
        var cut = await OpenSettingsAsync(PlaybackBarMedia.Movie, rate: 1d);
        await cut.Find("[data-psp-row='speed']").ClickAsync();
        cut.WaitForElement("[data-playback-speed-view]");
        Assert.Equal(["1×"], cut.FindAll("[data-speed-preset][aria-checked='true']").Select(preset => preset.GetAttribute("data-speed-preset")));

        cut.Render(parameters => parameters.Add(component => component.Rate, 1.5d));

        Assert.Equal(["1.5×"], cut.FindAll("[data-speed-preset][aria-checked='true']").Select(preset => preset.GetAttribute("data-speed-preset")));
        Assert.Equal("1.5", cut.Find("input[type=range]").GetAttribute("value"));
        Assert.Equal("1.5×", cut.Find("[data-speed-value]").TextContent);

        cut.Render(parameters => parameters.Add(component => component.Rate, 1.8d));
        Assert.Empty(cut.FindAll("[data-speed-preset][aria-checked='true']"));
        Assert.Equal("1.8", cut.Find("input[type=range]").GetAttribute("value"));
    }

    [Fact]
    public async Task SpeedPanel_ASpeedTheAppCannotPlayShowsNoNaNAndSaysUnavailable()
    {
        PrepareStandalone();
        var cut = Render<PlaybackSpeedPanel>(parameters => parameters
            .Add(component => component.Rate, double.NaN)
            .Add(component => component.TriggerText, "NaN×"));

        var trigger = cut.Find("button.playback-popover-trigger");
        Assert.Equal("Playback speed unavailable", trigger.GetAttribute("aria-label"));
        Assert.DoesNotContain("NaN", cut.Markup);
        Assert.True(trigger.HasAttribute("disabled"));
        await Task.CompletedTask;
    }

    [Fact]
    public void AudiobookGear_ShowsItsOnStateOnlyWhileASleepTimerRunsNotForASpeedThatIsNotNormal()
    {
        var fast = PlaybackControlCatalog.BuildAudiobookDockToolStrip(new PlaybackControlState(HasChapters: true, PlaybackRate: 1.5d));
        var sleeping = PlaybackControlCatalog.BuildAudiobookDockToolStrip(new PlaybackControlState(HasChapters: true, PlaybackRate: 1d, IsSleepTimerActive: true, SleepTimerValueText: "15:00"));

        Assert.False(fast.Single(control => control.Key == PlaybackControlKey.Settings).IsActive);
        Assert.True(sleeping.Single(control => control.Key == PlaybackControlKey.Settings).IsActive);
    }

    [Fact]
    public void PlaybackSettings_ResetFooterShowsOnlyWhenSpeedOrSkipRowsDo()
    {
        PrepareStandalone();
        var music = Render<PlaybackSettingsPanel>(parameters => parameters.Add(component => component.Media, PlaybackBarMedia.Music));
        var movie = RenderSettings(PlaybackBarMedia.Movie);

        Assert.Null(music.FindComponent<PlaybackPanel>().Instance.Footer);
        Assert.NotNull(movie.FindComponent<PlaybackPanel>().Instance.Footer);
    }

    [Theory]
    [InlineData(1d, "1×")]
    [InlineData(1.25d, "1.25×")]
    [InlineData(2d, "2×")]
    public void SpeedValuesPrintedOnTheBarUseTheTimesSign(double rate, string expected)
    {
        Assert.Equal(expected, MediaEngine.Web.Services.Formatting.DisplayFormat.FormatSpeedControl(rate));
        Assert.Equal(expected, PlaybackRateOptions.FormatLabel(rate));
    }

    // ---- Saved on the profile, through the real video host and session controller ----

    private PlaybackSessionController ArrangeVideo(UserPlaybackSettingsDto settings, List<UserPlaybackSettingsDto> saved)
    {
        var asset = Guid.NewGuid();
        var preferences = new Preferences(settings);
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackManifestAsync), _ => Task.FromResult<PlaybackManifestDto?>(null));
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
                { new(settings.ProfileId, "Viewer", "#000000", "RestrictedProfile", DateTimeOffset.UtcNow) }));
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackSettingsAsync), _ => Task.FromResult<UserPlaybackSettingsDto?>(saved.Count > 0 ? saved[^1] : UserPlaybackSettingsDto.CreateDefaults(settings.ProfileId)));
            stub.SetHandler(nameof(IEngineApiClient.UpdatePlaybackSettingsAsync), args =>
            {
                saved.Add((UserPlaybackSettingsDto)args![1]!);
                return Task.FromResult<UserPlaybackSettingsDto?>((UserPlaybackSettingsDto)args[1]!);
            });
        });
        var playback = new PlaybackSessionController(null!, api, preferences: preferences);
        playback.RestoreState(new()
        {
            Queue = [new() { WorkId = Guid.NewGuid(), AssetId = asset, Title = "The Glass Harbor", MediaType = "Movie", StreamUrl = "/engine-stream/video",
                Manifest = new() { AssetId = asset, MediaType = "Movie", DirectPlaySupported = true, DirectStreamUrl = "/stream/video", DurationSeconds = 7200 } }],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Video,
            IsVideoExpanded = true,
        });
        var profiles = new ActiveProfileSessionService(null!, api, authenticationStateProvider: new ProfileAuthentication(settings.ProfileId));
        var orchestrator = new UIOrchestratorService(api, new UniverseStateContainer(), profiles, new ConfigurationManager(), NullLogger<UIOrchestratorService>.Instance);
        var service = new PlayerPreferenceService(preferences, orchestrator);
        Services.AddSingleton(service);
        Services.AddSingleton(new PlaybackSettingsActions(service, playback));
        Services.AddSingleton(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(new VideoPresentationResolver(api, null!, playback, preferences));
        Services.AddSingleton(new PlaybackTransientToolCoordinator(playback));
        Services.AddSingleton(new UniverseStateContainer());
        Services.AddSingleton<ShellActivityState>();
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(preferences);
        Render<AppPopoverHost>();
        return playback;
    }

    private async Task<IRenderedComponent<VideoPlaybackHost>> OpenVideoGearAsync()
    {
        var cut = Render<VideoPlaybackHost>();
        cut.WaitForElement("button.playback-popover-trigger[aria-label='Playback settings']");
        await OpenAsync(cut.FindComponent<PlaybackSettingsPanel>().FindComponent<PlaybackPanel>());
        cut.WaitForElement("[data-playback-settings]");
        return cut;
    }

    [Fact]
    public async Task VideoGear_ChoosingASkipLengthSavesItOnTheProfileAndTheBarsSkipButtonsTakeItAtOnce()
    {
        var saved = new List<UserPlaybackSettingsDto>();
        var profile = Guid.NewGuid();
        var playback = ArrangeVideo(UserPlaybackSettingsDto.CreateDefaults(profile), saved);
        var cut = await OpenVideoGearAsync();

        await cut.Find("[data-psp-row='skip-back'] select").ChangeAsync(new ChangeEventArgs { Value = "15" });
        await cut.Find("[data-psp-row='skip-forward'] select").ChangeAsync(new ChangeEventArgs { Value = "90" });

        cut.WaitForAssertion(() => Assert.Equal(2, saved.Count));
        Assert.Equal(profile, saved[^1].ProfileId);
        Assert.Equal(15, saved[^1].Watching.SkipBackSeconds);
        Assert.Equal(90, saved[^1].Watching.SkipForwardSeconds);
        Assert.Equal(15, playback.SkipBackSeconds);
        Assert.Equal(90, playback.SkipForwardSeconds);
    }

    [Fact]
    public async Task EpisodesAutoplaySwitch_SavesThePreferenceOnTheProfileAndTheRunningPlayerFollowsItAtOnce()
    {
        var saved = new List<UserPlaybackSettingsDto>();
        var profile = Guid.NewGuid();
        var playback = ArrangeVideo(UserPlaybackSettingsDto.CreateDefaults(profile), saved);
        Assert.True(playback.AutoplayNextEpisode);

        var done = await Services.GetRequiredService<PlaybackSettingsActions>().SetAutoplayNextEpisodeAsync(false);

        Assert.True(done);
        Assert.Equal(profile, Assert.Single(saved).ProfileId);
        Assert.False(saved[0].Watching.AutoplayNextEpisode);
        Assert.False(playback.AutoplayNextEpisode);
    }

    [Fact]
    public async Task VideoGear_ResetPlaybackPreferencesPutsTheSkipLengthsBackToTenAndThirtyAndSpeedToNormal()
    {
        var saved = new List<UserPlaybackSettingsDto>();
        var profile = Guid.NewGuid();
        var settings = UserPlaybackSettingsDto.CreateDefaults(profile);
        var playback = ArrangeVideo(settings, saved);
        var cut = await OpenVideoGearAsync();
        await cut.Find("[data-psp-row='skip-back'] select").ChangeAsync(new ChangeEventArgs { Value = "30" });
        cut.WaitForAssertion(() => Assert.Single(saved));
        Assert.Equal(30, saved[0].Watching.SkipBackSeconds);

        await cut.Find("[data-psp-reset]").ClickAsync();

        cut.WaitForAssertion(() => Assert.Equal(2, saved.Count));
        Assert.Equal(10, saved[^1].Watching.SkipBackSeconds);
        Assert.Equal(30, saved[^1].Watching.SkipForwardSeconds);
        Assert.Equal(10, saved[^1].Listening.SkipBackSeconds);
        Assert.Equal(30, saved[^1].Listening.SkipForwardSeconds);
        Assert.Equal(1d, playback.PlaybackRate);
    }

    [Fact]
    public async Task VideoGear_OpensPlaybackSettingsFromTheBarAndReachesSpeedFromIt()
    {
        ArrangeVideo(UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid()), []);
        var cut = await OpenVideoGearAsync();

        await cut.Find("[data-psp-row='speed']").ClickAsync();

        cut.WaitForElement("[data-playback-speed-view]");
        Assert.DoesNotContain("Custom speed", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Defaults and the skip choices ----

    [Fact]
    public void SkipDefaults_AreTenBackAndThirtyForwardForEveryMedia()
    {
        var defaults = UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid());

        Assert.Equal((10, 30), (defaults.Watching.SkipBackSeconds, defaults.Watching.SkipForwardSeconds));
        Assert.Equal((10, 30), (defaults.Listening.SkipBackSeconds, defaults.Listening.SkipForwardSeconds));
        Assert.Equal((10, 30), (new ListeningSettingsDto().SkipBackSeconds, new ListeningSettingsDto().SkipForwardSeconds));
    }

    [Theory]
    [InlineData(true, PlaybackSkipDirection.Back, new[] { 5, 10, 15, 30 })]
    [InlineData(true, PlaybackSkipDirection.Forward, new[] { 10, 30, 60, 90 })]
    [InlineData(false, PlaybackSkipDirection.Back, new[] { 10, 15, 30 })]
    [InlineData(false, PlaybackSkipDirection.Forward, new[] { 15, 30, 60 })]
    public void SkipChoices_OfferExactlyTheLengthsTheEngineAcceptsForEachMedia(bool video, PlaybackSkipDirection direction, int[] expected)
    {
        Assert.Equal(expected, PlaybackSkipChoices.Options(video, direction));
    }

    [Theory]
    [InlineData(0.75d, true)]
    [InlineData(1.25d, true)]
    [InlineData(1.2500000000000002d, true)]
    [InlineData(1.26d, false)]
    public void SpeedChoices_APresetMatchesOnlyAnEqualSpeed(double rate, bool matchesAPreset)
    {
        Assert.Equal(matchesAPreset, PlaybackSpeedChoices.Presets.Any(preset => PlaybackSpeedChoices.Matches(rate, preset)));
    }

    [Theory]
    [InlineData(1d, "1×")]
    [InlineData(1.25d, "1.25×")]
    [InlineData(1.2345d, "1.23×")]
    [InlineData(0.5d, "0.5×")]
    public void SpeedChoices_WriteTheSpeedTheWayTheMockupDoes(double rate, string expected)
    {
        Assert.Equal(expected, PlaybackSpeedChoices.Format(rate));
    }
}
