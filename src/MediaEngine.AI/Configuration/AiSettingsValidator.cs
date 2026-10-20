using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Enums;

namespace MediaEngine.AI.Configuration;

public sealed record AiConfigurationError(string Path, string Message);

public sealed class AiConfigurationException(IReadOnlyList<AiConfigurationError> errors)
    : InvalidOperationException(BuildMessage(errors))
{
    public IReadOnlyList<AiConfigurationError> Errors { get; } = errors;

    private static string BuildMessage(IReadOnlyList<AiConfigurationError> errors) =>
        $"AI configuration is invalid: {string.Join("; ", errors.Select(error => $"{error.Path}: {error.Message}"))}";
}

/// <summary>
/// Central validation for AI runtime configuration. Catalog-only and disabled
/// experimental entries may remain not-ready; executable definitions must be safe.
/// </summary>
public static partial class AiSettingsValidator
{
    public static IReadOnlyList<AiConfigurationError> Validate(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<AiConfigurationError>();

        if (string.IsNullOrWhiteSpace(settings.ModelsDirectory))
        {
            Add("models_directory", "Models directory is required.");
        }
        else
        {
            ValidateDirectory(settings.ModelsDirectory, "models_directory", errors);
        }

        if (!string.IsNullOrEmpty(settings.NativeRuntimeDirectory)
            && settings.NativeRuntimeDirectory != "bundled")
        {
            if (!Path.IsPathFullyQualified(settings.NativeRuntimeDirectory))
            {
                Add("native_runtime_directory", "Use an absolute shared runtime path, or 'bundled' for a bundled deployment.");
            }
            else
            {
                ValidateDirectory(settings.NativeRuntimeDirectory, "native_runtime_directory", errors);
            }
        }

        if (settings.IdleUnloadSeconds <= 0)
        {
            Add("idle_unload_seconds", "Must be positive.");
        }

        if (settings.InferenceTimeoutSeconds <= 0)
        {
            Add("inference_timeout_seconds", "Must be positive.");
        }

        ValidatePluginBudget(settings, errors);

        if (settings.ReservedCpuCores < 1)
        {
            Add("reserved_cpu_cores", "Must reserve at least one core.");
        }

        if (settings.BackgroundQuietSeconds is < 1 or > 300)
        {
            Add("background_quiet_seconds", "Must be between 1 and 300 seconds.");
        }

        if (settings.MinimumFreeDiskMB < 256)
        {
            Add("minimum_free_disk_mb", "Must retain at least 256 MB.");
        }

        if (!AiResourceProfileNames.IsSupported(settings.ResourceProfile))
        {
            Add("resource_profile", "Must be essential, standard, or advanced.");
        }

        if (settings.EnrichmentBatchSize <= 0)
        {
            Add("enrichment_batch_size", "Must be positive.");
        }

        if (settings.Models is null)
        {
            Add("models", "Model definitions are required.");
        }
        else
        {
            foreach (var role in Enum.GetValues<AiModelRole>())
            {
                ValidateExecutableModel(settings.Models.GetByRole(role), $"models.{AiModelDefinitions.ToRoleKey(role)}", errors);
            }

            ValidateSharedArtifacts(settings.Models, errors);
        }

        var catalog = settings.ModelCatalog ?? new Dictionary<string, AiModelCatalogEntry>();
        foreach (var (key, entry) in catalog)
        {
            var path = $"model_catalog.{key}";
            if (string.IsNullOrWhiteSpace(key))
            {
                Add("model_catalog", "Catalog keys cannot be blank.");
            }

            if (!string.IsNullOrWhiteSpace(entry.File))
            {
                ValidateFileName(entry.File, $"{path}.file", errors);
            }

            ValidateOptionalHttpsUri(entry.DownloadUrl, $"{path}.download_url", errors);
            ValidateOptionalHttpsUri(entry.SourceUrl, $"{path}.source_url", errors);
            ValidateOptionalChecksum(entry.Sha256, $"{path}.sha256", errors);
            if (entry.SizeMB < 0)
            {
                Add($"{path}.size_mb", "Cannot be negative.");
            }

            if (entry.MemoryEnvelopeMB < 0)
            {
                Add($"{path}.memory_envelope_mb", "Cannot be negative.");
            }

            if (entry.MaxContextLength < 0)
            {
                Add($"{path}.max_context_length", "Cannot be negative.");
            }
        }

        return errors;

        void Add(string path, string message) => errors.Add(new AiConfigurationError(path, message));
    }

