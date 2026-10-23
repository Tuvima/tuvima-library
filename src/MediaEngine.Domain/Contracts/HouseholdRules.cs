namespace MediaEngine.Domain.Contracts;

/// <summary>Plain sentences for the rules that guard adding a household and making server administrators (users, profiles and households D10, D17).</summary>
public static class HouseholdRules
{
    /// <summary>Shown when a second household would be added while a server administrator has no email and password (or passkey) yet.</summary>
    public const string SecureAdministratorsFirst =
        "Set an email and password for your own sign-in first. A Library with several households must not be open to anyone who reaches it.";

    /// <summary>Shown when a profile is made a server administrator but its user has no email and password (or passkey), or it has no user.</summary>
    public const string ServerAdministratorNeedsSecuredSignIn =
        "A server administrator needs a sign-in with an email and a password or passkey. Attach one to this profile and set it up first.";
}
