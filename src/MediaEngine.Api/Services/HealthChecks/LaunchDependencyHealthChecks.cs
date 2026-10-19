using System.Runtime.InteropServices;
using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Api.Services.Tools;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MediaEngine.Api.Services.HealthChecks;

public sealed class ConfigurationHealthCheck(
    IConfigurationLoader configuration,
    AiSettings effectiveAiSettings) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = configuration.LoadCore();
            _ = configuration.LoadLibraries();
            _ = configuration.LoadPipelines();
            var providers = configuration.LoadAllProviders();
            var aiErrors = AiSettingsValidator.Validate(effectiveAiSettings);
            if (aiErrors.Count > 0)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"AI configuration has {aiErrors.Count} validation error(s).",
                    data: Data(true, ("errors", string.Join(" | ", aiErrors.Select(error => error.Message))))));
            }

            return Task.FromResult(HealthCheckResult.Healthy(
                "Required configuration files are valid.",
                Data(true, ("providers_loaded", providers.Count))));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Required configuration is invalid.",
                ex,
                Data(true, ("error", ex.Message))));
        }
    }

    private static Dictionary<string, object> Data(bool required, params (string Key, object Value)[] values)
    {
        var data = new Dictionary<string, object> { ["category"] = "configuration", ["required"] = required };
        foreach (var (key, value) in values)
        {
            data[key] = value;
        }

        return data;
    }
}

public sealed class MediaRuntimeHealthCheck(IToolRequirementsService tools, SharedAiRuntime runtime) : IHealthCheck
{
    private static readonly string[] RuntimeToolIds =
    [
        ToolRequirementIds.Ffmpeg,
        ToolRequirementIds.AdaptiveStreaming,
        ToolRequirementIds.ImageEngine,
        ToolRequirementIds.AiRuntime,
    ];

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // The tools catalogue is the one source; this check only turns its rows into a health result.
        var catalogue = await tools.GetAsync(cancellationToken).ConfigureAwait(false);
        var rows = catalogue.Tools.Where(tool => RuntimeToolIds.Contains(tool.Id, StringComparer.Ordinal)).ToList();
        ToolRequirementDto? Row(string id) => rows.FirstOrDefault(tool => tool.Id == id);
        bool Ready(string id) => Row(id)?.State == ToolRequirementStates.Ready;

        var failures = rows
            .Where(tool => tool.State != ToolRequirementStates.Ready)
            .Select(tool => $"{tool.Name} is {tool.State.Replace('_', ' ')}.")
            .ToList();

        var required = string.Equals(
            Environment.GetEnvironmentVariable("TUVIMA_REQUIRE_MEDIA_RUNTIME"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var data = new Dictionary<string, object>
        {
            ["category"] = "native_media_runtime",
            ["required"] = required,
            ["runtime_identifier"] = RuntimeInformation.RuntimeIdentifier,
            ["ffmpeg"] = Ready(ToolRequirementIds.Ffmpeg) ? Row(ToolRequirementIds.Ffmpeg)?.Version ?? "available" : "unavailable",
            ["skia"] = Ready(ToolRequirementIds.ImageEngine),
            ["llama_available"] = Ready(ToolRequirementIds.AiRuntime),
            ["llama_backend"] = runtime.LoadedBackend ?? "unavailable",
            ["llama_path"] = runtime.LoadedLlamaPath ?? "unavailable",
            ["llama_version"] = SharedAiRuntime.LlamaVersion,
            ["failures"] = string.Join(" | ", failures),
        };

        if (failures.Count == 0)
        {
            return HealthCheckResult.Healthy("FFmpeg, SkiaSharp, and LLamaSharp native runtimes are available.", data);
        }

        return required
            ? HealthCheckResult.Unhealthy("One or more required media runtimes are unavailable.", data: data)
            : HealthCheckResult.Degraded("One or more optional media runtimes are unavailable.", data: data);
    }
}

public sealed class ModelReadinessHealthCheck(IToolRequirementsService tools, AiSettings settings) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var catalogue = await tools.GetAsync(cancellationToken).ConfigureAwait(false);
        var models = catalogue.Tools.Where(tool => tool.Kind == "ai_model").ToList();
        var unavailable = models
            .Where(model => model.State is ToolRequirementStates.Missing or ToolRequirementStates.Problem)
            .Select(model => $"{model.Id[ToolRequirementIds.AiModelPrefix.Length..]}:{model.State}")
            .ToArray();
        var data = new Dictionary<string, object>
        {
            ["category"] = "models",
            ["required"] = false,
            ["configured"] = models.Count,
            ["unavailable"] = string.Join(", ", unavailable),
            ["automatic_download"] = settings.DevSkipDownload ? "bypassed" : "enabled",
        };

