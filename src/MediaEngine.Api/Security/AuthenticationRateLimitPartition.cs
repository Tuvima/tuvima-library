using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

namespace MediaEngine.Api.Security;

/// <summary>
/// Remembers the Dashboard service credential's hash in memory so the rate limiter, which runs before
/// authentication, can recognise the Dashboard without touching the data store.
/// </summary>
public sealed class DashboardServiceCredentialRecognizer
{
    private volatile byte[]? _hash;

    public void Remember(string plaintextToken) => _hash = Hash(plaintextToken);

    public bool Matches(string? presentedToken)
    {
        var known = _hash;
        return known is not null
            && !string.IsNullOrEmpty(presentedToken)
            && CryptographicOperations.FixedTimeEquals(known, Hash(presentedToken));
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

/// <summary>
/// Partitioning for the <c>"authentication"</c> rate-limit policy. Every Dashboard-forwarded sign-in reaches the
/// Engine from the Dashboard's own address, so a per-address bucket would let any ten bad attempts from anyone
/// block sign-in for everyone. The Dashboard limits each person's address itself, so requests carrying the
/// Dashboard service credential get one large partition of their own; every other caller keeps a small
/// per-address allowance. The limiter runs before authentication, so the credential is recognised from memory.
/// </summary>
public static class AuthenticationRateLimitPartition
{
    public const int PerAddressPermitLimit = 10;
    public const int DashboardPermitLimit = 300;

    /// <summary>Profile switches one signed-in session may make a minute (see <see cref="ForSession"/>).</summary>
    public const int PerSessionPermitLimit = 30;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static RateLimitPartition<string> For(HttpContext context)
    {
        var recognizer = context.RequestServices.GetService<DashboardServiceCredentialRecognizer>();
        if (recognizer is not null
            && context.Request.Headers.TryGetValue(TuvimaAuthDefaults.ServiceHeader, out var presented)
            && recognizer.Matches(presented.ToString()))
        {
            return RateLimitPartition.GetFixedWindowLimiter("dashboard-service", _ => Options(DashboardPermitLimit));
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => Options(PerAddressPermitLimit));
    }

    /// <summary>
    /// Partitioning for the <c>"authentication-session"</c> policy, used by actions a signed-in person repeats, such
    /// as switching profile. A Dashboard-forwarded request carrying a session gets that session's own allowance, so
    /// one person switching rapidly can never use up the shared Dashboard allowance that home sign-ins rely on.
    /// Anything else falls back to <see cref="For"/>.
    /// </summary>
    public static RateLimitPartition<string> ForSession(HttpContext context)
    {
        var recognizer = context.RequestServices.GetService<DashboardServiceCredentialRecognizer>();
        if (recognizer is not null
            && context.Request.Headers.TryGetValue(TuvimaAuthDefaults.ServiceHeader, out var presented)
            && recognizer.Matches(presented.ToString())
            && context.Request.Headers.TryGetValue(TuvimaAuthDefaults.SessionHeader, out var session)
            && !string.IsNullOrWhiteSpace(session.ToString()))
        {
            // Only a hash is kept, so the limiter never holds a usable session token.
            var key = "session:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session.ToString())));
            return RateLimitPartition.GetFixedWindowLimiter(key, _ => Options(PerSessionPermitLimit));
        }

        return For(context);
    }

    private static FixedWindowRateLimiterOptions Options(int permitLimit) => new()
    {
        PermitLimit = permitLimit,
        Window = Window,
        QueueLimit = 0,
    };
}
