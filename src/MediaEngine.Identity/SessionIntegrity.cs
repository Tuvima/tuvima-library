using System.Security.Cryptography;
using System.Text;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Identity;

/// <summary>
/// The one list of what must still hold for a managed sign-in session besides its own expiry, the profile it acts as and where the
/// request came from. <see cref="FirstPartyIdentityService.ValidateSessionAsync"/> and every grant that is tied to a session (the cast
/// grant for a signed-in Dashboard session) call this, so the two can never drift apart: signing in again with a changed password, a
/// temporary password that has run out, or a retired sign-in method ends the session everywhere at once.
/// </summary>
public static class SessionIntegrity
{
    /// <summary>
    /// True when the account is enabled, its temporary password (if any) has not run out, the session did not start from a retired
    /// sign-in method (a profile PIN only switches profiles inside a session an account sign-in already made), and a password sign-in's
    /// security stamp still matches the account's current password.
    /// </summary>
    public static async ValueTask<bool> HoldsAsync(
        IIdentityRepository identities,
        AuthSession session,
        Account account,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        if (!account.IsEnabled || account.IsTemporaryPasswordExpired(now))
        {
            return false;
        }

        if (session.AuthenticationMethod.Equals("Password", StringComparison.OrdinalIgnoreCase))
        {
            var credential = await identities.GetAccountCredentialAsync(account.Id, AccountCredentialKind.Password, ct).ConfigureAwait(false);
            return StampMatches(credential?.SecurityStamp, session.SecurityStamp);
        }

        // Retired sign-in methods: a session that claims to have started from one is never accepted.
        return session.AuthenticationMethod is not ("ProfilePin" or "ProfileEntry");
    }

    private static bool StampMatches(string? actual, string expected) =>
        actual is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
}
