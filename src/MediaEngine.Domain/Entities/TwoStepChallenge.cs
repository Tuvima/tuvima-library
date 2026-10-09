namespace MediaEngine.Domain.Entities;

/// <summary>
/// The half-finished sign-in between a correct password and the authenticator code. The token is random, stored only
/// as a hash, works once for five minutes and only from the kind of connection (ingress) that started it.
/// </summary>
public sealed class TwoStepChallenge
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string Ingress { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Client { get; set; } = string.Empty;
    public int FailedAttempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}
