using System.Security.Claims;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class StartWithoutPasswordTests
{
    [Fact]
    public async Task NoPasswordAtHome_GoesStraightToHome()
    {
        var run = await RunAsync(accountName: "Sam", session: Session(chooseProfile: false));

        Assert.Equal("/watch", run.Redirect);
        Assert.Equal(1, run.SignedIn);
        Assert.Equal(1, run.SignInCalls);
    }

    [Fact]
    public async Task NoPasswordAtHome_WithSeveralProfiles_GoesToThePicker()
    {
        var run = await RunAsync(accountName: "Sam", session: Session(chooseProfile: true));

        Assert.Equal(ProfilePickerRoute.For("/watch"), run.Redirect);
        Assert.Equal(1, run.SignedIn);
    }

    [Fact]
    public async Task Remote_OrPasswordSet_ShowsTheSignInPage()
    {
        // The Engine only names the account to a local visitor while no password exists, so null covers both.
        var run = await RunAsync(accountName: null, session: Session(chooseProfile: false));

        Assert.Null(run.Result);
        Assert.Equal(0, run.SignInCalls);
        Assert.Equal(0, run.SignedIn);
    }

    [Fact]
    public async Task EngineRefusesTheSignIn_ShowsTheSignInPage()
    {
        var run = await RunAsync(accountName: "Sam", session: null);

        Assert.Null(run.Result);
        Assert.Equal(1, run.SignInCalls);
        Assert.Equal(0, run.SignedIn);
    }

    private static AuthSessionResponse Session(bool chooseProfile) => new()
    {
        SessionToken = "token",
        AccountId = Guid.NewGuid(),
        ActiveProfileId = Guid.NewGuid(),
        DisplayName = "Sam",
        Authority = new DashboardAuthorityResponse(
            Guid.NewGuid(), Guid.NewGuid(), true, true, 1, 1, true, false, null, 1, [], [], []),
        AuthenticationMethod = "ThisComputer",
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        ChooseProfile = chooseProfile,
    };

    private static async Task<Run> RunAsync(string? accountName, AuthSessionResponse? session)
    {
        var auth = new CountingAuthentication();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuthenticationService>(auth);
        services.AddSingleton(new MediaEngine.Web.Services.Configuration.IngressClassifier(null, null));
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        var calls = 0;

        var result = await DashboardAuthenticationEndpoints.StartWithoutPasswordAsync(
            context, "device", "/watch",
            _ => Task.FromResult(accountName),
            (_, _) =>
            {
                calls++;
                return Task.FromResult(session);
            });

        string? redirect = null;
        if (result is not null)
        {
            await result.ExecuteAsync(context);
            redirect = context.Response.Headers.Location.ToString();
        }

        return new Run(result, redirect, auth.SignedIn, calls);
    }

    private sealed record Run(IResult? Result, string? Redirect, int SignedIn, int SignInCalls);

    private sealed class CountingAuthentication : IAuthenticationService
    {
        public int SignedIn { get; private set; }

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            SignedIn++;
            return Task.CompletedTask;
        }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
