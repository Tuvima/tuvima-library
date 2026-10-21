using System.Text.Json;
using System.Text.RegularExpressions;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Sandbox;

/// <summary>
/// Reads the <c>sandbox</c> part of a WebAssembly plugin's manifest and checks it against what the manifest asks permission
/// for. Every problem comes back as one plain sentence that names the item and says what to change.
/// </summary>
internal static partial class SandboxPluginParser
{
    private static readonly string[] KnownKeys = ["module", "jobs", "max_memory_mb", "max_seconds", "max_steps"];

    private const long MaxModuleBytes = 16 * 1024 * 1024;
    private const long MaxTextModuleBytes = 512 * 1024;

    public static (SandboxDefinition? Definition, string? Error) Parse(PluginManifest manifest, string? pluginDirectory)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.Sandbox is not { ValueKind: JsonValueKind.Object } root)
        {
            return (null, "A sandboxed plugin needs a \"sandbox\" section naming its WebAssembly module and its jobs.");
        }

        var unknown = root.EnumerateObject().Select(property => property.Name).FirstOrDefault(name => !KnownKeys.Contains(name));
        if (unknown is not null)
        {
            return (null, $"The \"sandbox\" section has an unknown setting \"{unknown}\". Use {string.Join(", ", KnownKeys)}.");
        }

        if (!manifest.Permissions.Contains(PluginPermissionIds.ScheduledWork, StringComparer.OrdinalIgnoreCase))
        {
            return (null, "The plugin has jobs but does not ask for the scheduled-work permission. Add it to \"permissions\" so the approval says the plugin runs on a schedule.");
        }

        if (!root.TryGetProperty("module", out var moduleElement) || moduleElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(moduleElement.GetString()))
        {
            return (null, "The \"sandbox\" section needs a \"module\": the name of the .wasm file in the plugin's folder.");
        }

        var module = moduleElement.GetString()!.Trim();
        if (!(module.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase) || module.EndsWith(".wat", StringComparison.OrdinalIgnoreCase)) || module.Any(char.IsControl))
        {
            return (null, "\"module\" must be a .wasm file (or a .wat text file) inside the plugin's folder.");
        }

        var modulePath = ResolveInside(pluginDirectory, module);
        if (modulePath is null)
        {
            return (null, "\"module\" points outside the plugin's own folder. Put the module inside the folder.");
        }

        if (!File.Exists(modulePath))
        {
            return (null, $"The module \"{module}\" was not found in the plugin's folder.");
        }

        var size = new FileInfo(modulePath).Length;
        if (size > (modulePath.EndsWith(".wat", StringComparison.OrdinalIgnoreCase) ? MaxTextModuleBytes : MaxModuleBytes))
        {
            return (null, "The module is larger than a sandboxed plugin may be (16 MB for .wasm, 512 KB for a .wat text file).");
        }

        var memory = Number(root, "max_memory_mb", SandboxDefinition.DefaultMaxMemoryMb, 1, 512, out var problem);
        if (problem is not null)
        {
            return (null, problem);
        }

        var seconds = Number(root, "max_seconds", SandboxDefinition.DefaultMaxSeconds, 1, 60, out problem);
        if (problem is not null)
        {
            return (null, problem);
        }

        var steps = Number(root, "max_steps", (int)Math.Min(int.MaxValue, SandboxDefinition.DefaultMaxSteps), 1000, int.MaxValue, out problem);
        if (problem is not null)
        {
            return (null, problem);
        }

        if (!root.TryGetProperty("jobs", out var jobList) || jobList.ValueKind != JsonValueKind.Array || jobList.GetArrayLength() is 0 or > 20)
        {
            return (null, "\"jobs\" must list between 1 and 20 jobs.");
        }

        var jobs = new List<SandboxJobDefinition>();
        var index = 0;
        foreach (var item in jobList.EnumerateArray())
        {
            index++;
            if (item.ValueKind != JsonValueKind.Object)
            {
                return (null, $"jobs item {index} must be an object.");
            }

            var key = Text(item, "key");
            if (key is null || !KeyPattern().IsMatch(key))
            {
                return (null, $"jobs item {index} needs a \"key\" of 1 to 64 lower-case letters, digits, hyphens or underscores.");
            }

            if (jobs.Any(job => job.Key == key))
            {
                return (null, $"The job key \"{key}\" is used more than once.");
            }

            var name = Text(item, "name");
            if (name is null || name.Length > 80)
            {
                return (null, $"Job \"{key}\" needs a \"name\" of 1 to 80 characters.");
            }

            var export = Text(item, "export") ?? "run";
            if (!ExportPattern().IsMatch(export))
            {
                return (null, $"Job \"{key}\" has an \"export\" that is not a plain function name.");
            }

            var schedule = PluginSchedule.Every(TimeSpan.FromHours(6));
            if (item.TryGetProperty("schedule", out var scheduleElement))
            {
                if (scheduleElement.ValueKind != JsonValueKind.String
                    || !PluginSchedule.TryParse(scheduleElement.GetString(), out var parsed)
                    || parsed is null)
                {
                    return (null, $"Job \"{key}\" has a \"schedule\" that cannot be used. Use a cron text such as \"0 */6 * * *\" or an interval of at least 5 minutes such as \"6h\".");
                }

                schedule = parsed;
            }

            jobs.Add(new SandboxJobDefinition(key, name, export, schedule));
        }

        return (new SandboxDefinition(modulePath, jobs, memory, seconds, steps), null);
    }

    private static string? ResolveInside(string? pluginDirectory, string relative)
    {
        if (string.IsNullOrEmpty(pluginDirectory))
        {
            return null;
        }

        var root = Path.GetFullPath(pluginDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
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

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex ExportPattern();
}
