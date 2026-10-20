using System.Collections.Concurrent;
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
    // One gate per installed plugin's manifest id (plugins cannot choose the id they are counted under), so this stays
    // small. A request counts both as stored and in flight for the moment between its usage record and its disposal:
    // that errs on the strict side and clears straight away.
    private readonly ConcurrentDictionary<string, PluginGate> _gates = new(StringComparer.Ordinal);

    public PluginAiBudget(AiSettings settings, IAiUsageRecordRepository usage, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(usage);
        _settings = settings;
        _usage = usage;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The plugin's standing from the stored records alone. Used for health.</summary>
    public async Task<PluginAiBudgetStatus> GetStatusAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return await ReadStatusAsync(pluginId, pending: 0, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks the allowance and admits the request as one step for that plugin, so two requests arriving together cannot
    /// both pass on the same count. Requests admitted but not yet recorded count too. Returns null when the plugin is over
    /// its allowance; otherwise a lease that must be disposed after the request's usage record has been written.
    /// </summary>
    public async Task<PluginAdmission?> TryAdmitAsync(string pluginId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var gate = _gates.GetOrAdd(pluginId, _ => new PluginGate());
        await gate.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var status = await ReadStatusAsync(pluginId, Volatile.Read(ref gate.Pending), ct).ConfigureAwait(false);
            if (status.IsOverBudget)
            {
                return null;
            }

            // Interlocked like the decrement in Dispose, which runs without this plugin's gate held.
            Interlocked.Increment(ref gate.Pending);
            return new PluginAdmission(gate);
        }
        finally
        {
            gate.Lock.Release();
        }
    }

    private async Task<PluginAiBudgetStatus> ReadStatusAsync(string pluginId, int pending, CancellationToken ct)
    {
        var budget = _settings.GetPluginBudget(pluginId);
        var since = _time.GetUtcNow() - Window;
        var totals = await _usage.GetTotalsSinceAsync(AiCallerKind.Plugin, pluginId, since, ct).ConfigureAwait(false);
        var requests = totals.Requests + pending;
        var over = requests >= budget.RequestsPerHour
            || totals.BusyMs >= (long)budget.BusySecondsPerHour * 1000;
        return new PluginAiBudgetStatus(over, requests, totals.BusyMs, budget);
    }

    internal sealed class PluginGate
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);

        public int Pending;
    }

    /// <summary>A request admitted against a plugin's allowance. Dispose it once the request's usage record is written.</summary>
    public sealed class PluginAdmission : IDisposable
    {
        private PluginGate? _gate;

        internal PluginAdmission(PluginGate gate) => _gate = gate;

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate is not null)
            {
                Interlocked.Decrement(ref gate.Pending);
            }
        }
    }
}
