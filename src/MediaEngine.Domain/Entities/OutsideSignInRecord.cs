namespace MediaEngine.Domain.Entities;

/// <summary>
/// One recorded attempt to sign in from outside the home network (sign-in history shown to the administrator). It keeps only
/// who, when, how and the coarse kind of place: never an address.
/// </summary>
/// <param name="At">When the attempt happened.</param>
/// <param name="ProfileName">The person's profile name, or null when the profile no longer exists or the attempt named nobody.</param>
/// <param name="Succeeded">True when the person was let in, false when Library refused.</param>
/// <param name="IngressKind">The coarse kind of place (<see cref="Authorization.ClientIngress.Remote"/> for every outside attempt).</param>
/// <param name="Method">How they tried: Password, Passkey, Oidc or ThisComputer.</param>
public sealed record OutsideSignInRecord(
    DateTimeOffset At,
    string? ProfileName,
    bool Succeeded,
    string IngressKind,
    string Method);

/// <summary>The two <c>identity_audit_events</c> types that make up the outside sign-in history.</summary>
public static class OutsideSignInEventTypes
{
    public const string SignedIn = "outside_sign_in";
    public const string Refused = "outside_sign_in_refused";

    /// <summary>How many sign-ins are kept; older ones are dropped as new ones arrive, so the history cannot grow without limit.</summary>
    public const int RetainedSignIns = 200;

    /// <summary>
    /// How many refusals are kept. Refusals are counted apart from sign-ins, so a flood of turned-away attempts can never push
    /// out the record of who really got in.
    /// </summary>
    public const int RetainedRefusals = 100;

    /// <summary>The most rows the history can hold (and the most a list can ask for).</summary>
    public const int RetainedRows = RetainedSignIns + RetainedRefusals;
}
