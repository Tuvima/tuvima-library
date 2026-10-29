using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Components.Profiles;

/// <summary>One role switch on a profile page: whether it is drawn, whether it is on, whether it can be changed, and why not.</summary>
/// <param name="Visible">False when the viewer is never offered this role at all.</param>
/// <param name="On">Whether the profile holds the role now.</param>
/// <param name="Enabled">False when the switch is shown but cannot be changed.</param>
/// <param name="Reason">A plain sentence for a disabled switch; null when it can be changed.</param>
public sealed record RoleSwitchState(bool Visible, bool On, bool Enabled, string? Reason)
{
    public static RoleSwitchState Hidden { get; } = new(false, false, false, null);
}

/// <summary>
/// What the Profiles screens offer for roles and Delete profile. The Engine enforces every one of these (and says so in its own
/// sentence if asked anyway), so these rules decide what is offered, never what is allowed.
/// </summary>
public static class ProfileRoleRules
{
    public const string NoSignInReason = "A household administrator needs a sign-in. A server administrator attaches one in Users & Households first.";
    public const string KidsReason = "A Kids profile can't look after a household.";
    public const string ServerAdministratorOnlyReason = "Only a server administrator can change a server administrator's roles.";
    public const string LastHouseholdAdministratorReason = "A household always keeps one household administrator.";
    public const string KidsServerReason = "A Kids profile can't be a server administrator.";
    public const string ServerNoSignInReason = "A server administrator needs a sign-in with an email and password. Attach one in Users & Households first.";
    public const string LastServerAdministratorReason = "The Library always keeps one server administrator.";

    /// <summary>The "Household administrator" switch for <paramref name="person"/>, given the people of their household.</summary>
    public static RoleSwitchState HouseholdAdministrator(ManagedProfileResponse person, IReadOnlyList<ManagedProfileResponse> household, bool viewerIsServerAdministrator)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(household);
        var on = person.IsHouseholdAdmin;
        if (person.IsServerAdmin && !viewerIsServerAdministrator)
        {
            return new(true, on, false, ServerAdministratorOnlyReason);
        }

        if (!on && person.IsRestricted)
        {
            return new(true, on, false, KidsReason);
        }

        if (!on && person.User is null)
        {
            return new(true, on, false, NoSignInReason);
        }

        if (on && !HasAnotherHouseholdAdministrator(person, household))
        {
            return new(true, on, false, LastHouseholdAdministratorReason);
        }

        return new(true, on, true, null);
    }

    /// <summary>
    /// The "Server administrator" switch: only a server administrator is offered it, and only for people in their own (the Library's)
    /// household.
    /// </summary>
    public static RoleSwitchState ServerAdministrator(ManagedProfileResponse person, IReadOnlyList<ManagedProfileResponse> household, bool viewerIsServerAdministrator, Guid? viewerHouseholdId)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(household);
        if (!viewerIsServerAdministrator || viewerHouseholdId is null || person.HouseholdId != viewerHouseholdId)
        {
            return RoleSwitchState.Hidden;
        }

        var on = person.IsServerAdmin;
        if (!on && person.IsRestricted)
        {
            return new(true, on, false, KidsServerReason);
        }

        if (!on && person.User is null)
        {
            return new(true, on, false, ServerNoSignInReason);
        }

        if (on && !household.Any(other => other.Id != person.Id && other.IsServerAdmin))
        {
            return new(true, on, false, LastServerAdministratorReason);
        }

        return new(true, on, true, null);
    }

    /// <summary>
    /// Whether Delete profile is offered: never the profile in use, never the last household administrator, never the household's only
    /// person, and a server administrator's profile only to a server administrator (and not the last server administrator).
    /// </summary>
    public static bool CanDelete(ManagedProfileResponse person, Guid? activeProfileId, IReadOnlyList<ManagedProfileResponse> household, bool viewerIsServerAdministrator)
    {
        ArgumentNullException.ThrowIfNull(person);
        ArgumentNullException.ThrowIfNull(household);
        if (person.Id == activeProfileId || household.Count <= 1)
        {
            return false;
        }

        if (person.IsServerAdmin && (!viewerIsServerAdministrator || !household.Any(other => other.Id != person.Id && other.IsServerAdmin)))
        {
            return false;
        }

        return !person.IsHouseholdAdmin || HasAnotherHouseholdAdministrator(person, household);
    }

    private static bool HasAnotherHouseholdAdministrator(ManagedProfileResponse person, IReadOnlyList<ManagedProfileResponse> household) =>
        household.Any(other => other.Id != person.Id && other.IsHouseholdAdmin && other.User is not null);
}
