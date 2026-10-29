using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>
/// Decides whose Activity and Taste a person may see. Members and child profiles see only their own. A household
/// administrator also sees one profile of, or all of, their own household. A server administrator sees anyone.
/// An administrator who turned on their PIN must have unlocked it before seeing other people. When the answer includes
/// other people, one audit entry is written per acting profile and scope in each 30-minute window.
/// </summary>
public sealed class PersonalScopeAuthorizer(
    IAccountAccessDecisionService decisions,
    IProfileRepository profiles,
    IHouseholdRepository households,
    IAccountRepository accounts,
    IAuthorizationAuditWriter audit,
    TimeProvider clock) : IPersonalScopeAuthorizer
{
    public const string ActivityViewedOthers = "activity.viewed_others";
    public const string TasteViewedOthers = "taste.viewed_others";

    // One check-then-write at a time, so two requests opened together record the view once.
    private static readonly SemaphoreSlim AuditGate = new(1, 1);

    public async Task<PersonalScopeDecision> AuthorizeAsync(
        RequestAuthority authority,
        PersonalScopeKind kind,
        PersonalScope requested,
        CancellationToken ct = default)
    {
        if (AuthorityValidity.ValidateHuman(authority) is not null || authority.ActiveProfileId is not { } active)
        {
            return PersonalScopeDecision.Refuse(PersonalScopeRefusal.NotSignedIn);
        }

        // Asking for your own profile by id is the same as asking for Me.
        if (requested.Target == PersonalScopeTarget.Self ||
            (requested.Target == PersonalScopeTarget.Profile && requested.Id == active))
        {
            return PersonalScopeDecision.Allow(new HashSet<Guid> { active }, includesOthers: false);
        }

        var decision = await ResolveOthersAsync(authority, active, requested, ct).ConfigureAwait(false);
        if (decision.Allowed && decision.IncludesOthers)
        {
            await RecordViewAsync(authority, active, kind, requested, ct).ConfigureAwait(false);
        }

        return decision;
    }

    public async ValueTask<bool> CanDeletePersonalHistoryAsync(RequestAuthority authority, CancellationToken ct = default) =>
        authority.PrincipalKind == PrincipalKind.Human &&
        (await decisions.EvaluateAdministratorAsync(authority, true, ct).ConfigureAwait(false)).IsAllowed;

    // A Kids profile sees its own taste but never changes it.
    public bool CanEditTaste(RequestAuthority authority, Guid profileId) =>
        AuthorityValidity.ValidateHuman(authority) is null &&
        !authority.ActiveProfileIsRestricted &&
        authority.ActiveProfileId is { } active && active != Guid.Empty && active == profileId;

    private async Task<PersonalScopeDecision> ResolveOthersAsync(
        RequestAuthority authority, Guid active, PersonalScope requested, CancellationToken ct)
    {
        // A paired app acting for a person sees that person's own data only; wider views are for a signed-in person.
        if (authority.PrincipalKind != PrincipalKind.Human)
        {
            return PersonalScopeDecision.Refuse(PersonalScopeRefusal.NotAllowed);
        }

        if (authority.IsEffectiveAdministrator)
        {
            if (!(await decisions.EvaluateAdministratorAsync(authority, true, ct).ConfigureAwait(false)).IsAllowed)
            {
                return PersonalScopeDecision.Refuse(PersonalScopeRefusal.UnlockRequired);
            }

            return requested.Target switch
            {
                PersonalScopeTarget.Everyone => PersonalScopeDecision.AllowEveryone(),
                PersonalScopeTarget.Profile => await ResolveProfileAsync(
                    requested.Id, active, ownHouseholdOnly: null, PersonalScopeRefusal.UnknownTarget, ct).ConfigureAwait(false),
                PersonalScopeTarget.Household => await ResolveHouseholdAsync(
                    requested.Id, active, ownHouseholdOnly: null, PersonalScopeRefusal.UnknownTarget, ct).ConfigureAwait(false),
                _ => PersonalScopeDecision.Refuse(PersonalScopeRefusal.NotAllowed),
            };
        }

        if (authority.IsEffectiveHouseholdAdministrator && authority.ActiveProfileHouseholdId is { } own)
        {
            if (requested.Target == PersonalScopeTarget.Everyone)
            {
                return PersonalScopeDecision.Refuse(PersonalScopeRefusal.NotAllowed);
            }

            if (!(await decisions.EvaluateHouseholdAdministratorAsync(authority, true, ct).ConfigureAwait(false)).IsAllowed)
            {
                return PersonalScopeDecision.Refuse(PersonalScopeRefusal.UnlockRequired);
            }

            // Another household's profile and a profile that does not exist get the same answer, so ids cannot be probed.
            return requested.Target == PersonalScopeTarget.Profile
                ? await ResolveProfileAsync(requested.Id, active, own, PersonalScopeRefusal.NotAllowed, ct).ConfigureAwait(false)
                : await ResolveHouseholdAsync(requested.Id, active, own, PersonalScopeRefusal.NotAllowed, ct).ConfigureAwait(false);
        }

        return PersonalScopeDecision.Refuse(PersonalScopeRefusal.NotAllowed);
    }

    private async Task<PersonalScopeDecision> ResolveProfileAsync(
        Guid? profileId, Guid active, Guid? ownHouseholdOnly, PersonalScopeRefusal missing, CancellationToken ct)
    {
        var profile = profileId is { } id ? await profiles.GetByIdAsync(id, ct).ConfigureAwait(false) : null;
        if (profile is null || (ownHouseholdOnly is { } own && profile.HouseholdId != own))
        {
            return PersonalScopeDecision.Refuse(missing);
        }

        return PersonalScopeDecision.Allow(new HashSet<Guid> { profile.Id }, includesOthers: profile.Id != active);
    }

    private async Task<PersonalScopeDecision> ResolveHouseholdAsync(
        Guid? householdId, Guid active, Guid? ownHouseholdOnly, PersonalScopeRefusal missing, CancellationToken ct)
    {
        if (householdId is not { } id || (ownHouseholdOnly is { } own && id != own) ||
            await households.GetByIdAsync(id, ct).ConfigureAwait(false) is null)
        {
            return PersonalScopeDecision.Refuse(missing);
        }

        var ids = (await households.ListProfilesAsync(id, ct).ConfigureAwait(false)).Select(profile => profile.Id).ToHashSet();
        return PersonalScopeDecision.Allow(ids, includesOthers: ids.Any(profile => profile != active));
    }

    private async Task RecordViewAsync(
        RequestAuthority authority, Guid active, PersonalScopeKind kind, PersonalScope requested, CancellationToken ct)
    {
        var eventType = kind == PersonalScopeKind.Activity ? ActivityViewedOthers : TasteViewedOthers;
        var subjectType = requested.Target.ToString().ToLowerInvariant();
        var subjectId = requested.Id?.ToString("D") ?? "everyone";
        var now = clock.GetUtcNow();

        await AuditGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await accounts.HasAuthorizationAuditSinceAsync(
                    eventType, active, subjectType, subjectId, now - IPersonalScopeAuthorizer.AuditWindow, ct).ConfigureAwait(false))
            {
                return;
            }

            await audit.WriteAsync(new AuthorizationAuditEvent(
                eventType,
                now,
                authority.AccountId,
                active,
                authority.ApplicationId,
                subjectType,
                subjectId,
                new Dictionary<string, string?> { ["scope"] = subjectType }), ct).ConfigureAwait(false);
        }
        finally
        {
            AuditGate.Release();
        }
    }
}
