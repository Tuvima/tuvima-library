namespace MediaEngine.Identity;

/// <summary>
/// Signals that someone tried to sign in from outside the home network and was refused: either the person has no password or
/// passkey (they stay home only, whatever the way in: password form, passkey, linked sign-in, invitation or a paired app), or
/// outside access is switched off (remote-access spec D6: one switch turns it off at once without touching home).
/// Derives from <see cref="UnauthorizedAccessException"/> like <see cref="ProfilePinLockedException"/>; callers that care
/// catch it first and answer with <see cref="ReasonCode"/> and <see cref="Exception.Message"/>.
/// </summary>
public sealed class OutsideSignInRefusedException : UnauthorizedAccessException
{
    /// <summary>The stable reason code sent to the Dashboard when the person has no password or passkey.</summary>
    public const string Code = "outside_needs_password";

    /// <summary>The refusal in plain words, safe to show to the person who was refused.</summary>
    public const string PlainMessage = "This person has no password or passkey yet, so they can only sign in at home. Set a password first.";

    /// <summary>The stable reason code sent to the Dashboard when outside access is switched off.</summary>
    public const string AccessOffCode = "outside_access_off";

    /// <summary>The refusal for switched-off outside access, in plain words.</summary>
    public const string AccessOffMessage = "Away-from-home access is switched off, so Library can only be used at home right now.";

    /// <summary>The stable reason code of this refusal (<see cref="Code"/> or <see cref="AccessOffCode"/>).</summary>
    public string ReasonCode { get; }

    /// <summary>A refusal because the person has no password or passkey.</summary>
    public OutsideSignInRefusedException()
        : this(Code, PlainMessage)
    {
    }

    private OutsideSignInRefusedException(string reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    /// <summary>A refusal because away-from-home access is switched off.</summary>
    public static OutsideSignInRefusedException AccessOff() => new(AccessOffCode, AccessOffMessage);

    /// <summary>The reason code for a refusal message returned as plain text (<c>null</c> when the text is not one of the refusals).</summary>
    public static string? CodeForMessage(string? message) => message switch
    {
        PlainMessage => Code,
        AccessOffMessage => AccessOffCode,
        _ => null,
    };
}
