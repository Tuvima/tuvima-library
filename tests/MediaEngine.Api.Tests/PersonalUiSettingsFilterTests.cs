using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

public sealed class PersonalUiSettingsFilterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherProfileIsRejectedBeforeConfigurationAccess(bool query)
    {
        var active = Guid.NewGuid();
        var authority = Human(active);
        using var services = Services(authority);
        var http = new DefaultHttpContext { RequestServices = services };
        if (query)
        {
            http.Request.QueryString = new QueryString($"?profile={Guid.NewGuid():D}");
        }
        else
        {
            http.Request.RouteValues["profileId"] = Guid.NewGuid().ToString("D");
        }

        var result = await Invoke(http, _ => throw new InvalidOperationException("Private configuration must not be accessed."));
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("")]
    [InlineData("owner")]
    public async Task NonGuidStorageKeysAreRejected(string requested)
    {
        using var services = Services(Human(Guid.NewGuid()));
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.RouteValues["profileId"] = requested;
        var result = await Invoke(http, _ => throw new InvalidOperationException("Invalid storage key reached configuration."));
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task ActiveProfileAndImplicitCurrentProfileAreAllowed()
    {
        var active = Guid.NewGuid();
        using var services = Services(Human(active));
        var http = new DefaultHttpContext { RequestServices = services };
        Assert.Equal("allowed", await Invoke(http, _ => ValueTask.FromResult<object?>("allowed")));
        http.Request.RouteValues["profileId"] = active.ToString("D").ToUpperInvariant();
        Assert.Equal("allowed", await Invoke(http, _ => ValueTask.FromResult<object?>("allowed")));
    }

    [Theory]
    [InlineData(PrincipalKind.Human, false)]
    [InlineData(PrincipalKind.ServiceApplication, true)]
    [InlineData(PrincipalKind.DelegatedUserClient, true)]
    public async Task RevokedGrantOrNonHumanCannotAccessPreferences(PrincipalKind kind, bool grantEnabled)
    {
        using var services = Services(Human(Guid.NewGuid()) with { PrincipalKind = kind, ActiveProfileInUserHousehold = grantEnabled });
        var http = new DefaultHttpContext { RequestServices = services };
        var result = await Invoke(http, _ => throw new InvalidOperationException("Unusable identity reached preferences."));
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    private static ValueTask<object?> Invoke(HttpContext http, EndpointFilterDelegate next) =>
        new PersonalUiSettingsFilter().InvokeAsync(new DefaultEndpointFilterInvocationContext(http, []), next);

    private static RequestAuthority Human(Guid profile) => new(PrincipalKind.Human, true,
        AccountId: Guid.NewGuid(), ActiveProfileId: profile, AccountEnabled: true, ActiveProfileInUserHousehold: true);

    private static ServiceProvider Services(RequestAuthority authority) => new ServiceCollection()
        .AddSingleton<IRequestAuthorityResolver>(new Resolver(authority)).BuildServiceProvider();

    private sealed class Resolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext http, CancellationToken ct = default) => ValueTask.FromResult(authority);
    }
}
