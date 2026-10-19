using System.Globalization;
using System.Runtime.InteropServices;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>Whether a plugin can be switched on here, and if not, why in plain English.</summary>
public sealed record PluginCompatibilityResult(bool IsCompatible, string? Problem)
{
    public static PluginCompatibilityResult Compatible { get; } = new(true, null);

    public static PluginCompatibilityResult Incompatible(string problem) => new(false, problem);
}

/// <summary>
/// Checks a plugin's declared kind, Library version range and platforms against this Library and this machine.
/// </summary>
public static class PluginCompatibility
{
    private static readonly string[] OsFamilies = ["windows", "linux", "macos"];
    private static readonly string[] RidOperatingSystems = ["win", "linux", "linux-musl", "osx"];
    private static readonly string[] RidArchitectures = ["x86", "x64", "arm", "arm64"];

    /// <summary>The runtime identifier of this machine in portable form, for example <c>win-x64</c> or <c>linux-arm64</c>.</summary>
    public static string CurrentRid { get; } = ComputeCurrentRid();

    /// <summary>
    /// Checks the parts of a manifest that must be well formed before it can be judged: version strings and
    /// platform names. Returns a load-error message, or null when the manifest is well formed.
    /// </summary>
    public static string? ValidateManifest(PluginManifest manifest)
    {
        if (!TryParseVersion(manifest.MinimumLibraryVersion, out _))
        {
            return $"Plugin manifest has an invalid minimum_library_version '{manifest.MinimumLibraryVersion}'. Use major.minor.patch, for example 1.0.0.";
        }

        if (manifest.MaximumLibraryVersion is not null && !TryParseVersion(manifest.MaximumLibraryVersion, out _))
        {
            return $"Plugin manifest has an invalid maximum_library_version '{manifest.MaximumLibraryVersion}'. Use major.minor.patch, for example 1.0.0.";
        }

        foreach (var platform in manifest.SupportedPlatforms)
        {
            if (!IsKnownPlatform(platform))
            {
                return $"Plugin manifest lists an unknown supported platform '{platform}'. Use an operating system (windows, linux, macos) or a runtime identifier such as win-x64 or linux-arm64.";
            }
        }

        return null;
    }

    /// <summary>
    /// Decides whether the plugin may run on this Library and machine.
    /// </summary>
    /// <param name="manifest">The plugin's description.</param>
    /// <param name="libraryVersion">This Library's version (major.minor.patch, any prerelease or build suffix is ignored).</param>
    /// <param name="currentRid">This machine's runtime identifier, for example <c>win-x64</c>.</param>
    /// <param name="loadError">A problem already found while loading the plugin; reported as is.</param>
    public static PluginCompatibilityResult Evaluate(
        PluginManifest manifest,
        string libraryVersion,
        string currentRid,
        string? loadError = null)
    {
        if (!string.IsNullOrWhiteSpace(loadError))
        {
            return PluginCompatibilityResult.Incompatible(loadError);
        }

        var invalid = ValidateManifest(manifest);
        if (invalid is not null)
        {
            return PluginCompatibilityResult.Incompatible(invalid);
        }

        if (manifest.Kind != PluginKind.Official)
        {
            return PluginCompatibilityResult.Incompatible(
                $"This kind of plugin ({KindName(manifest.Kind)}) is not supported by this version of the Library yet.");
        }

        if (!TryParseVersion(libraryVersion, out var current))
        {
            return PluginCompatibilityResult.Incompatible($"Could not read this Library's version ('{libraryVersion}').");
        }

        TryParseVersion(manifest.MinimumLibraryVersion, out var minimum);
        if (current < minimum)
        {
            return PluginCompatibilityResult.Incompatible(
                $"Needs Library {manifest.MinimumLibraryVersion} or later; this is {libraryVersion}.");
        }

        if (manifest.MaximumLibraryVersion is not null)
        {
            TryParseVersion(manifest.MaximumLibraryVersion, out var maximum);
            if (current > maximum)
            {
                return PluginCompatibilityResult.Incompatible(
                    $"Works only up to Library {manifest.MaximumLibraryVersion}; this is {libraryVersion}.");
            }
        }

        if (manifest.SupportedPlatforms.Count > 0
            && !manifest.SupportedPlatforms.Any(platform => PlatformMatches(platform, currentRid)))
        {
            return PluginCompatibilityResult.Incompatible($"Not available on {currentRid}.");
        }

        return PluginCompatibilityResult.Compatible;
    }

    /// <summary>
    /// Parses major.minor.patch, ignoring any <c>-prerelease</c> or <c>+build</c> suffix. Anything else is malformed.
    /// </summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim().Split('+')[0].Split('-')[0];
        var parts = core.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    private static bool IsKnownPlatform(string platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return false;
        }

        if (OsFamilies.Contains(platform, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var os in RidOperatingSystems)
        {
            foreach (var architecture in RidArchitectures)
            {
                if (string.Equals(platform, $"{os}-{architecture}", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool PlatformMatches(string declared, string currentRid)
    {
        if (string.Equals(declared, currentRid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = declared.ToLowerInvariant() switch
        {
            "windows" => "win-",
            "linux" => "linux-", // also matches linux-musl-*
            "macos" => "osx-",
            _ => null,
        };

        return prefix is not null && currentRid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string KindName(PluginKind kind) => kind switch
    {
        PluginKind.Official => "official",
        PluginKind.Declarative => "declarative",
        PluginKind.Service => "service",
        PluginKind.Sandboxed => "sandboxed",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string ComputeCurrentRid()
    {
        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            var other => other.ToString().ToLowerInvariant(),
        };
        return $"{os}-{architecture}";
    }
}
