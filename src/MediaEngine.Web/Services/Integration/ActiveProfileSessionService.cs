using System.Security.Claims;
using MediaEngine.Web.Models.ViewDTOs;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Stores the active local profile for the current browser.
/// </summary>
public sealed class ActiveProfileSessionService : IDisposable
{
    private readonly IEngineApiClient _api;
    private readonly ActiveProfileAccessor _activeProfileAccessor;
    private readonly AuthenticationStateProvider? _authenticationStateProvider;
    private readonly DashboardSessionAccessor? _dashboardSession;
    private readonly DashboardIdentityClient? _identityClient;
    private readonly SemaphoreSlim _profilesGate = new(1, 1);
    private List<ProfileViewModel> _profiles = [];
    private ProfileViewModel? _activeProfile;
    private bool _profilesLoaded;

    public ActiveProfileSessionService(
        IJSRuntime js,
        IEngineApiClient api,
        ActiveProfileAccessor? activeProfileAccessor = null,
        AuthenticationStateProvider? authenticationStateProvider = null,
        DashboardSessionAccessor? dashboardSession = null,
        DashboardIdentityClient? identityClient = null)
    {
        _ = js;
        _api = api;
        _activeProfileAccessor = activeProfileAccessor ?? new ActiveProfileAccessor();
        _authenticationStateProvider = authenticationStateProvider;
        _dashboardSession = dashboardSession;
        _identityClient = identityClient;
    }

    public ProfileViewModel? CurrentProfile => _activeProfile;

    public IReadOnlyList<ProfileViewModel> Profiles => _profiles;

    public async Task<List<ProfileViewModel>> GetProfilesAsync(
        CancellationToken ct = default,
        bool forceRefresh = false)
    {
        if (_profilesLoaded && !forceRefresh)
        {
            return [.. _profiles];
        }

        await _profilesGate.WaitAsync(ct);
        try
        {
            if (_profilesLoaded && !forceRefresh)
            {
                return [.. _profiles];
            }

            if (!await EnsureProfileAuthorityAsync(ct).ConfigureAwait(false))
            {
                return [];
            }

            var requestedSession = _dashboardSession?.CurrentSnapshot();
            var requestedProfileId = _dashboardSession is not null
                ? requestedSession?.ActiveProfileId
                : _activeProfileAccessor.ProfileId;
            var profiles = await _api.GetProfilesAsync(ct).ConfigureAwait(false);
            if (profiles.Count == 0)
            {
                _profilesLoaded = false;
                return [];
            }
            if (!IsCurrentProfileRequest(requestedSession, requestedProfileId))
            {
                return [];
            }

            if (_dashboardSession?.Authority is { } authority)
            {
                var grantedIds = authority.ProfileGrants.Where(grant => grant.IsEnabled)
                    .Select(grant => grant.ProfileId).ToHashSet();
                profiles = profiles.Where(profile => grantedIds.Contains(profile.Id)).ToList();
            }
            if (profiles.Count == 0)
            {
                _profilesLoaded = false;
                return [];
            }

            var activeProfile = await ResolveActiveProfileAsync(profiles, ct).ConfigureAwait(false);
            if (!IsCurrentProfileRequest(requestedSession, requestedProfileId))
            {
                return [];
            }

            _profiles = profiles;
            _activeProfile = activeProfile;
            _activeProfileAccessor.SetProfile(activeProfile?.Id);
            _profilesLoaded = true;
            return [.. profiles];
        }
        finally
        {
            _profilesGate.Release();
        }
    }

    public async Task<ProfileViewModel?> GetActiveProfileAsync(CancellationToken ct = default)
    {
        if (_profilesLoaded)
        {
            return _activeProfile;
        }

        await GetProfilesAsync(ct);
        return _activeProfile;
    }

    public async Task<ProfileSwitchOutcome> SetActiveProfileAsync(
        Guid profileId,
        string? secret = null,
        CancellationToken ct = default)
    {
        var profiles = await GetProfilesAsync(ct);
        var profile = profiles.FirstOrDefault(candidate => candidate.Id == profileId);
        if (profile is null)
        {
            return new ProfileSwitchOutcome(ProfileSwitchStatus.NotFound);
        }

        if (_identityClient is not null && _dashboardSession?.SessionToken is not null)
        {
            if (_dashboardSession.ShouldLockOnLeave)
            {
                await _identityClient.ExitAdministratorAsync(ct).ConfigureAwait(false);
            }
            var switched = await _identityClient.SwitchProfileAsync(
                new MediaEngine.Contracts.Authentication.SwitchProfileRequest { ProfileId = profileId, Secret = secret }, ct);
            if (switched.Status != DashboardProfileSwitchStatus.Succeeded || switched.Session is null)
            {
                return new ProfileSwitchOutcome(switched.Status switch
                {
                    DashboardProfileSwitchStatus.PinRequired => ProfileSwitchStatus.PinRequired,
                    DashboardProfileSwitchStatus.Forbidden => ProfileSwitchStatus.Forbidden,
                    DashboardProfileSwitchStatus.NotFound => ProfileSwitchStatus.NotFound,
                    _ => ProfileSwitchStatus.Failed,
                });
            }

            var session = switched.Session;
            _dashboardSession.Set(
                _dashboardSession.SessionToken,
                session.AccountId,
                session.ActiveProfileId,
                session.SessionId,
                session.Authority);
        }

        _activeProfile = profile;
        _activeProfileAccessor.SetProfile(profileId);
        return new ProfileSwitchOutcome(ProfileSwitchStatus.Succeeded, profile);
    }

