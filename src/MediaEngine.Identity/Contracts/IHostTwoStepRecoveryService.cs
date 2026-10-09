namespace MediaEngine.Identity.Contracts;

/// <summary>
/// Break-glass two-step reset available only to host-side administrative tooling, for someone who lost both their
/// authenticator app and their recovery codes. This contract must not be exposed through the HTTP surface.
/// </summary>
public interface IHostTwoStepRecoveryService
{
    /// <summary>Turns two-step codes off for the account with this email, with no code. False when it had none.</summary>
    /// <exception cref="UnauthorizedAccessException">No account has this email.</exception>
    Task<bool> ResetTwoStepFromHostAsync(string email, CancellationToken ct = default);
}
