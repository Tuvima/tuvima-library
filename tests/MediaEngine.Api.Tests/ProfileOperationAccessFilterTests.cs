using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

public sealed class ProfileOperationAccessFilterTests
{
    [Fact]
    public async Task HumanRequestPublishesActiveProfileForRepositoryKeys()
    {
        var profileId = Guid.NewGuid();
        var authority = HumanAuthority(profileId);
        var context = Context(authority, new StubEvaluator(AuthorizationDecision.Allow()));
        var nextCalled = false;

        var result = await new ProfileOperationAccessFilter(ApplicationPermissionIds.ProgressWrite)
            .InvokeAsync(
                new DefaultEndpointFilterInvocationContext(context, []),
                invocation =>
                {
                    nextCalled = true;
                    return ValueTask.FromResult<object?>(invocation.HttpContext.Items[
                        ProfileOperationAccessFilter.ActiveProfileItemKey]);
                });

        Assert.True(nextCalled);
        Assert.Equal(profileId, result);
    }

    [Fact]
    public async Task DelegatedRequestWithoutApplicationConsentIsDenied()
    {
        var authority = HumanAuthority(Guid.NewGuid()) with
        {
            PrincipalKind = PrincipalKind.DelegatedUserClient,
            ApplicationId = Guid.NewGuid(),
            ApplicationEnabled = true,
        };
        var evaluator = new StubEvaluator(AuthorizationDecision.Deny(
            AuthorizationDenialReason.MissingPermission));
        var context = Context(authority, evaluator);

        var result = await new ProfileOperationAccessFilter(ApplicationPermissionIds.ProgressWrite)
            .InvokeAsync(
                new DefaultEndpointFilterInvocationContext(context, []),
                _ => throw new InvalidOperationException("Denied requests must not reach the endpoint."));

        Assert.IsAssignableFrom<IResult>(result);
        Assert.Equal(ApplicationPermissionIds.ProgressWrite, evaluator.Requirement?.ApplicationPermission);
        Assert.True(evaluator.Requirement?.RequiresHumanContext);
        Assert.False(context.Items.ContainsKey(ProfileOperationAccessFilter.ActiveProfileItemKey));
    }

    [Theory]
    [InlineData(PrincipalKind.ServiceApplication)]
    [InlineData(PrincipalKind.DashboardTransport)]
    public async Task NonHumanPrincipalCannotReadOrWriteProfileState(PrincipalKind kind)
    {
        var authority = new RequestAuthority(
            kind,
            IsAuthenticated: true,
            ApplicationId: Guid.NewGuid(),
            ApplicationEnabled: true);
        var context = Context(authority, new StubEvaluator(AuthorizationDecision.Allow()));

        var result = await new ProfileOperationAccessFilter(ApplicationPermissionIds.ProgressRead)
            .InvokeAsync(
                new DefaultEndpointFilterInvocationContext(context, []),
                _ => throw new InvalidOperationException("Denied requests must not reach the endpoint."));

        Assert.IsAssignableFrom<IResult>(result);
        Assert.False(context.Items.ContainsKey(ProfileOperationAccessFilter.ActiveProfileItemKey));
    }

    private static RequestAuthority HumanAuthority(Guid profileId) =>
        new(
            PrincipalKind.Human,
            IsAuthenticated: true,
            AccountId: Guid.NewGuid(),
            ActiveProfileId: profileId,
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true);

    private static DefaultHttpContext Context(
        RequestAuthority authority,
        IAuthorizationEvaluator evaluator)
    {
        var services = new ServiceCollection()
            .AddSingleton<IRequestAuthorityResolver>(new StubAuthorityResolver(authority))
            .AddSingleton(evaluator)
            .BuildServiceProvider();
        return new DefaultHttpContext { RequestServices = services };
    }

    private sealed class StubAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(
            HttpContext context,
            CancellationToken ct = default) =>
            ValueTask.FromResult(authority);
    }

    private sealed class StubEvaluator(AuthorizationDecision decision) : IAuthorizationEvaluator
    {
        public AuthorizationRequirement? Requirement { get; private set; }

        public ValueTask<AuthorizationDecision> EvaluateAsync(
            RequestAuthority authority,
            AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default)
        {
            Requirement = requirement;
            return ValueTask.FromResult(decision);
        }
    }
}
