using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>
/// Reads and replaces what a household's Kid's profiles may see (library access D9, D10): a level for Watch, Read and Listen, and whether
/// unrated items are shown. A server administrator may read and replace any household's settings; a household administrator only their own
/// household's. Nobody else can: a Kid's profile or an ordinary member never changes levels. The settings are read live by every Kid's profile,
/// so a change applies to all of them on their next call. Throws <see cref="UnauthorizedAccessException"/>, <see cref="KeyNotFoundException"/>
/// and <see cref="ArgumentException"/> (an unknown level).
/// </summary>
public sealed class HouseholdKidSettingsService(
    IHouseholdKidSettingsRepository settings,
    IAccountAccessDecisionService accountDecisions,
    IAuthorizationEvaluator evaluator,
    IAuthorizationAuditWriter audit,
    TimeProvider clock)
{
    public async Task<HouseholdKidSettings> GetAsync(RequestAuthority actor, Guid householdId, CancellationToken ct = default)
    {
        await RequireAsync(actor, householdId, ApplicationPermissionIds.IdentityUsersRead, ct).ConfigureAwait(false);
        return await settings.GetAsync(householdId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Household not found.");
    }

    /// <summary>Replaces all four settings and returns what is now stored.</summary>
    public async Task<HouseholdKidSettings> ReplaceAsync(
        RequestAuthority actor, Guid householdId, HouseholdKidSettings requested, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        await RequireAsync(actor, householdId, ApplicationPermissionIds.IdentityUsersWrite, ct).ConfigureAwait(false);
        var normalized = new HouseholdKidSettings(
            Level(requested.WatchLevel, "Watch"), Level(requested.ReadLevel, "Read"), Level(requested.ListenLevel, "Listen"), requested.ShowUnrated);
        if (!await settings.ReplaceAsync(householdId, normalized, ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("Household not found.");
        }

        await audit.WriteAsync(new AuthorizationAuditEvent(
            "household.kid_settings_replaced",
            clock.GetUtcNow(),
            actor.AccountId,
            actor.ActiveProfileId,
            actor.ApplicationId,
            "household",
            householdId.ToString("D"),
            new Dictionary<string, string?>
            {
                ["watch_level"] = normalized.WatchLevel,
                ["read_level"] = normalized.ReadLevel,
                ["listen_level"] = normalized.ListenLevel,
                ["show_unrated"] = normalized.ShowUnrated ? "true" : "false",
            }), ct).ConfigureAwait(false);
        return normalized;
    }

    // A level is one of the ladder's choices; "Everything" is not a Kid level.
    private static string Level(string? value, string area) =>
        ProfileContentLimits.Normalize(value) ?? throw new ArgumentException($"Choose a {area} level: G, PG, PG-13 or R.");

    private async Task RequireAsync(RequestAuthority actor, Guid householdId, ApplicationPermissionId permission, CancellationToken ct)
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

        var application = await evaluator.EvaluateAsync(
            actor, new AuthorizationRequirement(ApplicationPermission: permission), null, ct).ConfigureAwait(false);
        if (!application.IsAllowed)
        {
            throw new UnauthorizedAccessException($"Application {permission.Value} permission is required.");
        }
    }
}
