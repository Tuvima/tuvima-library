using MediaEngine.AI.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.LocalAi;

/// <summary>Where a plugin stands against its Local AI allowance for the last hour.</summary>
/// <param name="IsOverBudget">True when the plugin has used up its request count or its running time.</param>
public sealed record PluginAiBudgetStatus(bool IsOverBudget, int Requests, long BusyMs, AiPluginBudget Budget);

/// <summary>
/// Counts a plugin's Local AI use over the last rolling hour from the durable usage records, so the allowance survives
/// an Engine restart, and compares it with the plugin's budget (<c>plugin_budget</c> plus any per-plugin override).
/// </summary>
public sealed class PluginAiBudget
{
    /// <summary>What the plugin's health says while it is over its allowance.</summary>
    public const string OverBudgetMessage = "Used its Local AI allowance for this hour";

    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly AiSettings _settings;
    private readonly IAiUsageRecordRepository _usage;
    private readonly TimeProvider _time;

    public PluginAiBudget(AiSettings settings, IAiUsageRecordRepository usage, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(usage);
        _settings = settings;
        _usage = usage;
        _time = time ?? TimeProvider.System;
    }

    public async Task<PluginAiBudgetStatus> GetStatusAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var budget = _settings.GetPluginBudget(pluginId);
        var since = _time.GetUtcNow() - Window;
        var totals = await _usage.GetTotalsSinceAsync(AiCallerKind.Plugin, pluginId, since, ct).ConfigureAwait(false);
        var over = totals.Requests >= budget.RequestsPerHour
            || totals.BusyMs >= (long)budget.BusySecondsPerHour * 1000;
        return new PluginAiBudgetStatus(over, totals.Requests, totals.BusyMs, budget);
    }
}
