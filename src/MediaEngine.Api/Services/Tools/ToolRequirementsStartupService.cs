using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Tools;

/// <summary>
/// Runs the tools check once at start-up and writes one summary line to the log: Information when every Required
/// tool is ready, Warning otherwise. It runs after start-up completes, so a slow probe never delays the Engine. When a
/// Required tool the Library can fetch (FFmpeg) is missing, it then starts that download once, so files waiting for
/// "media tools" are released without anyone pressing a button.
/// </summary>
public sealed class ToolRequirementsStartupService(
    IToolRequirementsService tools,
    IToolAutoInstaller autoInstaller,
    INotificationPublisher notifications,
    ILogger<ToolRequirementsStartupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            var result = await tools.CheckAsync(stoppingToken).ConfigureAwait(false);
            var summary = Summarise(result);
            if (result.NeedsAttention)
            {
                logger.LogWarning("Tools check: {Summary}", summary);
                await autoInstaller.StartMissingRequiredAsync(result, stoppingToken).ConfigureAwait(false);
                foreach (var tool in result.Tools.Where(tool => tool.Requirement == ToolRequirementLevels.Required && tool.State != ToolRequirementStates.Ready))
                {
                    notifications.Publish(new NotificationRequest(NotificationEventIds.ToolMissing, tool.Id, $"{tool.Name}: {tool.Effect} {tool.Fix}".Trim()));
                }
            }
            else
            {
                logger.LogInformation("Tools check: {Summary}", summary);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is stopping; there is nothing to report.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The start-up tools check could not finish.");
        }
    }

    internal static string Summarise(ToolRequirementsDto result)
    {
        var parts = result.Tools
            .Where(tool => tool.Kind != "ai_model")
            .Select(tool => tool.State == ToolRequirementStates.Ready
                ? $"{tool.Name} ready{(tool.Version is null ? string.Empty : $" ({tool.Version})")}"
                : $"{tool.Name} {tool.State.Replace('_', ' ')}{(tool.Requirement == ToolRequirementLevels.Optional ? " (optional)" : string.Empty)}")
            .ToList();
        var models = result.Tools.Where(tool => tool.Kind == "ai_model").ToList();
        if (models.Count > 0)
        {
            parts.Add($"AI models {models.Count(model => model.State == ToolRequirementStates.Ready)} of {models.Count} ready");
        }

        return string.Join(", ", parts);
    }
}
