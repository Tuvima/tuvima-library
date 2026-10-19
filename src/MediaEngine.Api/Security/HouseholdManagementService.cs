using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Security;

/// <summary>
/// Lists, adds, renames and removes households. Adding, renaming and removing belong to server administrators (and
/// applications trusted with identity.users.write). A household administrator can list, but only their own household.
/// Removing refuses while any person or sign-in still belongs to the household; nothing is moved or deleted for them.
/// Throws <see cref="UnauthorizedAccessException"/> (no authority), <see cref="KeyNotFoundException"/>,
/// <see cref="ArgumentException"/> (a bad name) or <see cref="InvalidOperationException"/> (refused by a rule).
/// </summary>
public sealed class HouseholdManagementService(
    IHouseholdRepository households,
    IAccountAccessDecisionService accountDecisions,
    IAuthorizationEvaluator evaluator,
    IAuthorizationAuditWriter audit,
    TimeProvider clock)
{
    public const int MaximumNameLength = 100;

    public const string NotEmptyMessage = "This household still has people or sign-ins, so it can't be removed. Nothing was changed.";
    public const string HoldsSharedLibraryMessage = "This household holds the Shared Library, so it can't be removed. Nothing was changed.";
    public const string HoldsSharedItemsMessage = "This household still has shared photos in View. Remove them first.";

    public async Task<IReadOnlyList<HouseholdSummary>> ListAsync(RequestAuthority actor, CancellationToken ct = default)
    {
        if (actor.PrincipalKind == PrincipalKind.Human)
        {
            if ((await accountDecisions.EvaluateAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed)
            {
                return await households.ListSummariesAsync(ct).ConfigureAwait(false);
            }

            // A household administrator sees their own household only.
            if (actor.AccountHouseholdId is { } own &&
                (await accountDecisions.EvaluateHouseholdAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed)
            {
                return await households.GetSummaryAsync(own, ct).ConfigureAwait(false) is { } summary ? [summary] : [];
            }

            throw new UnauthorizedAccessException("Unlocked administrator authority is required.");
        }

        await RequireApplicationAsync(actor, ApplicationPermissionIds.IdentityUsersRead, ct).ConfigureAwait(false);
        return await households.ListSummariesAsync(ct).ConfigureAwait(false);
    }

    public async Task<HouseholdSummary> CreateAsync(RequestAuthority actor, string name, CancellationToken ct = default)
    {
        await RequireServerWriteAsync(actor, ct).ConfigureAwait(false);
        var household = await households.CreateAsync(NormalizeName(name), clock.GetUtcNow(), ct).ConfigureAwait(false);
        await RecordAsync(actor, "household.created", household.Id, ct).ConfigureAwait(false);
        return new HouseholdSummary(household, 0, 0);
    }

    public async Task<HouseholdSummary> RenameAsync(RequestAuthority actor, Guid householdId, string name, CancellationToken ct = default)
    {
        await RequireServerWriteAsync(actor, ct).ConfigureAwait(false);
        if (!await households.RenameAsync(householdId, NormalizeName(name), ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("Household not found.");
        }

        await RecordAsync(actor, "household.renamed", householdId, ct).ConfigureAwait(false);
        return await households.GetSummaryAsync(householdId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Household not found.");
    }

    public async Task DeleteAsync(RequestAuthority actor, Guid householdId, CancellationToken ct = default)
    {
        await RequireServerWriteAsync(actor, ct).ConfigureAwait(false);
        switch (await households.DeleteWhenEmptyAsync(householdId, ct).ConfigureAwait(false))
        {
            case HouseholdDeleteOutcome.Deleted:
                await RecordAsync(actor, "household.deleted", householdId, ct).ConfigureAwait(false);
                return;
            case HouseholdDeleteOutcome.NotFound:
                throw new KeyNotFoundException("Household not found.");
            case HouseholdDeleteOutcome.NotEmpty:
                throw new InvalidOperationException(NotEmptyMessage);
            case HouseholdDeleteOutcome.HoldsSharedItems:
                throw new InvalidOperationException(HoldsSharedItemsMessage);
            default:
                throw new InvalidOperationException(HoldsSharedLibraryMessage);
        }
    }

    private async Task RequireServerWriteAsync(RequestAuthority actor, CancellationToken ct)
    {
        if (actor.PrincipalKind == PrincipalKind.Human)
        {
            // Server administrators only: a household administrator never creates, renames or removes a household.
            if ((await accountDecisions.EvaluateAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed)
            {
                return;
            }

            throw new UnauthorizedAccessException("Unlocked administrator authority is required.");
        }

        await RequireApplicationAsync(actor, ApplicationPermissionIds.IdentityUsersWrite, ct).ConfigureAwait(false);
    }

    private async Task RequireApplicationAsync(RequestAuthority actor, ApplicationPermissionId permission, CancellationToken ct)
    {
        var application = await evaluator.EvaluateAsync(
            actor, new AuthorizationRequirement(ApplicationPermission: permission), null, ct).ConfigureAwait(false);
        if (!application.IsAllowed)
        {
            throw new UnauthorizedAccessException($"Application {permission.Value} permission is required.");
        }
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A household needs a name.");
        }

        var trimmed = name.Trim();
        return trimmed.Length > MaximumNameLength
            ? throw new ArgumentException($"A household name must be {MaximumNameLength} characters or fewer.")
            : trimmed;
    }

    private ValueTask RecordAsync(RequestAuthority actor, string eventType, Guid householdId, CancellationToken ct) =>
        audit.WriteAsync(new AuthorizationAuditEvent(
            eventType,
            clock.GetUtcNow(),
            actor.AccountId,
            actor.ActiveProfileId,
            actor.ApplicationId,
            "household",
            householdId.ToString("D"),
            new Dictionary<string, string?> { ["changed"] = "true" }), ct);
}
