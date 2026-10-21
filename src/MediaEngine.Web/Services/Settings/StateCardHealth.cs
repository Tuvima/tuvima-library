using MediaEngine.Contracts.Ai;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Plugins;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.System;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Services.Settings;

/// <summary>The four health words every state card uses (Developer tools spec D-D).</summary>
public enum HealthWord
{
    /// <summary>Switched on and doing its job.</summary>
    Working,

    /// <summary>Switched off. Never red.</summary>
    Off,

    /// <summary>Switched on and working, but something needs a look.</summary>
    NeedsAttention,

    /// <summary>Switched on and cannot do its job. The only red word.</summary>
    Broken,
}

/// <summary>What a state card is about; shown as a small label above its name.</summary>
public enum StateCardKind
{
    Plugin,
    Integration,
    Tester,
    LocalAiEngine,
}

/// <summary>A health word with the plain-English reason behind it, when there is one.</summary>
public readonly record struct StateCardHealthResult(HealthWord Word, string? LastProblem = null);

/// <summary>
/// The one place that turns each area's existing status (plugin list and health, Local AI switches and tools, run
/// records) into the four health words and the plain words a state card shows for "Allowed to" and "Sends data out".
/// A switched-off item is always <see cref="HealthWord.Off"/>; <see cref="HealthWord.Broken"/> means it is switched on and
/// cannot do its job.
/// </summary>
public static class StateCardHealth
{
    /// <summary>Where Local AI work happens; Local AI never sends data out.</summary>
    public const string RunsOnThisMachine = "Runs on this machine";

    /// <summary>What a plugin that stopped running because its permissions changed says on its card.</summary>
    public const string NeedsApprovalMessage = "Needs approval: its permissions changed";

    private const string RunFailed = "failed";

    /// <summary>What the Local AI engine card says it is allowed to do.</summary>
    public static IReadOnlyList<string> EngineAllowedTo { get; } = ["Runs AI models on this machine"];

    // ── Health ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A plugin's health word. <paramref name="health"/> is the latest health check (null when not asked yet) and
    /// <paramref name="latestRun"/> the newest run record for this plugin (null when it has never run).
    /// </summary>
    public static StateCardHealthResult ForPlugin(PluginSummaryResponse plugin, PluginHealthResponse? health, ScheduledTaskLastRunDto? latestRun)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (plugin.NeedsApproval)
        {
            // Switched on before, but what it may do has changed, so it is not running until it is approved again.
            return new(HealthWord.NeedsAttention, NeedsApprovalMessage);
        }

        if (!plugin.Enabled)
        {
            return new(HealthWord.Off);
        }

        if (!string.IsNullOrWhiteSpace(plugin.LoadError))
        {
            return new(HealthWord.Broken, plugin.LoadError.Trim());
        }

        if (!plugin.IsCompatible)
        {
            return new(HealthWord.Broken, StringHelpers.FirstNonBlank(plugin.CompatibilityProblem)?.Trim() ?? "This plugin does not work with this version of the Library.");
        }

        // The Library's host services (its own records, recurring jobs, events, always-on work and web connection) stop or
        // refuse a plugin that goes over a limit. "broken" is work that stopped for good; "needs_attention" is a limit reached.
        if (plugin.HostServices is { Problem: { } hostProblem } hostServices && !string.IsNullOrWhiteSpace(hostProblem))
        {
            if (string.Equals(hostServices.State, "broken", StringComparison.OrdinalIgnoreCase))
            {
                return new(HealthWord.Broken, hostProblem.Trim());
            }

            if (string.Equals(hostServices.State, "needs_attention", StringComparison.OrdinalIgnoreCase))
            {
                return new(HealthWord.NeedsAttention, hostProblem.Trim());
            }
        }

        if (string.Equals(health?.Status, "degraded", StringComparison.OrdinalIgnoreCase))
        {
            var message = health!.Checks.Select(check => check.Message).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
            return new(HealthWord.NeedsAttention, message?.Trim());
        }

        if (latestRun is not null && string.Equals(latestRun.Outcome, RunFailed, StringComparison.OrdinalIgnoreCase))
        {
            return new(HealthWord.NeedsAttention, StringHelpers.FirstNonBlank(latestRun.Message)?.Trim() ?? "The last run failed.");
        }

