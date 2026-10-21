namespace MediaEngine.Web.Services.Vault;

/// <summary>How a Vault mode or Vault library call ended, in the terms the screens need.</summary>
public enum VaultOutcome
{
    /// <summary>The Engine could not be reached or answered something unexpected. The default, so an empty result never reads as success.</summary>
    Failed = 0,

    /// <summary>It worked.</summary>
    Ok,

    /// <summary>The Engine answered "not found": this profile cannot use Vault mode, so nothing about it is drawn.</summary>
    NotAvailable,

    /// <summary>A PIN is needed, or the one given was not right.</summary>
    PinRequired,

    /// <summary>The administrator needs a profile PIN of their own before the Engine will do this (Videos spec D25).</summary>
    AdministratorPinNotSet,

    /// <summary>Too many wrong tries; the Engine asks to wait.</summary>
    TooManyAttempts,

    /// <summary>The Engine wants the person to confirm it is them (a recent sign-in) before it saves a sensitive change.</summary>
    ConfirmItsYou,

    /// <summary>The Engine refused with a plain-language reason (<see cref="VaultCall{T}.Message"/>).</summary>
    Rejected,
}

/// <summary>The answer to one Vault mode or Vault call. A default value is a failure.</summary>
public readonly record struct VaultCall<T>(T? Value, VaultOutcome Outcome, string? Message = null)
{
    public bool Succeeded => Outcome == VaultOutcome.Ok;

    public static VaultCall<T> Ok(T? value) => new(value, VaultOutcome.Ok);

    public static VaultCall<T> Of(VaultOutcome outcome, string? message = null) => new(default, outcome, message);
}
