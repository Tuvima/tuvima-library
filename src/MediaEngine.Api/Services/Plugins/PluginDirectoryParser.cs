using System.Globalization;
using System.Text.Json;
using MediaEngine.Api.Services.Approvals;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>What was read from one copy of the plugin directory list.</summary>
/// <param name="SchemaVersion">The list's own format version (always a 2.x version here).</param>
/// <param name="UpdatedAt">When the list says it was last changed, or null.</param>
/// <param name="Entries">The entries that could be read, in the order they were listed.</param>
/// <param name="Skipped">How many entries could not be read and were left out.</param>
internal sealed record ParsedPluginDirectory(
    string SchemaVersion,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<PluginDirectoryEntryDto> Entries,
    int Skipped);

/// <summary>
/// Reads the plugin directory list (format 2.x, documented in the plugin catalog page on docs.tuvima.com). The list comes
/// from the internet, so every field is read defensively: an entry that cannot be understood is skipped and counted, and
/// never fails the whole list. Only a file that is not a version 2 list at all is refused.
/// </summary>
internal static class PluginDirectoryParser
{
    public const string SupportedMajorVersion = "2";
    public const string DefaultMinimumLibraryVersion = "1.0.0";

    private const int MaxIdLength = 128;
    private const int MaxTextLength = 2000;
    private const int MaxListLength = 64;

    private static readonly string[] KnownKinds = ["official", "declarative", "service", "sandboxed"];

    /// <summary>Reads a list. Returns null, with a short reason for the log, when the document is not a readable version 2 list.</summary>
    public static ParsedPluginDirectory? TryParse(JsonElement root, out string? problem)
    {
        problem = null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            problem = "The list is not a JSON object.";
            return null;
        }

        var schemaVersion = ReadString(root, "schema_version");
        if (schemaVersion is null || !schemaVersion.StartsWith(SupportedMajorVersion + ".", StringComparison.Ordinal))
        {
            problem = $"The list uses format '{schemaVersion ?? "(none)"}', not {SupportedMajorVersion}.x.";
            return null;
        }

        if (!root.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
        {
            problem = "The list has no 'plugins' array.";
            return null;
        }

        var entries = new List<PluginDirectoryEntryDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var element in plugins.EnumerateArray())
        {
            var entry = TryReadEntry(element);
            if (entry is null || !seen.Add(entry.Id))
            {
                skipped++;
                continue;
            }

            entries.Add(entry);
        }

        return new ParsedPluginDirectory(schemaVersion, ReadDate(root, "updated_at"), entries, skipped);
    }

    /// <summary>The trust the Library gives a listed plugin. Anything missing or not recognised is community (not verified).</summary>
    public static string NormalizeTrust(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "official" => "official",
        "verified" => "verified",
        _ => "community",
    };

    private static PluginDirectoryEntryDto? TryReadEntry(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = ReadString(element, "id")?.Trim();
        var name = ReadString(element, "name")?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength || id.Any(char.IsWhiteSpace) || string.IsNullOrEmpty(name))
        {
            return null;
        }

        // Without a kind the Library cannot say how the plugin runs, so it cannot describe it honestly.
        var kind = ReadString(element, "kind")?.Trim().ToLowerInvariant();
        if (kind is null || !KnownKinds.Contains(kind, StringComparer.Ordinal))
        {
            return null;
        }

        var permissions = ReadStrings(element, "permissions");

        // An entry that says where it connects must say it well and must hold the internet permission; one that does not
        // say at all is still listed (older lists), and is described only as connecting to the internet.
        var network = ReadNetwork(element);
        if (network is null || (network.Count > 0 && PluginNetworkRules.Validate(permissions, network) is not null))
        {
            return null;
        }

        var trust = NormalizeTrust(ReadString(element, "trust_level"));
        return new PluginDirectoryEntryDto
        {
            Id = id,
            Name = Limit(name),
            Author = Limit(ReadString(element, "author")?.Trim() ?? ""),
            Description = Limit(ReadString(element, "description")?.Trim() ?? ""),
            Version = Limit(ReadString(element, "version")?.Trim() ?? ""),
            Kind = kind,
            TrustLevel = trust,
            TrustWarning = trust == "community" ? ApprovalStatementBuilder.NotVerifiedWarning : null,
            Permissions = permissions,
            AllowedTo = permissions.SelectMany(permission => ApprovalStatementBuilder.PermissionLines(permission, network)).Distinct(StringComparer.Ordinal).ToList(),
            Network = network.Select(destination => new PluginNetworkDestinationDto { Host = destination.Host, Sends = destination.Sends.Trim() }).ToList(),
            Capabilities = ReadStrings(element, "capabilities"),
            MinimumLibraryVersion = ReadString(element, "minimum_library_version")?.Trim() is { Length: > 0 } minimum
                ? Limit(minimum)
                : DefaultMinimumLibraryVersion,
            MaximumLibraryVersion = ReadString(element, "maximum_library_version")?.Trim() is { Length: > 0 } maximum
                ? Limit(maximum)
                : null,
            SupportedPlatforms = ReadStrings(element, "supported_platforms"),
            RepositoryUrl = ReadWebAddress(element, "repository_url"),
            ReleaseUrl = ReadWebAddress(element, "release_url"),
            PackageUrl = ReadWebAddress(element, "package_url"),
            Sha256 = ReadChecksum(element, "sha256"),
            Signature = ReadString(element, "signature")?.Trim() is { Length: > 0 and <= 512 } signature ? signature : null,
            AddedAt = ReadDate(element, "added_at"),
        };
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> ReadStrings(JsonElement element, string property)
    {
        var result = new List<string>();
        if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (result.Count >= MaxListLength)
                {
                    break;
                }

                if (item.ValueKind == JsonValueKind.String && item.GetString()?.Trim() is { Length: > 0 } text)
                {
                    result.Add(Limit(text));
                }
            }
        }

        return result.Distinct(StringComparer.Ordinal).ToList();
    }

    // Absent means the list does not say where the plugin connects (an empty list). Anything present but not a list of
    // { host, sends } objects makes the whole entry unreadable (null), so a bad declaration is never shown as a smaller one.
    private static List<PluginNetworkDestination>? ReadNetwork(JsonElement element)
    {
        if (!element.TryGetProperty("network", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxListLength)
        {
            return null;
        }

        var result = new List<PluginNetworkDestination>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            int? rate = null;
            if (item.TryGetProperty("max_requests_per_minute", out var rateValue) && rateValue.ValueKind != JsonValueKind.Null)
            {
                if (rateValue.ValueKind != JsonValueKind.Number || !rateValue.TryGetInt32(out var parsed))
                {
                    return null;
                }

                rate = parsed;
            }

            result.Add(new PluginNetworkDestination
            {
                Host = ReadString(item, "host") ?? "",
                Sends = ReadString(item, "sends") ?? "",
                MaxRequestsPerMinute = rate,
            });
        }

        return result;
    }

    // The Dashboard shows these as links, so only a plain https address is kept.
    private static string? ReadWebAddress(JsonElement element, string property) =>
        Uri.TryCreate(ReadString(element, property)?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;

    private static string? ReadChecksum(JsonElement element, string property)
    {
        var text = ReadString(element, property)?.Trim().ToLowerInvariant();
        return text is { Length: 64 } && text.All(Uri.IsHexDigit) ? text : null;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string property) =>
        DateTimeOffset.TryParse(ReadString(element, property), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;

    private static string Limit(string text) => text.Length <= MaxTextLength ? text : text[..MaxTextLength];
}
