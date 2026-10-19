using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Approvals;

/// <summary>
/// What a person is shown before something is switched on: a title, what it can do, and whether anything leaves the
/// machine. The Engine writes every word here so the Dashboard, applications and the approval record all agree.
/// </summary>
internal sealed record ApprovalStatement(
    string Title,
    string KindLabel,
    IReadOnlyList<string> CanDo,
    bool SendsDataOut,
    string? SendsDataTo,
    IReadOnlyList<string> Notes);

/// <summary>
/// Builds <see cref="ApprovalStatement"/>s and their fingerprint. Pure: no storage, no clock, no secrets. A statement
/// never carries a secret value (a webhook signing secret, a key), only the facts a person decides on.
/// </summary>
internal static class ApprovalStatementBuilder
{
    /// <summary>Where a plugin's data goes when it can connect to the internet.</summary>
    public const string TheInternet = "the internet";

    /// <summary>Where a credential's holder is described in words, since the Engine cannot know the software.</summary>
    public const string TheSoftwareHoldingTheKey = "the software that holds this key";

    public const string PluginKindLabel = "Plugin";
    public const string AiFeatureKindLabel = "Local AI feature";
    public const string CredentialKindLabel = "Application key";
    public const string WebhookKindLabel = "Webhook";

    private const string UsesLocalAi = "Uses Local AI";
    private const string KeepsOwnKeysAndLogins = "Keeps its own keys and logins";
    private const string RunsWithLocalAi = "Runs on this machine with Local AI";
    private const string ModelMayDownload = "May download an AI model to this machine first";
    private const string UnsandboxedNote = "Plugins run inside the server without a sandbox. Only turn on plugins you trust.";

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly IReadOnlyDictionary<string, string> PermissionWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["media.read"] = "Reads your media files",
        ["network.http"] = "Connects to the internet",
        ["process.execute"] = "Runs helper programs",
        ["tool.download"] = "Downloads helper tools",
        ["ai.infer"] = UsesLocalAi,
        ["storage.plugin"] = "Keeps its own data",
        ["secrets.plugin"] = KeepsOwnKeysAndLogins,
    };

    private static readonly IReadOnlyDictionary<string, string[]> FeatureWords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["smart_labeling"] = ["Reads file and folder names", "Suggests titles, authors, years and episode numbers when files are added"],
        ["type_logic"] = ["Reads file names and basic file facts", "Decides the media type when it is unclear"],
        ["series_alignment"] = ["Reads series and title details", "Suggests the order within a series"],
        ["vibe_tags"] = ["Reads descriptions and genres", "Adds mood tags"],
        ["tldr"] = ["Shows the short summary made by Description insights"],
        ["description_intelligence"] = ["Reads descriptions", "Adds themes, mood, setting, period, audience, pace, content warnings and people"],
    };

    private static readonly IReadOnlyDictionary<string, string> FeatureTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["smart_labeling"] = "Smart labeling",
        ["type_logic"] = "Media-type assistance",
        ["series_alignment"] = "Series alignment",
        ["vibe_tags"] = "Vibe tags",
        ["tldr"] = "Description TL;DR",
        ["description_intelligence"] = "Description intelligence",
    };

    // ── State card words ─────────────────────────────────────────────────────

    /// <summary>The plain words for one plugin permission id; an id the Library does not know is shown as it is.</summary>
    public static string PermissionText(string permissionId) =>
        PermissionWords.TryGetValue(permissionId, out var words) ? words : permissionId;

    /// <summary>
    /// What a plugin may do: one line per declared permission, per tool it needs ("Needs FFmpeg"), once for Local AI use,
    /// and, when it declares keys or logins, which ones by label. Duplicate lines are shown once.
    /// </summary>
    public static IReadOnlyList<string> PluginAllowedTo(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var lines = new List<string>();
        var declaresSecrets = manifest.Secrets.Count > 0;
        foreach (var permission in manifest.Permissions)
        {
            // With declared secrets the labelled line below replaces the bare permission line.
            if (declaresSecrets && string.Equals(permission, PluginPermissionIds.PluginSecrets, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            lines.Add(PermissionText(permission));
        }

        lines.AddRange(manifest.ToolRequirements.Select(tool => $"Needs {ToolName(tool)}"));
        if (manifest.AiPermissions.Count > 0)
        {
            lines.Add(UsesLocalAi);
        }

        if (declaresSecrets)
        {
            var labels = manifest.Secrets
                .Select(secret => string.IsNullOrWhiteSpace(secret.Label) ? secret.Name : secret.Label.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            lines.Add($"{KeepsOwnKeysAndLogins}: {string.Join(", ", labels)}");
        }

        return lines.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>"the internet" when the plugin can reach the internet; otherwise null (nothing leaves this machine).</summary>
    public static string? PluginSendsDataTo(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Permissions.Any(permission => string.Equals(permission, PluginPermissionIds.NetworkHttp, StringComparison.OrdinalIgnoreCase))
            ? TheInternet
            : null;
    }

    /// <summary>What a Local AI feature is allowed to do, in plain words; empty for a feature the Library has no words for.</summary>
    public static IReadOnlyList<string> AiFeatureAllowedTo(string featureKey) =>
        FeatureWords.TryGetValue(featureKey, out var words) ? words : [];

    /// <summary>Local AI never sends data out, so this is always null.</summary>
    public static string? AiFeatureSendsDataTo(string featureKey) => null;

    // ── Statements ───────────────────────────────────────────────────────────

    public static ApprovalStatement ForPlugin(PluginManifest manifest, PluginTrustLevel trust)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var sendsTo = PluginSendsDataTo(manifest);
        return new ApprovalStatement(
            string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name,
            PluginKindLabel,
            PluginAllowedTo(manifest),
            sendsTo is not null,
            sendsTo,
            [TrustNote(trust), UnsandboxedNote]);
    }

    /// <param name="modelReady">True when the model the feature reads is already on this machine.</param>
    public static ApprovalStatement ForAiFeature(string featureKey, bool modelReady)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureKey);
        var canDo = AiFeatureAllowedTo(featureKey).Append(RunsWithLocalAi).ToList();
        IReadOnlyList<string> notes = modelReady ? [] : [ModelMayDownload];
        return new ApprovalStatement(
            FeatureTitles.TryGetValue(featureKey, out var title) ? title : featureKey,
            AiFeatureKindLabel,
            canDo,
            SendsDataOut: false,
            SendsDataTo: null,
            notes);
    }

    /// <param name="permissionNames">The human-readable name of each permission the application holds.</param>
    public static ApprovalStatement ForCredential(string applicationName, IEnumerable<string> permissionNames, DateTimeOffset? expiresAt)
    {
        ArgumentNullException.ThrowIfNull(permissionNames);
        var canDo = permissionNames.Distinct(StringComparer.Ordinal).ToList();
        canDo.Add(expiresAt is { } expiry
            ? $"Expires on {expiry.UtcDateTime.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}"
            : "Never expires");
        return new ApprovalStatement(
            $"Key for {applicationName}",
            CredentialKindLabel,
            canDo,
            SendsDataOut: true,
            SendsDataTo: TheSoftwareHoldingTheKey,
            ["Anyone with this key can do the above until it is revoked."]);
    }

    /// <summary>The receiver is shown by host only (no path or query), so nothing in the address that could be a secret is recorded.</summary>
    public static ApprovalStatement ForWebhook(Uri receiver, IEnumerable<string> eventTypes, bool allowLocalNetwork)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(eventTypes);
        var events = eventTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var host = receiver.Host;
        return new ApprovalStatement(
            $"Webhook to {host}",
            WebhookKindLabel,
            [$"Receives these events: {string.Join(", ", events)}"],
            SendsDataOut: true,
            SendsDataTo: host,
            allowLocalNetwork ? ["Allowed to reach a device on your home network"] : []);
    }

    // ── Hash and wire ────────────────────────────────────────────────────────

    /// <summary>SHA-256 (lower-case hex) of the canonical JSON of the statement's shown fields.</summary>
    public static string Hash(ApprovalStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var canonical = new CanonicalStatement(
            statement.Title,
            statement.KindLabel,
            statement.CanDo.ToArray(),
            statement.SendsDataOut,
            statement.SendsDataTo,
            statement.Notes.ToArray());
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical, CanonicalJson)));
        return Convert.ToHexStringLower(bytes);
    }

    public static ApprovalPreviewResponse ToPreview(string kind, string itemId, ApprovalStatement statement) => new()
    {
        Kind = kind,
        ItemId = itemId,
        Title = statement.Title,
        KindLabel = statement.KindLabel,
        CanDo = statement.CanDo.ToList(),
        SendsDataOut = statement.SendsDataOut,
        SendsDataTo = statement.SendsDataTo,
        Notes = statement.Notes.ToList(),
        StatementHash = Hash(statement),
    };

    private static string TrustNote(PluginTrustLevel trust) => trust switch
    {
        PluginTrustLevel.Official => "Official plugin from Tuvima",
        PluginTrustLevel.Verified => "Verified",
        PluginTrustLevel.Community => "Community, not checked by Tuvima",
        _ => "Added by hand, not checked by Tuvima",
    };

    // The tool ids the Library ships support for get their proper capitalisation; any other tool is named by its id.
    private static string ToolName(PluginToolRequirement tool)
    {
        var id = string.IsNullOrWhiteSpace(tool.Id) ? tool.ExecutableName : tool.Id;
        return ToolNames.TryGetValue(id, out var name) ? name : id;
    }

    private static readonly IReadOnlyDictionary<string, string> ToolNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ffmpeg"] = "FFmpeg",
        ["comskip"] = "Comskip",
        ["tesseract"] = "Tesseract",
    };

    private sealed record CanonicalStatement(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("kind_label")] string KindLabel,
        [property: JsonPropertyName("can_do")] string[] CanDo,
        [property: JsonPropertyName("sends_data_out")] bool SendsDataOut,
        [property: JsonPropertyName("sends_data_to")] string? SendsDataTo,
        [property: JsonPropertyName("notes")] string[] Notes);
}
