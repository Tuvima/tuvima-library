namespace MediaEngine.Domain.Entities;

/// <summary>
/// An account's authenticator-app (TOTP) key. A row without <see cref="EnabledAt"/> is a setup the person has not
/// confirmed yet and is never asked for at sign-in.
/// </summary>
public sealed class AccountTwoStep
{
    public Guid AccountId { get; set; }

    /// <summary>The authenticator key, encrypted with the Engine's data protection. Never stored or logged in the clear.</summary>
    public string SecretProtected { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EnabledAt { get; set; }

    /// <summary>The newest 30-second step a code was accepted for; a code for this step or an earlier one is refused.</summary>
    public long LastUsedStep { get; set; }

    public bool IsEnabled => EnabledAt is not null;
}
