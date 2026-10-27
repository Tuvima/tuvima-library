using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Api.Services.Plugins.Pages;
using MediaEngine.Contracts.Approvals;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Approvals;

/// <summary>
/// What a person is shown before something is switched on: a title, what it can do, and whether anything leaves the
/// machine. The Engine writes every word here so the Dashboard, applications and the approval record all agree.
/// </summary>
/// <param name="Notes">Every note shown to the person.</param>
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

    /// <summary>Where an integration's existing keys are described, since the Engine cannot know the software.</summary>
    public const string TheSoftwareHoldingItsKeys = "the software that holds its keys";

    /// <summary>The warning shown wherever a plugin Tuvima has not checked is offered (the plugin directory and its install approval).</summary>
    public const string NotVerifiedWarning = "Tuvima hasn't checked this plugin. Only install plugins from people you trust.";

    public const string PluginKindLabel = "Plugin";
    public const string CredentialKindLabel = "Application key";
    public const string ApplicationKindLabel = "Integration";
    public const string WebhookKindLabel = "Webhook";

    private const string UsesLocalAi = "Uses Local AI";
    private const string RunsAs = "Runs as";
    private const string KeepsOwnKeysAndLogins = "Keeps its own keys and logins";
    private const string UnsandboxedNote = "Plugins run inside the server without a sandbox. Only turn on plugins you trust.";
    private const string DeclarativeNote = "This plugin is a description file; it runs no code of its own.";
    private const string ServiceNote = "This plugin runs as a separate program on this machine. The Library limits what it can ask the Library to do, but not what the program does on its own, so only turn on plugins you trust.";
    private const string SandboxedNote = "This plugin runs in a sandbox with limited access.";
    private const string OfficialNote = "This plugin ships with the Library.";

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
        ["plugin-records"] = "Keeps its own records in the Library's data store",
        ["scheduled-work"] = "Runs its own recurring jobs, listed in Scheduled tasks",
        ["library-events"] = "Is told when things happen in the Library (items added, ingestion finished, metadata changes)",
        ["watch-activity"] = "Is told what people watch, read and listen to",
        ["background-work"] = "Runs continuously in the background while switched on",
        ["plugin-pages"] = "Adds pages and menu entries to the Library",
    };

    private static readonly IReadOnlyDictionary<string, string> AiRoleNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["text_fast"] = "write text (quick)",
        ["text_quality"] = "write text (careful)",
        ["text_scholar"] = "write text (long)",
        ["text_cjk"] = "write text (careful, Chinese, Japanese, Korean)",
    };

    // ── State card words ─────────────────────────────────────────────────────

    /// <summary>The plain words for one plugin permission id; an id the Library does not know is shown as it is.</summary>
    public static string PermissionText(string permissionId) =>
        PermissionWords.TryGetValue(permissionId, out var words) ? words : permissionId;

    /// <summary>
    /// The lines for one permission. Internet access with declared destinations is one "Contacts host: what it sends" line
    /// per destination instead of the general line; every other permission is its plain words.
    /// </summary>
    public static IEnumerable<string> PermissionLines(string permissionId, IReadOnlyList<PluginNetworkDestination> network)
    {
        ArgumentNullException.ThrowIfNull(network);
        return network.Count > 0 && IsNetworkHttp(permissionId)
            ? network.Select(DestinationLine)
            : [PermissionText(permissionId)];
    }

    /// <summary>
    /// What a plugin may do: one line per declared permission, per tool it needs ("Needs FFmpeg"), per Local AI ability it
    /// asks for ("Uses Local AI: write text (quick)", sorted), when it declares keys or logins which ones by label, and how
    /// it runs ("Runs as: separate program"). Duplicate lines are shown once. Every line is part of the fingerprint, so a
    /// plugin that asks for another AI ability, or runs another way, is asked about again.
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

            lines.AddRange(PermissionLines(permission, manifest.Network));
        }

        lines.AddRange(manifest.ToolRequirements.Select(tool => $"Needs {ToolName(tool)}"));
        // Who sees each page is part of what the administrator approves, so widening an audience asks again.
        lines.AddRange(PluginPageDeclarations.Read(manifest).Pages.Select(page =>
            $"Shows the page '{page.Title}' to {PluginPageDeclarations.AudienceWords(page.Audience)}"));
        lines.AddRange(LibraryKindLines(manifest));
        lines.AddRange(manifest.AiPermissions
            .Select(permission => $"{UsesLocalAi}: {AiRoleName(permission.Role)}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));

        if (declaresSecrets)
        {
            var labels = manifest.Secrets
                .Select(secret => string.IsNullOrWhiteSpace(secret.Label) ? secret.Name : secret.Label.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            lines.Add($"{KeepsOwnKeysAndLogins}: {string.Join(", ", labels)}");
        }

        lines.AddRange(AccessLines(manifest.Declaration, manifest.Network.Count > 0, manifest.MediaTypes, manifest.Category, manifest.ToolRequirements));
        lines.AddRange(ProgramLines(manifest));

        lines.Add($"{RunsAs}: {PluginKindName(manifest.Kind)}");
        return lines.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Who may use what the plugin provides, how it treats Vault items and what it adds to the Library's screens (Plugin
    /// architecture spec D3 and D13). Every line is part of the fingerprint, so a plugin that widens its audience, starts
    /// working on Vault items, adds an action or a display slot, or needs something new is asked about again. A declaration that
    /// states nothing still gets its lines ("administrators"), so adding a statement later is a visible change.
    /// </summary>
    internal static IEnumerable<string> AccessLines(
        PluginDeclaration? declaration,
        bool sendsDataOut,
        IEnumerable<string>? mediaTypes,
        string? category,
        IReadOnlyList<PluginToolRequirement>? toolRequirements = null)
    {
        var resolved = PluginDeclarationRules.Resolve(declaration, sendsDataOut, mediaTypes, category);
        yield return $"Can be used by {PluginDeclarationRules.AudienceWords(resolved.Audience)}";
        yield return $"Vault items: {PluginDeclarationRules.VaultWords(resolved.VaultUse)}";

        foreach (var requirement in declaration?.Requires ?? [])
        {
            // A tool the manifest also lists in tool_requirements is already described by its "Needs FFmpeg" line.
            if (string.Equals(requirement.Kind, "tool", StringComparison.OrdinalIgnoreCase)
                && (toolRequirements ?? []).Any(tool => string.Equals(tool.Id, requirement.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var range = string.IsNullOrWhiteSpace(requirement.VersionRange) ? "" : $" {requirement.VersionRange}";
            yield return $"{(requirement.Optional ? "Works better with" : "Needs")} {RequirementKindName(requirement.Kind)} {requirement.Id}{range}";
        }

        // How heavy it is, how it fails and what removal does are part of what is approved too (D13).
        yield return $"Weight: {PluginDeclarationRules.WeightWord(resolved.WeightClass)}";
        yield return resolved.RetryOnFailure ? "When it fails: skipped and retried later" : "When it fails: skipped";
        yield return resolved.TellMeOnFailure ? "Failures raise a notification" : "Failures are quiet";
        if (declaration?.Stores is { } stores)
        {
            if (stores.Records)
            {
                yield return "Keeps its own records in the Library";
            }

            if (!string.IsNullOrWhiteSpace(stores.SavedOutput))
            {
                yield return $"Saves {stores.SavedOutput.Trim()} in the Library; removal {(resolved.RemoveOutputOnRemoval ? "removes" : "keeps")} them by default";
            }
        }

        foreach (var slot in (declaration?.DisplaySlots ?? []).Select(slot => slot.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            yield return $"Adds information to: {slot.Replace('_', ' ')}";
        }

        foreach (var action in declaration?.Actions ?? [])
        {
            var audience = PluginDeclarationRules.ActionAudience(action, resolved.Audience);
            var slot = (action.Slot ?? "item_menu").Trim().ToLowerInvariant().Replace('_', ' ');
            var effect = PluginDeclarationRules.ActionEdits(action) ? "changes things, desktop only" : "read only";
            yield return $"Adds the action '{action.Label.Trim()}' ({action.Kind.Trim().ToLowerInvariant().Replace('_', ' ')}, {slot}, {effect}) for {PluginDeclarationRules.AudienceWords(audience)}";
        }

        if (declaration?.TextTrack is { } track)
        {
            yield return $"Supplies {track.Trim().ToLowerInvariant()} (the Library saves them; the plugin never writes to your folders)";
        }

        foreach (var (name, scope) in declaration?.CredentialScopes ?? [])
        {
            if (string.Equals(scope, "personal", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"Each profile links its own {name}";
            }
        }
    }

    private static string RequirementKindName(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "plugin" => "plugin",
        "local_ai" => "Local AI",
        "source" => "source",
        "host_service" => "host service",
        _ => kind,
    };

    /// <summary>
    /// The library kinds a JSON plugin adds, in plain words: its name, where it appears and where its details come from. Read
    /// leniently from the manifest (a malformed entry is described as far as it goes; the plugin is refused when it loads).
    /// </summary>
    private static IEnumerable<string> LibraryKindLines(PluginManifest manifest)
    {
        if (manifest.Declarative is not { ValueKind: JsonValueKind.Object } declarative
            || !declarative.TryGetProperty("library_kinds", out var kinds)
            || kinds.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        static string? Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        foreach (var kind in kinds.EnumerateArray())
        {
            var label = Text(kind, "label") ?? Text(kind, "key") ?? "(unnamed)";
            var area = Text(kind, "area") is { Length: > 0 } areaName ? char.ToUpperInvariant(areaName[0]) + areaName[1..].ToLowerInvariant() : "a library group";
            var providers = kind.ValueKind == JsonValueKind.Object && kind.TryGetProperty("lookup", out var lookup)
                && lookup.ValueKind == JsonValueKind.Object && lookup.TryGetProperty("providers", out var named) && named.ValueKind == JsonValueKind.Array
                    ? named.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
                    : [];
            yield return providers.Count == 0
                ? $"Adds the library type '{label}' under {area}, with no lookup (files are shown by folder and file name)"
                : $"Adds the library type '{label}' under {area}, looking details up from {string.Join(", ", providers)}";
        }
    }

    /// <summary>
    /// What a separate-program plugin runs or connects to, in full and never shortened: the program, each argument on its own
    /// line (so ["-c", "a b"] and ["-c", "a", "b"] read differently), and the two paths the Library calls on it. All of it is
    /// part of the fingerprint, so changing any of it asks again.
    /// </summary>
    private static IEnumerable<string> ProgramLines(PluginManifest manifest)
    {
        if (manifest.Kind != PluginKind.Service || manifest.Service is not { ValueKind: System.Text.Json.JsonValueKind.Object } service)
        {
            yield break;
        }

        static string? Text(System.Text.Json.JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;

        if (Text(service, "command") is { } command)
        {
            yield return $"Starts a program on this machine: {command}";
            if (service.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var number = 0;
                foreach (var argument in arguments.EnumerateArray().Where(item => item.ValueKind == System.Text.Json.JsonValueKind.String))
                {
                    number++;
                    yield return $"Program argument {number}: {System.Text.Json.JsonSerializer.Serialize(argument.GetString())}";
                }
            }
        }
        else if (Text(service, "address") is { } address)
        {
            yield return $"Connects to a program at {address}";
        }

        yield return $"Asks the program at: {Text(service, "health_path") ?? "/health"} and {Text(service, "lookup_path") ?? "/lookup"}";
    }

    // A role the Library has no words for is shown by its id, so it still reads as a distinct line.
    private static string AiRoleName(string role) => AiRoleNames.TryGetValue(role, out var name) ? name : role;

    private static string PluginKindName(PluginKind kind) => kind switch
    {
        PluginKind.Official => "Official built-in",
        PluginKind.Declarative => "JSON-only plugin",
        PluginKind.Service => "separate program",
        PluginKind.Sandboxed => "sandboxed plugin",
        _ => kind.ToString(),
    };

    /// <summary>
    /// The hosts the plugin declares it contacts, separated by commas, when it can reach the internet; "the internet" for a
    /// plugin that can but does not say where (a plugin directory entry that predates the network list); otherwise null
    /// (nothing leaves this machine).
    /// </summary>
    public static string? PluginSendsDataTo(PluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.Permissions.Any(IsNetworkHttp))
        {
            return null;
        }

        return manifest.Network.Count > 0
            ? string.Join(", ", manifest.Network.Select(destination => destination.Host).Distinct(StringComparer.OrdinalIgnoreCase))
            : TheInternet;
    }

    private static bool IsNetworkHttp(string permission) =>
        string.Equals(permission, PluginPermissionIds.NetworkHttp, StringComparison.OrdinalIgnoreCase);

    private static string DestinationLine(PluginNetworkDestination destination) =>
        $"Contacts {destination.Host}: {destination.Sends.Trim()}";

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
            [TrustNote(trust), manifest.Kind == PluginKind.Service ? ServiceNote : UnsandboxedNote]);
    }

    /// <summary>
    /// What a person is shown before installing a plugin from the plugin directory, built only from what the list declares.
    /// A plugin Tuvima has not checked gets the plain warning on top of the usual notes.
    /// </summary>
    public static ApprovalStatement ForDirectoryEntry(PluginManifest manifest, PluginTrustLevel trust)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var sendsTo = PluginSendsDataTo(manifest);
        var notes = new List<string> { TrustNote(trust) };
        if (trust is not (PluginTrustLevel.Official or PluginTrustLevel.Verified))
        {
            notes.Add(NotVerifiedWarning);
        }

        notes.Add(RunLocationNote(manifest.Kind));
        return new ApprovalStatement(
            string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name,
            PluginKindLabel,
            PluginAllowedTo(manifest),
            sendsTo is not null,
            sendsTo,
            notes);
    }

    // A listed plugin is described by how it runs, so a description file is not told it runs inside the server.
    private static string RunLocationNote(PluginKind kind) => kind switch
    {
        PluginKind.Declarative => DeclarativeNote,
        PluginKind.Service => ServiceNote,
        PluginKind.Sandboxed => SandboxedNote,
        _ => OfficialNote,
    };

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

    /// <summary>
    /// An integration whose existing keys gain permissions, or that is switched back on while it holds keys. The permission
    /// names are sorted so the same set always gives the same fingerprint.
    /// </summary>
    /// <param name="permissionNames">The human-readable name of every permission the integration's keys will hold.</param>
    /// <param name="activeKeyCount">How many keys are live now and would be affected.</param>
    public static ApprovalStatement ForApplication(string applicationName, IEnumerable<string> permissionNames, int activeKeyCount)
    {
        ArgumentNullException.ThrowIfNull(permissionNames);
        var names = permissionNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new ApprovalStatement(
            $"Keys for {applicationName}",
            ApplicationKindLabel,
            [$"Its keys can: {string.Join(", ", names)}"],
            SendsDataOut: true,
            SendsDataTo: TheSoftwareHoldingItsKeys,
            [activeKeyCount == 1 ? "Applies to 1 existing key" : $"Applies to {activeKeyCount.ToString(CultureInfo.InvariantCulture)} existing keys"]);
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
