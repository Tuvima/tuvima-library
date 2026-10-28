using MediaEngine.Contracts.Plugins;
using MediaEngine.Domain.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>Which plugin or source a line is about, and the version that wrote it.</summary>
public readonly record struct PluginActivityContext(string PluginId, string Version);

/// <summary>
/// The one place plugins and sources record what they did and how they failed (Plugin architecture spec D7, D10, C10 and C17).
/// A failure always carries a cause from the fixed list, a plain sentence, and a technical line redacted of keys and personal
/// paths; it feeds the Activity list, counts toward the automatic pause, and goes to the Engine log tagged with the plugin id so an
/// administrator can filter the log by plugin. Recording never throws and never blocks the work that failed: a plugin being down is
/// reported, not propagated (D7).
/// </summary>
public sealed class PluginActivityService(
    IPluginActivityRepository repository,
    PluginCircuitBreaker breaker,
    TimeProvider clock,
    ILogger<PluginActivityService> logger)
{
    /// <summary>The newest lines kept for each plugin.</summary>
    public const int KeepPerPlugin = 1000;

    /// <summary>True when the plugin may be asked now; false while it is paused after repeated failures.</summary>
    public bool Allow(string pluginId) => breaker.Allow(pluginId);

    public DateTimeOffset? PausedUntil(string pluginId) => breaker.PausedUntil(pluginId);

    /// <summary>Records a failure. <paramref name="itemId"/> is dropped for an item in a Vault section.</summary>
    /// <returns>True when this failure paused the plugin.</returns>
    public async Task<bool> RecordFailureAsync(
        PluginActivityContext context,
        PluginFailureReason reason,
        string? summary = null,
        Exception? exception = null,
        Guid? itemId = null,
        bool itemInVault = false,
        IEnumerable<string>? secretValues = null,
        CancellationToken ct = default)
    {
        var detail = PluginActivityRedactor.Redact(
            exception is null ? null : $"{exception.GetType().Name}: {exception.Message}",
            secretValues);
        using (logger.BeginScope(new Dictionary<string, object> { ["PluginId"] = context.PluginId }))
        {
            logger.LogWarning(
                "Plugin {PluginId} {Version} failed: {Reason} ({Cause})",
                context.PluginId, context.Version, PluginFailureReasons.Word(reason), detail ?? "no further detail");
        }

        // A caller refused for who it is or what it touches is not the plugin failing, so it does not count toward the pause.
        var paused = PluginFailureReasons.Group(reason) is not ("not_allowed" or "dependency_off") && breaker.RecordFailure(context.PluginId);
        await WriteAsync(context, PluginActivityKind.Failure, PluginFailureReasons.Word(reason), itemInVault ? null : itemId, 0,
            string.IsNullOrWhiteSpace(summary) ? PluginFailureReasons.Words(reason) : summary.Trim(), detail, ct).ConfigureAwait(false);
        if (paused)
        {
            await WriteAsync(context, PluginActivityKind.Note, null, null, 0,
                $"Paused after {PluginCircuitBreaker.FailuresBeforeOpen} failures in a row. The Library will try it again shortly.", null, ct).ConfigureAwait(false);
        }

        return paused;
    }

    /// <summary>Records a run that did work, and lets a paused plugin start again.</summary>
    public Task RecordRunAsync(PluginActivityContext context, int itemsChanged, string summary, CancellationToken ct = default)
    {
        breaker.RecordSuccess(context.PluginId);
        return WriteAsync(context, PluginActivityKind.Run, null, null, Math.Max(0, itemsChanged), summary, null, ct);
    }

    /// <summary>Records that the plugin deliberately did not work on something. A Vault item is recorded without its id.</summary>
    public Task RecordSkippedAsync(PluginActivityContext context, PluginFailureReason reason, string? summary = null, Guid? itemId = null, bool itemInVault = false, CancellationToken ct = default) =>
        WriteAsync(context, PluginActivityKind.Skipped, PluginFailureReasons.Word(reason), itemInVault ? null : itemId, 0,
            string.IsNullOrWhiteSpace(summary) ? PluginFailureReasons.Words(reason) : summary.Trim(), null, ct);

    public Task RecordNoteAsync(PluginActivityContext context, string summary, CancellationToken ct = default) =>
        WriteAsync(context, PluginActivityKind.Note, null, null, 0, summary, null, ct);

    public async Task<PluginActivityResponse> GetAsync(string pluginId, int limit, bool failuresOnly, CancellationToken ct = default)
    {
        var rows = await repository.GetRecentAsync(pluginId, limit, failuresOnly, ct).ConfigureAwait(false);
        return new PluginActivityResponse
        {
            PluginId = pluginId,
            PausedUntil = breaker.PausedUntil(pluginId),
            Entries = rows.Select(ToDto).ToList(),
        };
    }

    /// <summary>Forgets a plugin's pause, and optionally its history (switched on, off or removed).</summary>
    public async Task ForgetAsync(string pluginId, bool deleteHistory, CancellationToken ct = default)
    {
        breaker.Reset(pluginId);
        if (deleteHistory)
        {
            await SafelyAsync(() => repository.DeleteForPluginAsync(pluginId, ct), pluginId).ConfigureAwait(false);
        }
    }

    internal static PluginActivityEntryDto ToDto(StoredPluginActivity row)
    {
        var known = PluginFailureReasons.TryParse(row.Reason, out var reason);
        return new PluginActivityEntryDto
        {
            Id = row.Id,
            OccurredAt = row.OccurredAt,
            PluginVersion = row.PluginVersion,
            Kind = row.Kind switch
            {
                PluginActivityKind.Failure => "failure",
                PluginActivityKind.Skipped => "skipped",
                PluginActivityKind.Note => "note",
                _ => "run",
            },
            Reason = row.Reason,
            ReasonGroup = known ? PluginFailureReasons.Group(reason) : null,
            ReasonWords = known ? PluginFailureReasons.Words(reason) : null,
            ItemId = row.ItemId,
            ItemsChanged = row.ItemsChanged,
            Summary = row.Summary,
            Detail = row.Detail,
        };
    }

    private Task WriteAsync(PluginActivityContext context, PluginActivityKind kind, string? reason, Guid? itemId, int itemsChanged, string summary, string? detail, CancellationToken ct) =>
        SafelyAsync(
            () => repository.AddAsync(new StoredPluginActivity(0, clock.GetUtcNow(), context.PluginId, context.Version, kind, reason, itemId, itemsChanged, summary, detail), KeepPerPlugin, ct),
            context.PluginId);

    // Recording is best effort by design: a plugin's Activity list must never be the reason a plugin's work, ingestion or playback fails (D7).
    private async Task SafelyAsync(Func<Task> write, string pluginId)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record activity for plugin {PluginId}", pluginId);
        }
    }

    private async Task SafelyAsync<T>(Func<Task<T>> write, string pluginId) => await SafelyAsync(async () => { _ = await write().ConfigureAwait(false); }, pluginId).ConfigureAwait(false);
}
