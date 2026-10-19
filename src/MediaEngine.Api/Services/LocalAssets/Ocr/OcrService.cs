using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.LocalAssets.Ocr;

/// <summary>
/// Runs Tesseract as a child process. Detection order (first found wins): an explicit path from
/// <c>config/ocr.json</c> (a missing explicit path fails detection instead of falling back),
/// <c>tools/tesseract/</c> in the Tuvima data folder, <c>tools/tesseract/</c> beside the app, the
/// system PATH, then the operating system's standard install folders. Language data lives in the
/// Tuvima data location, seeded once from the helper's own English data.
/// </summary>
public sealed partial class OcrService : IOcrService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly string[] SeedFiles = ["eng.traineddata", "osd.traineddata"];

    private readonly IConfigurationLoader _configuration;
    private readonly IOcrProcessRunner _runner;
    private readonly OcrEnvironment _environment;
    private readonly ILogger<OcrService> _logger;
    private readonly SemaphoreSlim _detectGate = new(1, 1);
    private OcrCapability? _cached;
    private string? _cachedKey;
    private DateTimeOffset _cachedAt;

    public OcrService(
        IConfigurationLoader configuration,
        AssetPathService paths,
        IOcrProcessRunner runner,
        ILogger<OcrService> logger,
        OcrEnvironment? environment = null,
        TuvimaDataPaths? dataPaths = null)
    {
        _configuration = configuration;
        _runner = runner;
        _logger = logger;
        _environment = environment ?? OcrEnvironment.Current(dataPaths?.Root);
        LanguageDataDirectory = Path.Combine(paths.DataRoot, "ocr", "tessdata");
        TemporaryRoot = Path.Combine(paths.DataRoot, "ocr", "tmp");
    }

    public string LanguageDataDirectory { get; }

    public string TemporaryRoot { get; }

    public void Invalidate()
    {
        _cached = null;
        _cachedKey = null;
    }

    public async Task<OcrCapability> GetCapabilityAsync(CancellationToken ct = default)
    {
        var settings = _configuration.LoadOcr();
        var key = settings.TesseractBinaryPath ?? string.Empty;
        if (_cached is not null && key == _cachedKey && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
        {
            return _cached;
        }

        await _detectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var capability = await DetectAsync(settings, ct).ConfigureAwait(false);
            _cached = capability;
            _cachedKey = key;
            _cachedAt = DateTimeOffset.UtcNow;
            return capability;
        }
        finally
        {
            _detectGate.Release();
        }
    }

    public async Task<OcrRecognition> RecognizeAsync(string imagePath, OcrRunOptions options, CancellationToken ct = default)
    {
        var capability = await RequireCapabilityAsync(ct).ConfigureAwait(false);
        var outputBase = Path.Combine(Path.GetDirectoryName(imagePath)!, Path.GetFileNameWithoutExtension(imagePath) + "-out");
        var result = await _runner.RunAsync(new OcrProcessRequest(
            capability.ExecutablePath!,
            [
                imagePath, outputBase,
                "-l", string.Join('+', options.Languages),
                "--tessdata-dir", capability.TessdataDirectory!,
                "--psm", "3",
                "-c", "tessedit_create_txt=1",
                "-c", "tessedit_create_tsv=1",
            ],
            options.Timeout,
            options.LowPriority,
            ThreadEnvironment(options)), ct).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new TimeoutException("Text recognition took too long for one page.");
        }

        if (result.ExitCode != 0)
        {
            // The helper's own messages are not logged: they can quote the text it was reading.
            throw new InvalidOperationException($"Text recognition failed (exit code {result.ExitCode}).");
        }

        var text = await ReadIfPresentAsync(outputBase + ".txt", ct).ConfigureAwait(false);
        var tsv = await ReadIfPresentAsync(outputBase + ".tsv", ct).ConfigureAwait(false);
        TryDelete(outputBase + ".txt");
        TryDelete(outputBase + ".tsv");
        var (confidence, words) = ParseConfidence(tsv);
        return new OcrRecognition(text.Trim(), confidence, words);
    }

    public async Task<int?> DetectRotationAsync(string imagePath, OcrRunOptions options, CancellationToken ct = default)
    {
        var capability = await RequireCapabilityAsync(ct).ConfigureAwait(false);
        if (!capability.HasOrientationData)
        {
            return null;
        }

        var outputBase = Path.Combine(Path.GetDirectoryName(imagePath)!, Path.GetFileNameWithoutExtension(imagePath) + "-osd");
        var result = await _runner.RunAsync(new OcrProcessRequest(
            capability.ExecutablePath!,
            [imagePath, outputBase, "--psm", "0", "-l", "osd", "--tessdata-dir", capability.TessdataDirectory!],
            options.Timeout,
            options.LowPriority,
            ThreadEnvironment(options)), ct).ConfigureAwait(false);
        var report = await ReadIfPresentAsync(outputBase + ".osd", ct).ConfigureAwait(false);
        TryDelete(outputBase + ".osd");
        if (result.ExitCode != 0)
        {
            // Orientation is a best-effort second opinion; a page it cannot judge is simply left as read.
            return null;
        }

        var match = RotateLine().Match(report);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var degrees)
            ? degrees
            : null;
    }

    internal static (double? Confidence, int Words) ParseConfidence(string tsv)
    {
        if (string.IsNullOrWhiteSpace(tsv))
        {
            return (null, 0);
        }

        double total = 0;
        var words = 0;
        foreach (var line in tsv.Split('\n'))
        {
            var columns = line.TrimEnd('\r').Split('\t');
            if (columns.Length < 12 || columns[0] != "5")
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(columns[11])
                || !double.TryParse(columns[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence)
                || confidence < 0)
            {
                continue;
            }

            total += confidence;
            words++;
        }

        return words == 0 ? (null, 0) : (Math.Round(total / words, 1, MidpointRounding.AwayFromZero), words);
    }

    private async Task<OcrCapability> RequireCapabilityAsync(CancellationToken ct)
    {
        var capability = await GetCapabilityAsync(ct).ConfigureAwait(false);
        return capability.IsAvailable
            ? capability
            : throw new InvalidOperationException(capability.Reason ?? "Text recognition is not installed.");
    }

    private async Task<OcrCapability> DetectAsync(OcrSettings settings, CancellationToken ct)
    {
        var executable = Locate(settings.TesseractBinaryPath, out var explicitMissing);
        if (explicitMissing)
        {
            return OcrCapability.Unavailable("The text recognition path in settings does not exist.") with
            {
                ExplicitPathMissing = true,
            };
        }

        if (executable is null)
        {
            return OcrCapability.Unavailable("Text recognition is not installed.");
        }

        OcrProcessResult probe;
        try
        {
            probe = await _runner.RunAsync(
                new OcrProcessRequest(executable, ["--version"], TimeSpan.FromSeconds(15)), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "The text recognition helper at {Path} could not be started.", executable);
            return OcrCapability.Unavailable("The text recognition helper could not be started.") with { ExecutablePath = executable };
        }

        if (probe.TimedOut || probe.ExitCode != 0)
        {
            return OcrCapability.Unavailable("The text recognition helper did not respond.") with { ExecutablePath = executable };
        }

        var version = ParseVersion(probe.StandardOutput + "\n" + probe.StandardError);
        SeedLanguageData(executable);
        var languages = ListInstalledLanguages();
        if (languages.Count == 0)
        {
            return new OcrCapability(false, executable, version, LanguageDataDirectory, [], false,
                "Text recognition has no language data installed.");
        }

        var hasOsd = File.Exists(Path.Combine(LanguageDataDirectory, "osd.traineddata"));
        _logger.LogInformation("Text recognition helper located: {Path} ({Version}).", executable, version ?? "unknown version");
        return new OcrCapability(true, executable, version, LanguageDataDirectory, languages, hasOsd, null);
    }

    private string? Locate(string configuredPath, out bool explicitMissing)
    {
        explicitMissing = false;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
            {
                return Path.GetFullPath(configuredPath);
            }

            explicitMissing = true;
            return null;
        }

        var fileName = OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract";
        if (!string.IsNullOrWhiteSpace(_environment.DataRoot))
        {
            var fetched = Path.Combine(_environment.DataRoot, "tools", "tesseract", fileName);
            if (File.Exists(fetched))
            {
                return fetched;
            }
        }

        var directory = new DirectoryInfo(_environment.BaseDirectory);
        for (var depth = 0; depth < 6 && directory is not null; depth++)
        {
            var packaged = Path.Combine(directory.FullName, "tools", "tesseract", fileName);
            if (File.Exists(packaged))
            {
                return packaged;
            }

            directory = directory.Parent;
        }

        foreach (var folder in _environment.PathDirectories.Concat(_environment.StandardFolders ?? []))
        {
            var candidate = Path.Combine(folder, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private void SeedLanguageData(string executable)
    {
        try
        {
            Directory.CreateDirectory(LanguageDataDirectory);
            foreach (var file in SeedFiles)
            {
                var target = Path.Combine(LanguageDataDirectory, file);
                if (File.Exists(target))
                {
                    continue;
                }

                var source = SeedSources(executable)
                    .Select(directory => Path.Combine(directory, file))
                    .FirstOrDefault(File.Exists);
                if (source is not null)
                {
                    File.Copy(source, target);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not prepare text recognition language data in {Directory}.", LanguageDataDirectory);
        }
    }

    private static IEnumerable<string> SeedSources(string executable)
    {
        var beside = Path.GetDirectoryName(executable);
        if (beside is not null)
        {
            yield return Path.Combine(beside, "tessdata");
        }

        var prefix = Environment.GetEnvironmentVariable("TESSDATA_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            yield return prefix;
            yield return Path.Combine(prefix, "tessdata");
        }

        foreach (var root in new[] { "/usr/share/tesseract-ocr", "/usr/share/tessdata", "/usr/local/share/tessdata" })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            yield return root;
            foreach (var versioned in Directory.EnumerateDirectories(root))
            {
                yield return Path.Combine(versioned, "tessdata");
            }
        }
    }

    private List<string> ListInstalledLanguages()
    {
        if (!Directory.Exists(LanguageDataDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(LanguageDataDirectory, "*.traineddata")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(code => !string.IsNullOrWhiteSpace(code) && !string.Equals(code, "osd", StringComparison.OrdinalIgnoreCase))
            .Select(code => code!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyDictionary<string, string>? ThreadEnvironment(OcrRunOptions options) =>
        options.LowPriority ? new Dictionary<string, string> { ["OMP_THREAD_LIMIT"] = "1" } : null;

    private static async Task<string> ReadIfPresentAsync(string path, CancellationToken ct) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false) : string.Empty;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The per-file scratch folder is removed as a whole afterwards.
        }
    }

    private static string? ParseVersion(string output)
    {
        var match = VersionLine().Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"tesseract\s+v?(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"Rotate:\s*(\d+)")]
    private static partial Regex RotateLine();
}
