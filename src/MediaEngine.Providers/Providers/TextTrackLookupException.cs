namespace MediaEngine.Providers.Providers;

/// <summary>
/// A subtitle or lyrics provider could not answer a request; <see cref="Status"/> says why
/// (<c>AuthenticationRequired</c>, <c>QuotaExceeded</c>, <c>RateLimited</c>, <c>ProviderUnavailable</c>,
/// <c>ProviderError</c>, <c>IdentityMismatch</c>, <c>AmbiguousMatch</c> …) and the message is fit to show.
/// </summary>
public sealed class TextTrackLookupException(string status, string message, DateTimeOffset? retryAt = null) : Exception(message)
{
    public string Status { get; } = status;

    public DateTimeOffset? RetryAt { get; } = retryAt;
}
