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

    /// <summary>The stable <c>code</c> the Engine puts in the 409 problem body for <see cref="SecureAdministratorsFirst"/>, so the Dashboard can offer "Set an email and password" without reading the sentence.</summary>
    public const string SecureAdministratorsFirstCode = "secure_administrators_first";
}

/// <summary>A second household was refused because a server administrator has no email and password (or passkey) yet (D10). Carries <see cref="HouseholdRules.SecureAdministratorsFirst"/>.</summary>
public sealed class SecureAdministratorsFirstException() : InvalidOperationException(HouseholdRules.SecureAdministratorsFirst);

