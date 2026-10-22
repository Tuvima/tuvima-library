using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Tools.ExifTool;

/// <summary>A program and the arguments that must come before ExifTool's own, such as <c>perl.exe exiftool.pl</c>.</summary>
/// <param name="FileName">The program to start.</param>
/// <param name="LeadingArguments">Arguments placed before ExifTool's options (the Perl script to run, when there is one).</param>
public sealed record ExifToolCommand(string FileName, IReadOnlyList<string> LeadingArguments);

/// <summary>Finds the ExifTool the Library runs, and the Library's own ExifTool configuration file.</summary>
public interface IExifToolLocator
{
    /// <summary>The command to start ExifTool with, or <c>null</c> when none is usable here.</summary>
    ExifToolCommand? Resolve();

    /// <summary>The path of Perl, or <c>null</c> when this machine has none on its search path.</summary>
    string? FindPerl();

    /// <summary>
    /// The Library's ExifTool configuration file (it defines the <c>XMP-tuvima</c> tags), or <c>null</c> when it cannot be
    /// found or created. ExifTool needs it as its first option.
    /// </summary>
    string? ResolveConfigFile();
}

/// <summary>
/// Looks for ExifTool in this order: the copy the Library downloaded into <c>tools/exiftool</c> in its data folder, then the
/// <c>TUVIMA_EXIFTOOL_PATH</c> setting (the Docker image sets it), then <c>exiftool</c> on the machine's search path.
/// The Windows build runs as <c>perl.exe exiftool.pl</c> from inside its own folder; the Linux build is a Perl script and
/// runs as <c>perl exiftool</c>.
/// </summary>
public sealed class ExifToolLocator : IExifToolLocator
{
    public const string PathVariable = "TUVIMA_EXIFTOOL_PATH";
    public const string ToolFolderName = "exiftool";
    public const string ConfigFileName = "tuvima.config";

    private const string EmbeddedConfigName = "MediaEngine.Api.exiftool.tuvima.config";

    private readonly TuvimaDataPaths _paths;
    private readonly IConfigurationLoader _configuration;
    private readonly Func<string, string?> _readEnvironment;
    private readonly ILogger<ExifToolLocator> _logger;

    public ExifToolLocator(
        TuvimaDataPaths paths,
        IConfigurationLoader configuration,
        ILogger<ExifToolLocator> logger,
        Func<string, string?>? readEnvironment = null)
    {
        _paths = paths;
        _configuration = configuration;
        _logger = logger;
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>The folder the Library downloads ExifTool into.</summary>
    public string DownloadedFolder => Path.Combine(_paths.Root, "tools", ToolFolderName);

    public ExifToolCommand? Resolve()
    {
        if (FromFolder(DownloadedFolder) is { } downloaded)
        {
            return downloaded;
        }

        var configured = _readEnvironment(PathVariable)?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            var fromSetting = Directory.Exists(configured) ? FromFolder(configured) : FromFile(configured);
            if (fromSetting is not null)
            {
                return fromSetting;
            }

            _logger.LogWarning("{Variable} does not point at a working ExifTool; looking elsewhere.", PathVariable);
        }

        var onPath = FindOnSearchPath(OperatingSystem.IsWindows() ? "exiftool.exe" : "exiftool");
        return onPath is null ? null : new ExifToolCommand(onPath, []);
    }

    public string? FindPerl() => FindOnSearchPath(OperatingSystem.IsWindows() ? "perl.exe" : "perl");

    public string? ResolveConfigFile()
    {
        try
        {
            var shipped = Path.Combine(Path.GetFullPath(_configuration.ConfigDirectoryPath), ToolFolderName, ConfigFileName);
            if (File.Exists(shipped))
            {
                return shipped;
            }

            // A configuration folder from before ExifTool was added has no copy, so the one built into the Engine is used.
            using var embedded = typeof(ExifToolLocator).Assembly.GetManifestResourceStream(EmbeddedConfigName);
            if (embedded is null)
            {
                _logger.LogWarning("The Library's ExifTool configuration file was not found.");
                return null;
            }

            var folder = Path.Combine(_paths.Root, "tools", "exiftool-config");
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, ConfigFileName);
            using var memory = new MemoryStream();
            embedded.CopyTo(memory);
            var bytes = memory.ToArray();
            if (!File.Exists(copy) || !File.ReadAllBytes(copy).AsSpan().SequenceEqual(bytes))
            {
                File.WriteAllBytes(copy, bytes);
            }

            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logger.LogWarning(ex, "The Library's ExifTool configuration file could not be prepared.");
            return null;
        }
    }

    // ── Where ExifTool can be ────────────────────────────────────────────────

    /// <summary>A folder holding the Windows build (its own Perl) or the Perl distribution's <c>exiftool</c> script.</summary>
    private ExifToolCommand? FromFolder(string folder)
    {
        var perlExe = Path.Combine(folder, "exiftool_files", "perl.exe");
        var windowsScript = Path.Combine(folder, "exiftool_files", "exiftool.pl");
        if (OperatingSystem.IsWindows() && File.Exists(perlExe) && File.Exists(windowsScript))
        {
            return new ExifToolCommand(perlExe, [windowsScript]);
        }

        var script = Path.Combine(folder, "exiftool");
        return File.Exists(script) ? FromScript(script) : null;
    }

    private ExifToolCommand? FromFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        // A Windows program runs as it is; an ExifTool script runs through Perl.
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new ExifToolCommand(path, [])
            : FromScript(path);
    }

    private ExifToolCommand? FromScript(string script)
    {
        var perl = FindPerl();
        if (perl is not null)
        {
            return new ExifToolCommand(perl, [script]);
        }

        // Without a Perl on the search path the script can still run on its own when it is marked executable.
        return !OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(script) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0
            ? new ExifToolCommand(script, [])
            : null;
    }

    private string? FindOnSearchPath(string fileName)
    {
        var search = _readEnvironment("PATH");
        if (string.IsNullOrWhiteSpace(search))
        {
            return null;
        }

        foreach (var directory in search.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A search-path entry that is not a valid folder name (stray quotes, for example) is skipped.
            }
        }

        return null;
    }
}
