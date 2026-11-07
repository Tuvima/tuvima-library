using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Domain.Services;

/// <summary>
/// Supplies the file-name cleaning rules from <c>config/filename_cleaning.json</c>, re-read when the file changes (universal editor v5,
/// D33). The shipped file is built into the Domain as well, so a config folder that predates it, or a file that cannot be read or
/// holds a pattern that does not compile, still gives the shipped rules, with a logged warning. A key a person's file leaves out takes
/// the shipped value, so a short file that only adds words never breaks the cleaner.
/// </summary>
public sealed class FilenameCleaningConfigurationProvider
{
    private const string FileName = "filename_cleaning.json";
    private const string ResourceName = "MediaEngine.Domain.filename_cleaning.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<FilenameCleaningRules> Shipped = new(LoadShipped, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Func<string?> _configDirectory;
    private readonly Action<string> _warn;
    private readonly object _gate = new();
    private FilenameCleaningRules? _current;
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private string? _loadedPath;

    public FilenameCleaningConfigurationProvider(IConfigurationLoader loader, Action<string>? warn = null)
        : this(() => loader.ConfigDirectoryPath, warn)
    {
    }

    public FilenameCleaningConfigurationProvider(Func<string?> configDirectory, Action<string>? warn = null)
    {
        _configDirectory = configDirectory;
        _warn = warn ?? (message => System.Diagnostics.Trace.TraceWarning(message));
    }

    /// <summary>The rules shipped with this build (<c>config/filename_cleaning.json</c> as built in).</summary>
    public static FilenameCleaningRules BuiltIn => Shipped.Value;

    /// <summary>The rules in force, read again when the config file changed.</summary>
    public FilenameCleaningRules Current
    {
        get
        {
            var directory = _configDirectory();
            var path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, FileName);
            var writeTime = path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            lock (_gate)
            {
                if (_current is not null && path == _loadedPath && writeTime == _loadedWriteTimeUtc)
                {
                    return _current;
                }

                _current = Load(path, writeTime);
                _loadedPath = path;
                _loadedWriteTimeUtc = writeTime;
                return _current;
            }
        }
    }

    /// <summary>
    /// Reads a rules file. Anything the file leaves out is filled from <paramref name="fallback"/> (the shipped rules); with no fallback the
    /// file must state every rule. Null, with the reason, when the file is unreadable or a pattern in it does not compile.
    /// </summary>
    public static FilenameCleaningRules? TryRead(string json, FilenameCleaningRules? fallback, out string? problem, Action<string>? warn = null)
    {
        problem = null;
        try
        {
            var read = JsonSerializer.Deserialize<FilenameCleaningConfiguration>(json, Json);
            if (read is null)
            {
                problem = "it is empty";
                return null;
            }

            if (fallback is not null)
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                FillMissing(read, document.RootElement, fallback.Configuration);
            }

            return FilenameCleaningRules.Compile(read, out problem, warn);
        }
        catch (JsonException ex)
        {
            problem = $"it is not valid JSON ({ex.Message})";
            return null;
        }
    }

    private static void FillMissing(FilenameCleaningConfiguration read, JsonElement root, FilenameCleaningConfiguration shipped)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!Has(root, "noise_words"))
        {
            read.NoiseWords = shipped.NoiseWords;
        }

        if (!Has(root, "ambiguous_words"))
        {
            read.AmbiguousWords = shipped.AmbiguousWords;
        }

        if (!Has(root, "noise_patterns"))
        {
            read.NoisePatterns = shipped.NoisePatterns;
        }

        if (!Has(root, "separators"))
        {
            read.Separators = shipped.Separators;
        }

        if (!(root.TryGetProperty("year", out var year) && year.ValueKind == JsonValueKind.Object && Has(year, "pattern")))
        {
            read.Year = shipped.Year;
        }

        if (!Has(root, "season_episode_patterns"))
        {
            read.SeasonEpisodePatterns = shipped.SeasonEpisodePatterns;
        }

        if (!Has(root, "absolute_episode_patterns"))
        {
            read.AbsoluteEpisodePatterns = shipped.AbsoluteEpisodePatterns;
        }

        if (!Has(root, "season_folder_pattern"))
        {
            read.SeasonFolderPattern = shipped.SeasonFolderPattern;
        }

        if (!Has(root, "track_patterns"))
        {
            read.TrackPatterns = shipped.TrackPatterns;
        }

        if (!Has(root, "edition_markers"))
        {
            read.EditionMarkers = shipped.EditionMarkers;
        }
    }

    private static bool Has(JsonElement element, string name) =>
        element.EnumerateObject().Any(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private FilenameCleaningRules Load(string? path, DateTime writeTime)
    {
        if (path is null || writeTime == DateTime.MinValue)
        {
            return BuiltIn;
        }

        try
        {
            var read = TryRead(File.ReadAllText(path), BuiltIn, out var problem, _warn);
            if (read is not null)
            {
                return read;
            }

            _warn($"config/filename_cleaning.json was not used because {problem}; using the shipped rules until it is fixed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A broken file never stops a scan or the editor: the shipped rules apply until it is fixed.
            _warn($"config/filename_cleaning.json could not be read ({ex.GetType().Name}); using the shipped rules.");
        }

        return BuiltIn;
    }

    private static FilenameCleaningRules LoadShipped()
    {
        using var stream = typeof(FilenameCleaningConfigurationProvider).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The shipped file-name cleaning rules ({ResourceName}) are missing.");
        using var reader = new StreamReader(stream);
        return TryRead(reader.ReadToEnd(), fallback: null, out var problem)
            ?? throw new InvalidOperationException($"The shipped file-name cleaning rules (config/filename_cleaning.json) are not valid: {problem}.");
    }
}
