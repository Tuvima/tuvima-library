using System.Security.Claims;
using Bunit;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Ensures native UI and application services that implement only
/// <see cref="IAsyncDisposable"/> are released through bUnit's asynchronous path.
/// </summary>
public abstract class AsyncBunitContext : LocalizedBunitContext, IAsyncLifetime
{
    protected AsyncBunitContext()
    {
        // WaitForAssertion and WaitForElement return the moment their condition holds, so this is only the point at which a
        // hung component is reported as one. bUnit's one-second default instead reports a slow one: the first render of a
        // large page in a test process takes several seconds (Settings: 7 seconds measured, 0.1 for the next one)
        // and longer while the rest of the suite runs alongside. Waits that outlast this are genuine hangs.
        DefaultWaitTimeout = TimeSpan.FromSeconds(30);
        Services.AddNativeUiServices();
        Services.AddSingleton<AuthenticationStateProvider>(new AuthenticatedTestStateProvider());
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    private sealed class AuthenticatedTestStateProvider : AuthenticationStateProvider
    {
        private static readonly AuthenticationState State = new(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "Owner"),
            new Claim(ClaimTypes.Role, "Administrator"),
            new Claim("tuvima:profile_id", "00000000-0000-0000-0000-000000000001"),
            new Claim("tuvima:active_profile_id", "00000000-0000-0000-0000-000000000001"),
            new Claim("tuvima:session_id", "00000000-0000-0000-0000-000000000002"),
            new Claim(DashboardEngineAuthenticationHandler.SessionTokenClaim, "test-session-token"),
        ], "Test")));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(State);
    }
}
