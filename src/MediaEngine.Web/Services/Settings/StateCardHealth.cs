using MediaEngine.Contracts.Ai;
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
    /// <summary>The model role every Local AI feature reads from today.</summary>
    public const string FeatureModelRole = "text_quality";

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

        if (string.Equals(health?.Status, "degraded", StringComparison.OrdinalIgnoreCase))
        {
            var message = health!.Checks.Select(check => check.Message).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
            return new(HealthWord.NeedsAttention, message?.Trim());
        }

        if (latestRun is not null && string.Equals(latestRun.Outcome, RunFailed, StringComparison.OrdinalIgnoreCase))
        {
            return new(HealthWord.NeedsAttention, StringHelpers.FirstNonBlank(latestRun.Message)?.Trim() ?? "The last run failed.");
        }

        return new(HealthWord.Working);
    }

    /// <summary>True when the plugin is switched on and cannot do its job; needs no health check or run record.</summary>
    public static bool IsPluginBroken(PluginSummaryResponse plugin) =>
        ForPlugin(plugin, health: null, latestRun: null).Word == HealthWord.Broken;

    /// <summary>
    /// A Local AI feature's health word. <paramref name="firstBlocker"/> is the plain words for the first thing stopping
    /// Local AI (null when nothing is); <paramref name="modelReady"/> says whether the text model the feature reads is ready.
    /// </summary>
    public static StateCardHealthResult ForAiFeature(bool enabled, bool localAiBlocked, string? firstBlocker, bool modelReady)
    {
        if (!enabled)
        {
            return new(HealthWord.Off);
        }

        if (localAiBlocked)
        {
            return new(HealthWord.Broken, firstBlocker ?? "Local AI is not ready.");
        }

        return modelReady ? new(HealthWord.Working) : new(HealthWord.NeedsAttention, "Model not ready");
    }

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

    /// <summary>True when the model file is ready to use (downloaded, or already loaded).</summary>
    public static bool IsModelReady(AiModelStatusDto? model) =>
        model is not null && (model.Loaded || Is(model.State, "Ready") || Is(model.State, "Loaded"));

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
