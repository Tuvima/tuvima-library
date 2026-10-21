using System.Collections.Concurrent;
using System.Text.Json;
using MediaEngine.Api.Services.Events;
using MediaEngine.Domain.Events;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Hands Library events to plugins that subscribe to them (<see cref="IPluginEventSubscriber"/>). It reads the same
/// <c>application_events</c> list that webhooks read, after a cursor kept for each plugin, so a restart carries on where it
/// stopped. A plugin gets events only while it is switched on and approved, and only for types its declared permissions
/// cover: <c>library-events</c> for everything except <c>playback.*</c>, which needs <c>watch-activity</c>. A plugin that
/// subscribes for the first time, or is switched off and on again, starts at the newest event with no backlog. Each event
/// is tried once with a 30-second limit and the cursor moves on either way; five failures in a row pause the plugin's
/// subscription until it is switched off and on.
/// </summary>
internal sealed class PluginEventDispatcher : BackgroundService
{
    private static readonly JsonSerializerOptions EnvelopeJson = new(JsonSerializerDefaults.Web);

    private readonly PluginCatalog _catalog;
    private readonly PluginHostStatusRegistry _statuses;
    private readonly IPluginExecutionContextFactory _contexts;
    private readonly IApplicationEventRepository _events;
    private readonly IPluginEventCursorRepository _cursors;
    private readonly TimeProvider _clock;
    private readonly PluginHostLimits _limits;
    private readonly ILogger<PluginEventDispatcher> _logger;
    // Each plugin's pass runs on its own task while the loop in DispatchOnceAsync reads and removes other plugins' entries,
    // so both are concurrent collections (the value of _cursorCleared is unused; it is a set).
    private readonly ConcurrentDictionary<string, int> _seenGeneration = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _cursorCleared = new(StringComparer.OrdinalIgnoreCase);

