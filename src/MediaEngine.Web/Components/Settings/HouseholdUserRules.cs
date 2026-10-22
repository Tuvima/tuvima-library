using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Components.Settings;

/// <summary>What a household owner is offered to do with the other users of their household.</summary>
public static class HouseholdUserRules
{
    /// <summary>
    /// Whether a household owner is offered Delete user for this user: not themselves, not a server administrator, and not the
    /// household's last owner. The Engine enforces the same rules (and one more: an owner never deletes their own sign-in) and says
    /// so if asked anyway; this only hides what it would refuse.
    /// </summary>
    public static bool CanDelete(AccountAccessResponse target, Guid? ownAccountId, IReadOnlyList<AccountAccessResponse> household)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(household);
        if (target.Id == ownAccountId || target.IsAdministrator)
        {
            return false;
        }

        var lastOwner = target.HouseholdAdmin && target.IsEnabled
            && !household.Any(other => other.Id != target.Id && other.HouseholdAdmin && other.IsEnabled);
        return !lastOwner;
    }
}
