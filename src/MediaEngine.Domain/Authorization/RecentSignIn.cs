namespace MediaEngine.Domain.Authorization;

/// <summary>
/// The "signed in recently" rule for sensitive account actions (change password, passkeys, linked accounts,
/// recovery codes, signing out other devices, PINs). A session that is older than <see cref="Window"/> since the
/// person last proved it was them is asked to confirm first.
/// </summary>
public static class RecentSignIn
{
    /// <summary>How long a sign-in, or a confirmation, counts as recent.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>The stable <c>code</c> in the 403 problem body that tells the Dashboard to ask the person to confirm.</summary>
    public const string ConfirmItsYouCode = "confirm_its_you";

    public static bool IsRecent(DateTimeOffset authenticatedAt, DateTimeOffset now) => now - authenticatedAt <= Window;
}
