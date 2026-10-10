using System.Security.Claims;
using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Leaving a page, or moving to another item, must cancel the Engine calls the old view still has in flight.</summary>
public sealed class StaleFetchCancellationTests : AsyncBunitContext
{
    private const int DetailTokenIndex = 6;
    private const int RecentTokenIndex = 3;

    private readonly Guid _profile = Guid.NewGuid();
    private readonly List<CancellationToken> _detailTokens = [];
    private readonly List<CancellationToken> _recentTokens = [];

    public StaleFetchCancellationTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton<AuthenticationStateProvider>(new ProfileAuth(_profile));
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(_profile, "One", "#aaa", "RestrictedProfile", DateTimeOffset.UtcNow),
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), args =>
            {
                _detailTokens.Add((CancellationToken)args![DetailTokenIndex]!);
                return new TaskCompletionSource<DetailPageViewModel?>().Task;
            });
            stub.SetHandler(nameof(IEngineApiClient.GetDisplayRecentAsync), args =>
            {
                _recentTokens.Add((CancellationToken)args![RecentTokenIndex]!);
                return new TaskCompletionSource<DisplayRecentPageDto?>().Task;
            });
        }));
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton(new ViewMediaGrantService(new byte[32], TimeSpan.FromMinutes(1)));
        Services.AddScoped<RecentViewAssetAdapter>();
        Services.AddScoped<UserProgressChangeNotifier>();
    }

    [Fact]
    public void ChangingRouteParametersCancelsThePreviousDetailLoad()
    {
        var first = DetailRouteRequest.ForUnified("musicalbum", Guid.NewGuid(), null);
        var second = DetailRouteRequest.ForUnified("musicalbum", Guid.NewGuid(), null);
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, first));
        cut.WaitForAssertion(() => Assert.Single(_detailTokens));
        Assert.False(_detailTokens[0].IsCancellationRequested);

        cut.Render(p => p.Add(c => c.Request, second));
        cut.WaitForAssertion(() => Assert.Equal(2, _detailTokens.Count));

        Assert.True(_detailTokens[0].IsCancellationRequested);
        Assert.False(_detailTokens[1].IsCancellationRequested);
    }

    [Fact]
    public void DisposingTheDetailHostCancelsTheLoadInFlight()
    {
        var request = DetailRouteRequest.ForUnified("musicalbum", Guid.NewGuid(), null);
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, request));
        cut.WaitForAssertion(() => Assert.Single(_detailTokens));

        cut.Dispose();

        Assert.True(_detailTokens[0].IsCancellationRequested);
    }

    [Fact]
    public void DisposingTheRecentFeedCancelsTheLoadInFlight()
    {
        var cut = Render<RecentMediaFeed>();
        cut.WaitForAssertion(() => Assert.Single(_recentTokens));
        Assert.False(_recentTokens[0].IsCancellationRequested);

        cut.Dispose();

        Assert.True(_recentTokens[0].IsCancellationRequested);
    }

    private sealed class ProfileAuth(Guid profile) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim("tuvima:active_profile_id", profile.ToString("D"))], "test"))));
    }
}
