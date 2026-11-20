namespace MediaEngine.Identity;

/// <summary>
/// Signals that someone tried to sign in from outside the home network to a person who has no password or passkey. Such a
/// person stays home only, whatever the way in (password form, passkey, linked sign-in, invitation or a paired app).
/// Derives from <see cref="UnauthorizedAccessException"/> like <see cref="ProfilePinLockedException"/>; callers that care
/// catch it first and answer with <see cref="Code"/> and <see cref="PlainMessage"/>.
/// </summary>
public sealed class OutsideSignInRefusedException : UnauthorizedAccessException
{
    /// <summary>The stable reason code sent to the Dashboard.</summary>
    public const string Code = "outside_needs_password";

    /// <summary>The refusal in plain words, safe to show to the person who was refused.</summary>
    public const string PlainMessage = "This person has no password or passkey yet, so they can only sign in at home. Set a password first.";

    public OutsideSignInRefusedException()
        : base(PlainMessage)
    {
    }
}
