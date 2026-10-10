using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.View;

public enum ViewResourceKind
{
    Asset,
    Thumbnail,
    Original,
    Search,
    Gallery,
    Preference,
    Upload,
    Folder,
    FolderPolicy,
}

public enum ViewResourceAction
{
    Read,
    Contribute,
    Manage,
}

public enum ViewAccessOutcome
{
    Allowed,
    Unauthenticated,
    Forbidden,
    NotFound,
}

public sealed record ViewResourceDescriptor(
    ViewResourceKind Kind,
    Guid ResourceId,
    Guid? OwnerProfileId,
    Guid? LibraryId,
    IReadOnlySet<Guid>? SharedWithProfileIds = null,
    IReadOnlySet<Guid>? ContributingProfileIds = null,
    bool IsSharedLibraryAsset = false);

public sealed record ViewResourceRequest(
    ViewScopeRequest Scope,
    ViewResourceKind Kind,
    Guid? ResourceId,
    ViewResourceAction Action = ViewResourceAction.Read,
    bool AllowStaleSelectionFallback = false);

public sealed record ViewAccessDecision(
    ViewAccessOutcome Outcome,
    ResolvedViewScope? Scope = null)
{
    public bool IsAllowed => Outcome == ViewAccessOutcome.Allowed;

    public static ViewAccessDecision Allowed(ResolvedViewScope scope) =>
        new(ViewAccessOutcome.Allowed, scope);

    public static ViewAccessDecision Unauthenticated() =>
        new(ViewAccessOutcome.Unauthenticated);

    public static ViewAccessDecision Forbidden() =>
        new(ViewAccessOutcome.Forbidden);

    public static ViewAccessDecision NotFound(ResolvedViewScope? scope = null) =>
        new(ViewAccessOutcome.NotFound, scope);
}

