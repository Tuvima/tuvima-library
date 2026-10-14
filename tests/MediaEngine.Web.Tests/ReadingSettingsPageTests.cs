using System.Security.Claims;
using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    public ReadingSettingsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
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
                return Task.FromResult(id == OtherId ? _otherSettings : UserPlaybackSettingsDto.CreateDefaults(id));
            });
            stub.SetHandler(nameof(IEngineApiClient.UpdatePlaybackSettingsAsync), args =>
            {
                var id = (Guid)args![0]!;
                _apiCalls.Add((id, "put"));
                var settings = (UserPlaybackSettingsDto)args[1]!;
                _saved.Add(settings);
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

    private sealed class CountingAccessor(UserPlaybackSettingsDto settings) : IUserPlaybackPreferencesAccessor
    {
        public UserPlaybackSettingsDto Settings => settings;
        public int GetCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public UserPlaybackSettingsDto? Last { get; private set; }
        public Guid? ActiveProfileId => settings.ProfileId;

        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult<UserPlaybackSettingsDto?>(settings);
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
