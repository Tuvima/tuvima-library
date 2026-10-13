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

public sealed class ListeningSettingsPageTests : AsyncBunitContext
{
    private static readonly Guid ActiveId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherId = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private readonly CountingAccessor _accessor = new(UserPlaybackSettingsDto.CreateDefaults(ActiveId));
    private readonly List<(Guid Profile, string Call)> _apiCalls = [];
    private UserPlaybackSettingsDto? _otherSettings = UserPlaybackSettingsDto.CreateDefaults(OtherId);

    public ListeningSettingsPageTests()
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
    public void OwnProfile_ShowsTheSkipAndSleepTimerGroupsOnly()
    {
        var cut = Render<ListeningSettingsPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Skip back", cut.Markup);
            Assert.Contains("Skip forward", cut.Markup);
            Assert.Contains("Resume rewind", cut.Markup);
            Assert.Contains("Rewind slightly when resuming so spoken context is not lost.", cut.Markup);
            Assert.Contains("Default length", cut.Markup);
            Assert.Contains("Offer end of chapter", cut.Markup);
            Assert.Equal(2, cut.FindAll(".listening-settings-card").Count);
            Assert.DoesNotContain("speed", cut.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("silence", cut.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("crossfade", cut.Markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Autoplay", cut.Markup);
        });
    }

    [Fact]
    public void OwnProfile_SavesThroughTheAccessorPath()
    {
        var cut = Render<ListeningSettingsPage>();
        cut.WaitForAssertion(() => Assert.Contains("Offer end of chapter", cut.Markup));

        cut.Find("input[aria-label='Offer end of chapter']").Change(false);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(1, _accessor.GetCalls);
            Assert.Equal(1, _accessor.UpdateCalls);
            Assert.False(_accessor.Last!.Listening.AllowEndOfChapterSleepTimer);
        });
    }

    [Fact]
    public void OtherProfile_UsesProfileIdClientCallsAndLeavesTheAccessorAlone()
    {
        var cut = Render<ListeningSettingsPage>(parameters => parameters.Add(page => page.ProfileId, OtherId));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("How Mira listens.", cut.Markup);
            Assert.Contains("/settings/profiles/" + OtherId, cut.Markup);
        });

        cut.Find("input[aria-label='Offer end of chapter']").Change(false);

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

        var cut = Render<ListeningSettingsPage>(parameters => parameters.Add(page => page.ProfileId, OtherId));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("You can't change this person's settings.", cut.Markup);
            Assert.Empty(cut.FindAll("input"));
        });
    }

    private sealed class CountingAccessor(UserPlaybackSettingsDto settings) : IUserPlaybackPreferencesAccessor
    {
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
