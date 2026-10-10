using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Api.Security;

/// <summary>
/// Partitioning for the <c>"reader_files"</c> rate-limit policy. A browser reader asks for many small byte ranges
/// of one book, and the Dashboard forwards every person's reads from its own address, so a per-address bucket
/// would make everyone share one allowance. A request carrying the Dashboard service credential and a session gets
/// that session's own allowance; anything else (including a browser-spoofed session header without the service
/// credential) is counted per address. The limiter runs before authentication, so the credential is recognised
/// from memory and only a hash of the session token is kept.
/// </summary>
public static class ReaderFileRateLimitPartition
{
    public static RateLimitPartition<string> For(
        HttpContext context, RateLimitPolicy perSession, RateLimitPolicy perAddress)
    {
        var recognizer = context.RequestServices.GetService<DashboardServiceCredentialRecognizer>();
        if (recognizer is not null
            && context.Request.Headers.TryGetValue(TuvimaAuthDefaults.ServiceHeader, out var presented)
            && recognizer.Matches(presented.ToString())
            && context.Request.Headers.TryGetValue(TuvimaAuthDefaults.SessionHeader, out var session)
            && !string.IsNullOrWhiteSpace(session.ToString()))
        {
            var key = "reader-session:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.ToString())));
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => Options(perSession));
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            "reader-address:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            _ => Options(perAddress));
    }

    private static FixedWindowRateLimiterOptions Options(RateLimitPolicy policy) => new()
    {
        PermitLimit = policy.PermitLimit,
        Window = TimeSpan.FromMinutes(policy.WindowMinutes),
        QueueLimit = 0,
    };
}
