namespace MediaEngine.Api.Services.EngineLogs;

/// <summary>
/// Groups the Engine's logger names (Serilog's <c>SourceContext</c>) into the plain areas the Logs page offers.
/// The project a logger lives in decides first; wording in the name decides the rest.
/// </summary>
public static class LogAreas
{
    public const string Ingestion = "Ingestion";
    public const string Playback = "Playback";
    public const string Network = "Network";
    public const string Providers = "Providers";
    public const string Security = "Security";
    public const string Ai = "AI";
    public const string Other = "Other";

    /// <summary>Every area, in the order the page lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Ingestion, Playback, Network, Providers, Security, Ai, Other];

    // Checked in order; the first match wins. Security and AI come first so a "SignIn" or "Whisper" logger is never
    // swallowed by a broader word further down.
    private static readonly (string Area, string[] Prefixes, string[] Words)[] Rules =
    [
        (Security,
            ["MediaEngine.Identity"],
            ["Security", "Authenticat", "Authoriz", "Passkey", "SignIn", "Credential", "Account", "Pairing", "Invitation"]),
        (Ai,
            ["MediaEngine.AI"],
            ["Whisper", "DescriptionIntelligence", "ModelAutoDownload", "ModelLifecycle", "VibeBatch", "SeriesAlign", "HardwareBenchmark", "LocalAi"]),
        (Playback,
            [],
            ["Playback", "Hls", "Encode", "Transcod", "Stream", "Telemetry"]),
        (Network,
            ["Microsoft.AspNetCore", "System.Net"],
            ["Network", "Connectivity", "PortMapping", "Upnp", "Discovery"]),
        (Providers,
            ["MediaEngine.Providers"],
            ["Provider", "Wikidata", "MusicBrainz", "Tmdb", "Tvdb", "Retail", "Enrichment", "Hydration", "Harvest"]),
        (Ingestion,
            ["MediaEngine.Ingestion"],
            ["Ingestion", "Pipeline", "Watcher", "Scan", "Organi", "Reconciliation", "Retag", "Sweep", "Intake", "Library"]),
    ];

    /// <summary>The area for a logger name; <see cref="Other"/> when nothing fits or the name is missing.</summary>
    public static string Classify(string? sourceContext)
    {
        if (string.IsNullOrWhiteSpace(sourceContext))
        {
            return Other;
        }

        foreach (var (area, prefixes, _) in Rules)
        {
            if (prefixes.Any(prefix => sourceContext.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return area;
            }
        }

        foreach (var (area, _, words) in Rules)
        {
            if (words.Any(word => sourceContext.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                return area;
            }
        }

        return Other;
    }

    /// <summary>Matches a requested area name to a known one, ignoring case; <see langword="null"/> when unknown.</summary>
    public static string? Normalize(string? area) =>
        All.FirstOrDefault(known => string.Equals(known, area?.Trim(), StringComparison.OrdinalIgnoreCase));
}
