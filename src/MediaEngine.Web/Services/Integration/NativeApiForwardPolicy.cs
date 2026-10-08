namespace MediaEngine.Web.Services.Integration;

/// <summary>What the Dashboard's app door should do with one request from a paired native app.</summary>
public enum NativeApiForwardDecision
{
    /// <summary>Pass the request on to the private Engine.</summary>
    Forward,

    /// <summary>The action is not part of the app surface; answer as if it does not exist.</summary>
    NotFound,

    /// <summary>The action needs a paired-device token and none was supplied.</summary>
    Unauthorized,
}

/// <summary>
/// The allow-list for the Dashboard's app door. The Engine stays private; only the paired-device
/// actions below are ever passed through. Everything else (health, diagnostics, settings, analytics,
/// the sign-in-based pairing review) is answered as "not found" without contacting the Engine.
/// </summary>
public static class NativeApiForwardPolicy
{
    // Start pairing. Anonymous by design: an unpaired app has no token yet.
    private static readonly string[] AnonymousActions = ["oauth/device_authorization", "oauth/token"];

    // Actions that need a paired-device token (the Engine still checks scope and revocation per request).
    private static readonly string[] TokenAreas =
    [
        "devices", "display", "details", "player", "progress", "profile-state", "playback",
        "stream", "persons", "library/portraits",
    ];

    // Inside an allowed area but meant for administrators/integrations, not paired apps.
    private static readonly string[] BlockedActions = ["playback/sessions", "playback/history"];

    public static NativeApiForwardDecision Evaluate(string method, string? clientPath, bool hasBearerToken)
    {
        if (!TryNormalize(clientPath, out var path))
        {
            return NativeApiForwardDecision.NotFound;
        }

        if (Matches(path, BlockedActions))
        {
            return NativeApiForwardDecision.NotFound;
        }

        if (AnonymousActions.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return HttpMethods.IsPost(method) ? NativeApiForwardDecision.Forward : NativeApiForwardDecision.NotFound;
        }

        if (!Matches(path, TokenAreas))
        {
            return NativeApiForwardDecision.NotFound;
        }

        return hasBearerToken ? NativeApiForwardDecision.Forward : NativeApiForwardDecision.Unauthorized;
    }

    // Rejects anything that could climb out of /api/v1 once the path is joined to the Engine address.
    private static bool TryNormalize(string? clientPath, out string path)
    {
        path = (clientPath ?? string.Empty).TrimStart('/');
        if (path.Length == 0 || path.Any(c => c is '\\' or '%' or '?' or '#' || char.IsControl(c)))
        {
            return false;
        }

        return path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static bool Matches(string path, IEnumerable<string> prefixes) =>
        prefixes.Any(prefix =>
            path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
}
