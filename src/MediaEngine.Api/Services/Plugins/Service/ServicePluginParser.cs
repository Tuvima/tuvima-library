using System.Text.Json;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Service;

/// <summary>
/// Reads the <c>service</c> part of a program plugin's manifest and checks it against what the manifest asks permission
/// for. Every problem comes back as one plain sentence that names the item and says what to change.
/// </summary>
internal static class ServicePluginParser
{
    private static readonly string[] KnownKeys =
        ["command", "arguments", "address", "health_path", "lookup_path", "max_memory_mb", "startup_seconds"];

    public static (ServiceDefinition? Definition, string? Error) Parse(PluginManifest manifest, string? pluginDirectory)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Service is not { ValueKind: JsonValueKind.Object } root)
        {
            return (null, "A plugin that is a separate program needs a \"service\" section saying how to start it or where it is.");
        }

        var unknown = root.EnumerateObject().Select(property => property.Name).FirstOrDefault(name => !KnownKeys.Contains(name));
        if (unknown is not null)
        {
            return (null, $"The \"service\" section has an unknown setting \"{unknown}\". Use {string.Join(", ", KnownKeys)}.");
        }

        var command = Text(root, "command");
        var addressText = Text(root, "address");
        if ((command is null) == (addressText is null))
        {
            return (null, "The \"service\" section must give either a \"command\" to start the program or an \"address\" where it already runs, not both and not neither.");
        }

        Uri? address = null;
        if (command is not null)
        {
            if (!manifest.Permissions.Contains(PluginPermissionIds.ProcessExecute, StringComparer.OrdinalIgnoreCase))
            {
                return (null, "The plugin starts a program but does not ask for the process.execute permission. Add it to \"permissions\" so the approval says the plugin runs a program on this machine.");
            }

            if (command.Any(char.IsControl) || (command.Contains(' ') && !HasDirectory(command)))
            {
                return (null, "\"command\" must be one program name or a path inside the plugin's folder, with the arguments listed separately in \"arguments\".");
            }

            if (HasDirectory(command) && !IsInside(pluginDirectory, command))
            {
                return (null, "\"command\" points outside the plugin's own folder. Put the program inside the folder or use a program name found on the machine, such as python3.");
            }
        }
        else
        {
            if (!Uri.TryCreate(addressText, UriKind.Absolute, out address) || address.Scheme is not ("http" or "https"))
            {
                return (null, "\"address\" must be a web address starting with http:// or https://.");
            }

            if (!address.IsLoopback)
            {
                if (!manifest.Permissions.Contains(PluginPermissionIds.NetworkHttp, StringComparer.OrdinalIgnoreCase))
                {
                    return (null, "The plugin connects to another machine but does not ask for the network.http permission.");
                }

                if (!manifest.Network.Any(destination => string.Equals(destination.Host, address.Host, StringComparison.OrdinalIgnoreCase)))
                {
                    return (null, $"\"address\" uses {address.Host}, which is not listed in the plugin's \"network\" destinations. Declare it so the approval names it.");
                }
            }
        }

        var arguments = new List<string>();
        if (root.TryGetProperty("arguments", out var argumentList))
        {
            if (command is null)
            {
                return (null, "\"arguments\" only makes sense with a \"command\".");
            }

            if (argumentList.ValueKind != JsonValueKind.Array || argumentList.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            {
                return (null, "\"arguments\" must be a list of text values.");
            }

            arguments.AddRange(argumentList.EnumerateArray().Select(item => item.GetString() ?? string.Empty));
        }

        var healthPath = Text(root, "health_path") ?? "/health";
        var lookupPath = Text(root, "lookup_path") ?? "/lookup";
        foreach (var (name, value) in new[] { ("health_path", healthPath), ("lookup_path", lookupPath) })
        {
            if (!value.StartsWith('/') || value.Contains("..", StringComparison.Ordinal) || value.Any(char.IsControl) || value.Contains('?'))
            {
                return (null, $"\"{name}\" must be a path on the program starting with / (for example /health).");
            }
        }

        var memory = Number(root, "max_memory_mb", ServiceDefinition.DefaultMaxMemoryMb, 32, 8192, out var memoryProblem);
        if (memoryProblem is not null)
        {
            return (null, memoryProblem);
        }

        var startup = Number(root, "startup_seconds", ServiceDefinition.DefaultStartupSeconds, 1, 300, out var startupProblem);
        return startupProblem is not null
            ? (null, startupProblem)
            : (new ServiceDefinition(command, arguments, address, healthPath, lookupPath, memory, startup), null);
    }

    private static bool HasDirectory(string command) => command.Contains('/') || command.Contains('\\');

    private static bool IsInside(string? pluginDirectory, string command)
    {
        if (string.IsNullOrEmpty(pluginDirectory))
        {
            return false;
        }

        var root = Path.GetFullPath(pluginDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, command));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static int Number(JsonElement root, string name, int fallback, int min, int max, out string? problem)
    {
        problem = null;
        if (!root.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max)
        {
            problem = $"\"{name}\" must be a whole number from {min} to {max}.";
            return fallback;
        }

        return number;
    }
}
