namespace MediaEngine.Plugins;

/// <summary>
/// Well-known <see cref="IPluginCapability.Kind"/> string constants shared across plugins.
/// No Api code currently filters capabilities by <see cref="IPluginCapability.Kind"/> value —
/// this class pins the contract now, before anything does, so plugins stop inventing their
/// own ad-hoc spellings.
/// </summary>
public static class PluginCapabilityKinds
{
    /// <summary>
    /// Kind reported by an <see cref="IPluginHealthCheck"/> capability. This was already the
    /// majority spelling (<c>CommercialSkipHealthCheck</c>, <c>FandomLoreHealthCheck</c>); the
    /// lone divergent spelling <c>"health-check"</c> (previously used by
    /// <c>MediaEngine.Plugin.MediaSegments</c>'s health checks) has been switched to this
    /// constant as a deliberate contract fix.
    /// </summary>
    public const string HealthCheckKind = "plugin-health-check";

    /// <summary>Kind reported by an <see cref="IPluginScheduledJob"/> capability.</summary>
    public const string ScheduledJobKind = "plugin-scheduled-job";

    /// <summary>Kind reported by an <see cref="IPluginEventSubscriber"/> capability.</summary>
    public const string EventSubscriberKind = "plugin-event-subscriber";

    /// <summary>Kind reported by an <see cref="IPluginBackgroundService"/> capability.</summary>
    public const string BackgroundServiceKind = "plugin-background-service";
}
