using System.Net;

namespace MediaEngine.Web.Services.Integration;

/// <summary>The Engine refused or could not mint a live-updates token, so the connection attempt stops before reaching the hub.</summary>
public sealed class IntercomTokenUnavailableException(HttpStatusCode status, TimeSpan? retryAfter)
    : Exception($"The Engine did not issue a live-updates token ({(int)status}).")
{
    public HttpStatusCode Status { get; } = status;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}