    public static void ValidateAndThrow(AiSettings settings)
    {
        var errors = Validate(settings);
        if (errors.Count > 0)
        {
            throw new AiConfigurationException(errors);
        }
    }

    internal static bool IsSafeFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && !Path.IsPathRooted(fileName)
        && fileName is not "." and not ".."
        && string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
        && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static void ValidatePluginBudget(AiSettings settings, List<AiConfigurationError> errors)
    {
        if (settings.PluginBudget is null)
        {
            errors.Add(new AiConfigurationError("plugin_budget", "Plugin budget is required."));
            return;
        }

        ValidateBudgetFields(
            "plugin_budget",
            settings.PluginBudget.RequestsPerHour,
            settings.PluginBudget.BusySecondsPerHour,
            settings.PluginBudget.MaxWaitSeconds,
            errors);

        foreach (var (pluginId, budget) in settings.PluginBudgetOverrides ?? [])
        {
            var path = $"plugin_budget_overrides.{pluginId}";
            if (string.IsNullOrWhiteSpace(pluginId))
            {
                errors.Add(new AiConfigurationError("plugin_budget_overrides", "Plugin ids cannot be blank."));
            }
            else if (budget is null)
            {
                errors.Add(new AiConfigurationError(path, "Definition is required."));
            }
            else
            {
                ValidateBudgetFields(path, budget.RequestsPerHour, budget.BusySecondsPerHour, budget.MaxWaitSeconds, errors);
            }
        }
    }

    private static void ValidateBudgetFields(
        string path,
        int? requestsPerHour,
        int? busySecondsPerHour,
        int? maxWaitSeconds,
        List<AiConfigurationError> errors)
    {
        if (requestsPerHour is < 1 or > 100_000)
        {
            errors.Add(new AiConfigurationError($"{path}.requests_per_hour", "Must be between 1 and 100000."));
        }

        if (busySecondsPerHour is < 1 or > 3600)
        {
            errors.Add(new AiConfigurationError($"{path}.busy_seconds_per_hour", "Must be between 1 and 3600 seconds."));
        }

        if (maxWaitSeconds is < 1 or > 3600)
        {
            errors.Add(new AiConfigurationError($"{path}.max_wait_seconds", "Must be between 1 and 3600 seconds."));
        }
    }

    private static void ValidateExecutableModel(
        AiModelDefinition model,
        string path,
        List<AiConfigurationError> errors)
    {
        if (model is null)
        {
            errors.Add(new AiConfigurationError(path, "Definition is required."));
            return;
        }

        ValidateFileName(model.File, $"{path}.file", errors);
        ValidateOptionalHttpsUri(model.DownloadUrl, $"{path}.download_url", errors);
        ValidateOptionalChecksum(model.Sha256, $"{path}.sha256", errors);
        if (model.SizeMB <= 0)
        {
            errors.Add(new AiConfigurationError($"{path}.size_mb", "Must be positive."));
        }

        if (model.ContextLength <= 0)
        {
            errors.Add(new AiConfigurationError($"{path}.context_length", "Must be positive."));
        }

        if (model.MaxTokens <= 0)
        {
            errors.Add(new AiConfigurationError($"{path}.max_tokens", "Must be positive."));
        }

        if (model.Temperature is < 0 or > 2)
        {
            errors.Add(new AiConfigurationError($"{path}.temperature", "Must be between 0 and 2."));
        }

        if (model.GpuLayers < 0)
        {
            errors.Add(new AiConfigurationError($"{path}.gpu_layers", "Cannot be negative."));
        }

        if (model.Threads <= 0)
        {
            errors.Add(new AiConfigurationError($"{path}.threads", "Must be positive."));
        }
    }

