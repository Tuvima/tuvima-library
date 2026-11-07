using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Supplies the editor's numbers and per-kind rules from <c>config/editor.json</c>, re-read when the file changes (universal editor v5
/// D34). The shipped file is built into the Engine as well, so a config folder that predates it, or a file that cannot be read, still
/// gives the shipped rules.
/// </summary>
public sealed class EditorConfigurationProvider : IEditorConfigurationSource
{
    private const string FileName = "editor.json";
    private const string ResourceName = "MediaEngine.Api.editor.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<EditorConfiguration> Shipped = new(LoadShipped, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Func<string?> _configDirectory;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private EditorConfiguration? _current;
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private string? _loadedPath;

    public EditorConfigurationProvider(IConfigurationLoader loader, ILogger<EditorConfigurationProvider>? logger = null)
        : this(() => loader.ConfigDirectoryPath, logger)
    {
    }

    public EditorConfigurationProvider(Func<string?> configDirectory, ILogger? logger = null)
    {
        _configDirectory = configDirectory;
        _logger = logger;
    }

    /// <summary>The rules shipped with this build (<c>config/editor.json</c> as built into the Engine).</summary>
    public static EditorConfiguration BuiltIn => Shipped.Value;

    /// <summary>The rules in force, read again when the config file changed.</summary>
    public EditorConfiguration Current
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
                return _current;
            }
        }
    }

    /// <summary>Reads a rules file; null when it is unreadable or leaves out something the editor cannot work without.</summary>
    public static EditorConfiguration? TryRead(string json, out string? problem)
    {
        problem = null;
        try
        {
            var read = JsonSerializer.Deserialize<EditorConfiguration>(json, Json);
            if (read is null)
            {
                problem = "it is empty";
                return null;
            }

            problem = Check(read);
            return problem is null ? read : null;
        }
        catch (JsonException ex)
        {
            problem = $"it is not valid JSON ({ex.Message})";
            return null;
        }
    }

    private static string? Check(EditorConfiguration config)
    {
        if (config.InlineSupportingFiles < 1 || config.CardsPerGroup < 1 || config.RowsPerPage < 1
            || config.ItemListPageSize < 1 || config.FolderTreePageSize < 1)
        {
            return "a count in it is not a positive number";
        }

        if (config.SuggestDetails.FirstLines < 1 || config.SuggestDetails.DescriptionMaxCharacters < 1)
        {
            return "the document-details limits are not positive numbers";
        }

        if (!config.Kinds.ContainsKey(EditorKinds.Item))
        {
            return "it has no generic \"item\" kind";
        }

        foreach (var (kind, rules) in config.Kinds)
        {
            if (!rules.Tabs.Contains("details", StringComparer.Ordinal))
            {
                return $"the kind \"{kind}\" has no Details tab";
            }
        }

        return null;
    }

    private static EditorConfiguration Load(string? path, DateTime writeTime, ILogger? logger)
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

            Warn(logger, $"config/editor.json was not used because {problem}; using the shipped rules until it is fixed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A broken file never stops the editor from opening: the shipped rules apply until it is fixed.
            Warn(logger, $"config/editor.json could not be read ({ex.GetType().Name}); using the shipped rules.");
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

    private static EditorConfiguration LoadShipped()
    {
        using var stream = typeof(EditorConfigurationProvider).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The shipped editor rules ({ResourceName}) are missing from the Engine.");
        using var reader = new StreamReader(stream);
        return TryRead(reader.ReadToEnd(), out var problem)
            ?? throw new InvalidOperationException($"The shipped editor rules (config/editor.json) are not valid: {problem}.");
    }
}
