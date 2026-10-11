using System.Net;
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

    [Theory]
    [InlineData("/watch", "/watch")]
    [InlineData("/\\evil.com", "/")]
    [InlineData("//evil.com", "/")]
    [InlineData("https://evil.com", "/")]
    [InlineData("/ok\r\nSet-Cookie: x=1", "/")]
    [InlineData("", "/")]
    public void ReturnUrl_RejectsBackslashesControlCharactersAndOtherSites(string value, string expected) =>
        Assert.Equal(expected, DashboardAuthenticationEndpoints.SafeReturnUrl(value));

    [Fact]
    public void SigningOut_LandsOnTheSignInPageThatDoesNotSignStraightBackIn()
    {
        Assert.Equal("/auth/login?signedOut=1", DashboardAuthenticationEndpoints.SignedOutRedirect);
        var source = File.ReadAllText(RepoPath("src/MediaEngine.Web/Services/Integration/DashboardAuthenticationEndpoints.cs"));
        Assert.Contains("Results.Redirect(SignedOutRedirect)", source, StringComparison.Ordinal);
        Assert.Contains("if (string.IsNullOrEmpty(signedOut))", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteVisitor_MakesNoEngineCall()
    {
        var handler = new CountingHandler(HttpStatusCode.OK);
        var identity = IdentityFor(handler, Visitor("203.0.113.9"));

        Assert.Null(await identity.GetThisComputerAccountNameAsync());
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ForwardedVisitor_IsMarkedForTheEngineWhichRefuses()
    {
        var handler = new CountingHandler(HttpStatusCode.NotFound);
        var identity = IdentityFor(handler, Visitor("127.0.0.1", ("CF-Connecting-IP", "198.51.100.7")));

        Assert.Null(await identity.GetThisComputerAccountNameAsync());
        Assert.Equal(1, handler.Calls);
        Assert.Equal("true", handler.LastForwardedHeader);
    }

    [Fact]
    public async Task UnreachableEngine_ShowsTheSignInPage()
    {
        var identity = IdentityFor(new CountingHandler(null), Visitor("127.0.0.1"));

        Assert.Null(await identity.GetThisComputerAccountNameAsync());
    }

    private static HttpContext Visitor(string address, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(address);
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        return context;
    }

    private static DashboardIdentityClient IdentityFor(CountingHandler handler, HttpContext context) =>
        new(new Factory(handler), new HttpContextAccessor { HttpContext = context },
            ingress: new MediaEngine.Web.Services.Configuration.IngressClassifier(null, null));

    private static string RepoPath(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(relative);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class CountingHandler(HttpStatusCode? status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastForwardedHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastForwardedHeader = request.Headers.TryGetValues(ClientIngressValues.ForwardedHeader, out var values) ? values.Single() : null;
            return status is { } code
                ? Task.FromResult(new HttpResponseMessage(code))
                : throw new HttpRequestException("Engine is down");
        }
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