    public Task<List<ProfileViewModel>> RefreshProfilesAsync(CancellationToken ct = default) =>
        GetProfilesAsync(ct, forceRefresh: true);

    public void UpsertProfile(ProfileViewModel profile)
    {
        if (_profilesLoaded)
        {
            var index = _profiles.FindIndex(candidate => candidate.Id == profile.Id);
            if (index >= 0)
            {
                _profiles[index] = profile;
            }
            else
            {
                _profiles.Add(profile);
            }
        }

        if (_activeProfile?.Id == profile.Id)
        {
            _activeProfile = profile;
        }
    }

    private async Task<ProfileViewModel?> ResolveActiveProfileAsync(
        IReadOnlyList<ProfileViewModel> profiles,
        CancellationToken ct)
    {
        if (profiles.Count == 0)
        {
            return null;
        }

        if (_authenticationStateProvider is not null)
        {
            var principal = (await _authenticationStateProvider.GetAuthenticationStateAsync()).User;
            // Loading presentation data must not overwrite live authority or a
            // profile switch with the retained cookie principal.
            _dashboardSession?.InitializeFromPrincipal(principal);
            var activeId = _dashboardSession is null
                ? ParseGuid(principal.FindFirstValue("tuvima:active_profile_id"))
                : _dashboardSession.CurrentSnapshot().ActiveProfileId;

            if (activeId is { } authenticatedActiveId)
            {
                return profiles.FirstOrDefault(profile => profile.Id == authenticatedActiveId);
            }
        }

        return null;
    }

    private async Task<bool> EnsureProfileAuthorityAsync(CancellationToken ct)
    {
        if (_dashboardSession is null)
        {
            return true;
        }

        if (_dashboardSession.Authority is not null)
        {
            return HasEnabledActiveGrant();
        }

        if (_authenticationStateProvider is null)
        {
            return false;
        }

        var principal = (await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        if (!_dashboardSession.InitializeFromPrincipal(principal))
        {
            return false;
        }

        // Some test constructions omit identity transport. Production registration
        // supplies it; without it, an unvalidated circuit fails closed.
        if (_identityClient is null)
        {
            return HasEnabledActiveGrant();
        }

        await _identityClient.EnsureInitialAuthorityAsync(_dashboardSession, ct).ConfigureAwait(false);
        return HasEnabledActiveGrant();
    }

    private bool HasEnabledActiveGrant()
    {
        if (_dashboardSession?.Authority is not { } authority)
        {
            return _dashboardSession is null;
        }

        var activeProfileId = _dashboardSession.ActiveProfileId;
        return authority.AccountEnabled
            && authority.GrantEnabled
            && activeProfileId is { } profileId
            && authority.ProfileGrants.Any(grant => grant.ProfileId == profileId && grant.IsEnabled);
    }

    private bool IsCurrentProfileRequest(DashboardSessionSnapshot? requestedSession, Guid? requestedProfileId)
    {
        if (requestedSession is { } snapshot && _dashboardSession is not null)
        {
            var current = _dashboardSession.CurrentSnapshot();
            var authority = _dashboardSession.Authority;
            if (authority is null
                || current.SessionToken != snapshot.SessionToken
                || current.AccountId != snapshot.AccountId
                || current.ActiveProfileId != snapshot.ActiveProfileId
                || current.SessionId != snapshot.SessionId)
            {
                return false;
            }

            var grants = authority.ProfileGrants;
            if (!authority.AccountEnabled
                || !authority.GrantEnabled
                || current.ActiveProfileId is not { } activeProfileId
                || !grants.Any(grant => grant.ProfileId == activeProfileId && grant.IsEnabled))
            {
                return false;
            }
        }

        var currentProfileId = _dashboardSession is not null
            ? _dashboardSession.CurrentSnapshot().ActiveProfileId
            : _activeProfileAccessor.ProfileId;
        return currentProfileId == requestedProfileId;
    }

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var parsed) ? parsed : null;

    public void Dispose() => _profilesGate.Dispose();
}
