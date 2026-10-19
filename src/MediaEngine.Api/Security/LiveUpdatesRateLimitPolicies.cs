using System.Threading.RateLimiting;

namespace MediaEngine.Api.Security;

/// <summary>
/// Rate-limit policies for live updates: minting the short-lived Intercom token and opening the two SignalR hubs.
/// Each has its own name, because ASP.NET Core keeps one budget per policy name and every Dashboard tab reaches the
/// Engine from the Dashboard's own address.
/// <list type="bullet">
/// <item>The token mint already requires a signed-in session, so there is nothing to guess. Dashboard-forwarded
/// requests get a budget per session; other callers keep the small per-address sign-in allowance.</item>
/// <item>Hub negotiation and transport need a valid token or account, so their per-address limit only guards
/// against floods; the general limiter still applies to every request.</item>
/// </list>
/// Real sign-in actions keep the <c>"authentication"</c> policy (<see cref="AuthenticationRateLimitPartition"/>).
/// </summary>
public static class LiveUpdatesRateLimitPolicies
{
    public const string TokenMint = "intercom-token";
    public const string IntercomHub = "intercom";
    public const string ApplicationEventsHub = "application-events";

    /// <summary>Tokens one signed-in session may mint a minute: one per tab connect, plus renewals on reconnect.</summary>
    public const int TokenMintPerSessionPermitLimit = 60;

    /// <summary>Negotiate and transport requests one address may make a minute on each hub (two per connect).</summary>
    public const int HubPerAddressPermitLimit = 120;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static RateLimitPartition<string> ForTokenMint(HttpContext context) =>
        AuthenticationRateLimitPartition.ForSession(context, TokenMintPerSessionPermitLimit);

    public static RateLimitPartition<string> ForHub(HttpContext context) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = HubPerAddressPermitLimit,
                Window = Window,
                QueueLimit = 0,
            });

    /// <summary>The partitioner each live-update policy uses, keyed by policy name.</summary>
    public static Func<HttpContext, RateLimitPartition<string>> PartitionerFor(string policyName) => policyName switch
    {
        TokenMint => ForTokenMint,
        IntercomHub or ApplicationEventsHub => ForHub,
        _ => throw new ArgumentOutOfRangeException(nameof(policyName), policyName, "Not a live-update rate-limit policy."),
    };
}
