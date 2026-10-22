using System.Collections.Concurrent;
using System.Text.Json;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Configuration;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Secrets;

namespace MediaEngine.Storage;

/// <summary>
/// Multi-file configuration loader that reads and writes individual JSON files
/// from a structured directory layout.
///
/// <para>
/// <b>Directory layout:</b>
/// <code>
/// config/
///   core.json                 ← CoreConfiguration
///   scoring.json              ← ScoringSettings
///   maintenance.json          ← MaintenanceSettings
///   providers/                ← Per-provider ProviderConfiguration
///     local_filesystem.json
///     apple_api.json
///     wikidata.json
///     …
///   universe/                 ← Universe knowledge models
///     wikidata.json
///     …
/// </code>
/// </para>
///
/// <para>If the config directory does not exist, a default directory is created
/// with sensible defaults for all configuration files.</para>
/// </summary>
public sealed class ConfigurationDirectoryLoader : IConfigurationLoader, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly string[] SecretFieldNames =
        ["api_key", "api_key_override", "client_key", "access_token", "pin", "username", "password"];

    private readonly string _configDir;
    private readonly IConfigSecretsStore _secrets;
    private readonly ConcurrentDictionary<string, byte> _unreadableSecrets = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _reloadLock = new();
    private readonly Dictionary<string, object> _lastKnownGood = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Timer> _reloadTimers = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    // ── Subdirectory constants ────────────────────────────────────────────────

    private const string ProvidersSubdir = "providers";

    // ── File names ────────────────────────────────────────────────────────────

    private const string CoreFileName = "core.json";
    private const string ScoringFileName = "scoring.json";
    private const string MaintenanceFileName = "maintenance.json";
    private const string HydrationFileName = "hydration.json";
    private const string MediaTypesFileName = "media_types.json";
    private const string LibraryKindsFileName = "library_kinds.json";
    private const string DisambiguationFileName = "disambiguation.json";
    private const string TranscodingFileName = "transcoding.json";
    private const string OcrFileName = "ocr.json";
    private const string ToolDownloadsFileName = "tool_downloads.json";
    private const string NetworkFileName = "network.json";
    private const string FieldPrioritiesFileName = "field_priorities.json";
    private const string PipelinesFileName = "pipelines.json";

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <param name="configDirectoryPath">
    /// Root directory for all configuration files (e.g. <c>"config"</c>).
    /// </param>
    /// <param name="secrets">
    /// Where provider keys are read from. When null, the loader uses a <see cref="ConfigFolderSecretsStore"/>
    /// over the same config folder (the host passes one shared store so writes and reads use one lock).
    /// </param>
    public ConfigurationDirectoryLoader(string configDirectoryPath, IConfigSecretsStore? secrets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectoryPath);
        _configDir = configDirectoryPath;
        _secrets = secrets ?? new ConfigFolderSecretsStore(configDirectoryPath);

        EnsureInitialized();
    }

    /// <inheritdoc/>
    public string ConfigDirectoryPath => _configDir;

    public event EventHandler<ConfigurationFileChangedEventArgs>? ConfigurationChanged;

    public void StartWatching()
    {
        if (_watcher is not null || !Directory.Exists(_configDir))
        {
            return;
        }

        _watcher = new FileSystemWatcher(_configDir, "*.json")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnConfigFileChanged;
        _watcher.Created += OnConfigFileChanged;
        _watcher.Renamed += OnConfigFileRenamed;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Current multi-file configuration loading
    // ═══════════════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════════════
    //  IConfigurationLoader — Granular Access
    // ═══════════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public CoreConfiguration LoadCore() =>
        LoadFile<CoreConfiguration>(CoreFileName)
        ?? new();

    /// <inheritdoc/>
    public void SaveCore(CoreConfiguration config) =>
        SaveFile(CoreFileName, config);

    /// <inheritdoc/>
    public ScoringSettings LoadScoring() =>
        LoadFile<ScoringSettings>(ScoringFileName) ?? new();

    /// <inheritdoc/>
    public void SaveScoring(ScoringSettings settings) =>
        SaveFile(ScoringFileName, settings);

    /// <inheritdoc/>
    public MaintenanceSettings LoadMaintenance() =>
        LoadFile<MaintenanceSettings>(MaintenanceFileName) ?? new();

    /// <inheritdoc/>
    public void SaveMaintenance(MaintenanceSettings settings) =>
        SaveFile(MaintenanceFileName, settings);

    /// <inheritdoc/>
    public HydrationSettings LoadHydration() =>
        LoadFile<HydrationSettings>(HydrationFileName) ?? new();

    /// <inheritdoc/>
    public void SaveHydration(HydrationSettings settings) =>
        SaveFile(HydrationFileName, settings);

    /// <inheritdoc/>
    public DisambiguationSettings LoadDisambiguation() =>
        LoadFile<DisambiguationSettings>(DisambiguationFileName) ?? new();

    /// <inheritdoc/>
    public void SaveDisambiguation(DisambiguationSettings settings) =>
        SaveFile(DisambiguationFileName, settings);

    /// <inheritdoc/>
    public TranscodingSettings LoadTranscoding() =>
        LoadFile<TranscodingSettings>(TranscodingFileName) ?? new();

    /// <inheritdoc/>
    public void SaveTranscoding(TranscodingSettings settings) =>
        SaveFile(TranscodingFileName, settings);

    /// <inheritdoc/>
    public OcrSettings LoadOcr() =>
        LoadFile<OcrSettings>(OcrFileName) ?? new();

    /// <inheritdoc/>
    public void SaveOcr(OcrSettings settings) =>
        SaveFile(OcrFileName, settings);

    /// <inheritdoc/>
    public ToolDownloadSettings LoadToolDownloads()
    {
        var settings = LoadFile<ToolDownloadSettings>(ToolDownloadsFileName) ?? new();
        // A downloaded program is only ever fetched from a fixed github.com address with a recorded checksum.
        settings.RemoveDisallowedEntries();
        return settings;
    }

    /// <inheritdoc/>
    public NetworkSettings LoadNetwork() =>
        LoadFile<NetworkSettings>(NetworkFileName) ?? new();

    /// <inheritdoc/>
    public void SaveNetwork(NetworkSettings settings) =>
        SaveFile(NetworkFileName, settings);

    /// <inheritdoc/>
    public FieldPriorityConfiguration LoadFieldPriorities() =>
        LoadFile<FieldPriorityConfiguration>(FieldPrioritiesFileName) ?? new();

    /// <inheritdoc/>
    public void SaveFieldPriorities(FieldPriorityConfiguration config) =>
        SaveFile(FieldPrioritiesFileName, config);

    // ── UI Palette ───────────────────────────────────────────────────────────

    private const string UiSubdir = "ui";
    private const string PaletteFileName = "palette.json";

    /// <inheritdoc/>
    public PaletteConfiguration LoadPalette() =>
        LoadFile<PaletteConfiguration>(Path.Combine(UiSubdir, PaletteFileName)) ?? new();

    /// <inheritdoc/>
    public void SavePalette(PaletteConfiguration palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        SaveFile(Path.Combine(UiSubdir, PaletteFileName), palette);
    }

    // ── AI Settings ──────────────────────────────────────────────────────────

    private const string AiFileName = "ai.json";

    /// <inheritdoc/>
    public T? LoadAi<T>() where T : class =>
        LoadFile<T>(AiFileName);

    /// <inheritdoc/>
    public void SaveAi<T>(T settings) where T : class
    {
        ArgumentNullException.ThrowIfNull(settings);
        SaveFile(AiFileName, settings);
    }

    // ── Libraries ────────────────────────────────────────────────────────────

    private const string LibrariesFileName = "libraries.json";

    /// <inheritdoc/>
    public LibrariesConfiguration LoadLibraries() =>
        LoadFile<LibrariesConfiguration>(LibrariesFileName) ?? new();

    /// <inheritdoc/>
    public void SaveLibraries(LibrariesConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        SaveFile(LibrariesFileName, LibrariesConfigurationNormalizer.Normalize(config));
    }

    /// <inheritdoc/>
    public PipelineConfiguration LoadPipelines()
    {
        var pipelines = LoadFile<Dictionary<string, MediaTypePipeline>>(PipelinesFileName);
        if (pipelines is not null && pipelines.Count > 0)
        {
            return new PipelineConfiguration { Pipelines = new Dictionary<string, MediaTypePipeline>(pipelines, StringComparer.OrdinalIgnoreCase) };
        }

        return new PipelineConfiguration();
    }

    /// <inheritdoc/>
    public void SavePipelines(PipelineConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        // Store as a flat dictionary (matching pipelines.json format).
        SaveFile(PipelinesFileName, config.Pipelines);
    }

    /// <inheritdoc/>
    public MediaTypeConfiguration LoadMediaTypes() =>
        LoadFile<MediaTypeConfiguration>(MediaTypesFileName)
        ?? new MediaTypeConfiguration();

    /// <inheritdoc/>
    public LibraryKindsConfiguration LoadLibraryKinds() =>
        LoadFile<LibraryKindsConfiguration>(LibraryKindsFileName)
        ?? new LibraryKindsConfiguration();

    /// <inheritdoc/>
    public void SaveMediaTypes(MediaTypeConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        SaveFile(MediaTypesFileName, config);
    }

    /// <inheritdoc/>
    public ProviderConfiguration? LoadProvider(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var config = LoadFile<ProviderConfiguration>(Path.Combine(ProvidersSubdir, $"{name}.json"));
        if (config is not null)
        {
            ApplySecrets(config, name);
        }

        return config;
    }

    /// <inheritdoc/>
    public void SaveProvider(ProviderConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Name);
        SaveFile(Path.Combine(ProvidersSubdir, $"{config.Name}.json"), config);
    }

    /// <inheritdoc/>
    public void DeleteProvider(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(c => c is '/' or '\\') || name.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("A provider name cannot be a path.", nameof(name));
        }

        var path = Path.Combine(_configDir, ProvidersSubdir, $"{name}.json");
        File.Delete(path);
        File.Delete(path + ".bak");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ProviderConfiguration> LoadAllProviders()
    {
        var dir = Path.Combine(_configDir, ProvidersSubdir);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var results = new List<ProviderConfiguration>();
        // Sorted by file name: the operating system lists files in its own order (alphabetical on Windows, not on
        // Linux), and the order decides which provider registers, and so answers, first.
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            var relativePath = Path.Combine(ProvidersSubdir, Path.GetFileName(file));
            var config = LoadFile<ProviderConfiguration>(relativePath);
            if (config is null)
            {
                continue;
            }

            ApplySecrets(config, Path.GetFileNameWithoutExtension(file));
            results.Add(config);
        }
        return results;
    }

    /// <summary>
    /// Overlays the provider's secrets (from <see cref="IConfigSecretsStore"/>, which keeps them in
    /// <c>config/secrets/{providerName}.json</c>) onto a loaded provider configuration. The secrets are
    /// flat strings with keys like <c>api_key</c>, <c>username</c>, <c>password</c> that are merged
    /// into the provider's <see cref="HttpClientConfig"/>. An unreadable secrets file leaves the
    /// provider without keys and is reported once through <see cref="ConfigurationChanged"/>
    /// (file name and error type only, never content).
    /// </summary>
    private void ApplySecrets(ProviderConfiguration config, string providerName)
    {
        SecretOwner owner;
        try
        {
            owner = SecretOwner.Provider(providerName);
        }
        catch (ArgumentException)
        {
            // A provider whose name cannot be a secrets file name has no secrets file.
            return;
        }

        IReadOnlyDictionary<string, string> secrets;
        try
        {
            secrets = _secrets.GetAllAsync(owner).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            ReportUnreadableSecrets(owner, ex);
            return;
        }

        _unreadableSecrets.TryRemove(owner.Name, out _);
        if (secrets.Count == 0)
        {
            return;
        }

        // Ensure HttpClient exists if secrets reference it
        if (config.HttpClient is null && SecretFieldNames.Any(secrets.ContainsKey))
        {
            config.HttpClient = new HttpClientConfig();
        }

        if (config.HttpClient is null)
        {
            return;
        }

        if (secrets.TryGetValue("api_key", out var apiKey))
        {
            config.HttpClient.ApiKey = apiKey;
        }

        if (secrets.TryGetValue("api_key_override", out var apiKeyOverride))
        {
            config.HttpClient.ApiKeyOverride = apiKeyOverride;
        }

        if (secrets.TryGetValue("client_key", out var clientKey))
        {
            config.HttpClient.ClientKey = clientKey;
        }

        if (secrets.TryGetValue("access_token", out var accessToken))
        {
            config.HttpClient.AccessToken = accessToken;
        }

        if (secrets.TryGetValue("pin", out var pin))
        {
            config.HttpClient.Pin = pin;
        }

        if (secrets.TryGetValue("username", out var user))
        {
            config.HttpClient.Username = user;
        }

        if (secrets.TryGetValue("password", out var pass))
        {
            config.HttpClient.Password = pass;
        }
    }

    private void ReportUnreadableSecrets(SecretOwner owner, Exception error)
    {
        // Report each unreadable file once until it reads cleanly again, so a broken file does not repeat on every load.
        if (!_unreadableSecrets.TryAdd(owner.Name, 0))
        {
            return;
        }

        var relativePath = $"secrets/{owner.Name}.json";
        ConfigurationChanged?.Invoke(this, new ConfigurationFileChangedEventArgs(
            relativePath, Path.Combine(_configDir, "secrets", $"{owner.Name}.json"), applied: false, error));
    }

    /// <inheritdoc/>
    public T? LoadConfig<T>(string subdirectory, string name) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var relativePath = string.IsNullOrWhiteSpace(subdirectory)
            ? $"{name}.json"
            : Path.Combine(subdirectory, $"{name}.json");
        return LoadFile<T>(relativePath);
    }

    /// <inheritdoc/>
    public void SaveConfig<T>(string subdirectory, string name, T config) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);
        var relativePath = string.IsNullOrWhiteSpace(subdirectory)
            ? $"{name}.json"
            : Path.Combine(subdirectory, $"{name}.json");
        SaveFile(relativePath, config);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  File I/O — Generic Read / Write with .bak Rotation
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads and deserializes a JSON file relative to the config directory.
    /// Falls back to <c>.bak</c> if the primary file is missing or corrupt.
    /// Returns <c>null</c> if both attempts fail.
    /// </summary>
    private T? LoadFile<T>(string relativePath) where T : class
    {
        var fullPath = Path.Combine(_configDir, relativePath);
        var backupPath = fullPath + ".bak";
        var schemaName = ResolveSchemaName(relativePath);

        // Attempt 1 — primary file
        var result = TryDeserializeFile<T>(fullPath, relativePath, out var primaryErrors);
        if (result is not null)
        {
            RememberLastKnownGood(relativePath, result);
            return result;
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        // Attempt 2 — backup file; restore primary on success
        result = TryDeserializeFile<T>(backupPath, relativePath, out var backupErrors);
        if (result is not null)
        {
            try { File.Copy(backupPath, fullPath, overwrite: true); }
            catch (IOException) { /* Best-effort restore */ }
            RememberLastKnownGood(relativePath, result);
            return result;
        }

        if (TryGetLastKnownGood<T>(relativePath, out var cached))
        {
            return cached;
        }

        var errors = primaryErrors.Count > 0 ? primaryErrors : backupErrors;
        throw new ConfigValidationException(fullPath, schemaName, errors);
    }

    /// <summary>
    /// Serializes and writes a JSON file relative to the config directory.
    /// Rotates the existing file to <c>.bak</c> before overwriting.
    /// Creates parent directories as needed.
    /// </summary>
    private void SaveFile<T>(string relativePath, T config) where T : class
    {
        var fullPath = Path.Combine(_configDir, relativePath);
        var backupPath = fullPath + ".bak";
        var validationErrors = JsonConfigValidator.Validate(config, relativePath);
        if (validationErrors.Count > 0)
        {
            throw new ConfigValidationException(fullPath, ResolveSchemaName(relativePath), validationErrors);
        }

        // Ensure parent directory exists
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is not null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Rotate current to backup
        if (File.Exists(fullPath))
        {
            try { File.Copy(fullPath, backupPath, overwrite: true); }
            catch { /* Best-effort backup */ }
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, fullPath, overwrite: true);
            RememberLastKnownGood(relativePath, config);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>Deserialize a file; returns <c>null</c> on any failure.</summary>
    private static T? TryDeserializeFile<T>(string path, string relativePath, out IReadOnlyList<string> errors) where T : class
    {
        errors = [];
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = TuvimaDataPaths.ExpandConfigTokens(File.ReadAllText(path));
            var result = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (result is null)
            {
                errors = ["The file is empty or does not contain a JSON object."];
                return null;
            }

            errors = JsonConfigValidator.Validate(result, relativePath);
            return errors.Count == 0 ? result : null;
        }
        catch (JsonException ex)
        {
            errors = [$"JSON syntax or type error at {ex.Path ?? "$"}."];
            return null;
        }
        catch (IOException ex)
        {
            errors = [$"The file could not be read: {ex.GetType().Name}."];
            return null;
        }
    }

    private void RememberLastKnownGood<T>(string relativePath, T value) where T : class
    {
        lock (_reloadLock)
        {
            _lastKnownGood[NormalizeRelativePath(relativePath)] = value;
        }
    }

    private bool TryGetLastKnownGood<T>(string relativePath, out T? value) where T : class
    {
        lock (_reloadLock)
        {
            if (_lastKnownGood.TryGetValue(NormalizeRelativePath(relativePath), out var cached) && cached is T typed)
            {
                value = typed;
                return true;
            }
        }

        value = null;
        return false;
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs args)
    {
        if (_disposed || !args.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var relativePath = NormalizeRelativePath(Path.GetRelativePath(_configDir, args.FullPath));
        if (relativePath.StartsWith("schemas/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains(".bak", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("secrets/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_reloadLock)
        {
            if (!_reloadTimers.TryGetValue(relativePath, out var timer))
            {
                timer = new Timer(_ => ReloadChangedFile(relativePath), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _reloadTimers[relativePath] = timer;
            }

            timer.Change(TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
        }
    }

    private void OnConfigFileRenamed(object sender, RenamedEventArgs args) =>
        OnConfigFileChanged(sender, args);

    private void ReloadChangedFile(string relativePath)
    {
        var fullPath = Path.Combine(_configDir, relativePath);
        Exception? error = null;
        var applied = false;

        try
        {
            _ = relativePath.Replace('\\', '/') switch
            {
                CoreFileName => ReloadFile<CoreConfiguration>(relativePath),
                ScoringFileName => ReloadFile<ScoringSettings>(relativePath),
                MaintenanceFileName => ReloadFile<MaintenanceSettings>(relativePath),
                HydrationFileName => ReloadFile<HydrationSettings>(relativePath),
                MediaTypesFileName => ReloadFile<MediaTypeConfiguration>(relativePath),
                LibraryKindsFileName => ReloadFile<LibraryKindsConfiguration>(relativePath),
                PipelinesFileName => ReloadFile<Dictionary<string, MediaTypePipeline>>(relativePath),
                NetworkFileName => ReloadFile<NetworkSettings>(relativePath),
                "ui/palette.json" => ReloadFile<PaletteConfiguration>(relativePath),
                var path when path.StartsWith("providers/", StringComparison.OrdinalIgnoreCase) => ReloadFile<ProviderConfiguration>(relativePath),
                _ => LoadConfig<object>(Path.GetDirectoryName(relativePath) ?? string.Empty, Path.GetFileNameWithoutExtension(relativePath)),
            };
            applied = true;
        }
        catch (Exception ex) when (ex is IOException or ConfigValidationException or JsonException)
        {
            error = ex;
        }

        ConfigurationChanged?.Invoke(this, new ConfigurationFileChangedEventArgs(relativePath, fullPath, applied, error));
    }

    private T ReloadFile<T>(string relativePath) where T : class
    {
        var fullPath = Path.Combine(_configDir, relativePath);
        var result = TryDeserializeFile<T>(fullPath, relativePath, out var errors);
        if (result is null)
        {
            throw new ConfigValidationException(fullPath, ResolveSchemaName(relativePath), errors);
        }

        RememberLastKnownGood(relativePath, result);
        return result;
    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/');

    private static string ResolveSchemaName(string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (normalized.StartsWith("providers/", StringComparison.OrdinalIgnoreCase))
        {
            return "providers/provider.schema.json";
        }

        return normalized switch
        {
            "core.json" => "core.schema.json",
            "hydration.json" => "hydration.schema.json",
            "scoring.json" => "scoring.schema.json",
            "maintenance.json" => "maintenance.schema.json",
            "media_types.json" => "media_types.schema.json",
            "library_kinds.json" => "library_kinds.schema.json",
            "pipelines.json" => "pipelines.schema.json",
            "network.json" => "network.schema.json",
            "ui/palette.json" => "ui/palette.schema.json",
            "ui/library-preferences.json" => "ui/library-preferences.schema.json",
            _ => $"{Path.GetFileNameWithoutExtension(normalized)}.schema.json",
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Initialization — Migration & First-Run
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Ensures the config directory exists and is populated.
    /// Called once from the constructor.
    /// </summary>
    private void EnsureInitialized()
    {
        if (Directory.Exists(_configDir))
        {
            EnsureSecretsDirectory();
            return;
        }

        // First run — create defaults
        CreateDefaultDirectory();
        EnsureSecretsDirectory();
    }

    private void EnsureSecretsDirectory()
    {
        var secretsDirectory = Directory.CreateDirectory(Path.Combine(_configDir, "secrets"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                secretsDirectory.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Creates a default config directory with sensible defaults when no
    /// prior configuration exists (first-run experience).
    /// </summary>
    private void CreateDefaultDirectory()
    {
        Directory.CreateDirectory(_configDir);
        Directory.CreateDirectory(Path.Combine(_configDir, ProvidersSubdir));
        Directory.CreateDirectory(Path.Combine(_configDir, "ui"));
        Directory.CreateDirectory(Path.Combine(_configDir, "ui", "devices"));
        Directory.CreateDirectory(Path.Combine(_configDir, "ui", "profiles"));

        // Core defaults
        SaveCore(new CoreConfiguration());
        SaveScoring(new ScoringSettings());
        SaveMaintenance(new MaintenanceSettings());
        SaveDisambiguation(new DisambiguationSettings());
        SaveNetwork(new NetworkSettings());
        SaveFieldPriorities(new FieldPriorityConfiguration
        {
            FieldOverrides = new(StringComparer.OrdinalIgnoreCase)
            {
                ["description"] = new() { Priority = ["wikidata_reconciliation", "apple_api", "tmdb"], Note = "Prefer rich Wikipedia descriptions from the reconciliation provider, then fall back to enabled retail descriptions" },
                ["short_description"] = new() { Priority = ["wikidata_reconciliation", "tmdb", "apple_api"], Note = "Prefer Wikidata entity descriptions for hero summaries; full Wikipedia extracts stay in description" },
                ["biography"] = new() { Priority = ["wikidata_reconciliation"], Note = "Rich Wikipedia bios for persons" },
                ["cover"] = new() { Priority = ["apple_api", "tmdb", "musicbrainz"], Note = "Enabled retail providers have high-res commercial art" },
                ["rating"] = new() { Priority = ["apple_api", "tmdb"], Note = "Wikidata does not carry ratings" },
            }
        });
        SaveMediaTypes(new MediaTypeConfiguration());
        SaveFile(LibraryKindsFileName, new LibraryKindsConfiguration());

        // Default providers for a newly initialized configuration directory.
        SaveProvider(new ProviderConfiguration
        {
            Name = "local_filesystem",
            Enabled = true,
            Weight = 1.0,
            Domain = ProviderDomain.Universal,
        });

        SaveProvider(new ProviderConfiguration
        {
            Name = "apple_api",
            Enabled = true,
            Weight = 0.7,
            Domain = ProviderDomain.Universal,
            CapabilityTags = ["cover", "description", "rating"],
            FieldWeights = new() { ["cover"] = 0.85, ["description"] = 0.85, ["rating"] = 0.7 },
            Endpoints = new() { ["api"] = "https://itunes.apple.com" },
            ThrottleMs = 300,
        });

        SaveProvider(new ProviderConfiguration
        {
            Name = "wikidata",
            Enabled = true,
            Weight = 0.7,
            Domain = ProviderDomain.Universal,
            CapabilityTags = ["series", "franchise", "person_id"],
            FieldWeights = new() { ["series"] = 1.0, ["franchise"] = 1.0, ["person_id"] = 1.0 },
            Endpoints = new()
            {
                ["wikidata_api"] = "https://www.wikidata.org/w/api.php",
                ["wikidata_sparql"] = "https://query.wikidata.org/sparql",
            },
            ThrottleMs = 1100,
        });

        // Default UI configuration
        SavePalette(new PaletteConfiguration());
        SaveConfig("ui", "global", new UIGlobalSettings());
        SaveConfig("ui/devices", "web", new UIDeviceProfile
        {
            DeviceClass = "web",
            DisplayName = "Desktop Web",
        });
        SaveConfig("ui/devices", "mobile", new UIDeviceProfile
        {
            DeviceClass = "mobile",
            DisplayName = "Mobile",
            ContentPadding = "tl-sp-pa-2",
            ContentMaxWidth = "Full",
            Constraints = new UIDeviceConstraints
            {
                FeaturesDisabled = ["view_toggle"],
                MinTouchTargetPx = 48,
            },
            Shell = new UIShellSettings
            {
                AppBarStyle = "compact",
                LogoVariant = "icon",
                IntentDockItems = ["Home", "Libraries", "Search", "Downloads", "Settings"],
                IntentDockStyle = "normal",
            },
            Pages = new UIPageSettings
            {
                Home = new UIHomePageSettings
                {
                    CollectionHeroLayout = "stacked",
                    ProgressCardsLayout = "stacked",
                    BentoColumns = 1,
                    PendingFilesDisplay = "badge",
                },
                Preferences = new UIPreferencesPageSettings
                {
                    TabBarLayout = "vertical",
                    GeneralTabLayout = "stacked",
                    ColorSwatchCount = 4,
                },
                ServerSettings = new UIServerSettingsPageSettings
                {
                    TabBarLayout = "vertical-accordion",
                    TabContentLayout = "stacked-card",
                },
            },
        });
        SaveConfig("ui/devices", "television", new UIDeviceProfile
        {
            DeviceClass = "television",
            DisplayName = "Television",
            ContentPadding = "tl-sp-pa-6",
            ContentMaxWidth = "Full",
            BorderRadius = 40,
            Constraints = new UIDeviceConstraints
            {
                FeaturesDisabled = [
                    "command_palette", "search_button", "theme_toggle",
                    "avatar_menu", "pending_files_alert", "view_toggle",
                    "profile_section", "color_picker",
                ],
                PagesDisabled = ["server_settings"],
                AllowTextInput = false,
                MinTouchTargetPx = 64,
            },
            Shell = new UIShellSettings
            {
                AppBarStyle = "oversized",
                LogoVariant = "wordmark-large",
                IntentDockItems = ["Home", "Libraries", "Search", "Downloads", "Settings"],
                IntentDockStyle = "oversized",
            },
            Pages = new UIPageSettings
            {
                Home = new UIHomePageSettings
                {
                    CollectionHeroLayout = "two-column-oversized",
                    ProgressCardsLayout = "row-oversized",
                    BentoColumns = 2,
                    BentoTileStyle = "large",
                    PendingFilesDisplay = "hidden",
                },
                Preferences = new UIPreferencesPageSettings
                {
                    TabBarLayout = "focus-nav",
                    GeneralTabLayout = "theme-only",
                    ColorSwatchCount = 0,
                },
                ServerSettings = new UIServerSettingsPageSettings { PageEnabled = false },
            },
        });
        SaveConfig("ui/devices", "automotive", new UIDeviceProfile
        {
            DeviceClass = "automotive",
            DisplayName = "Automotive",
            DarkMode = true,
            ContentMaxWidth = "Full",
            Constraints = new UIDeviceConstraints
            {
                FeaturesDisabled = [
                    "command_palette", "search_button", "theme_toggle",
                    "avatar_menu", "pending_files_alert", "view_toggle",
                    "profile_section", "color_picker", "server_settings",
                ],
                PagesDisabled = ["server_settings"],
                AllowTextInput = false,
                MinTouchTargetPx = 80,
                ForceDarkMode = true,
            },
            Shell = new UIShellSettings
            {
                AppBarStyle = "minimal",
                LogoVariant = "icon-large",
                IntentDockItems = ["Collections", "Listen"],
                IntentDockStyle = "oversized",
            },
            Pages = new UIPageSettings
            {
                Home = new UIHomePageSettings
                {
                    CollectionHeroEnabled = false,
                    CollectionHeroLayout = "hidden",
                    ProgressCardsLayout = "single",
                    BentoColumns = 1,
                    BentoTileStyle = "audio-only",
                    PendingFilesDisplay = "hidden",
                },
                Preferences = new UIPreferencesPageSettings
                {
                    TabBarLayout = "single",
                    GeneralTabLayout = "theme-only",
                    ColorSwatchCount = 0,
                },
                ServerSettings = new UIServerSettingsPageSettings { PageEnabled = false },
            },
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Conversion Helpers — ProviderBootstrap ↔ ProviderConfiguration
    // ═══════════════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════════════
    //  Endpoint Distribution
    // ═══════════════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════════════
    //  Default Rate Limits — Known Provider Behaviour
    // ═══════════════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnConfigFileChanged;
            _watcher.Created -= OnConfigFileChanged;
            _watcher.Renamed -= OnConfigFileRenamed;
            _watcher.Dispose();
        }

        lock (_reloadLock)
        {
            foreach (var timer in _reloadTimers.Values)
            {
                timer.Dispose();
            }

            _reloadTimers.Clear();
        }
    }
}
