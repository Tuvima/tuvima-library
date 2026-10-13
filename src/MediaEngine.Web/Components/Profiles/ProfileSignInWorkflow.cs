using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Components.Profiles;

/// <summary>What the person needs to hand over once: a starting password or an invitation. Never shown again.</summary>
/// <param name="Password">The starting password the administrator chose, when one was set.</param>
/// <param name="PasswordExpiresAt">When a temporary password stops working; empty for an ordinary password.</param>
/// <param name="MustChangePassword">Whether the person will be asked to choose their own password at first sign-in.</param>
/// <param name="Invitation">The invitation (code, link, QR), when an invitation was chosen instead of a password.</param>
public sealed record SignInIssued(
    string? Password,
    DateTimeOffset? PasswordExpiresAt,
    bool MustChangePassword,
    AccountInvitationResponse? Invitation);

/// <summary>The sign-in sheet's choices for a person with no sign-in yet. A null password means an invitation link.</summary>
public sealed record SignInGiveChoice(string Email, string? Password, bool MustChangePassword);

/// <summary>The sign-in sheet's choices for a new password on an existing sign-in.</summary>
public sealed record SignInPasswordChoice(string Password, bool MustChangePassword);

/// <summary>What happened when a sign-in change was sent from the sign-in sheet.</summary>
/// <param name="Done">True when the Engine accepted the change.</param>
/// <param name="Error">A plain-language message to show in the sheet, if something went wrong.</param>
/// <param name="NeedsUnlock">True when the household's administrator PIN has to be entered before trying again.</param>
/// <param name="Issued">A password or invitation to hand over, shown once.</param>
/// <param name="Notice">Something to tell the person on the page afterwards.</param>
public sealed record SignInOutcome(
    bool Done,
    string? Error = null,
    bool NeedsUnlock = false,
    SignInIssued? Issued = null,
    string? Notice = null);

/// <summary>
/// Sends the choices made in the profile page's sign-in sheet to the Engine: give a person their own sign-in, set a new
/// password, change the email, or remove the sign-in. The person stays in the household either way.
/// </summary>
public sealed class ProfileSignInWorkflow(DashboardIdentityClient identity, ConfirmedActionRunner confirmed)
{
    public async Task<SignInOutcome> GiveAsync(
        Guid profileId, string displayName, string email, string? password, bool mustChangePassword, bool invite,
        bool afterUnlock = false)
    {
        var request = new GiveOwnSignInRequest(email.Trim(), invite ? null : password, mustChangePassword, invite);
        var given = await confirmed.RunAsync(() => identity.GiveOwnSignInResultAsync(profileId, request)).ConfigureAwait(false);
        if (!given.Succeeded || given.Value is null)
        {
            return Fail(given.Failure, afterUnlock,
                "Check the email, and use a password of at least 12 characters that is not common and not their email address or name.",
                "That email already has a sign-in, or this person already has one.");
        }

        if (invite)
        {
            return given.Value.Invitation is { } invitation
                ? new SignInOutcome(true, Issued: new SignInIssued(null, null, true, invitation),
                    Notice: $"{displayName} now has their own sign-in.")
                : new SignInOutcome(false, "The sign-in was made, but its invitation couldn't be shown. Remove the sign-in and try again.");
        }

        return new SignInOutcome(true,
            Issued: new SignInIssued(password, given.Value.TemporaryPasswordExpiresAt, mustChangePassword, null),
            Notice: $"{displayName} now has their own sign-in.");
    }

    public async Task<SignInOutcome> SetPasswordAsync(
        Guid accountId, string displayName, string password, bool mustChangePassword, bool afterUnlock = false)
    {
        var request = new SetTemporaryPasswordRequest(password, mustChangePassword);
        var set = await confirmed.RunAsync(() => identity.SetTemporaryPasswordResultAsync(accountId, request)).ConfigureAwait(false);
        if (!set.Succeeded)
        {
            return Fail(set.Failure, afterUnlock,
                "Use a password of at least 12 characters that is not common and not their email address or name.",
                "That can't be done right now.");
        }

        return new SignInOutcome(true,
            Issued: new SignInIssued(password, set.Value?.TemporaryPasswordExpiresAt, mustChangePassword, null),
            Notice: $"{displayName}'s password was changed and they were signed out everywhere.");
    }

    public async Task<SignInOutcome> ChangeEmailAsync(Guid accountId, string displayName, string email, bool afterUnlock = false)
    {
        var request = new ChangeAccountEmailRequest(email.Trim());
        var changed = await confirmed.RunAsync(() => identity.ChangeAccountEmailResultAsync(accountId, request)).ConfigureAwait(false);
        return changed.Succeeded
            ? new SignInOutcome(true, Notice: $"{displayName}'s sign-in email was changed.")
            : Fail(changed.Failure, afterUnlock,
                "Enter a valid email address.",
                "That email already has a sign-in.");
    }

    public async Task<SignInOutcome> RemoveAsync(Guid accountId, string displayName, bool afterUnlock = false)
    {
        var removed = await confirmed.RunAsync(() => identity.RemoveOwnSignInResultAsync(accountId)).ConfigureAwait(false);
        return removed.Succeeded
            ? new SignInOutcome(true, Notice: $"{displayName}'s own sign-in was removed. {displayName} stays in the household.")
            : Fail(removed.Failure, afterUnlock,
                "That sign-in can't be removed.",
                "That can't be removed right now. A person needs at least one way in.");
    }

    // A household administrator who turned on an administrator PIN is refused until it is entered, so ask for it once.
    private static SignInOutcome Fail(
        DashboardAccessMutationFailure failure, bool afterUnlock, string validation, string conflict)
    {
        if (failure == DashboardAccessMutationFailure.Forbidden && !afterUnlock)
        {
            return new SignInOutcome(false, NeedsUnlock: true);
        }

        return new SignInOutcome(false, Error: failure switch
        {
            DashboardAccessMutationFailure.ConfirmItsYou => "Nothing was changed. Confirm it's you to continue.",
            DashboardAccessMutationFailure.Validation => validation,
            DashboardAccessMutationFailure.Conflict => conflict,
            DashboardAccessMutationFailure.NotFound => "That person or sign-in is no longer there.",
            DashboardAccessMutationFailure.Forbidden => "You can't change this person's sign-in.",
            DashboardAccessMutationFailure.Unauthorized => "Sign in again to continue.",
            DashboardAccessMutationFailure.Transient => "The Engine is unavailable. Try again.",
            _ => "That couldn't be saved. Try again.",
        });
    }
}