        // Still working: a refused address, a single failed event or a crash below the limit does not change the word,
        // but it is still the plugin's last problem and the card shows it.
        var lastProblem = StringHelpers.FirstNonBlank(plugin.HostServices?.LastProblem)?.Trim();
        return new(HealthWord.Working, lastProblem);
    }

    /// <summary>
    /// An application's health word (spec D-W): switched off is Off; a live key that expires soon, or one nobody has used for
    /// 90 days, needs attention. Revoked and expired keys are not live and say nothing here.
    /// </summary>
    public static StateCardHealthResult ForApplication(ApplicationResponse application, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (!application.IsEnabled)
        {
            return new(HealthWord.Off);
        }

        foreach (var credential in application.Credentials.Where(credential => credential.RevokedAt is null))
        {
            if (credential.ExpiresAt is { } expires && expires <= now)
            {
                continue;
            }

            if (IntegrationHealthRules.KeyNearExpiry(credential.ExpiresAt, now))
            {
                return new(HealthWord.NeedsAttention, $"The key \"{credential.Name}\" expires {RelativeFuture(credential.ExpiresAt!.Value, now)}.");
            }

            if (IntegrationHealthRules.KeyUnused(credential.CreatedAt, credential.LastUsedAt, now))
            {
                return new(HealthWord.NeedsAttention, $"The key \"{credential.Name}\" has not been used for {IntegrationHealthRules.UnusedKeyDays} days.");
            }
        }

        return new(HealthWord.Working);
    }

    /// <summary>
    /// A webhook's health word: failing deliveries in a row three times needs attention and five times is Broken (red in the
    /// menu), because a switched-on webhook that keeps failing cannot do its job. One failure is not enough to alarm anyone.
    /// </summary>
    public static StateCardHealthResult ForWebhook(ApplicationWebhookResponse webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        if (!webhook.IsEnabled)
        {
            return new(HealthWord.Off);
        }

        if (webhook.ConsecutiveFailures >= IntegrationHealthRules.WebhookBrokenFailures)
        {
            return new(HealthWord.Broken, $"The last {webhook.ConsecutiveFailures} deliveries failed: {webhook.LastStatus}");
        }

        return webhook.ConsecutiveFailures >= IntegrationHealthRules.WebhookAttentionFailures
            ? new(HealthWord.NeedsAttention, $"The last {webhook.ConsecutiveFailures} deliveries failed: {webhook.LastStatus}")
            : new(HealthWord.Working);
    }

    private static string RelativeFuture(DateTimeOffset when, DateTimeOffset now)
    {
        var days = (int)Math.Ceiling((when - now).TotalDays);
        return days <= 1 ? "within a day" : $"in {days} days";
    }

    /// <summary>True when the plugin is switched on and cannot do its job; needs no health check or run record.</summary>
    public static bool IsPluginBroken(PluginSummaryResponse plugin) =>
        ForPlugin(plugin, health: null, latestRun: null).Word == HealthWord.Broken;

    /// <summary>The Local AI engine card's health word. The engine has no switch, so it is never <see cref="HealthWord.Off"/>.</summary>
    public static StateCardHealthResult ForLocalAiEngine(bool localAiBlocked, string? firstBlocker, AiModelStatusDto? activeModel)
    {
        if (localAiBlocked)
        {
            return new(HealthWord.Broken, firstBlocker ?? "Local AI is not ready.");
        }

        if (activeModel is null)
        {
            return new(HealthWord.Working);
        }

        if (Is(activeModel.State, "Error"))
        {
            return new(HealthWord.Broken, "The model could not be loaded.");
        }

        return Is(activeModel.State, "Downloading")
            ? new(HealthWord.NeedsAttention, "The model is still downloading.")
            : new(HealthWord.Working);
    }

    /// <summary>The newest run record among the scheduled tasks that belong to a plugin, by finish time, else start time.</summary>
    public static ScheduledTaskLastRunDto? LatestRun(IEnumerable<ScheduledTaskDto>? tasks, string pluginId) =>
        tasks?
            .Where(task => string.Equals(task.PluginId, pluginId, StringComparison.OrdinalIgnoreCase) && task.LastRun is not null)
            .Select(task => task.LastRun!)
            .OrderByDescending(run => run.FinishedAt ?? run.StartedAt)
            .FirstOrDefault();

    /// <summary>When the run record last did something: finish time, else start time.</summary>
    public static DateTimeOffset? ActivityTime(ScheduledTaskLastRunDto? run) => run is null ? null : run.FinishedAt ?? run.StartedAt;

    /// <summary>"Just now", "5 minutes ago", "2 hours ago", "3 days ago", then the plain date. A moment in the future reads as "Just now".</summary>
    public static string RelativeTime(DateTimeOffset when, DateTimeOffset now)
    {
        var age = now - when;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "Just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return Plural((int)age.TotalMinutes, "minute");
        }

        if (age < TimeSpan.FromDays(1))
        {
            return Plural((int)age.TotalHours, "hour");
        }

        return age < TimeSpan.FromDays(7)
            ? Plural((int)age.TotalDays, "day")
            : DisplayFormat.FormatPlainDate(when);
    }

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")} ago";

    /// <summary>Plain words for a tool or model that stops Local AI, for example "Local AI text model is missing".</summary>
    public static string BlockerText(ToolRequirementDto tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.State switch
        {
            ToolRequirementStates.Downloading => $"{tool.Name} is still downloading",
            ToolRequirementStates.Problem => $"{tool.Name} is not working",
            ToolRequirementStates.InstallerReady => $"{tool.Name} needs its installer finished",
            _ => $"{tool.Name} is missing",
        };
    }

    // ── Words ────────────────────────────────────────────────────────────────

    public static string Text(HealthWord word) => word switch
    {
        HealthWord.Working => "Working",
        HealthWord.Off => "Off",
        HealthWord.NeedsAttention => "Needs attention",
        _ => "Broken",
    };

    public static AppUiTone Tone(HealthWord word) => word switch
    {
        HealthWord.Working => AppUiTone.Success,
        HealthWord.Off => AppUiTone.Neutral,
        HealthWord.NeedsAttention => AppUiTone.Warning,
        _ => AppUiTone.Error,
    };

    public static string KindLabel(StateCardKind kind) => kind switch
    {
        StateCardKind.Integration => "Integration",
        StateCardKind.Tester => "Tester",
        StateCardKind.LocalAiEngine => "Local AI engine",
        _ => "Plugin",
    };

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

}
