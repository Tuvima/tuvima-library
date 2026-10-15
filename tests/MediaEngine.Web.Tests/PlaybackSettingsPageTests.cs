using System.Security.Claims;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackSettingsPageTests : AsyncBunitContext
{
    private static readonly Guid ActiveId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherId = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private readonly CountingAccessor _accessor = new(UserPlaybackSettingsDto.CreateDefaults(ActiveId));
    private readonly List<(Guid Profile, string Call)> _apiCalls = [];
    private readonly List<UserPlaybackSettingsDto> _saved = [];
    private UserPlaybackSettingsDto? _otherSettings = UserPlaybackSettingsDto.CreateDefaults(OtherId);
    private readonly CapturingLoggerProvider _logs = new();
    private bool _putThrows;
    private UserPlaybackSettingsDto? _remote = UserPlaybackSettingsDto.CreateDefaults(ActiveId);

    public PlaybackSettingsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddLogging(logging => logging.AddProvider(_logs));
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(_accessor);
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(ActiveId, "Test User", "#C9922E", "Administrator", DateTimeOffset.UtcNow),
                new(OtherId, "Mira", "#336699", "StandardProfile", DateTimeOffset.UtcNow),
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackSettingsAsync), args =>
            {
                var id = (Guid)args![0]!;
                _apiCalls.Add((id, "get"));
                return Task.FromResult(Copy(id == OtherId ? _otherSettings : id == ActiveId ? _remote : UserPlaybackSettingsDto.CreateDefaults(id)));
            });
            stub.SetHandler(nameof(IEngineApiClient.UpdatePlaybackSettingsAsync), args =>
            {
                var id = (Guid)args![0]!;
                _apiCalls.Add((id, "put"));
                if (_putThrows)
                {
                    return Task.FromException<UserPlaybackSettingsDto?>(new HttpRequestException("The Engine dropped the connection."));
                }

                Store(id, (UserPlaybackSettingsDto)args[1]!);
                return Task.FromResult<UserPlaybackSettingsDto?>((UserPlaybackSettingsDto)args[1]!);
            });
        });
        Services.AddSingleton(api);
        var profiles = new ActiveProfileSessionService(null!, api, authenticationStateProvider: new ProfileAuthentication(ActiveId));
        Services.AddSingleton(profiles);
        Services.AddSingleton(new UIOrchestratorService(api, new UniverseStateContainer(), profiles, new ConfigurationManager(), NullLogger<UIOrchestratorService>.Instance));
        Render<AppPopoverHost>();
    }

    [Fact]
    public void WatchingMode_ShowsAutoplayResumeSkipAndSubtitlesOnly()
    {
        var cut = Render<PlaybackSettingsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Autoplay next episode", cut.Markup);
            Assert.Contains("Resume where you left off", cut.Markup);
            Assert.Contains("Skip back", cut.Markup);
            Assert.Contains("Skip forward", cut.Markup);
            Assert.Equal(5, cut.FindAll(".playback-settings-card").Count);
            Assert.DoesNotContain("Default playback speed", cut.Markup);
            Assert.DoesNotContain("Audiobook default speed", cut.Markup);
            Assert.DoesNotContain("Adjust text while reading", cut.Markup);
            Assert.DoesNotContain("Choose tracks in the player", cut.Markup);
        });
    }

    [Fact]
    public void OwnProfile_ReadsAndSavesThroughTheAccessorPath()
    {
        var cut = Render<PlaybackSettingsPage>();
        cut.WaitForAssertion(() => Assert.Contains("Autoplay next episode", cut.Markup));

        cut.Find("input[aria-label='Autoplay next episode']").Change(false);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(1, _accessor.GetCalls);
            Assert.Equal(1, _accessor.UpdateCalls);
            Assert.False(_accessor.Last!.Watching.AutoplayNextEpisode);
            Assert.Contains((ActiveId, "put"), _apiCalls);
        });
    }

    [Fact]
    public void OtherProfile_UsesProfileIdClientCallsAndLeavesTheAccessorAlone()
    {
        var cut = Render<PlaybackSettingsPage>(parameters => parameters.Add(page => page.ProfileId, OtherId));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("How Mira watches.", cut.Markup);
            Assert.Contains("/settings/profiles/" + OtherId, cut.Markup);
        });

        cut.Find("input[aria-label='Autoplay next episode']").Change(false);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains((OtherId, "get"), _apiCalls);
            Assert.Contains((OtherId, "put"), _apiCalls);
            Assert.DoesNotContain(_apiCalls, call => call.Profile == ActiveId);
            Assert.Equal(0, _accessor.GetCalls);
            Assert.Equal(0, _accessor.UpdateCalls);
        });
    }

    [Fact]
    public void OtherProfile_ThatTheEngineRefusesShowsAMessageAndNoControls()
    {
        _otherSettings = null;

        var cut = Render<PlaybackSettingsPage>(parameters => parameters.Add(page => page.ProfileId, OtherId));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("You can't change this person's settings.", cut.Markup);
            Assert.Empty(cut.FindAll("input"));
        });
    }

    [Fact]
    public void SubtitlesGroup_IsTheSubtitlesAnchorWithThreeSelectsAndNoPositionOrStyle()
    {
        var cut = Render<PlaybackSettingsPage>();

        cut.WaitForAssertion(() =>
        {
            var group = cut.Find("#subtitles");
            Assert.Contains("Subtitles &amp; audio", group.InnerHtml);
            Assert.Contains("Subtitle appearance", group.InnerHtml);
            Assert.Equal(new[] { "Audio language", "Subtitle language", "Show subtitles" },
                cut.FindComponents<AppSelect>().Select(select => (string)select.Instance.AdditionalAttributes!["aria-label"]).ToArray());
            Assert.Contains("Auto (only when audio is not my language)", group.InnerHtml);
            Assert.Contains("Text size", group.InnerHtml);
            Assert.Contains("Background", group.InnerHtml);
            Assert.DoesNotContain("Position", group.InnerHtml);
            Assert.DoesNotContain("Style", group.InnerHtml);
        });
    }

    [Fact]
    public void SubtitlePreview_FollowsTextSizeAndBackgroundAndSavesThroughTheAccessorPath()
    {
        var cut = Render<PlaybackSettingsPage>();
        cut.WaitForAssertion(() => Assert.Equal("Medium", cut.Find(".subtitle-preview").GetAttribute("data-subtitle-size")));
        Assert.Equal("true", cut.Find(".subtitle-preview").GetAttribute("data-subtitle-background"));

        cut.FindAll(".app-segmented-control button").Single(button => button.TextContent.Trim() == "Large").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Large", cut.Find(".subtitle-preview").GetAttribute("data-subtitle-size"));
            Assert.Equal(PlaybackPreferenceValues.Large, _accessor.Last!.Subtitles.SubtitleSize);
        });

        cut.Find("input[aria-label='Background']").Change(false);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("false", cut.Find(".subtitle-preview").GetAttribute("data-subtitle-background"));
            Assert.False(_accessor.Last!.Subtitles.SubtitleBackground);
            // The size chosen a moment ago is written back unchanged.
            Assert.Equal(PlaybackPreferenceValues.Large, _accessor.Last.Subtitles.SubtitleSize);
        });
    }

    [Fact]
    public async Task LanguageAndShowSubtitlesSelects_SaveTheDisplayNameAndTheEngineValues()
    {
        var cut = Render<PlaybackSettingsPage>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindComponents<AppSelect>().Count));

        await cut.InvokeAsync(() => SelectNamed(cut, "Audio language").Instance.ValueChanged.InvokeAsync("Japanese"));
        await cut.InvokeAsync(() => SelectNamed(cut, "Subtitle language").Instance.ValueChanged.InvokeAsync("Spanish"));
        await cut.InvokeAsync(() => SelectNamed(cut, "Show subtitles").Instance.ValueChanged.InvokeAsync(PlaybackPreferenceValues.Never));

        cut.WaitForAssertion(() =>
        {
            var last = _accessor.Last!.Subtitles;
            Assert.Equal("Japanese", last.AudioLanguage);
            Assert.Equal("Spanish", last.DefaultSubtitleLanguage);
            Assert.Equal(PlaybackPreferenceValues.Never, last.ForcedSubtitlesMode);
        });
    }

    [Fact]
    public async Task OtherProfile_SavesSubtitleChoicesThroughTheProfileIdClientCall()
    {
        var cut = Render<PlaybackSettingsPage>(parameters => parameters.Add(page => page.ProfileId, OtherId));
        cut.WaitForAssertion(() => Assert.Contains("How Mira watches.", cut.Markup));

        cut.FindAll(".app-segmented-control button").Single(button => button.TextContent.Trim() == "Small").Click();
        await cut.InvokeAsync(() => SelectNamed(cut, "Show subtitles").Instance.ValueChanged.InvokeAsync(PlaybackPreferenceValues.Always));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains((OtherId, "put"), _apiCalls);
            Assert.DoesNotContain(_apiCalls, call => call.Profile == ActiveId);
            Assert.Equal(PlaybackPreferenceValues.Small, _saved.Last().Subtitles.SubtitleSize);
            Assert.Equal(PlaybackPreferenceValues.Always, _saved.Last().Subtitles.ForcedSubtitlesMode);
            Assert.Equal(0, _accessor.GetCalls);
            Assert.Equal(0, _accessor.UpdateCalls);
        });
    }

    [Fact]
    public void SavedLanguageOutsideTheCommonList_IsStillOffered()
    {
        _accessor.Settings.Subtitles.DefaultSubtitleLanguage = "Klingon";

        var cut = Render<PlaybackSettingsPage>();

        cut.WaitForAssertion(() => Assert.Contains("Klingon", SelectNamed(cut, "Subtitle language").Markup));
    }

    [Fact]
    public void SubtitlesFragment_ScrollsTheGroupIntoViewOnceItHasLoaded()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/playback#subtitles");

        Render<PlaybackSettingsPage>();

        JSInterop.VerifyInvoke("tuvimaEditorScrollTo");
    }

    [Fact]
    public void OwnProfile_WhenTheEngineCannotBeReached_ShowsRetryAndNoControlsAndNeverSavesDefaults()
    {
        _accessor.Unavailable = true;

        var cut = Render<PlaybackSettingsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Playback settings could not be loaded.", cut.Markup);
            Assert.NotEmpty(cut.FindAll(".app-error-state__retry"));
            Assert.Empty(cut.FindAll("input"));
            Assert.Empty(cut.FindAll(".playback-settings-card"));
        });
        Assert.DoesNotContain(_apiCalls, call => call.Call == "put");

        _accessor.Unavailable = false;
        cut.Find(".app-error-state__retry").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".app-error-state"));
            Assert.Contains("Autoplay next episode", cut.Markup);
        });
    }

    [Fact]
    public void Save_MergesOnlyTheChangedFieldIntoTheCurrentSettings()
    {
        var cut = Render<PlaybackSettingsPage>();
        cut.WaitForAssertion(() => Assert.Contains("Autoplay next episode", cut.Markup));
        // Another tab changed these fields after this page opened.
        _remote!.General.ResumePlayback = false;
        _remote.Subtitles.AudioLanguage = "Japanese";
        _remote.Listening.SkipBackSeconds = 30;

        cut.Find("input[aria-label='Autoplay next episode']").Change(false);

        cut.WaitForAssertion(() =>
        {
            var saved = _saved.Last();
            Assert.False(saved.Watching.AutoplayNextEpisode);
            Assert.False(saved.General.ResumePlayback);
            Assert.Equal("Japanese", saved.Subtitles.AudioLanguage);
            Assert.Equal(30, saved.Listening.SkipBackSeconds);
        });
    }

    [Fact]
    public void OtherProfile_Save_MergesOnlyTheChangedFieldIntoTheCurrentSettings()
    {
        var cut = Render<PlaybackSettingsPage>(parameters => parameters.Add(page => page.ProfileId, OtherId));
        cut.WaitForAssertion(() => Assert.Contains("How Mira watches.", cut.Markup));
        _otherSettings!.General.ResumePlayback = false;

        cut.Find("input[aria-label='Autoplay next episode']").Change(false);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains((OtherId, "put"), _apiCalls);
            Assert.False(_saved.Last().Watching.AutoplayNextEpisode);
            Assert.False(_saved.Last().General.ResumePlayback);
        });
    }

    [Fact]
    public void Save_WhenTheCurrentSettingsCannotBeReRead_ShowsTheErrorAndSavesNothing()
    {
        var cut = Render<PlaybackSettingsPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input")));
        _remote = null;

        cut.Find("input[aria-label='Autoplay next episode']").Change(false);

        cut.WaitForAssertion(() => Assert.Contains("Playback settings could not be saved.", cut.Markup));
        Assert.DoesNotContain(_apiCalls, call => call.Call == "put");
        Assert.Equal(0, _accessor.UpdateCalls);
    }

    // What the Engine holds for each profile. Reads return a copy and writes replace it, as over HTTP.
    private static UserPlaybackSettingsDto? Copy(UserPlaybackSettingsDto? source) => source is null
        ? null
        : JsonSerializer.Deserialize<UserPlaybackSettingsDto>(JsonSerializer.Serialize(source, MediaEngineJson.Web), MediaEngineJson.Web);

    private void Store(Guid id, UserPlaybackSettingsDto settings)
    {
        _saved.Add(settings);
        if (id == OtherId)
        {
            _otherSettings = settings;
        }
        else
        {
            _remote = settings;
        }
    }

    private static IRenderedComponent<AppSelect> SelectNamed(IRenderedComponent<PlaybackSettingsPage> cut, string label) =>
        cut.FindComponents<AppSelect>().Single(select => (string)select.Instance.AdditionalAttributes!["aria-label"] == label);

    [Fact]
    public void Load_WhenTheEngineCallThrows_LogsTheFailureAndShowsTheErrorWithNoControls()
    {
        _accessor.Throws = true;

        var cut = Render<PlaybackSettingsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Playback settings could not be loaded.", cut.Markup);
            Assert.Empty(cut.FindAll("input"));
        });
        Assert.Contains(_logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is HttpRequestException);
    }

    [Fact]
    public void Save_WhenTheEngineCallThrows_LogsTheFailureAndShowsTheError()
    {
        var cut = Render<PlaybackSettingsPage>();
        cut.WaitForAssertion(() => Assert.Contains("Autoplay next episode", cut.Markup));
        _putThrows = true;

        cut.Find("input[aria-label='Autoplay next episode']").Change(false);

        cut.WaitForAssertion(() => Assert.Contains("Playback settings could not be saved.", cut.Markup));
        Assert.Contains(_logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is HttpRequestException);
    }

    [Fact]
    public void OtherProfile_Save_WhenTheCurrentSettingsBelongToAnotherProfile_SavesNothingAndStartsAgain()
    {
        var cut = Render<PlaybackSettingsPage>(parameters => parameters.Add(page => page.ProfileId, OtherId));
        cut.WaitForAssertion(() => Assert.Contains("How Mira watches.", cut.Markup));
        var readsBefore = _apiCalls.Count(call => call.Call == "get");
        _otherSettings = UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid());

        cut.Find("input[aria-label='Autoplay next episode']").Change(false);

        cut.WaitForAssertion(() =>
        {
            // One read to merge into, one to start again; the second is refused because it is not Mira's either.
            Assert.True(_apiCalls.Count(call => call.Call == "get") >= readsBefore + 2);
            Assert.Contains("Playback settings could not be loaded.", cut.Markup);
        });
        Assert.DoesNotContain(_apiCalls, call => call.Call == "put");
    }

    private sealed class CountingAccessor(UserPlaybackSettingsDto settings) : IUserPlaybackPreferencesAccessor
    {
        public UserPlaybackSettingsDto Settings => settings;
        public int GetCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public UserPlaybackSettingsDto? Last { get; private set; }
        public Guid? ActiveProfileId => settings.ProfileId;

        /// <summary>When set, the accessor answers null, as it does when the Engine cannot be reached.</summary>
        public bool Unavailable { get; set; }
        public bool Throws { get; set; }

        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default)
        {
            GetCalls++;
            if (Throws)
            {
                throw new HttpRequestException("The Engine dropped the connection.");
            }

            return Task.FromResult<UserPlaybackSettingsDto?>(Unavailable ? null : settings);
        }

        public void UpdateCache(UserPlaybackSettingsDto next)
        {
            UpdateCalls++;
            Last = next;
        }

        public void Invalidate() { }
    }

    private sealed class ProfileAuthentication(Guid profileId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("tuvima:active_profile_id", profileId.ToString("D"))], "test"))));
    }
}