        return unavailable.Length == 0
            ? HealthCheckResult.Healthy("Configured AI model artifacts are ready.", data)
            : HealthCheckResult.Degraded(
                $"{unavailable.Length} AI model role(s) are unavailable; dependent features will wait without consuming poison attempts.",
                data: data);
    }
}

public sealed class ProviderReadinessHealthCheck(
    IConfigurationLoader configuration,
    IProviderHealthMonitor healthMonitor) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var providers = configuration.LoadAllProviders();
            var enabled = providers.Where(provider => provider.Enabled).ToArray();
            var down = enabled
                .Where(provider => healthMonitor.GetStatus(provider.Name) == ProviderHealthStatus.Down)
                .Select(provider => provider.Name)
                .ToArray();
            var data = new Dictionary<string, object>
            {
                ["category"] = "providers",
                ["required"] = false,
                ["enabled"] = enabled.Length,
                ["disabled"] = providers.Count - enabled.Length,
                ["down"] = string.Join(", ", down),
            };
            return Task.FromResult(down.Length == 0
                ? HealthCheckResult.Healthy("Enabled metadata providers are configured.", data)
                : HealthCheckResult.Degraded(
                    $"{down.Length} enabled provider(s) are unavailable; their work will remain retryable.",
                    data: data));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Provider configuration could not be evaluated.",
                ex,
                new Dictionary<string, object> { ["category"] = "providers", ["required"] = true }));
        }
    }
}

public sealed class WorkerReadinessHealthCheck(IEnumerable<IHostedService> hostedServices) : IHealthCheck
{
    private static readonly string[] RequiredWorkerNames =
    [
        "LibraryReconciliationService",
        "RetailMatchHostedService",
        "WikidataBridgeHostedService",
        "QuickHydrationHostedService",
    ];

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var services = hostedServices.ToArray();
        var missing = RequiredWorkerNames
            .Where(name => services.All(service => !string.Equals(service.GetType().Name, name, StringComparison.Ordinal)))
            .ToArray();
        var requiredServices = services
            .Where(service => RequiredWorkerNames.Contains(service.GetType().Name, StringComparer.Ordinal))
            .OfType<BackgroundService>()
            .ToArray();
        var failed = requiredServices
            .Where(service => service.ExecuteTask is { IsFaulted: true })
            .Select(service => service.GetType().Name)
            .ToArray();
        var stopped = requiredServices
            .Where(service => service.ExecuteTask is { IsCompletedSuccessfully: true })
            .Select(service => service.GetType().Name)
            .ToArray();
        var data = new Dictionary<string, object>
        {
            ["category"] = "workers",
            ["required"] = true,
            ["registered"] = services.Length,
            ["missing"] = string.Join(", ", missing),
            ["faulted"] = string.Join(", ", failed),
            ["stopped"] = string.Join(", ", stopped),
        };

        return Task.FromResult(missing.Length == 0 && failed.Length == 0 && stopped.Length == 0
            ? HealthCheckResult.Healthy("Required background workers are running.", data)
            : HealthCheckResult.Unhealthy("One or more required background workers are missing, stopped, or faulted.", data: data));
    }
}
