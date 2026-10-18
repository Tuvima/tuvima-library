using System.Text.Json;

namespace MediaEngine.Storage;

/// <summary>
/// Finds access settings left over from older releases. They are ignored (never converted), so the Engine still
/// starts; the caller logs these messages so an administrator knows where the setting lives now.
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
                warnings.Add("network.json still has remote.enabled, which is ignored. Choose a secure outside address under Settings > Network > Remote Access instead.");
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
                    warnings.Add($"core.json still has auth.{key}, which is ignored.");
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
