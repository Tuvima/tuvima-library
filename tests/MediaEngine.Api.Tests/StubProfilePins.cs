using MediaEngine.Identity;
using MediaEngine.Identity.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A stand-in for the profile PIN check in tests that never type a PIN: no profile has one, so anything that asks for it fails closed.
/// Tests that do need PINs use the real <c>FirstPartyIdentityService</c> instead.
/// </summary>
internal sealed class StubProfilePins : IProfilePinVerifier
{
    private readonly Dictionary<Guid, string> _pins = [];

    /// <summary>Gives a profile a PIN that this stand-in accepts (no lockout is counted).</summary>
    public StubProfilePins With(Guid profileId, string pin)
    {
        _pins[profileId] = pin;
        return this;
    }

    public Task<bool> HasProfilePinAsync(Guid profileId, CancellationToken ct = default) =>
        Task.FromResult(_pins.ContainsKey(profileId));

    public Task VerifyProfilePinAsync(Guid profileId, string? pin, bool countsTowardLockout, CancellationToken ct = default) =>
        !_pins.TryGetValue(profileId, out var expected) || pin == expected
            ? Task.CompletedTask
            : throw new ProfilePinRequiredException();
}
