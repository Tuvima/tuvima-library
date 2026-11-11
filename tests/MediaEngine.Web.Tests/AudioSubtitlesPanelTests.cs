using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Unified player v2 D12 and acceptance A9: the one Audio &amp; subtitles panel (mockup screen 3 and the phone sheet of screen 15), its footer, and the
/// rows it builds from a file's track data.
/// </summary>
public sealed class AudioSubtitlesPanelTests : AsyncBunitContext
{
    private readonly PlaybackTransientToolCoordinator _tools = new();
    private readonly List<TrackSwitchRequest> _switches = [];

    private IStringLocalizer<SharedStrings> L => Services.GetRequiredService<IStringLocalizer<SharedStrings>>();

    public AudioSubtitlesPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton(_tools);
        Render<AppPopoverHost>();
    }

    private static readonly IReadOnlyList<PlaybackTrackDto> FileAudio =
    [
        new() { Index = 0, Language = "eng", Codec = "eac3", Channels = 8, DisplayName = "English (Atmos)", IsDefault = true },
        new() { Index = 1, Language = "eng", Codec = "aac", Channels = 2, DisplayName = "Director commentary" },
        new() { Index = 2, Language = "eng", Codec = "aac", Channels = 2, DisplayName = "English Audio description" },
        new() { Index = 3, Language = "spa", Codec = "ac3", Channels = 6, DisplayName = "Spanish" },
        new() { Index = 4, Language = "eng", Codec = "aac", Channels = 2, DisplayName = "English" },
    ];

    private static readonly IReadOnlyList<SubtitleChoice> FileSubtitles =
    [
        new(null, "Off", false),
        new("manifest:0", "English", true),
        new("manifest:1", "English · SDH", false),
        new("manifest:2", "Spanish", false),
        new("manifest:3", "English · Forced", false),
    ];

    private async Task<IRenderedComponent<AudioSubtitlesPanel>> OpenAsync(
        IReadOnlyList<AudioChoice>? audio = null,
        IReadOnlyList<SubtitleChoice>? subtitles = null,
        double position = 1694.4,
        bool phone = false,
        Action<ComponentParameterCollectionBuilder<AudioSubtitlesPanel>>? configure = null)
    {
        var cut = Render<AudioSubtitlesPanel>(parameters =>
        {
            parameters.Add(p => p.Audio, audio ?? AudioSubtitlesChoices.BuildAudio(L, FileAudio, null, canSwitch: true))
                .Add(p => p.Subtitles, subtitles ?? FileSubtitles)
                .Add(p => p.Position, position)
                .Add(p => p.OnSwitch, EventCallback.Factory.Create<TrackSwitchRequest>(this, request => _switches.Add(request)));
            configure?.Invoke(parameters);
        });
        var panel = cut.FindComponent<PlaybackPanel>();
        if (phone)
        {
            await cut.InvokeAsync(() => panel.Instance.SetViewport(true));
        }

        await cut.InvokeAsync(() => panel.Instance.OpenAsync(true));
        cut.WaitForElement("[data-audio-subtitles]");
        return cut;
    }

    [Fact]
    public async Task OnADesktop_TheWidePanelShowsAudioAndSubtitlesAsTwoColumnsUnderOneHeader()
    {
        var cut = await OpenAsync();

        var panel = cut.Find("[data-playback-popover-panel]");
        Assert.Contains("playback-panel--wide", panel.ClassList);
        Assert.Equal("Audio & subtitles", cut.Find("header h2").TextContent);
        Assert.Equal("columns", cut.Find("[data-audio-subtitles]").GetAttribute("data-layout"));
        Assert.Equal(["audio", "subtitles"], cut.FindAll("[data-asv-column]").Select(column => column.GetAttribute("data-asv-column")));
        Assert.Equal(["Audio", "Subtitles"], cut.FindAll(".asv__heading").Select(heading => heading.TextContent));
        Assert.Empty(cut.FindAll("[role='tab']"));
    }

    [Fact]
    public async Task TheTriggerIsOneButtonNamedAudioAndSubtitles()
    {
        var cut = await OpenAsync();

        var trigger = cut.Find("button.playback-popover-trigger");
        Assert.Equal("Audio & subtitles", trigger.GetAttribute("aria-label"));
        Assert.Equal("AudioSubtitles", trigger.QuerySelector("[data-playback-glyph]")!.GetAttribute("data-playback-glyph"));
    }

    [Fact]
    public void AudioRows_NameTheLanguageThenTheFormatAndChannels()
    {
        var rows = AudioSubtitlesChoices.BuildAudio(L, FileAudio, null, canSwitch: true);

        var atmos = rows.Single(row => row.Index == 0);
        Assert.Equal("English", atmos.Label);
        Assert.Equal("Dolby Atmos · 7.1", atmos.Detail);
        Assert.Equal("Dolby Digital · 5.1", rows.Single(row => row.Index == 3).Detail);
        Assert.Equal("Stereo · 2.0", rows.Single(row => row.Index == 4).Detail);
    }

    [Fact]
    public async Task CommentaryAndAudioDescription_AreLabelledAndListedAfterTheOrdinaryTracks()
    {
        var cut = await OpenAsync();

        var order = cut.FindAll("[data-asv-audio]").Select(row => row.GetAttribute("data-asv-audio")!).ToArray();
        Assert.Equal(["0", "3", "4", "2", "1"], order);
        var rows = cut.FindAll("[data-asv-audio]").Select(row => row.QuerySelector(".asv__label")!.TextContent).ToArray();
        Assert.Equal(["English", "Spanish", "English", "English · Audio description", "Director commentary"], rows);
        Assert.Equal("commentary", cut.Find("[data-asv-audio='1']").GetAttribute("data-asv-purpose"));
        Assert.Equal("audiodescription", cut.Find("[data-asv-audio='2']").GetAttribute("data-asv-purpose"));
        Assert.Contains("English · Stereo · 2.0", cut.Find("[data-asv-audio='1'] .asv__sub").TextContent);
    }

    [Fact]
    public async Task Subtitles_StartWithOffAndKeepEachTracksLabel()
    {
        var cut = await OpenAsync();

        var rows = cut.FindAll("[data-asv-subtitle]");
        Assert.Equal("off", rows[0].GetAttribute("data-asv-subtitle"));
        Assert.Equal(["Off", "English", "English · SDH", "Spanish", "English · Forced"], rows.Select(row => row.QuerySelector(".asv__label")!.TextContent));
    }

    [Fact]
    public void SubtitleLabels_KeepSdhAndForcedAndFallBackToThePlayersOwnName()
    {
        Assert.Equal("English · SDH", AudioSubtitlesChoices.SubtitleLabel(L, "eng", null, isForced: false, isHearingImpaired: true));
        Assert.Equal("French · Forced", AudioSubtitlesChoices.SubtitleLabel(L, "fre", null, isForced: true, isHearingImpaired: false));
        Assert.Equal("Commentary subs", AudioSubtitlesChoices.SubtitleLabel(L, "und", "Commentary subs", isForced: false, isHearingImpaired: false));
        Assert.True(AudioSubtitlesChoices.NamesHearingImpaired("English (SDH)"));
        Assert.False(AudioSubtitlesChoices.NamesHearingImpaired("English"));
    }

    [Fact]
    public async Task TheChosenRowInEachColumnHasACheckAndTheSoftPurpleBackground()
    {
        var cut = await OpenAsync();

        var audio = cut.Find("[data-asv-audio='0']");
        Assert.Contains("is-selected", audio.ClassList);
        Assert.Equal("true", audio.GetAttribute("aria-checked"));
        Assert.NotNull(audio.QuerySelector(".asv__check [data-playback-glyph='Check']"));
        var subtitle = cut.Find("[data-asv-subtitle='manifest:0']");
        Assert.Contains("is-selected", subtitle.ClassList);
        Assert.NotNull(subtitle.QuerySelector(".asv__check"));
        Assert.Single(cut.FindAll("[data-asv-column='audio'] .asv__check"));
        Assert.Single(cut.FindAll("[data-asv-column='subtitles'] .asv__check"));
        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Watch/AudioSubtitlesChoicesView.razor.css"));
        Assert.Contains("background: var(--playback-state-selected)", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChoosingASubtitle_AsksTheHostToSwitchAndCarriesTheCurrentPosition()
    {
        var cut = await OpenAsync(position: 1694.4);

        await cut.Find("[data-asv-subtitle='manifest:1']").ClickAsync();

        var request = Assert.Single(_switches);
        Assert.Equal(TrackSwitchKind.Subtitles, request.Kind);
        Assert.Equal("manifest:1", request.CaptionKey);
        Assert.InRange(request.ResumeSeconds, 1693.4, 1695.4);
    }

    [Fact]
    public async Task ChoosingOff_AsksTheHostForNoSubtitleTrack()
    {
        var cut = await OpenAsync();

        await cut.Find("[data-asv-subtitle='off']").ClickAsync();

        var request = Assert.Single(_switches);
        Assert.Equal(TrackSwitchKind.Subtitles, request.Kind);
        Assert.Null(request.CaptionKey);
    }

    [Fact]
    public async Task ChoosingAnAudioTrack_AsksTheHostToSwitchAndKeepsThePositionWithinOneSecond()
    {
        var cut = await OpenAsync(position: 322.75);

        await cut.Find("[data-asv-audio='3']").ClickAsync();

        var request = Assert.Single(_switches);
        Assert.Equal(TrackSwitchKind.Audio, request.Kind);
        Assert.Equal(3, request.AudioIndex);
        Assert.True(Math.Abs(request.ResumeSeconds - 322.75) <= 1d, "A9: the resume position is the current time, within one second.");
    }

    [Fact]
    public async Task AudioAndSubtitles_AreChosenIndependently()
    {
        var cut = await OpenAsync();

        await cut.Find("[data-asv-audio='3']").ClickAsync();
        await cut.Find("[data-asv-subtitle='manifest:3']").ClickAsync();

        Assert.Equal([TrackSwitchKind.Audio, TrackSwitchKind.Subtitles], _switches.Select(request => request.Kind));
        Assert.Null(_switches[0].CaptionKey);
        Assert.Equal(0, _switches[1].AudioIndex);
    }

    [Fact]
    public async Task ChoosingTheRowAlreadyChosen_AsksForNothing()
    {
        var cut = await OpenAsync();

        await cut.Find("[data-asv-audio='0']").ClickAsync();
        await cut.Find("[data-asv-subtitle='manifest:0']").ClickAsync();

        Assert.Empty(_switches);
    }

    [Fact]
    public async Task ATrackTheAppCannotSwitch_IsListedNotSelectableWithTheBrowserHelper()
    {
        var audio = AudioSubtitlesChoices.BuildAudio(L, FileAudio, null, canSwitch: false);
        var cut = await OpenAsync(audio);

        var spanish = cut.Find("[data-asv-audio='3']");
        Assert.True(spanish.HasAttribute("disabled"));
        // The row keeps its facts and adds the note under them; the note does not replace them.
        var lines = spanish.QuerySelectorAll(".asv__sub").Select(line => line.TextContent).ToList();
        Assert.Equal(["Dolby Digital · 5.1", "Not switchable in this browser"], lines);
        await spanish.ClickAsync();
        Assert.Empty(_switches);
        // The track that is playing keeps its check and its facts.
        Assert.DoesNotContain("Not switchable", cut.Find("[data-asv-audio='0']").TextContent);
        Assert.Contains("Dolby Atmos · 7.1", cut.Find("[data-asv-audio='0']").TextContent);
    }

    [Fact]
    public async Task OneAudioTrackAndNoSubtitles_StillShowsThePanelWithTheTrackListedAndNotSelectable()
    {
        var single = AudioSubtitlesChoices.BuildAudio(L, [new() { Index = 0, Language = "jpn", Codec = "aac", Channels = 2, IsDefault = true }], null, canSwitch: true);
        var cut = await OpenAsync(single, [new SubtitleChoice(null, "Off", true)]);

        var track = cut.Find("[data-asv-audio='0']");
        Assert.Equal("Japanese", track.QuerySelector(".asv__label")!.TextContent);
        Assert.True(track.HasAttribute("disabled"));
        Assert.NotNull(track.QuerySelector(".asv__check"));
        Assert.DoesNotContain("Not switchable", track.TextContent);
        Assert.Equal(["off"], cut.FindAll("[data-asv-subtitle]").Select(row => row.GetAttribute("data-asv-subtitle")));
        await track.ClickAsync();
        Assert.Empty(_switches);
    }

    [Fact]
    public async Task TheFooterOffersSubtitleAppearanceAndFindSubtitles()
    {
        var cut = await OpenAsync();

        var footer = cut.Find("footer.playback-panel__footer");
        Assert.Equal(["appearance", "find"], footer.QuerySelectorAll("[data-asa-action]").Select(action => action.GetAttribute("data-asa-action")));
        Assert.Equal("Subtitle appearance", footer.QuerySelector("[data-asa-action='appearance']")!.TextContent.Trim());
        Assert.Equal("Find subtitles…", footer.QuerySelector("[data-asa-action='find']")!.TextContent.Trim());
    }

    [Fact]
    public async Task FindSubtitles_OpensTheSearchViewInsideThePanelAndTheBackArrowReturns()
    {
        var searches = 0;
        var cut = await OpenAsync(configure: p => p.Add(x => x.OnSearch, EventCallback.Factory.Create(this, () => searches++)));

        await cut.Find("[data-asa-action='find']").ClickAsync();

        Assert.Equal("Find subtitles", cut.Find("header h2").TextContent);
        Assert.NotNull(cut.Find("[data-subtitle-find]"));
        await cut.Find("[data-sfv-action='search']").ClickAsync();
        Assert.Equal(1, searches);
        await cut.Find("button[aria-label='Back']").ClickAsync();
        Assert.Equal("Audio & subtitles", cut.Find("header h2").TextContent);
        Assert.NotNull(cut.Find("[data-audio-subtitles]"));
    }

    [Fact]
    public async Task FindSubtitles_SaysSoWhenNoSubtitleSourceIsSwitchedOn()
    {
        var cut = await OpenAsync(configure: p => p.Add(x => x.NoSubtitleSource, true).Add(x => x.IsAdministrator, true));

        await cut.Find("[data-asa-action='find']").ClickAsync();

        Assert.Contains("No subtitle source is switched on", cut.Find("[data-subtitles-state='no-source']").TextContent);
        Assert.NotNull(cut.Find("[data-subtitles-state='no-source'] a[href='/settings/plugins']"));
        Assert.Empty(cut.FindAll("[data-sfv-action='search']"));
    }

    [Fact]
    public async Task OnAPhone_AudioAndSubtitlesAreTwoTabsInOneSheetWithTheActionsAsRows()
    {
        var cut = await OpenAsync(phone: true);

        var sheet = cut.Find("[data-playback-popover-panel]");
        Assert.Contains("playback-panel--sheet", sheet.ClassList);
        Assert.Equal("tabs", cut.Find("[data-audio-subtitles]").GetAttribute("data-layout"));
        var tabs = cut.FindAll("[role='tab']");
        Assert.Equal(["Audio", "Subtitles"], tabs.Select(tab => tab.TextContent.Trim()));
        // Only one list is drawn at a time; the mockup opens on Subtitles.
        Assert.Equal(["subtitles"], cut.FindAll("[data-asv-column]").Select(column => column.GetAttribute("data-asv-column")));
        Assert.Empty(cut.FindAll(".asv__heading"));

        await cut.Find("[data-asv-tab='audio']").ClickAsync();

        Assert.Equal(["audio"], cut.FindAll("[data-asv-column]").Select(column => column.GetAttribute("data-asv-column")));
        Assert.Equal("true", cut.Find("[data-asv-tab='audio']").GetAttribute("aria-selected"));
        Assert.Equal("rows", cut.Find(".asa").GetAttribute("data-layout"));
    }

    [Fact]
    public async Task OnADesktop_NoTabsAreDrawn()
    {
        var cut = await OpenAsync();

        Assert.Empty(cut.FindAll(".asv__tabs"));
        Assert.Equal("bar", cut.Find(".asa").GetAttribute("data-layout"));
    }

    [Fact]
    public void TheVideoBar_HasNoSeparateSubtitleStyleButtonAndHostsOnlyTheAudioSubtitlesPanel()
    {
        var root = FindRepoRoot();
        var host = LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor"));
        var catalog = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackControlCatalog.Bar.cs"));

        Assert.DoesNotContain("StylingOnly", host, StringComparison.Ordinal);
        Assert.DoesNotContain("subtitle-style", host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PlayerSettingsMenu", host, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(catalog, "PlaybackControlKey\\.AudioSubtitles,"));
        Assert.DoesNotContain("PlaybackControlKey.Captions", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaybackControlKey.AudioTrack", catalog, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    [Fact]
    public async Task EachRadioGroupHasOneTabStop_TheChosenRow_SoTheArrowKeysCanMoveWithinIt()
    {
        var cut = await OpenAsync();

        var audio = cut.FindAll("[data-asv-audio]");
        Assert.Equal(["0"], audio.Where(row => row.GetAttribute("tabindex") == "0").Select(row => row.GetAttribute("data-asv-audio")));
        Assert.All(audio.Where(row => row.GetAttribute("data-asv-audio") != "0"), row => Assert.Equal("-1", row.GetAttribute("tabindex")));
        var subtitles = cut.FindAll("[data-asv-subtitle]");
        Assert.Equal(["manifest:0"], subtitles.Where(row => row.GetAttribute("tabindex") == "0").Select(row => row.GetAttribute("data-asv-subtitle")));
        Assert.All(subtitles.Where(row => row.GetAttribute("data-asv-subtitle") != "manifest:0"), row => Assert.Equal("-1", row.GetAttribute("tabindex")));
        // The arrow keys come from the shared roving-focus script, told to move with Up and Down as well.
        var invocations = JSInterop.Invocations.Where(call => call.Identifier == "attach").ToList();
        Assert.True(invocations.Count >= 2, "both radio groups attach the roving focus script");
    }

    [Fact]
    public async Task WhenNoAudioRowCanBeChosen_NoRowHoldsATabStop()
    {
        var cut = await OpenAsync(AudioSubtitlesChoices.BuildAudio(L, FileAudio, null, canSwitch: false));

        Assert.DoesNotContain(cut.FindAll("[data-asv-audio]"), row => row.GetAttribute("tabindex") == "0");
    }

    [Fact]
    public async Task OnAPhone_TheTabsAreLinkedToTheirPanelAndMoveWithTheArrowKeys()
    {
        var cut = await OpenAsync(phone: true);

        var subtitlesTab = cut.Find("[data-asv-tab='subtitles']");
        var panel = cut.Find("[data-asv-column='subtitles']");
        Assert.Equal("tabpanel", panel.GetAttribute("role"));
        Assert.Equal(subtitlesTab.Id, panel.GetAttribute("aria-labelledby"));
        Assert.Equal(panel.Id, subtitlesTab.GetAttribute("aria-controls"));
        Assert.Equal("0", subtitlesTab.GetAttribute("tabindex"));
        Assert.Equal("-1", cut.Find("[data-asv-tab='audio']").GetAttribute("tabindex"));

        await cut.Find("[role='tablist']").KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowLeft" });

        Assert.Equal("true", cut.Find("[data-asv-tab='audio']").GetAttribute("aria-selected"));
        Assert.Equal("0", cut.Find("[data-asv-tab='audio']").GetAttribute("tabindex"));
        Assert.Equal(["audio"], cut.FindAll("[data-asv-column]").Select(column => column.GetAttribute("data-asv-column")));
        await cut.Find("[role='tablist']").KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowRight" });
        Assert.Equal("true", cut.Find("[data-asv-tab='subtitles']").GetAttribute("aria-selected"));
    }

    [Fact]
    public async Task OnADesktop_TheColumnsAreNamedRegionsNotTabPanels()
    {
        var cut = await OpenAsync();

        Assert.All(cut.FindAll("[data-asv-column]"), column =>
        {
            Assert.Null(column.GetAttribute("role"));
            Assert.False(string.IsNullOrWhiteSpace(column.GetAttribute("aria-label")));
        });
    }
}
