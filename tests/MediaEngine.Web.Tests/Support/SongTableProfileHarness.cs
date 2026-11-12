using System.Security.Claims;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests.Support;

/// <summary>
/// The signed-in profile and an Engine that keeps its song table texts, built from hand-written spies: reading hands out a fresh copy of
/// what the Engine holds, saving copies the sent map back and counts, and <see cref="EngineDown"/> makes both fail like an unreachable Engine.
/// </summary>
internal sealed class SongTableProfileHarness
{
    public SongTableProfileHarness(Guid? profile = null)
    {
        Profile = profile ?? Guid.NewGuid();
        var profileId = Profile;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
                { new(profileId, "Viewer", "#000000", "RestrictedProfile", DateTimeOffset.UtcNow) }));
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackSettingsAsync), _ =>
            {
                if (EngineDown)
                {
                    throw new HttpRequestException("The Engine is not reachable.");
                }

                return Task.FromResult<UserPlaybackSettingsDto?>(Copy());
            });
            stub.SetHandler(nameof(IEngineApiClient.UpdatePlaybackSettingsAsync), args =>
            {
                if (EngineDown)
                {
                    throw new HttpRequestException("The Engine is not reachable.");
                }

                var sent = (UserPlaybackSettingsDto)args![1]!;
                Stored.Clear();
                foreach (var (key, value) in sent.Listening.SongTableColumns)
                {
                    Stored[key] = value;
                }

                Saves++;
                return Task.FromResult<UserPlaybackSettingsDto?>(Copy());
            });
        });

        var profiles = new ActiveProfileSessionService(null!, api, authenticationStateProvider: new ProfileAuthentication(profileId));
        Orchestrator = new UIOrchestratorService(api, new UniverseStateContainer(), profiles, new ConfigurationManager(), NullLogger<UIOrchestratorService>.Instance);
        Accessor = new UserPlaybackPreferencesAccessor(Orchestrator);
        Log = new CapturingLoggerProvider();
        Settings = new SongTableProfileSettings(
            Accessor,
            Orchestrator,
            LoggerFactory.Create(builder => builder.AddProvider(Log)).CreateLogger<SongTableProfileSettings>());
    }

    public Guid Profile { get; }

    /// <summary>The song table texts the Engine holds for the profile.</summary>
    public Dictionary<string, string> Stored { get; } = [];

    /// <summary>How many times the Engine accepted a save.</summary>
    public int Saves { get; private set; }

    public bool EngineDown { get; set; }

    public UIOrchestratorService Orchestrator { get; }

    public UserPlaybackPreferencesAccessor Accessor { get; }

    public CapturingLoggerProvider Log { get; }

    public SongTableProfileSettings Settings { get; }

    private UserPlaybackSettingsDto Copy()
    {
        var settings = UserPlaybackSettingsDto.CreateDefaults(Profile);
        foreach (var (key, value) in Stored)
        {
            settings.Listening.SongTableColumns[key] = value;
        }

        return settings;
    }

    private sealed class ProfileAuthentication(Guid profileId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("tuvima:active_profile_id", profileId.ToString("D"))], "test"))));
    }
}
