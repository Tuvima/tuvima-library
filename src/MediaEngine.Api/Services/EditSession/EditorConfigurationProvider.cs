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

    /// <summary>Reads the shipped rules file on its own: every count must be in it. Null when it is unreadable or leaves out something the editor cannot work without.</summary>
    public static EditorConfiguration? TryRead(string json, out string? problem) => TryRead(json, fallback: null, out problem);

    /// <summary>
    /// Reads a person's rules file. Anything the file leaves out (a count, the document-details limits, a whole kind) is filled from
    /// <paramref name="fallback"/>, the shipped rules, so a short file never breaks the editor. With no fallback every count must be in the file.
    /// Null when the file is unreadable, or a count it does state is not a positive number.
    /// </summary>
    public static EditorConfiguration? TryRead(string json, EditorConfiguration? fallback, out string? problem)
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

            if (fallback is not null)
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                FillMissing(read, document.RootElement, fallback);
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

    // A key that is not in the file takes the shipped value. A key that is in the file is checked as written, so a stated 0 is refused
    // rather than quietly replaced.
    private static void FillMissing(EditorConfiguration read, JsonElement root, EditorConfiguration shipped)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!Has(root, "inline_supporting_files"))
        {
            read.InlineSupportingFiles = shipped.InlineSupportingFiles;
        }

        if (!Has(root, "cards_per_group"))
        {
            read.CardsPerGroup = shipped.CardsPerGroup;
        }

        if (!Has(root, "rows_per_page"))
        {
            read.RowsPerPage = shipped.RowsPerPage;
        }

        if (!Has(root, "item_list_page_size"))
        {
            read.ItemListPageSize = shipped.ItemListPageSize;
        }

        if (!Has(root, "folder_tree_page_size"))
        {
            read.FolderTreePageSize = shipped.FolderTreePageSize;
        }

        JsonElement? details = root.TryGetProperty("suggest_details", out var stated) && stated.ValueKind == JsonValueKind.Object ? stated : null;
        if (details is not { } firstLinesOwner || !Has(firstLinesOwner, "first_lines"))
        {
            read.SuggestDetails.FirstLines = shipped.SuggestDetails.FirstLines;
        }

        if (details is not { } maxOwner || !Has(maxOwner, "description_max_characters"))
        {
            read.SuggestDetails.DescriptionMaxCharacters = shipped.SuggestDetails.DescriptionMaxCharacters;
        }

        foreach (var (kind, rules) in shipped.Kinds)
        {
            read.Kinds.TryAdd(kind, rules);
        }
    }

    private static bool Has(JsonElement element, string name) =>
        element.EnumerateObject().Any(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

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
            var read = TryRead(File.ReadAllText(path), BuiltIn, out var problem);
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
