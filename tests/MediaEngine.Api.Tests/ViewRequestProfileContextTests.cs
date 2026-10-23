using MediaEngine.Api.Security;
using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

public sealed class ViewRequestProfileContextTests
{
    [Fact]
    public async Task RawBrowserProfileIdentifiersAreNeverTrusted()
    {
        var requested = Guid.NewGuid();
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString($"?profileId={requested}");
        http.Request.Headers["X-Tuvima-Profile"] = requested.ToString();
        var accessor = new HttpContextAccessor { HttpContext = http };
        var authority = new RequestAuthority(PrincipalKind.Human, true,
            Guid.NewGuid(), Guid.NewGuid(), AccountEnabled: true, ActiveProfileInUserHousehold: true);
        var context = new HttpViewRequestProfileContext(accessor, new StubResolver(authority));

        Assert.Equal(authority, await context.ResolveAuthorityAsync());
        Assert.NotEqual(requested, (await context.ResolveAuthorityAsync()).ActiveProfileId);
    }

    private sealed class StubResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context,
            CancellationToken ct = default) => ValueTask.FromResult(authority);
    }
}
