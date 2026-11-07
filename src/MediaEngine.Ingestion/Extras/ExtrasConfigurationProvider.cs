using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion.Extras;

/// <summary>
/// Supplies the supporting-files rules from <c>config/extras.json</c>, re-read when the file changes (universal editor v5 D16, D34). The
/// shipped file is built into the Engine as well, so a config folder that predates it, or a file that cannot be read, still gives the
/// shipped rules. The rules in force are also published as <see cref="Active"/>, which the places that read them without a context
/// (<see cref="ExtrasRule"/>'s file-type checks) use.
/// </summary>
public sealed class ExtrasConfigurationProvider : IExtrasConfigurationSource
{
    private const string FileName = "extras.json";
    private const string ResourceName = "MediaEngine.Ingestion.extras.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<ExtrasConfiguration> Shipped = new(LoadShipped, LazyThreadSafetyMode.ExecutionAndPublication);

    private static volatile ExtrasConfiguration? _active;

    private readonly Func<string?> _configDirectory;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private ExtrasConfiguration? _current;
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private string? _loadedPath;

    public ExtrasConfigurationProvider(IConfigurationLoader loader, ILogger? logger = null)
        : this(() => loader.ConfigDirectoryPath, logger)
    {
        // Read once at startup so the rules apply before the first scan.
        _ = Current;
    }

    public ExtrasConfigurationProvider(Func<string?> configDirectory, ILogger? logger = null)
    {
        _configDirectory = configDirectory;
        _logger = logger;
    }

    /// <summary>The rules shipped with this build (<c>config/extras.json</c> as built into the Engine).</summary>
    public static ExtrasConfiguration BuiltIn => Shipped.Value;

    /// <summary>The rules in force: the last ones a provider read, else <see cref="BuiltIn"/>.</summary>
    public static ExtrasConfiguration Active => _active ?? BuiltIn;

    /// <summary>The rules in force, read again when the config file changed.</summary>
    public ExtrasConfiguration Current
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

                _current = Load(path, writeTime, _logger);
                _loadedPath = path;
                _loadedWriteTimeUtc = writeTime;
                _active = _current;
                return _current;
            }
        }
    }

    /// <summary>Reads a rules file; null when it is unreadable, names no accepted types at all, or holds a pattern that does not compile.</summary>
    public static ExtrasConfiguration? TryRead(string json) => TryRead(json, out _);

    /// <summary>As <see cref="TryRead(string)"/>, and says in plain words why a file was refused.</summary>
    public static ExtrasConfiguration? TryRead(string json, out string? problem)
    {
        problem = null;
        try
        {
            var read = JsonSerializer.Deserialize<ExtrasConfiguration>(json, Json);
            if (read is null || !IsUsable(read))
            {
                problem = "it names no accepted file types or roles";
                return null;
            }

            // Compile every pattern now: a bad one in a person's file must be refused here, never when the first scan builds the rule book.
            _ = ExtrasRuleBook.For(read);
            return read;
        }
        catch (JsonException ex)
        {
            problem = $"it is not valid JSON ({ex.Message})";
            return null;
        }
        catch (ArgumentException ex)
        {
            // RegexParseException is an ArgumentException.
            problem = $"one of its patterns is not valid ({ex.Message})";
            return null;
        }
    }

    private static ExtrasConfiguration Load(string? path, DateTime writeTime, ILogger? logger)
    {
        if (path is null || writeTime == DateTime.MinValue)
        {
            return BuiltIn;
        }

        try
        {
            var read = TryRead(File.ReadAllText(path), out var problem);
            if (read is not null)
            {
                return read;
            }

            Warn(logger, $"config/extras.json was not used because {problem}; using the shipped rules until it is fixed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A broken file never stops supporting files from working: the shipped rules apply until it is fixed.
            Warn(logger, $"config/extras.json could not be read ({ex.GetType().Name}); using the shipped rules.");
        }

        return BuiltIn;
    }

    private static void Warn(ILogger? logger, string message)
    {
        if (logger is not null)
        {
            logger.LogWarning("{Message}", message);
        }
        else
        {
            System.Diagnostics.Trace.TraceWarning(message);
        }
    }

    private static bool IsUsable(ExtrasConfiguration config) =>
        config.AcceptedTypes.Documents.Count + config.AcceptedTypes.Pictures.Count + config.AcceptedTypes.Videos.Count + config.AcceptedTypes.Audio.Count > 0
        && config.Roles.Count > 0;

    private static ExtrasConfiguration LoadShipped()
    {
        using var stream = typeof(ExtrasConfigurationProvider).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The shipped supporting-files rules ({ResourceName}) are missing from the Engine.");
        using var reader = new StreamReader(stream);
        return TryRead(reader.ReadToEnd())
            ?? throw new InvalidOperationException("The shipped supporting-files rules (config/extras.json) are not valid.");
    }
}
