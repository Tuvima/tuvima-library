using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Tests;

public sealed class DetailActionAuthorizationPolicyTests
{
    [Fact]
    public async Task HumanMetadataActionRequiresEffectiveUnlockedAdministrator()
    {
        var authority = HumanAuthority();

        var allowed = await DetailActionAuthorizationPolicy.ResolveAsync(
            authority, new AccountDecisions(AuthorizationDecision.Allow()), new AppEvaluator(false), default);
        var denied = await DetailActionAuthorizationPolicy.ResolveAsync(
            authority,
            new AccountDecisions(AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired)),
            new AppEvaluator(true),
            default);

        Assert.True(allowed.Allows("edit"));
        Assert.False(denied.Allows("edit"));
    }

    [Fact]
    public async Task ServiceApplicationRequiresMetadataWritePermission()
    {
        var authority = new RequestAuthority(
            PrincipalKind.ServiceApplication,
            true,
            ApplicationId: Guid.NewGuid(),
            ApplicationEnabled: true);
        var evaluator = new AppEvaluator(true);

        var allowed = await DetailActionAuthorizationPolicy.ResolveAsync(
            authority, new AccountDecisions(AuthorizationDecision.Allow()), evaluator, default);

        Assert.True(allowed.Allows("edit"));
        Assert.Equal(ApplicationPermissionIds.MetadataWrite, evaluator.Permission);
    }

    [Fact]
    public async Task DelegatedClientRequiresApplicationConsentAndHumanAdministratorIntersection()
    {
        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient,
            true,
            AccountId: Guid.NewGuid(),
            ActiveProfileId: Guid.NewGuid(),
            ApplicationId: Guid.NewGuid(),
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true,
            ApplicationEnabled: true);

        var missingConsent = await DetailActionAuthorizationPolicy.ResolveAsync(
            authority,
            new AccountDecisions(AuthorizationDecision.Allow()),
            new AppEvaluator(false),
            default);
        var ordinaryHuman = await DetailActionAuthorizationPolicy.ResolveAsync(
            authority,
            new AccountDecisions(AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired)),
            new AppEvaluator(true),
            default);
        var allowed = await DetailActionAuthorizationPolicy.ResolveAsync(
            authority,
            new AccountDecisions(AuthorizationDecision.Allow()),
            new AppEvaluator(true),
            default);

        Assert.False(missingConsent.Allows("edit"));
        Assert.False(ordinaryHuman.Allows("edit"));
        Assert.True(allowed.Allows("edit"));
    }

    [Fact]
    public async Task UnknownPrincipalAndUnknownActionFailClosed()
    {
        var result = await DetailActionAuthorizationPolicy.ResolveAsync(
            new RequestAuthority(PrincipalKind.DashboardTransport, true),
            new AccountDecisions(AuthorizationDecision.Allow()),
            new AppEvaluator(true),
            default);

        Assert.False(result.Allows("edit"));
        Assert.False(result.Allows("delete-library-item"));
    }

    [Theory]
    [InlineData("unmatched-retry")]
    [InlineData("unmatched-keep")]
    [InlineData("unmatched-look-up-again")]
    public async Task UnmatchedActionsNeedTheSameAdministratorCheckAsEditing(string key)
    {
        var administrator = await DetailActionAuthorizationPolicy.ResolveAsync(
            HumanAuthority(), new AccountDecisions(AuthorizationDecision.Allow()), new AppEvaluator(false), default);
        var ordinary = await DetailActionAuthorizationPolicy.ResolveAsync(
            HumanAuthority(),
            new AccountDecisions(AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired)),
            new AppEvaluator(true),
            default);

        Assert.True(administrator.Allows(key));
        Assert.False(ordinary.Allows(key));
        Assert.False(default(DetailActionAuthorizationContext).Allows(key));
    }

    [Theory]
    [InlineData("unmatched-retry")]
    [InlineData("unmatched-keep")]
    [InlineData("unmatched-look-up-again")]
    public async Task UnmatchedActionsAreRefusedToAServiceApplicationWithMetadataWrite_AndToADelegatedClient(string key)
    {
        var service = await DetailActionAuthorizationPolicy.ResolveAsync(
            new RequestAuthority(PrincipalKind.ServiceApplication, true, ApplicationId: Guid.NewGuid(), ApplicationEnabled: true),
            new AccountDecisions(AuthorizationDecision.Allow()), new AppEvaluator(true), default);
        var delegated = await DetailActionAuthorizationPolicy.ResolveAsync(
            new RequestAuthority(
                PrincipalKind.DelegatedUserClient, true, AccountId: Guid.NewGuid(), ActiveProfileId: Guid.NewGuid(),
                ApplicationId: Guid.NewGuid(), AccountEnabled: true, ActiveProfileInUserHousehold: true, ApplicationEnabled: true),
            new AccountDecisions(AuthorizationDecision.Allow()), new AppEvaluator(true), default);

        Assert.True(service.Allows("edit"));
        Assert.False(service.Allows(key));
        Assert.False(delegated.Allows(key));
    }

    private static RequestAuthority HumanAuthority() => new(
        PrincipalKind.Human,
        true,
        AccountId: Guid.NewGuid(),
        ActiveProfileId: Guid.NewGuid(),
        AccountEnabled: true,
        ActiveProfileInUserHousehold: true);

    private sealed class AccountDecisions(AuthorizationDecision administrator) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Deny(AuthorizationDenialReason.MissingFeatureGrant));
        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid libraryId, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Deny(AuthorizationDenialReason.LibraryNotVisible));
        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken cancellationToken = default) => ValueTask.FromResult(administrator);
    }

    private sealed class AppEvaluator(bool allowed) : IAuthorizationEvaluator
    {
        public ApplicationPermissionId? Permission { get; private set; }

        public ValueTask<AuthorizationDecision> EvaluateAsync(
            RequestAuthority authority,
            AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default)
        {
            Permission = requirement.ApplicationPermission;
            return ValueTask.FromResult(allowed
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.MissingPermission));
        }
    }
}