    private static void ValidateSharedArtifacts(
        AiModelDefinitions models,
        List<AiConfigurationError> errors)
    {
        var definitions = Enum.GetValues<AiModelRole>()
            .Select(role => new
            {
                Role = role,
                Definition = models.GetByRole(role),
                Artifact = $"{(role == AiModelRole.Audio ? "whisper" : "llama")}/{models.GetByRole(role).File}",
            })
            .GroupBy(item => item.Artifact, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1);

        foreach (var group in definitions)
        {
            var expected = group.First();
            foreach (var item in group.Skip(1))
            {
                var path = $"models.{AiModelDefinitions.ToRoleKey(item.Role)}";
                if (!string.Equals(
                        expected.Definition.Sha256,
                        item.Definition.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new AiConfigurationError(
                        $"{path}.sha256",
                        $"Must match the checksum for shared artifact {group.Key}."));
                }

                if (!string.Equals(
                        expected.Definition.DownloadUrl,
                        item.Definition.DownloadUrl,
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new AiConfigurationError(
                        $"{path}.download_url",
                        $"Must match the download URL for shared artifact {group.Key}."));
                }

                if (expected.Definition.SizeMB != item.Definition.SizeMB)
                {
                    errors.Add(new AiConfigurationError(
                        $"{path}.size_mb",
                        $"Must match the size for shared artifact {group.Key}."));
                }
            }
        }
    }

    private static void ValidateDirectory(string directory, string path, List<AiConfigurationError> errors)
    {
        try
        {
            _ = Path.GetFullPath(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add(new AiConfigurationError(path, "Must be a valid filesystem path."));
        }
    }

    private static void ValidateFileName(string file, string path, List<AiConfigurationError> errors)
    {
        if (!IsSafeFileName(file))
        {
            errors.Add(new AiConfigurationError(path, "Must be a simple file name without path traversal."));
        }
    }

    private static void ValidateOptionalHttpsUri(string? value, string path, List<AiConfigurationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add(new AiConfigurationError(path, "Must be an absolute HTTPS URI."));
        }
    }

    private static void ValidateOptionalChecksum(string? value, string path, List<AiConfigurationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!Sha256Regex().IsMatch(value))
        {
            errors.Add(new AiConfigurationError(path, "Must contain exactly 64 hexadecimal SHA-256 characters."));
        }
    }

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}

/// <summary>Immutable runtime copy detached from mutable configuration binding objects.</summary>
public sealed class AiRuntimeSettingsSnapshot
{
    private AiRuntimeSettingsSnapshot(
        string modelsDirectory,
        int idleUnloadSeconds,
        int inferenceTimeoutSeconds,
        int minimumFreeDiskMb,
        IReadOnlyDictionary<AiModelRole, AiModelRuntimeDefinition> models)
    {
        ModelsDirectory = modelsDirectory;
        IdleUnloadSeconds = idleUnloadSeconds;
        InferenceTimeoutSeconds = inferenceTimeoutSeconds;
        MinimumFreeDiskMB = minimumFreeDiskMb;
        Models = models;
    }

    public string ModelsDirectory { get; }
    public int IdleUnloadSeconds { get; }
    public int InferenceTimeoutSeconds { get; }
    public int MinimumFreeDiskMB { get; }
    public IReadOnlyDictionary<AiModelRole, AiModelRuntimeDefinition> Models { get; }

    public AiModelRuntimeDefinition GetModel(AiModelRole role) => Models[role];

    public static AiRuntimeSettingsSnapshot Create(AiSettings settings)
    {
        AiSettingsValidator.ValidateAndThrow(settings);
        var models = Enum.GetValues<AiModelRole>().ToDictionary(
            role => role,
            role => AiModelRuntimeDefinition.From(settings.Models.GetByRole(role)));

        return new AiRuntimeSettingsSnapshot(
            Path.GetFullPath(settings.ModelsDirectory),
            settings.IdleUnloadSeconds,
            settings.InferenceTimeoutSeconds,
            settings.MinimumFreeDiskMB,
            new ReadOnlyDictionary<AiModelRole, AiModelRuntimeDefinition>(models));
    }
}

public sealed record AiModelRuntimeDefinition(
    string? CatalogKey,
    string Description,
    string File,
    Uri? DownloadUri,
    string? Sha256,
    int SizeMB,
    int ContextLength,
    int MaxTokens,
    double Temperature,
    int GpuLayers,
    int Threads,
    string? Language,
    bool Translate)
{
    internal static AiModelRuntimeDefinition From(AiModelDefinition model) => new(
        model.CatalogKey,
        model.Description,
        model.File,
        string.IsNullOrWhiteSpace(model.DownloadUrl) ? null : new Uri(model.DownloadUrl, UriKind.Absolute),
        string.IsNullOrWhiteSpace(model.Sha256) ? null : model.Sha256.ToLowerInvariant(),
        model.SizeMB,
        model.ContextLength,
        model.MaxTokens,
        model.Temperature,
        model.GpuLayers,
        model.Threads,
        model.Language,
        model.Translate);
}
