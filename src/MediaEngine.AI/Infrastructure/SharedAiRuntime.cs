using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text.Json;
using LLama.Native;
using MediaEngine.AI.Configuration;
using Microsoft.Extensions.Logging;
using Whisper.net.LibraryLoader;

namespace MediaEngine.AI.Infrastructure;

/// <summary>Resolves versioned native installations before any inference or health probe.</summary>
public sealed class SharedAiRuntime(AiSettings settings, ILogger<SharedAiRuntime> logger)
{
    // Guarded against Directory.Packages.props by SharedAiRuntimeTests.
    public const string LlamaVersion = "0.27.0";
    public const string WhisperVersion = "1.9.1";
    private static readonly object LoadLock = new();
    private static string? _llamaPath;
    private static string? _whisperPath;
    private static string? _backend;

    public string Root => ResolveRoot(settings.NativeRuntimeDirectory);
    public string? LoadedLlamaPath => _llamaPath;
    public string? LoadedBackend => _backend;

    public static string ResolveRoot(string configured) => configured == "bundled"
        ? AppContext.BaseDirectory
        : Path.GetFullPath(string.IsNullOrEmpty(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tuvima", "AI Runtimes")
            : configured);

    public string EnsureLlama(bool? useCuda = null)
    {
        lock (LoadLock)
        {
            var root = ComponentRoot("llamasharp", LlamaVersion);
            if (_llamaPath is not null)
            {
                RequireInside(root, _llamaPath);
                return _llamaPath;
            }

            ValidateInstallation(root, LlamaVersion);
            var native = Path.Combine(root, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
            var library = OperatingSystem.IsWindows() ? "llama.dll" : OperatingSystem.IsMacOS() ? "libllama.dylib" : "libllama.so";
            var cpu = Avx512F.IsSupported ? "avx512" : Avx2.IsSupported ? "avx2" : Avx.IsSupported ? "avx" : "noavx";
            var cuda = useCuda ?? new GpuBackendDetector(Microsoft.Extensions.Logging.Abstractions.NullLogger<GpuBackendDetector>.Instance).Detect().Backend == "cuda";
            var candidates = new List<(string Path, string Backend)>();
            if (cuda)
            {
                candidates.Add((Path.Combine(native, "cuda12", library), "cuda"));
            }
            candidates.Add((Path.Combine(native, cpu, library), "cpu"));
            candidates.Add((Path.Combine(native, library), "cpu"));
            foreach (var candidate in candidates.Where(candidate => File.Exists(candidate.Path)))
            {
                if (candidate.Backend == "cuda" && OperatingSystem.IsWindows())
                {
                    try
                    {
                        var nvidia = Path.Combine(Root, "nvidia", "12.8.1");
                        if (settings.NativeRuntimeDirectory != "bundled")
                        {
                            VerifyManifest(nvidia, "12.8.1", "win-x64");
                            foreach (var name in new[] { "cudart64_12.dll", "cublasLt64_12.dll", "cublas64_12.dll" })
                            {
                                NativeLibrary.Load(Path.Combine(nvidia, "bin", name));
                            }
                        }
                        // CUDA packages omit ggml-cpu; load the matching CPU package's implementation.
                        NativeLibrary.Load(Path.Combine(native, "cuda12", "ggml-base.dll"));
                        NativeLibrary.Load(Path.Combine(native, cpu, "ggml-cpu.dll"));
                    }
                    catch (Exception ex) when (ex is DllNotFoundException or FileNotFoundException)
                    {
                        logger.LogWarning(ex, "CUDA runtime dependencies are unavailable; using the installed CPU fallback");
                        continue;
                    }
                }
                if (!NativeLibrary.TryLoad(candidate.Path, out var handle))
                {
                    logger.LogWarning("AI backend {Backend} could not load {Path}; checking CPU fallback", candidate.Backend, candidate.Path);
                    continue;
                }

                // Keep this handle for the process lifetime, matching LLamaSharp's static loader.
                // Loading by absolute path also resolves sibling native dependencies on Windows.
                NativeLibraryConfig.LLama.WithLibrary(candidate.Path);
                _llamaPath = candidate.Path;
                _backend = candidate.Backend;
                logger.LogInformation("Shared AI native runtime {Version}: {Backend}, {Path}, handle {Handle}", LlamaVersion, _backend, _llamaPath, handle);
                return _llamaPath;
            }

            throw new InvalidOperationException($"No compatible LLamaSharp {LlamaVersion} runtime can load from {native}. Run tools/Install-AiRuntime.ps1 for this platform; check driver and native dependencies.");
        }
    }

    public string EnsureWhisper()
    {
        lock (LoadLock)
        {
            var root = ComponentRoot("whisper", WhisperVersion);
            if (_whisperPath is not null)
            {
                RequireInside(root, _whisperPath);
                return _whisperPath;
            }

            ValidateInstallation(root, WhisperVersion);
            var library = OperatingSystem.IsWindows() ? "whisper.dll" : OperatingSystem.IsMacOS() ? "libwhisper.dylib" : "libwhisper.so";
            var path = Path.Combine(root, "runtimes", RuntimeInformation.RuntimeIdentifier, library);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Install the optional Whisper runtime with tools/Install-AiRuntime.ps1 -Whisper.", path);
            }
            // Whisper 1.9.1 takes the parent of this hint, then appends runtimes/<rid>.
            // Supply a file-shaped hint at the component root, not the native DLL itself.
            RuntimeOptions.LibraryPath = Path.Combine(root, "Whisper.net.dll");
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
            _whisperPath = path;
            return path;
        }
    }

    private string ComponentRoot(string component, string version) => settings.NativeRuntimeDirectory == "bundled"
        ? Root : Path.Combine(Root, component, version);

    private void ValidateInstallation(string root, string version)
    {
        if (settings.NativeRuntimeDirectory == "bundled")
        {
            return;
        }
        VerifyManifest(root, version, RuntimeInformation.RuntimeIdentifier);
    }

    public static void VerifyManifest(string root, string version, string rid)
    {
        var manifest = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifest))
        {
            throw new FileNotFoundException("Shared AI runtime is not installed. Run tools/Install-AiRuntime.ps1 with the configured runtime directory.", manifest);
        }
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var data = document.RootElement;
        if (data.GetProperty("version").GetString() != version || data.GetProperty("rid").GetString() != rid)
        {
            throw new InvalidDataException($"AI runtime version/platform mismatch in {manifest}; expected {version}/{rid}.");
        }
        var files = data.GetProperty("files").EnumerateArray().ToArray();
        if (files.Length == 0)
        {
            throw new InvalidDataException($"Empty AI runtime manifest: {manifest}");
        }
        foreach (var file in files)
        {
            var path = Path.GetFullPath(Path.Combine(root, file.GetProperty("path").GetString()!));
            RequireInside(root, path);
            using var stream = File.OpenRead(path);
            if (stream.Length != file.GetProperty("size").GetInt64()
                || !Convert.ToHexString(SHA256.HashData(stream)).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Shared AI runtime verification failed: {path}");
            }
        }
    }

    private static void RequireInside(string root, string path)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI runtime path changed or escaped its installation. Restart the Engine after changing runtime configuration.");
        }
    }
}
