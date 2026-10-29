using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>
/// Reads and replaces the libraries hidden for a whole household (library access D2, LA1). Replacing belongs to server administrators (and
/// applications trusted with identity.users.write); a household administrator may read their own household's list so their screen can grey
/// what the household lacks. The list names only configured, catalogued, non-Vault libraries: a Vault id is refused exactly like an id that
/// names no library, whoever asks. Throws <see cref="UnauthorizedAccessException"/> (no authority), <see cref="KeyNotFoundException"/> and
/// <see cref="ArgumentException"/> (an unknown or Vault library).
/// </summary>
public sealed class HouseholdLibraryAccessService(
    IHouseholdRepository households,
    IHouseholdLibraryAccessRepository householdLibraries,
    IVaultLibraryRepository vaultLibraries,
    IConfigurationLoader configuration,
    IAccountAccessDecisionService accountDecisions,
    IAuthorizationEvaluator evaluator,
    IAuthorizationAuditWriter audit,
    TimeProvider clock)
{
    /// <summary>The libraries hidden for the household. A server administrator may read any household; a household administrator only their own.</summary>
    public async Task<IReadOnlySet<Guid>> GetAsync(RequestAuthority actor, Guid householdId, CancellationToken ct = default)
    {
        await RequireReadAsync(actor, householdId, ct).ConfigureAwait(false);
        if (await households.GetByIdAsync(householdId, ct).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException("Household not found.");
        }

        return await householdLibraries.GetHiddenLibrariesAsync(householdId, ct).ConfigureAwait(false);
    }

    /// <summary>Replaces the household's hidden libraries; server administrators only. Returns the list now stored.</summary>
    public async Task<IReadOnlySet<Guid>> ReplaceAsync(
        RequestAuthority actor, Guid householdId, IReadOnlySet<Guid> hiddenLibraries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hiddenLibraries);
        await RequireServerWriteAsync(actor, ct).ConfigureAwait(false);
        if (await households.GetByIdAsync(householdId, ct).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException("Household not found.");
        }

        await ValidateAsync(householdId, hiddenLibraries, ct).ConfigureAwait(false);
        if (!await householdLibraries.ReplaceHiddenLibrariesAsync(householdId, hiddenLibraries, clock.GetUtcNow(), actor.ActiveProfileId, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("Household not found.");
        }

        await audit.WriteAsync(new AuthorizationAuditEvent(
            "household.libraries_replaced",
            clock.GetUtcNow(),
            actor.AccountId,
            actor.ActiveProfileId,
            actor.ApplicationId,
            "household",
            householdId.ToString("D"),
            new Dictionary<string, string?> { ["hidden_count"] = hiddenLibraries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) }), ct)
            .ConfigureAwait(false);
        return await householdLibraries.GetHiddenLibrariesAsync(householdId, ct).ConfigureAwait(false);
    }

    // Every id must be a configured catalogued library that is not a Vault library. An id already stored for this household whose library
    // has since left the configuration is allowed to stay, so it can never block a save.
    private async Task ValidateAsync(Guid householdId, IReadOnlySet<Guid> hiddenLibraries, CancellationToken ct)
    {
        var vaultIds = await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false);
        var known = configuration.LoadLibraries().Libraries
            .Where(library => library.Kind == LibraryKinds.Catalogued && !library.IsVault && !vaultIds.Contains(library.Id))
            .Select(library => Guid.TryParse(library.Id, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var stored = await householdLibraries.GetHiddenLibrariesAsync(householdId, ct).ConfigureAwait(false);
        var refused = hiddenLibraries
            .Where(id => !known.Contains(id) && (id == Guid.Empty || !stored.Contains(id) || vaultIds.Contains(id.ToString("D"))))
            .Select(id => (Guid?)id)
            .FirstOrDefault();
        if (refused is { } unknown)
        {
            throw new ArgumentException($"Unknown catalogued library '{unknown:D}'.");
        }
    }

    private async Task RequireReadAsync(RequestAuthority actor, Guid householdId, CancellationToken ct)
    {
        if (actor.PrincipalKind == PrincipalKind.Human)
        {
            if ((await accountDecisions.EvaluateAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed)
            {
                return;
            }

            if (actor.ActiveProfileHouseholdId is { } own && own == householdId &&
                (await accountDecisions.EvaluateHouseholdAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed)
            {
                return;
            }

            throw new UnauthorizedAccessException("Unlocked administrator authority is required.");
        }

        await RequireApplicationAsync(actor, ApplicationPermissionIds.IdentityUsersRead, ct).ConfigureAwait(false);
    }

    private async Task RequireServerWriteAsync(RequestAuthority actor, CancellationToken ct)
    {
        if (actor.PrincipalKind == PrincipalKind.Human)
        {
            // Server administrators only: a household administrator never changes what their own household may use.
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
}
