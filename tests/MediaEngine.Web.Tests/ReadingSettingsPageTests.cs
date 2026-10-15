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
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class ReadingSettingsPageTests : AsyncBunitContext
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

    public ReadingSettingsPageTests()
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

                var settings = (UserPlaybackSettingsDto)args[1]!;
                Store(id, settings);
                return Task.FromResult<UserPlaybackSettingsDto?>(settings);
            });
        });
        Services.AddSingleton(api);
        var profiles = new ActiveProfileSessionService(null!, api, authenticationStateProvider: new ProfileAuthentication(ActiveId));
        Services.AddSingleton(profiles);
        Services.AddSingleton(new UIOrchestratorService(api, new UniverseStateContainer(), profiles, new ConfigurationManager(), NullLogger<UIOrchestratorService>.Instance));
        Render<AppPopoverHost>();
    }

    [Fact]
    public void OwnProfile_ShowsTheSampleAndEveryReadingRow()
    {
        var cut = Render<ReadingSettingsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Theme", cut.Markup);
            Assert.Contains("Typeface", cut.Markup);
            Assert.Contains("Text size", cut.Markup);
            Assert.Contains("Line spacing", cut.Markup);
            Assert.Contains("Page width", cut.Markup);
            Assert.Contains("Layout", cut.Markup);
            Assert.Contains("Match app", cut.Markup);
            Assert.Contains("Sepia", cut.Markup);
            Assert.Contains("Scrolled", cut.Markup);
            Assert.NotEmpty(cut.FindAll(".reading-sample"));
            Assert.Equal(2, cut.FindAll(".reading-settings-card").Count);
        });
    }

    [Fact]
    public void Sample_FollowsTheChosenThemeTypefaceSizeAndLineSpacing()
    {
        _accessor.Settings.Reading.Theme = ReadingPreferenceValues.ThemeSepia;
        _accessor.Settings.Reading.Font = ReadingPreferenceValues.FontSerif;
        _accessor.Settings.Reading.FontSizePx = 24;
        _accessor.Settings.Reading.LineHeight = 1.9m;

        var cut = Render<ReadingSettingsPage>();

        cut.WaitForAssertion(() =>
        {
            var sample = cut.Find(".reading-sample");
            Assert.Equal("sepia", sample.GetAttribute("data-theme"));
            Assert.Equal("serif", sample.GetAttribute("data-font"));
            var css = sample.GetAttribute("style");
            Assert.Contains("--reading-sample-size: 24px", css);
            Assert.Contains("--reading-sample-line-height: 1.9", css);
        });
    }

    [Fact]
    public void OwnProfile_SavesThroughTheAccessorPathAndTheSampleFollows()
    {
        var cut = Render<ReadingSettingsPage>();
        cut.WaitForAssertion(() => Assert.Contains("Match app", cut.Markup));

        cut.FindAll(".app-segmented-control button")
            .Single(button => button.TextContent.Trim() == "Dark")
            .Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(1, _accessor.GetCalls);
            Assert.Equal(1, _accessor.UpdateCalls);
            Assert.Equal(ReadingPreferenceValues.ThemeDark, _accessor.Last!.Reading.Theme);
            Assert.Equal("dark", cut.Find(".reading-sample").GetAttribute("data-theme"));
        });

        cut.FindAll(".app-segmented-control button")
            .Single(button => button.TextContent.Trim() == "Scrolled")
            .Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(PlaybackPreferenceValues.Scroll, _accessor.Last!.Reading.ReadingMode);
            // Other reading fields are written back as they were.
            Assert.Equal(ReadingPreferenceValues.ThemeDark, _accessor.Last.Reading.Theme);
            Assert.Equal(100, _accessor.Last.Reading.FontSizePercent);
        });
    }

    [Fact]
    public void OtherProfile_UsesProfileIdClientCallsAndLeavesTheAccessorAlone()
    {
        var cut = Render<ReadingSettingsPage>(parameters => parameters.Add(page => page.ProfileId, OtherId));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("How Mira reads.", cut.Markup);
            Assert.Contains("/settings/profiles/" + OtherId, cut.Markup);
        });

        cut.FindAll(".app-segmented-control button")
            .Single(button => button.TextContent.Trim() == "Sepia")
            .Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains((OtherId, "get"), _apiCalls);
            Assert.Contains((OtherId, "put"), _apiCalls);
            Assert.DoesNotContain(_apiCalls, call => call.Profile == ActiveId);
            Assert.Equal(ReadingPreferenceValues.ThemeSepia, _saved.Last().Reading.Theme);
            Assert.Equal(0, _accessor.GetCalls);
            Assert.Equal(0, _accessor.UpdateCalls);
        });
    }

    [Fact]
    public void OtherProfile_ThatTheEngineRefusesShowsAMessageAndNoControls()
    {
        _otherSettings = null;

        var cut = Render<ReadingSettingsPage>(parameters => parameters.Add(page => page.ProfileId, OtherId));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("You can't change this person's settings.", cut.Markup);
            Assert.Empty(cut.FindAll("button"));
        });
    }

    [Fact]
    public void OwnProfile_WhenTheEngineCannotBeReached_ShowsRetryAndNoControlsAndNeverSavesDefaults()
    {
        _accessor.Unavailable = true;

        var cut = Render<ReadingSettingsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Reading settings could not be loaded.", cut.Markup);
            Assert.NotEmpty(cut.FindAll(".app-error-state__retry"));
            Assert.Empty(cut.FindAll("input"));
            Assert.Empty(cut.FindAll(".reading-settings-card"));
        });
        Assert.DoesNotContain(_apiCalls, call => call.Call == "put");

        _accessor.Unavailable = false;
        cut.Find(".app-error-state__retry").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".app-error-state"));
            Assert.Contains("Match app", cut.Markup);
        });
    }

    [Fact]
    public void Save_MergesOnlyTheChangedFieldIntoTheCurrentSettings()
    {
        var cut = Render<ReadingSettingsPage>();
        cut.WaitForAssertion(() => Assert.Contains("Match app", cut.Markup));
        // Another tab changed these fields after this page opened.
        _remote!.Reading.FontSizePx = 24;
        _remote.Listening.SkipBackSeconds = 30;
        _remote.Watching.AutoplayNextEpisode = false;

        cut.FindAll(".app-segmented-control button")
            .Single(button => button.TextContent.Trim() == "Dark")
            .Click();

        cut.WaitForAssertion(() =>
        {
            var saved = _saved.Last();
            Assert.Equal(ReadingPreferenceValues.ThemeDark, saved.Reading.Theme);
            Assert.Equal(24, saved.Reading.FontSizePx);
            Assert.Equal(30, saved.Listening.SkipBackSeconds);
            Assert.False(saved.Watching.AutoplayNextEpisode);
        });
    }

    [Fact]
    public void Save_WhenTheCurrentSettingsCannotBeReRead_ShowsTheErrorAndSavesNothing()
    {
        var cut = Render<ReadingSettingsPage>();
        cut.WaitForAssertion(() => Assert.Contains("Match app", cut.Markup));
        _remote = null;

        cut.FindAll(".app-segmented-control button")
            .Single(button => button.TextContent.Trim() == "Dark")
            .Click();

        cut.WaitForAssertion(() => Assert.Contains("Reading settings could not be saved.", cut.Markup));
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

    [Fact]
    public void Load_WhenTheEngineCallThrows_LogsTheFailureAndShowsTheErrorWithNoControls()
    {
        _accessor.Throws = true;

        var cut = Render<ReadingSettingsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Reading settings could not be loaded.", cut.Markup);
            Assert.Empty(cut.FindAll("input"));
        });
        Assert.Contains(_logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is HttpRequestException);
    }

    [Fact]
    public void Save_WhenTheEngineCallThrows_LogsTheFailureAndShowsTheError()
    {
        var cut = Render<ReadingSettingsPage>();
        cut.WaitForAssertion(() => Assert.Contains("Match app", cut.Markup));
        _putThrows = true;

        cut.FindAll(".app-segmented-control button")
            .Single(button => button.TextContent.Trim() == "Dark")
            .Click();

        cut.WaitForAssertion(() => Assert.Contains("Reading settings could not be saved.", cut.Markup));
        Assert.Contains(_logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception is HttpRequestException);
    }

    [Fact]
    public void OtherProfile_Save_WhenTheCurrentSettingsBelongToAnotherProfile_SavesNothingAndStartsAgain()
    {
        var cut = Render<ReadingSettingsPage>(parameters => parameters.Add(page => page.ProfileId, OtherId));
        cut.WaitForAssertion(() => Assert.Contains("Match app", cut.Markup));
        var readsBefore = _apiCalls.Count(call => call.Call == "get");
        _otherSettings = UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid());

        cut.FindAll(".app-segmented-control button")
            .Single(button => button.TextContent.Trim() == "Dark")
            .Click();

        cut.WaitForAssertion(() =>
        {
            // One read to merge into, one to start again; the second is refused because it is not Mira's either.
            Assert.True(_apiCalls.Count(call => call.Call == "get") >= readsBefore + 2);
            Assert.Contains("Reading settings could not be loaded.", cut.Markup);
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
