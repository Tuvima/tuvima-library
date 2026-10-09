using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.View;

/// <summary>
/// Resolves friendly View scopes without exposing physical library identifiers.
/// </summary>
public sealed class ViewScopeResolver(IViewScopeStore store) : IViewScopeResolver
{
    public async Task<ViewScopeResolution?> ResolveAsync(
        RequestAuthority caller,
        ViewScopeRequest requested,
        bool allowStaleSelectionFallback = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(requested);

        if (!caller.IsAuthenticated)
        {
            return null;
        }

        var enabledProfiles = (await store.GetProfilesAsync(ct).ConfigureAwait(false)).Where(IsUsable).ToList();
        var callerState = caller.ActiveProfileId is { } activeId
            ? enabledProfiles.FirstOrDefault(profile => profile.Policy.ProfileId == activeId)
            : null;
        if (caller.HasHumanContext && callerState is null)
        {
            return null;
        }

        if (!caller.HasHumanContext && caller.PrincipalKind != PrincipalKind.ServiceApplication)
        {
            return null;
        }

        var options = await BuildOptionsAsync(caller, callerState, enabledProfiles, ct).ConfigureAwait(false);
        var resolved = requested.Kind switch
        {
            ViewScopeKind.Mine when callerState is not null => Personal(ViewScopeKind.Mine, callerState),
            ViewScopeKind.Shared when callerState?.Policy.AccessSharedLibrary == true
                || caller.PrincipalKind == PrincipalKind.ServiceApplication =>
                await SharedAsync(callerState?.Policy.ProfileId, ct).ConfigureAwait(false),
            ViewScopeKind.Profile when requested.ProfileId is { } targetId =>
                ResolveProfile(caller, callerState, enabledProfiles, targetId),
            _ => null,
        };
        if (resolved is not null)
        {
            return new ViewScopeResolution(resolved, options);
        }

        if (!allowStaleSelectionFallback || callerState is null)
        {
            return null;
        }

        return new ViewScopeResolution(Personal(ViewScopeKind.Mine, callerState, fellBack: true), options);
    }

    private static bool IsUsable(ViewScopeStoreEntry? profile) =>
        profile is { Policy.ViewEnabled: true, PersonalSpace: not null };

    private async Task<ResolvedViewScope?> SharedAsync(Guid? callerProfileId, CancellationToken ct)
    {
        var libraryId = await store.GetSharedLibraryIdAsync(callerProfileId, ct).ConfigureAwait(false);
        return libraryId is { } id && id != Guid.Empty
            ? new(ViewScopeKind.Shared, null, new HashSet<Guid> { id })
            : null;
    }

    private static ResolvedViewScope? ResolveProfile(RequestAuthority caller, ViewScopeStoreEntry? active,
        IReadOnlyList<ViewScopeStoreEntry> profiles, Guid targetId)
    {
        if (active?.Policy.ProfileId == targetId)
        {
            return Personal(ViewScopeKind.Mine, active);
        }

        if (!caller.IsEffectiveAdministrator && !caller.IsAdministratorApplication)
        {
            return null;
        }

        var target = profiles.FirstOrDefault(profile => profile.Policy.ProfileId == targetId);
        return target is null ? null : Personal(ViewScopeKind.Profile, target);
    }

    private async Task<IReadOnlyList<ViewScopeOption>> BuildOptionsAsync(RequestAuthority caller,
        ViewScopeStoreEntry? active, IReadOnlyList<ViewScopeStoreEntry> profiles, CancellationToken ct)
    {
        var result = new List<ViewScopeOption>();
        if (active is not null)
        {
            result.Add(Option(ViewScopeKind.Mine, active));
        }

        if (caller.IsEffectiveAdministrator)
        {
            result.AddRange(profiles.Where(profile => profile.Policy.ProfileId != active?.Policy.ProfileId)
                .Select(profile => Option(ViewScopeKind.Profile, profile)));
        }

        if (await store.GetSharedLibraryIdAsync(active?.Policy.ProfileId, ct).ConfigureAwait(false) is not null
            && (active?.Policy.AccessSharedLibrary == true || caller.HasApplicationContext))
        {
            result.Add(new ViewScopeOption(ViewScopeKind.Shared, null, "Shared Library"));
        }

        return result;
    }

    private static ResolvedViewScope Personal(ViewScopeKind kind, ViewScopeStoreEntry profile, bool fellBack = false) =>
        new(kind, profile.Policy.ProfileId, new HashSet<Guid> { profile.PersonalSpace!.LibraryId }, fellBack);

    private static ViewScopeOption Option(ViewScopeKind kind, ViewScopeStoreEntry profile) =>
        new(kind, profile.Policy.ProfileId,
            string.IsNullOrWhiteSpace(profile.DisplayName) ? "Profile" : profile.DisplayName,
            profile.AvatarColor, profile.AvatarUrl);
}
