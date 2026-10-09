using System.Text.Json;

namespace MediaEngine.Storage;

/// <summary>
/// Finds access settings left over from before the single "who can connect" rule. They are ignored (never converted),
/// so the Engine still starts; the caller logs these messages so an administrator knows to re-enable remote access
/// through <c>who_can_connect</c>.
/// </summary>
public static class LegacyAccessSettingsCheck
{
    public static IReadOnlyList<string> Find(string configDirectory)
    {
        var warnings = new List<string>();
        var network = ReadObject(Path.Combine(configDirectory, "network.json"));
        if (network is { } net)
        {
            if (net.TryGetProperty("remote", out var remote) && remote.ValueKind == JsonValueKind.Object
                && remote.TryGetProperty("enabled", out _))
            {
                warnings.Add("network.json still has remote.enabled, which is ignored. Set who_can_connect (this_computer, home_network or anywhere) under Settings > Network instead; remote access stays off until you choose Anywhere.");
            }

            var anywhere = net.TryGetProperty("who_can_connect", out var who)
                && who.ValueKind == JsonValueKind.String
                && string.Equals(who.GetString()?.Trim(), "anywhere", StringComparison.OrdinalIgnoreCase);
            if (!anywhere
                && net.TryGetProperty("native_app_access", out var apps) && apps.ValueKind == JsonValueKind.Object
                && apps.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)
            {
                warnings.Add("native_app_access.enabled is on but who_can_connect is not anywhere, so app access is turned off. Choose Anywhere under Settings > Network to allow apps again.");
            }
        }

        var core = ReadObject(Path.Combine(configDirectory, "core.json"));
        if (core is { } c && c.TryGetProperty("auth", out var auth) && auth.ValueKind == JsonValueKind.Object)
        {
            if (auth.TryGetProperty("password_reset", out var reset) && reset.ValueKind == JsonValueKind.Object
                && reset.TryGetProperty("public_base_url", out var legacyAddress)
                && legacyAddress.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(legacyAddress.GetString())
                && !HasPublicHostname(network))
            {
                warnings.Add("core.json still has auth.password_reset.public_base_url, which is ignored. Set the public address under Settings > Network (remote.public_hostname); passkeys, linked sign-in and reset emails stay off until you do.");
            }

            foreach (var key in new[] { "allow_remote_sign_in", "require_https_remote" })
            {
                if (auth.TryGetProperty(key, out _))
                {
                    warnings.Add($"core.json still has auth.{key}, which is ignored. Who can connect is now set under Settings > Network (who_can_connect), and remote sign-in always requires HTTPS.");
                }
            }
        }

        return warnings;
    }

    private static bool HasPublicHostname(JsonElement? network) =>
        network is { } net
        && net.TryGetProperty("remote", out var remote) && remote.ValueKind == JsonValueKind.Object
        && remote.TryGetProperty("public_hostname", out var host) && host.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(host.GetString());

    private static JsonElement? ReadObject(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable files are reported by the normal configuration loader; this check only adds hints.
            return null;
        }
    }
}
