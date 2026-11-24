namespace MediaEngine.Web.Components.Profiles;

/// <summary>
/// Who sees what in the picker's Household mode. The Engine enforces the same limits (only a household or server
/// administrator can change people), so these rules decide what is offered, never what is allowed.
/// </summary>
public static class ProfileManageRules
{
    public const int MaxProfiles = 8;

    /// <summary>Household and Add profile are for administrators, and never inside a Kids (restricted) profile.</summary>
    public static bool CanManage(bool activeProfileIsKids, bool isAdministrator) => isAdministrator && !activeProfileIsKids;

    /// <summary>A household holds up to eight people.</summary>
    public static bool CanAdd(bool canManage, int profileCount) => canManage && profileCount < MaxProfiles;

    /// <summary>
    /// The one line of plain words under a person's name on the Household page (profile and household D7): their role, how they sign in
    /// and whether a PIN is set, for example "Administrator · no sign-in" or "Kids · PIN". A household administrator and a server
    /// administrator both read "Administrator"; the person's own page gives the detail. A Kids profile has no sign-in, so it never says so.
    /// </summary>
    /// <param name="isAdministrator">A household or server administrator.</param>
    /// <param name="isKids">A Kids profile.</param>
    /// <param name="signInEmail">The email of the sign-in that opens this person, or null when there is none.</param>
    /// <param name="hasPin">Whether the person has a profile PIN.</param>
    public static string PersonLine(bool isAdministrator, bool isKids, string? signInEmail, bool hasPin)
    {
        var parts = new List<string> { isKids ? "Kids" : isAdministrator ? "Administrator" : "Member" };
        if (!isKids)
        {
            parts.Add(string.IsNullOrWhiteSpace(signInEmail) ? "no sign-in" : signInEmail);
        }

        if (hasPin)
        {
            parts.Add("PIN");
        }

        return string.Join(" · ", parts);
    }
}