    public PluginEventDispatcher(
        PluginCatalog catalog,
        PluginHostStatusRegistry statuses,
        IPluginExecutionContextFactory contexts,
        IApplicationEventRepository events,
        IPluginEventCursorRepository cursors,
        PluginHostLimits limits,
        TimeProvider clock,
        ILogger<PluginEventDispatcher> logger)
    {
        _catalog = catalog;
        _statuses = statuses;
        _contexts = contexts;
        _events = events;
        _cursors = cursors;
        _limits = limits;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(_limits.EventPollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin event dispatch failed; trying again shortly");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>One pass: gives every subscribed plugin the events it has not had yet.</summary>
    public async Task DispatchOnceAsync(CancellationToken ct)
    {
        var passes = new List<Task>();
        foreach (var registration in _catalog.List())
        {
            var subscribers = registration.Capabilities.OfType<IPluginEventSubscriber>().ToList();
            if (subscribers.Count == 0)
            {
                continue;
            }

            if (!registration.Enabled || registration.LoadError is not null)
            {
                await ForgetAsync(registration.Manifest.Id, ct).ConfigureAwait(false);
                continue;
            }

            var covered = subscribers
                .Select(subscriber => (Subscriber: subscriber, Types: CoveredTypes(registration, subscriber)))
                .Where(entry => entry.Types.Count > 0)
                .ToList();
            if (covered.Count == 0)
            {
                _statuses.Get(registration.Manifest.Id).SetEventSubscription(null);
                continue;
            }

            _cursorCleared.TryRemove(registration.Manifest.Id, out _);
            passes.Add(DispatchToPluginAsync(registration.Manifest.Id, covered, ct));
        }

        await Task.WhenAll(passes).ConfigureAwait(false);
    }

    private static HashSet<string> CoveredTypes(PluginRegistration registration, IPluginEventSubscriber subscriber) =>
        subscriber.EventTypes
            .Where(type => PluginHostCapabilityRules.PermissionCoversEvent(registration.Manifest.Permissions, type))
            .ToHashSet(StringComparer.Ordinal);

    // A plugin that is switched off keeps no place in the event list: when it is switched on again it starts at the newest
    // event rather than receiving everything that happened while it was off.
    private async Task ForgetAsync(string pluginId, CancellationToken ct)
    {
        _seenGeneration.TryRemove(pluginId, out _);
        if (_cursorCleared.TryAdd(pluginId, 0))
        {
            await _cursors.DeleteAsync(pluginId, ct).ConfigureAwait(false);
        }
    }

    private async Task DispatchToPluginAsync(
        string pluginId,
        IReadOnlyList<(IPluginEventSubscriber Subscriber, HashSet<string> Types)> subscribers,
        CancellationToken ct)
    {
        var status = _statuses.Get(pluginId);
        if (status.IsPaused(PluginHostMessages.EventsProblemKey))
        {
            status.SetEventSubscription("paused");
            return;
        }

        status.SetEventSubscription("active");
        var generation = status.Generation;
        var cursor = await _cursors.GetAsync(pluginId, ct).ConfigureAwait(false);
        var seenBefore = _seenGeneration.TryGetValue(pluginId, out var seen);
        if (cursor is null || (seenBefore && seen != generation))
        {
            // New subscriber (or switched off and on since the last pass): start at the newest event, no backlog.
            cursor = (await _events.GetBoundsAsync(ct).ConfigureAwait(false)).LatestSequence ?? 0;
            await _cursors.SetAsync(pluginId, cursor.Value, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        }

        _seenGeneration[pluginId] = generation;

        while (!ct.IsCancellationRequested)
        {
            var page = await _events.ReadAfterAsync(cursor.Value, _limits.EventPageSize, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return;
            }

            foreach (var stored in page)
            {
                if (status.Generation != generation)
                {
                    return; // switched off or on while delivering: the next pass starts fresh
                }

                var wanted = subscribers.Where(entry => entry.Types.Contains(stored.EventType)).ToList();
                if (wanted.Count > 0 && !status.IsPaused(PluginHostMessages.EventsProblemKey))
                {
                    var delivery = ToPluginEvent(stored);
                    foreach (var (subscriber, _) in wanted)
                    {
                        await DeliverAsync(pluginId, subscriber, delivery, status, ct).ConfigureAwait(false);
                    }
                }

                cursor = stored.Sequence;
                if (wanted.Count > 0)
                {
                    await _cursors.SetAsync(pluginId, cursor.Value, _clock.GetUtcNow(), ct).ConfigureAwait(false);
                }

                if (status.IsPaused(PluginHostMessages.EventsProblemKey))
                {
                    return;
                }
            }

            await _cursors.SetAsync(pluginId, cursor.Value, _clock.GetUtcNow(), ct).ConfigureAwait(false);
            if (page.Count < _limits.EventPageSize)
            {
                return;
            }
        }
    }

    private async Task DeliverAsync(
        string pluginId,
        IPluginEventSubscriber subscriber,
        PluginEvent delivery,
        PluginHostStatus status,
        CancellationToken ct)
    {
        string? failure = null;
        using var handlerCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? handler = null;
        try
        {
            handler = Task.Run(async () =>
            {
                await using var context = _contexts.Create(pluginId, "events");
                await subscriber.HandleAsync(delivery, context, handlerCancellation.Token).ConfigureAwait(false);
            }, CancellationToken.None);

            var limit = Task.Delay(_limits.EventHandlerTimeout, ct);
            if (await Task.WhenAny(handler, limit).ConfigureAwait(false) == handler)
            {
                await handler.ConfigureAwait(false);
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                handlerCancellation.Cancel();
                _ = handler.ContinueWith(task => task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                failure = $"took longer than {PluginHostMessages.FormatDuration(_limits.EventHandlerTimeout)}";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plugin {PluginId} failed to handle event {EventType}", pluginId, delivery.Type);
            failure = ex.Message;
        }

        if (failure is null)
        {
            status.ClearCounter("event-failures");
            return;
        }

        status.SetLastProblem($"Could not handle {delivery.Type}: {failure}");
        if (status.Increment("event-failures") >= _limits.EventFailuresBeforePause)
        {
            status.Pause(PluginHostMessages.EventsProblemKey);
            status.SetProblem(PluginHostMessages.EventsProblemKey, PluginHostSeverity.NeedsAttention, PluginHostMessages.EventsPaused(_limits));
            status.SetEventSubscription("paused");
            _logger.LogWarning("Plugin {PluginId} stopped receiving events after {Failures} failures", pluginId, _limits.EventFailuresBeforePause);
        }
    }

    private static PluginEvent ToPluginEvent(StoredApplicationEvent stored) =>
        new(
            stored.EventType,
            stored.EventId.ToString(),
            stored.Sequence,
            stored.OccurredAt,
            JsonSerializer.Serialize(ApplicationEventEnvelopeMapper.ToEnvelope(stored), EnvelopeJson));
}
