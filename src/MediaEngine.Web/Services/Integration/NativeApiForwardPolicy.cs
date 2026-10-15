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
/// The allow-list for the Dashboard's app door. The Engine stays private; only the specific paired-device
/// actions below are ever passed through, by method and path shape. Everything else (health, diagnostics,
/// settings, analytics, the sign-in-based pairing review, and every administrator-level edit such as person
/// editing, artwork changes or chapter overrides) is answered as "not found" without contacting the Engine.
/// A paired administrator's phone is still an app: it gets the app surface, not the admin surface.
/// </summary>
public static class NativeApiForwardPolicy
{
    private const string Guid = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
    private static readonly string[] Read = ["GET", "HEAD"];
    private static readonly string[] Write = ["POST", "PUT", "PATCH", "DELETE"];

    private sealed record Rule(string[] Methods, System.Text.RegularExpressions.Regex Path, bool NeedsToken = true);

    private static Rule Of(string[] methods, string pattern, bool needsToken = true) => new(
        methods,
        new System.Text.RegularExpressions.Regex($"^(?:{pattern})$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant),
        needsToken);

    private static readonly Rule[] Rules =
    [
        // Start pairing and refresh tokens. Anonymous by design: an unpaired app has no token yet.
        Of(["POST"], "oauth/device_authorization", needsToken: false),
        Of(["POST"], "oauth/token", needsToken: false),

        // Devices: exactly what the apps call, each anchored. The Engine scopes them to the calling device: the list holds only
        // this device and a revoke is accepted only for this device's own id (the apps use it to sign out). Managing other devices
        // is the Dashboard's /access/devices. No other devices/ route (including devices/current and backup-profile) is forwarded.
        Of(["GET"], "devices"),
        Of(["DELETE"], $"devices/{Guid}"),

        // Report capabilities (the TV's HEVC toggle).
        Of(["PUT"], "devices/current/capabilities"),

        // Whose profile this device uses: the people it may switch to, and the switch itself. The Engine re-checks the PIN at every switch.
        Of(["GET"], "devices/current/profiles"),
        Of(["POST"], "devices/current/switch-profile"),

        // The watching and subtitle preferences of the profile this device is using now. Read-only and GET only: the Engine takes the
        // profile from the token, there is no profile id in the path, and nothing here can change a setting.
        Of(["GET"], "devices/current/playback-preferences"),

        // The offline downloads this device reports as stored: list and report (GET, POST on the collection), forget a variant
        // (DELETE by id) or a kept photo (DELETE .../photos/{itemId}). The Engine decides which device from the token alone.
        Of(["GET"], "devices/current/downloads"),
        Of(["POST"], "devices/current/downloads"),
        Of(["DELETE"], $"devices/current/downloads/{Guid}"),
        Of(["DELETE"], $"devices/current/downloads/photos/{Guid}"),

        // Browse and artwork: read-only for apps, and only the routes the apps use. The artwork workspace under display/artwork
        // (library browse, asset search, entity and Universe workspaces, effective work artwork, raw asset content) is the Dashboard's
        // artwork management and is never forwarded; only the one profile-checked effective-cover route is.
        Of(Read, "display/(?:home|browse|continue|recent|search|contributor-shelves)"),
        Of(Read, "display/shelves/[^/]+"),
        Of(Read, $"display/groups/{Guid}"),
        Of(Read, $"display/artwork/media-assets/{Guid}/effective-cover"),

        // Details, people and artwork: read-only for apps.
        Of(Read, "details/.*"),

        // Collections and Universes: the catalogue page, one collection and its item pages, and one Universe by QID. Read only.
        Of(Read, $"collections(?:/{Guid}(?:/items)?)?"),
        Of(Read, "universe/Q[0-9]{1,15}"),
        // Blocked paths match on "any single segment", not GUID shape, so a dash-less or braced ID can't slip by.
        Of(Read, "persons/(?![^/]+/editor$).*"),
        Of(Read, "library/portraits/.*"),

        // Photos and personal videos (the person's own, read-only; the Engine still needs the photo consent on the device token).
        // Exactly the eleven GET routes of /api/v1/view, nothing else under view/ and no write verb, no HEAD. The person key is any one segment.
        Of(["GET"], $"view/(?:timeline|galleries|people|places|galleries/{Guid}/items|people/[^/]+/items|items/{Guid}(?:/(?:thumbnail|preview|original|playback))?)"),

        // Streaming and subtitles: read, plus choosing the preferred subtitle track.
        Of(Read, "stream/.*"),
        Of(["POST"], $"stream/{Guid}/text-tracks/{Guid}/preferred"),

        // Playback manifests, offline downloads and encode jobs. No diagnostics, sessions or history.
        Of(Read, $"playback/{Guid}/manifest"),
        Of(Read, $"playback/{Guid}/offline/{Guid}"),
        Of(Read, "playback/encode/jobs"),
        Of(["POST"], $"playback/{Guid}/encode"),
        Of(["POST"], $"playback/encode/jobs/{Guid}/cancel"),

        // The player session, progress, saved items and reactions belong to the device's own profile.
        Of([..Read, ..Write], "player/(?!audiobooks/[^/]+/chapter-overrides(?:/|$)).*"),
        Of(Read, "player/audiobooks/[^/]+/chapter-overrides"),
        Of([..Read, ..Write], "progress/.*"),
        Of([..Read, ..Write], "profile-state/.*"),
    ];

    public static NativeApiForwardDecision Evaluate(string method, string? clientPath, bool hasBearerToken)
    {
        if (!TryNormalize(clientPath, out var path))
        {
            return NativeApiForwardDecision.NotFound;
        }

        var matched = Rules.FirstOrDefault(rule =>
            rule.Methods.Contains(method, StringComparer.OrdinalIgnoreCase) && rule.Path.IsMatch(path));
        if (matched is null)
        {
            return NativeApiForwardDecision.NotFound;
        }

        return matched.NeedsToken && !hasBearerToken
            ? NativeApiForwardDecision.Unauthorized
            : NativeApiForwardDecision.Forward;
    }

    /// <summary>
    /// True for "start pairing", the one action the Dashboard throttles per app address. Approval polling and
    /// token refresh are left to the Engine's own slow-down reply so one TV pairing cannot starve other devices.
    /// </summary>
    public static bool IsStartPairing(string method, string? clientPath) =>
        HttpMethods.IsPost(method) && string.Equals(clientPath?.TrimStart('/'), "oauth/device_authorization", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a decoded route value can be joined to an Engine path without climbing out of it:
    /// no backslashes, percent signs, query/fragment characters, control characters, empty or dot segments.
    /// An empty value is treated as safe (the caller's own prefix is used as is).
    /// </summary>
    public static bool IsSafeSubPath(string? subPath)
    {
        var path = (subPath ?? string.Empty).TrimStart('/');
        return path.Length == 0 || TryNormalize(path, out _);
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
}
