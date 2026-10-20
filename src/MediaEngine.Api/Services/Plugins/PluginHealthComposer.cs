using MediaEngine.Api.Services.LocalAi;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Builds a plugin's health answer from its own health checks plus the Engine's view of it. A plugin that has used up its
/// Local AI allowance for the hour, or that was stopped or refused for going over a host-service limit (its records, jobs,
/// events, always-on work or web connection), reports degraded, which the Dashboard shows as Needs attention.
/// </summary>
internal static class PluginHealthComposer
{
    public static async Task<PluginHealthResponse> ComposeAsync(
        string pluginId,
        IReadOnlyList<PluginHealthResult> checks,
        PluginAiBudget aiBudget,
        CancellationToken ct,
        PluginHostServicesDto? hostServices = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(aiBudget);

        var dtos = checks.Select(check => new PluginHealthCheckDto
        {
            Status = check.Status,
            Message = check.Message,
            Warnings = check.Warnings.ToList(),
        }).ToList();

        var budget = await aiBudget.GetStatusAsync(pluginId, ct).ConfigureAwait(false);
        if (budget.IsOverBudget)
        {
            dtos.Add(new PluginHealthCheckDto
            {
                Status = "degraded",
                Message = PluginAiBudget.OverBudgetMessage,
            });
        }

        if (hostServices is { State: not "working", Problem: { } hostProblem })
        {
            dtos.Add(new PluginHealthCheckDto
            {
                Status = "degraded",
                Message = hostProblem,
            });
        }

        return new PluginHealthResponse
        {
            PluginId = pluginId,
            Status = dtos.Any(check => check.Status == "degraded") ? "degraded" : dtos.Count == 0 ? "unknown" : "healthy",
            Checks = dtos,
        };
    }
}
