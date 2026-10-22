using System.Security.Claims;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

public sealed class ProfileAndSavedItemServiceTests : AsyncBunitContext
{
    private static readonly Guid ProfileId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public ProfileAndSavedItemServiceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task ActiveProfileSession_ConcurrentConsumersShareOneProfileRequest()
    {
        var profileRequests = 0;
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<List<ProfileViewModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
            {
                Interlocked.Increment(ref profileRequests);
                requestStarted.TrySetResult();
                return response.Task;
            }));
        var activeProfile = new ActiveProfileAccessor();
        var session = new ActiveProfileSessionService(
            Services.GetRequiredService<IJSRuntime>(),
            api,
            activeProfile,
            new ProfileAuthenticationStateProvider(ProfileId));

        var pendingProfiles = Enumerable.Range(0, 32)
            .Select(_ => session.GetActiveProfileAsync())
            .ToList();

        await requestStarted.Task;
        Assert.Equal(1, profileRequests);

        response.SetResult([CreateProfile()]);
        var profiles = await Task.WhenAll(pendingProfiles);

        Assert.Equal(1, profileRequests);
        Assert.All(profiles, profile => Assert.Equal(ProfileId, profile?.Id));
        Assert.Equal(ProfileId, activeProfile.ProfileId);
    }

    [Fact]
    public async Task LoadingProfiles_PreservesValidatedAuthorityAndCurrentProfileOverRetainedCookie()
    {
        var retainedProfile = Guid.NewGuid();
        var account = Guid.NewGuid();
        var authority = new DashboardAuthorityResponse(account, ProfileId, true, true, 1, 1,
            true, true, null, 1,
            [new AccountProfileGrantDto(Guid.NewGuid(), ProfileId, "Current", null, true, true, true,
                new GrantAdminProtectionDto(false, "UntilProfileSwitch", 30, 1), 1, DateTimeOffset.UtcNow)],
            ["settings.administration"], ["access.manage"]);
        var dashboard = new DashboardSessionAccessor();
        dashboard.Set("current-session", account, ProfileId, Guid.NewGuid(), authority);
        var snapshot = dashboard.CurrentSnapshot();
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync),
            _ => Task.FromResult(new List<ProfileViewModel> { CreateProfile() })));
        using var profiles = new ActiveProfileSessionService(Services.GetRequiredService<IJSRuntime>(), api,
            authenticationStateProvider: new ProfileAuthenticationStateProvider(retainedProfile),
            dashboardSession: dashboard);

        var active = await profiles.GetActiveProfileAsync();
        await profiles.RefreshProfilesAsync();

        Assert.Equal(ProfileId, active?.Id);
        Assert.Equal(snapshot, dashboard.CurrentSnapshot());
        Assert.Same(authority, dashboard.Authority);
        Assert.True(dashboard.HasNavigation("settings.administration"));
        Assert.True(dashboard.HasAction("access.manage"));
    }

    [Fact]
    public async Task SavedItemToggle_UsesProfileStateAndNeverCreatesACollection()
    {
        var saved = false;
        var createCollectionRequests = 0;
        var saveRequests = 0;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetSavedItemAsync), _ => Task.FromResult<ProfileSavedItemDto?>(saved
                ? new(ProfileEntityKind.Book, Guid.Empty, DateTimeOffset.UtcNow, null)
                : null));
            stub.SetHandler(nameof(IEngineApiClient.SaveItemAsync), _ =>
            {
                saved = true;
                Interlocked.Increment(ref saveRequests);
                return Task.FromResult<ProfileStateMutationDto?>(new(
                    ProfileEntityKind.Book, Guid.Empty, true, null, DateTimeOffset.UtcNow));
            });
            stub.SetHandler(nameof(IEngineApiClient.CreateCollectionAsync), _ =>
            {
                Interlocked.Increment(ref createCollectionRequests);
                return Task.FromResult(true);
            });
        });
        var savedItems = new SavedItemService(api);

        var membership = await savedItems.ToggleAsync(ProfileEntityKind.Book, Guid.NewGuid());

        Assert.NotNull(membership);
        Assert.True(membership.IsSaved);
        Assert.Equal(1, saveRequests);
        Assert.Equal(0, createCollectionRequests);
    }

    [Fact]
    public async Task SavedItemList_ReturnsFirstClassProfileState()
    {
        var createCollectionRequests = 0;
        var entityId = Guid.NewGuid();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetSavedItemsAsync), _ =>
                Task.FromResult<IReadOnlyList<ProfileSavedItemDto>>(
                    [new(ProfileEntityKind.Movie, entityId, DateTimeOffset.UtcNow, null)]));
            stub.SetHandler(nameof(IEngineApiClient.CreateCollectionAsync), _ =>
            {
                Interlocked.Increment(ref createCollectionRequests);
                return Task.FromResult(true);
            });
        });
        var savedItems = new SavedItemService(api);

        var list = await savedItems.GetListAsync();

        Assert.Collection(list, item => Assert.Equal(entityId, item.EntityId));
        Assert.Equal(0, createCollectionRequests);
    }

    private sealed class ProfileAuthenticationStateProvider(Guid profileId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("tuvima:active_profile_id", profileId.ToString()),
                new Claim(DashboardEngineAuthenticationHandler.SessionTokenClaim, "retained-session")
            ], "test"))));
    }

    private static ProfileViewModel CreateProfile() => new(
        ProfileId,
        "Test User",
        "#C9922E",
        "Administrator",
        DateTimeOffset.UtcNow);
}