/// <summary>
/// Single policy boundary for assets, derivatives, originals, search, and
/// Galleries. Missing or unauthorized profile, library, and resource identities
/// collapse to NotFound; caller capability failures are returned as Forbidden
/// before any private scope is resolved.
/// </summary>
public sealed class ViewResourceAuthorizationService(
    IViewScopeResolver scopeResolver,
    IViewResourceStore resourceStore,
    IAuthorizationEvaluator evaluator) : IViewResourceAuthorizationService
{
    public async Task<ViewAccessDecision> AuthorizeAsync(
        RequestAuthority caller,
        ViewResourceRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!caller.IsAuthenticated)
        {
            return ViewAccessDecision.Unauthenticated();
        }

        // Establish the account/Application capability before asking the scope store
        // to enumerate profiles, labels, or physical library identities.
        if (!await AuthorizePermissionAsync(caller, request, ct).ConfigureAwait(false))
        {
            return ViewAccessDecision.Forbidden();
        }

        var resolution = await scopeResolver.ResolveAsync(
            caller, request.Scope, request.AllowStaleSelectionFallback, ct).ConfigureAwait(false);
        if (resolution is null)
        {
            return ViewAccessDecision.NotFound();
        }

        if (request.Kind == ViewResourceKind.Search)
        {
            return request.Action == ViewResourceAction.Read
                ? ViewAccessDecision.Allowed(resolution.Scope)
                : ViewAccessDecision.NotFound(resolution.Scope);
        }

        if (request.ResourceId is not { } resourceId)
        {
            var galleryRequest = request.Kind == ViewResourceKind.Gallery
                && (request.Action == ViewResourceAction.Read
                    || request.Action == ViewResourceAction.Manage
                       && resolution.Scope.Kind == ViewScopeKind.Mine);
            var scopedMutation = (request.Kind is ViewResourceKind.Folder or ViewResourceKind.FolderPolicy
                    || (request.Kind is ViewResourceKind.Preference or ViewResourceKind.Upload
                        && resolution.Scope.Kind == ViewScopeKind.Mine))
                && (request.Action == ViewResourceAction.Manage
                    || request.Action == ViewResourceAction.Contribute);
            return galleryRequest || scopedMutation
                ? ViewAccessDecision.Allowed(resolution.Scope)
                : ViewAccessDecision.NotFound(resolution.Scope);
        }

        var resource = await resourceStore.FindAsync(
            request.Kind,
            resourceId,
            caller.ActiveProfileId ?? Guid.Empty,
            ct).ConfigureAwait(false);
        if (resource is null || resource.Kind != request.Kind)
        {
            return ViewAccessDecision.NotFound(resolution.Scope);
        }

        // Explicit shares are independent of Shared View access and inclusion.
        // For an asset/derivative, the store may populate SharedWithProfileIds
        // only after proving that asset belongs to a Gallery shared with the
        // caller. This grants the specific resource, never the owner's Space.
        var explicitlyShared = caller.ActiveProfileId is { } profileId
            && resource.SharedWithProfileIds?.Contains(profileId) == true;
        var mayContribute = caller.ActiveProfileId is { } contributorId
            && resource.ContributingProfileIds?.Contains(contributorId) == true;
        if (request.Action == ViewResourceAction.Read
            && resource.IsSharedLibraryAsset
            && resolution.Scope.Kind == ViewScopeKind.Shared
            && resource.LibraryId is { } sharedLibraryId
            && resolution.Scope.ContainsLibrary(sharedLibraryId))
        {
            return ViewAccessDecision.Allowed(resolution.Scope);
        }

        if ((request.Action == ViewResourceAction.Read
                && (explicitlyShared
                || (request.Kind == ViewResourceKind.Gallery
                    && resource.OwnerProfileId == caller.ActiveProfileId)))
            || (request.Kind == ViewResourceKind.Gallery
                && request.Action == ViewResourceAction.Contribute
                && mayContribute))
        {
            var exactScope = resource.LibraryId is { } grantedLibrary
                && !resolution.Scope.ContainsLibrary(grantedLibrary)
                    ? new ResolvedViewScope(
                        ViewScopeKind.Profile,
                        resource.OwnerProfileId,
                        new HashSet<Guid> { grantedLibrary })
                    : resolution.Scope;
            return ViewAccessDecision.Allowed(exactScope);
        }

        if (resource.LibraryId is not { } libraryId
            || !resolution.Scope.ContainsLibrary(libraryId))
        {
            return ViewAccessDecision.NotFound(resolution.Scope);
        }

        if (request.Action is ViewResourceAction.Contribute or ViewResourceAction.Manage)
        {
            var ownsResource = resource.OwnerProfileId == caller.ActiveProfileId;
            var isMine = resolution.Scope.Kind == ViewScopeKind.Mine;
            return ownsResource && isMine
                ? ViewAccessDecision.Allowed(resolution.Scope)
                : ViewAccessDecision.NotFound(resolution.Scope);
        }

        // A person reads (never edits) a household member's Galleries too; Galleries stay private to everyone
        // outside the Personal Space's household because the scope itself is only resolved inside the household.
        var householdGallery = caller.HasHumanContext
            && resolution.Scope.Kind == ViewScopeKind.Profile
            && resource.OwnerProfileId == resolution.Scope.ProfileId;
        return request.Kind != ViewResourceKind.Gallery || householdGallery
            ? ViewAccessDecision.Allowed(resolution.Scope)
            : ViewAccessDecision.NotFound(resolution.Scope);
    }

    /// <summary>
    /// Establishes the account/Application capability without resolving a scope.
    /// This permits an authorized profile's Personal Space to be provisioned before
    /// the scope resolver requires that space to exist.
    /// </summary>
    public async Task<ViewAccessOutcome> AuthorizePersonalScopeBootstrapAsync(
        RequestAuthority caller,
        CancellationToken ct = default)
    {
        if (!caller.IsAuthenticated)
        {
            return ViewAccessOutcome.Unauthenticated;
        }

        // Establish the account/Application capability before asking the scope store
        // to enumerate profiles, labels, or physical library identities.
        var request = new ViewResourceRequest(
            ViewScopeRequest.Mine, ViewResourceKind.Search, null);
        return await AuthorizePermissionAsync(caller, request, ct).ConfigureAwait(false)
            ? ViewAccessOutcome.Allowed
            : ViewAccessOutcome.Forbidden;
    }

    private async Task<bool> AuthorizePermissionAsync(
        RequestAuthority caller,
        ViewResourceRequest request,
        CancellationToken ct)
    {
        var requestedScope = request.Scope.Kind;
        var permission = request.Kind switch
        {
            ViewResourceKind.Original => ApplicationPermissionIds.ViewOriginalsRead,
            ViewResourceKind.Upload => ApplicationPermissionIds.ViewUpload,
            ViewResourceKind.Gallery when request.Action != ViewResourceAction.Read => ApplicationPermissionIds.ViewGalleriesWrite,
            ViewResourceKind.Gallery => ApplicationPermissionIds.ViewGalleriesRead,
            _ when requestedScope == ViewScopeKind.Shared => ApplicationPermissionIds.ViewSharedRead,
            _ => ApplicationPermissionIds.ViewPersonalRead,
        };

        if (request.Action != ViewResourceAction.Read && requestedScope == ViewScopeKind.Profile
            && request.Kind != ViewResourceKind.Folder)
        {
            return false;
        }

        if (caller.HasApplicationContext && request.Action != ViewResourceAction.Read
            && request.Kind is ViewResourceKind.Preference or ViewResourceKind.Folder or ViewResourceKind.FolderPolicy)
        {
            return false;
        }

        var protectedSharedFolderPolicy = request.Kind == ViewResourceKind.FolderPolicy
            && request.Action != ViewResourceAction.Read
            && requestedScope == ViewScopeKind.Shared;

        AuthorizationRequirement requirement;
        if (caller.PrincipalKind == PrincipalKind.ServiceApplication)
        {
            if (requestedScope != ViewScopeKind.Shared
                && (!caller.IsAdministratorApplication || requestedScope != ViewScopeKind.Profile
                    || request.Action != ViewResourceAction.Read))
            {
                return false;
            }

            requirement = new(ApplicationPermission: permission);
        }
        else
        {
            if (!caller.HasHumanContext)
            {
                return false;
            }

            requirement = caller.PrincipalKind == PrincipalKind.DelegatedUserClient
                ? new(permission, AccountFeatureId.View, RequiresHumanContext: true,
                    RequiresAdministrator: protectedSharedFolderPolicy,
                    RequiresAdministratorSurfaceUnlock: protectedSharedFolderPolicy)
                : new(AccountFeature: AccountFeatureId.View, RequiresHumanContext: true,
                    RequiresAdministrator: protectedSharedFolderPolicy,
                    RequiresAdministratorSurfaceUnlock: protectedSharedFolderPolicy);
        }

        var resource = new ResourceAuthorizationContext(
            caller.IsAdministratorApplication && requestedScope == ViewScopeKind.Profile
                ? "view-profile-admin-read"
                : requestedScope == ViewScopeKind.Shared ? "view-shared" : "view-profile",
            request.Scope.ProfileId?.ToString("D") ??
                (requestedScope == ViewScopeKind.Mine ? caller.ActiveProfileId?.ToString("D") : null) ?? "shared",
            OwnerProfileId: request.Scope.ProfileId ??
                (requestedScope == ViewScopeKind.Mine ? caller.ActiveProfileId : null),
            IsPrivate: requestedScope != ViewScopeKind.Shared);
        var decision = await evaluator.EvaluateAsync(caller, requirement, resource, ct).ConfigureAwait(false);
        return decision.IsAllowed;
    }

}
