using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins;

internal sealed class PluginToolRuntime
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PluginToolRuntime> _logger;
    private readonly string _toolRoot;

    public PluginToolRuntime(
        IHttpClientFactory httpClientFactory,
        IConfigurationLoader configurationLoader,
        ILogger<PluginToolRuntime> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        var core = configurationLoader.LoadCore();
        var root = string.IsNullOrWhiteSpace(core.LibraryRoot)
            ? Path.GetFullPath(".data")
            : Path.Combine(core.LibraryRoot, ".data");
        _toolRoot = Path.Combine(root, "plugin-tools");
    }

    public async Task<PluginToolResolution> ResolveToolAsync(
        string pluginId,
        PluginToolRequirement requirement,
        IReadOnlyDictionary<string, JsonElement> settings,
        Action authorizeDownload,
        CancellationToken cancellationToken = default)
    {
        ValidateRequirement(requirement);
        if (TryGetSetting(settings, $"{requirement.Id}_tool_path", out var explicitPath)
            || TryGetSetting(settings, "tool_path", out explicitPath))
        {
            return File.Exists(explicitPath)
                ? new PluginToolResolution { IsAvailable = true, ExecutablePath = explicitPath, Status = "mounted", Message = "Using configured tool path." }
                : new PluginToolResolution { IsAvailable = false, Status = "missing", Message = $"Configured tool path does not exist: {explicitPath}" };
        }

        var cached = FindCachedTool(requirement);
        if (cached is not null)
        {
            return new PluginToolResolution { IsAvailable = true, ExecutablePath = cached, Status = "cached", Message = "Using cached plugin tool." };
        }

        var pathTool = FindOnPath(requirement.ExecutableName);
        if (pathTool is not null)
        {
            return new PluginToolResolution { IsAvailable = true, ExecutablePath = pathTool, Status = "path", Message = "Using tool from PATH." };
        }

        var autoInstall = TryGetBool(settings, "auto_install_tools", defaultValue: true);
        if (!autoInstall)
        {
            return new PluginToolResolution { IsAvailable = false, Status = "disabled", Message = "Automatic tool installation is disabled." };
        }

        var platform = SelectPlatform(requirement);
        if (platform is null || string.IsNullOrWhiteSpace(platform.DownloadUrl) || string.IsNullOrWhiteSpace(platform.Sha256))
        {
            return new PluginToolResolution
            {
                IsAvailable = false,
                Status = "degraded",
                Message = "No checksum-pinned download is configured for this platform. Configure tool_path or add an approved tool download.",
            };
        }

        authorizeDownload();
        var installDir = ContainedToolPath(requirement.Id, requirement.Version);
        Directory.CreateDirectory(installDir);
        var archiveName = Path.GetFileName(new Uri(platform.DownloadUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(archiveName))
        {
            throw new InvalidOperationException("Plugin tool download URL has no archive name.");
        }

        var archivePath = ContainedPath(installDir, archiveName);

        try
        {
            var client = _httpClientFactory.CreateClient("plugin_tools");
            await using (var stream = await client.GetStreamAsync(platform.DownloadUrl, cancellationToken).ConfigureAwait(false))
            {
                await using (var file = File.Create(archivePath))
                {
                    await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }

            var hash = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, platform.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(archivePath);
                return new PluginToolResolution { IsAvailable = false, Status = "checksum_failed", Message = "Downloaded tool checksum did not match manifest." };
            }

            ExtractToolPayload(archivePath, installDir);

            var executable = ContainedPath(installDir, platform.RelativeExecutablePath ?? requirement.ExecutableName);
            if (!File.Exists(executable) && IsDirectExecutable(archivePath))
            {
                File.Copy(archivePath, executable, overwrite: true);
            }

            TryMakeExecutable(executable);
            return File.Exists(executable)
                ? new PluginToolResolution { IsAvailable = true, ExecutablePath = executable, Status = "installed", Message = "Tool installed into plugin cache." }
                : new PluginToolResolution { IsAvailable = false, Status = "install_failed", Message = "Tool archive downloaded, but executable was not found." };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Plugin tool installation failed for {PluginId}/{ToolId}", pluginId, requirement.Id);
            return new PluginToolResolution { IsAvailable = false, Status = "install_failed", Message = ex.Message };
        }
    }

    public async Task<PluginToolRunResult> RunToolAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
            EnableRaisingEvents = true,
        };
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new PluginToolRunResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdout.ToString(),
                StandardError = stderr.ToString(),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new PluginToolRunResult
            {
                ExitCode = -1,
                StandardOutput = stdout.ToString(),
                StandardError = stderr.ToString(),
                TimedOut = true,
            };
        }
    }

    private string? FindCachedTool(PluginToolRequirement requirement)
    {
        var baseDir = ContainedToolPath(requirement.Id, requirement.Version);
        var candidate = ContainedPath(baseDir, requirement.ExecutableName);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
        {
            return candidate + ".exe";
        }

        var discovered = Directory.Exists(baseDir)
            ? Directory.EnumerateFiles(baseDir, requirement.ExecutableName, SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (discovered is not null)
        {
            return discovered;
        }

        if (OperatingSystem.IsWindows() && Directory.Exists(baseDir))
        {
            return Directory.EnumerateFiles(baseDir, requirement.ExecutableName + ".exe", SearchOption.AllDirectories).FirstOrDefault();
        }

        return null;
    }

    private static string? FindOnPath(string executableName)
    {
        var names = OperatingSystem.IsWindows() && !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { executableName, executableName + ".exe" }
            : new[] { executableName };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    private static PluginToolPlatform? SelectPlatform(PluginToolRequirement requirement)
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        return requirement.Platforms.FirstOrDefault(p => string.Equals(p.Rid, rid, StringComparison.OrdinalIgnoreCase))
            ?? requirement.Platforms.FirstOrDefault(p => rid.StartsWith(p.Rid, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateRequirement(PluginToolRequirement requirement)
    {
        ValidateSegment(requirement.Id, nameof(requirement.Id));
        ValidateSegment(requirement.Version, nameof(requirement.Version));
        if (Path.GetFileName(requirement.ExecutableName) != requirement.ExecutableName || string.IsNullOrWhiteSpace(requirement.ExecutableName))
        {
            throw new InvalidOperationException("Plugin tool executable_name must be a file name.");
        }

        foreach (var platform in requirement.Platforms)
        {
            if (string.IsNullOrWhiteSpace(platform.Sha256) || platform.Sha256.Length != 64 || !platform.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidOperationException("Downloaded plugin tools require a SHA-256 checksum.");
            }
        }
    }

    private string ContainedToolPath(string toolId, string version) =>
        ContainedPath(Path.GetFullPath(_toolRoot), Path.Combine(toolId, version));

    private static string ContainedPath(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Plugin tool path escapes its allowed directory.");
        }

        return candidate;
    }

    private static void ValidateSegment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
        {
            throw new InvalidOperationException($"Plugin tool {name} must be a simple path segment.");
        }
    }

    private static bool TryGetSetting(IReadOnlyDictionary<string, JsonElement> settings, string key, out string value)
    {
        value = "";
        if (!settings.TryGetValue(key, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetBool(IReadOnlyDictionary<string, JsonElement> settings, string key, bool defaultValue)
    {
        if (!settings.TryGetValue(key, out var element))
        {
            return defaultValue;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(element.GetString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ExtractToolPayload(string payloadPath, string installDir)
    {
        var name = Path.GetFileName(payloadPath);
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(payloadPath, installDir, overwriteFiles: true);
            return;
        }

        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(payloadPath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, installDir, overwriteFiles: true);
            return;
        }

        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            TarFile.ExtractToDirectory(payloadPath, installDir, overwriteFiles: true);
        }
    }

    private static bool IsDirectExecutable(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !name.Contains('.', StringComparison.Ordinal);
    }

    private void TryMakeExecutable(string path)
    {
        if (!File.Exists(path) || OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var chmod = Process.Start("chmod", ["+x", path]);
            chmod?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not mark plugin tool executable: {Path}", path);
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not terminate plugin tool process {ProcessId}", process.Id);
        }
    }
}
