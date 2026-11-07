using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Services.Playback;

/// <summary>What asking for a cast link came to: the address a receiver can fetch, or why there is none (for the log; the person sees one calm line).</summary>
public sealed record PlaybackCastLink(string? Url, string? ContentType, string? Reason)
{
    public bool IsReady => !string.IsNullOrEmpty(Url);
}

/// <summary>
/// Gets the Engine's existing short-lived cast link for one title (unified player v2 D28) and turns it into an address the receiver can use. The Engine
/// answers a relative path under <c>/cast/v1/{grant}/</c>; the receiver reaches it through the Dashboard's relay, so the address is the Dashboard's
/// own, and a Dashboard opened as localhost cannot be reached by a TV. Nothing here changes what the Engine allows: when it refuses, this reports why
/// and the picker is not opened.
/// </summary>
public sealed class PlaybackCastLinkService(IEngineApiClient api, NavigationManager navigation, ILogger<PlaybackCastLinkService> logger)
{
    public async Task<PlaybackCastLink> RequestAsync(Guid assetId, CancellationToken ct = default)
    {
        var attempt = await api.CreateCastSessionAsync(assetId, ct);
        var link = Build(new Uri(navigation.BaseUri), attempt);
        if (!link.IsReady)
        {
            // Not an error to the person: the row says "Can't play on another device yet". The reason is kept here so a refusal is never silent.
            logger.LogInformation("Play on another device was not started: {Reason}.", link.Reason);
        }

        return link;
    }

    /// <summary>The pure part: the Engine's answer and the Dashboard's address in, a usable link or a reason out.</summary>
    public static PlaybackCastLink Build(Uri dashboardBase, CastSessionAttempt attempt)
    {
        if (attempt.Session is not { } session)
        {
            return new(null, null, $"the Engine did not make a cast link ({attempt.StatusCode?.ToString() ?? "no answer"}: {attempt.Reason ?? "no reason given"})");
        }

        if (!attempt.IsReady)
        {
            return new(null, null, $"the Engine's cast session is {session.Status}: {session.Message ?? "no message"}");
        }

        if (session.MediaPath is not { } path || !path.StartsWith("/cast/v1/", StringComparison.Ordinal))
        {
            return new(null, null, "the Engine's cast link was not under /cast/v1/");
        }

        if (dashboardBase.IsLoopback)
        {
            return new(null, null, "the Dashboard is open on this computer's own address, which a receiver cannot reach");
        }

        return new(new Uri(dashboardBase, path).AbsoluteUri, session.ContentType, null);
    }
}
