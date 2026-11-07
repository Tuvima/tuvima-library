using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Matching;

/// <summary>
/// <c>config/wikidata_match.json</c> (universal editor v5 D34): how the Match tab's Wikidata side searches and which Wikidata classes are an item
/// itself and which are the thing it belongs to. The shipped file states every key; a person's file may leave keys out.
/// </summary>
public sealed class WikidataMatchConfiguration
{
    [JsonPropertyName("search_limit")]
    public int SearchLimit { get; set; }

    [JsonPropertyName("year_tolerance")]
    public int YearTolerance { get; set; }

    [JsonPropertyName("kinds")]
    public Dictionary<string, WikidataKindClasses> Kinds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("type_words")]
    public Dictionary<string, string> TypeWords { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("identifiers")]
    public List<WikidataIdentifierRule> Identifiers { get; set; } = [];

    [JsonPropertyName("year_properties")]
    public List<string> YearProperties { get; set; } = [];

    [JsonPropertyName("creator_properties")]
    public List<string> CreatorProperties { get; set; } = [];

    [JsonPropertyName("series_properties")]
    public List<string> SeriesProperties { get; set; } = [];

    [JsonPropertyName("type_property")]
    public string TypeProperty { get; set; } = string.Empty;
}

/// <summary>The Wikidata classes that are an item of one kind, and the ones that are the thing it belongs to.</summary>
public sealed class WikidataKindClasses
{
    [JsonPropertyName("direct")]
    public List<string> Direct { get; set; } = [];

    [JsonPropertyName("parent")]
    public List<string> Parent { get; set; } = [];
}

/// <summary>An id the item stores, with the Wikidata properties that hold the same id, and the provider it belongs to.</summary>
public sealed class WikidataIdentifierRule
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("properties")]
    public List<string> Properties { get; set; } = [];

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }
}

/// <summary>
/// Supplies <see cref="WikidataMatchConfiguration"/> from <c>config/wikidata_match.json</c>, read again when the file changes. The shipped file
/// is built into the Engine, so a config folder that predates it, or a file that cannot be read, still gives the shipped rules; a key a
/// person's file leaves out takes the shipped value.
/// </summary>
public sealed class WikidataMatchConfigurationProvider
{
    private const string FileName = "wikidata_match.json";
    private const string ResourceName = "MediaEngine.Api.wikidata_match.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Lazy<WikidataMatchConfiguration> Shipped = new(LoadShipped, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Func<string?> _configDirectory;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private WikidataMatchConfiguration? _current;
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private string? _loadedPath;

    public WikidataMatchConfigurationProvider(IConfigurationLoader loader, ILogger<WikidataMatchConfigurationProvider>? logger = null)
        : this(() => loader.ConfigDirectoryPath, logger)
    {
    }

    public WikidataMatchConfigurationProvider(Func<string?> configDirectory, ILogger? logger = null)
    {
        _configDirectory = configDirectory;
        _logger = logger;
    }

    /// <summary>The rules shipped with this build.</summary>
    public static WikidataMatchConfiguration BuiltIn => Shipped.Value;

    /// <summary>The rules in force, read again when the config file changed.</summary>
    public WikidataMatchConfiguration Current
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

    /// <summary>Reads a rules file; keys it leaves out take <paramref name="fallback"/>'s. Null when it cannot be read or states a number that is not usable.</summary>
    public static WikidataMatchConfiguration? TryRead(string json, WikidataMatchConfiguration? fallback, out string? problem)
    {
        problem = null;
        try
        {
            var read = JsonSerializer.Deserialize<WikidataMatchConfiguration>(json, Json);
            if (read is null)
            {
                problem = "it is empty";
                return null;
            }

            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (fallback is not null && root.ValueKind == JsonValueKind.Object)
            {
                if (!Has(root, "search_limit"))
                {
                    read.SearchLimit = fallback.SearchLimit;
                }

                if (!Has(root, "year_tolerance"))
                {
                    read.YearTolerance = fallback.YearTolerance;
                }

                if (!Has(root, "kinds"))
                {
                    read.Kinds = fallback.Kinds;
                }

                if (!Has(root, "type_words"))
                {
                    read.TypeWords = fallback.TypeWords;
                }

                if (!Has(root, "identifiers"))
                {
                    read.Identifiers = fallback.Identifiers;
                }

                if (!Has(root, "year_properties"))
                {
                    read.YearProperties = fallback.YearProperties;
                }

                if (!Has(root, "creator_properties"))
                {
                    read.CreatorProperties = fallback.CreatorProperties;
                }

                if (!Has(root, "series_properties"))
                {
                    read.SeriesProperties = fallback.SeriesProperties;
                }

                if (!Has(root, "type_property"))
                {
                    read.TypeProperty = fallback.TypeProperty;
                }
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

    private static bool Has(JsonElement element, string name) =>
        element.EnumerateObject().Any(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Check(WikidataMatchConfiguration config)
    {
        if (config.SearchLimit < 1 || config.SearchLimit > 50)
        {
            return "the search limit is not a number from 1 to 50";
        }

        if (config.YearTolerance < 0)
        {
            return "the year tolerance is below zero";
        }

        if (string.IsNullOrWhiteSpace(config.TypeProperty))
        {
            return "it names no 'instance of' property";
        }

        return null;
    }

    private static WikidataMatchConfiguration Load(string? path, DateTime writeTime, ILogger? logger)
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

            logger?.LogWarning("config/wikidata_match.json was not used because {Problem}; the shipped rules apply.", problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "config/wikidata_match.json could not be read; the shipped rules apply.");
        }

        return BuiltIn;
    }

    private static WikidataMatchConfiguration LoadShipped()
    {
        using var stream = typeof(WikidataMatchConfigurationProvider).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"The shipped {FileName} is missing from the Engine.");
        using var reader = new StreamReader(stream);
        return TryRead(reader.ReadToEnd(), fallback: null, out var problem)
               ?? throw new InvalidOperationException($"The shipped {FileName} is not usable: {problem}.");
    }
}
