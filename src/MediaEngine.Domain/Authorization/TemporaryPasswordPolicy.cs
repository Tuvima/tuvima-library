namespace MediaEngine.Domain.Authorization;

/// <summary>
/// Rules for the temporary password an administrator can set for someone: it works only to choose a password of
/// their own, and it stops working when the invitation lifetime (a week by default) runs out.
/// </summary>
public static class TemporaryPasswordPolicy
{
    /// <summary>Length of a generated temporary password.</summary>
    public const int GeneratedLength = 16;

    /// <summary>How long an administrator-set temporary password works.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>Error code for every Engine action other than choosing a new password while a temporary password is in force.</summary>
    public const string PasswordChangeRequiredCode = "password_change_required";

    /// <summary>Shown when a temporary password has run out.</summary>
    public const string ExpiredMessage = "Ask your administrator for a new temporary password.";
}
