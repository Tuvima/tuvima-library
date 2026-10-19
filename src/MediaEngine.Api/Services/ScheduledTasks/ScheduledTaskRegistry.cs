using System.Collections.Concurrent;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ScheduledTasks;

/// <summary>Who owns a scheduled task. These exact words are shown to people and sent over the wire.</summary>
public static class ScheduledTaskOwners
{
    public const string Library = "Library";
    public const string LocalAi = "Local AI";
    public const string Plugin = "Plugin";
}

/// <summary>
/// One recurring task as the Scheduled tasks list sees it.
/// </summary>
/// <param name="Key">Snake_case key shared with the run records.</param>
/// <param name="Name">Plain-English name, for example "Vibe tags".</param>
/// <param name="Owner">One of <see cref="ScheduledTaskOwners"/>.</param>
/// <param name="PluginId">Set when a single plugin owns the task.</param>
/// <param name="ScheduleProvider">
/// Returns the schedule currently in force, read from settings each time it is called so edits show straight away.
/// A cron expression, or plain text for continuous work.
/// </param>
/// <param name="SettingsPlace">Plain text saying where the schedule is set, for example "Local AI &gt; Automation".</param>
/// <param name="IsContinuous">True for work that runs all the time and has no cron schedule.</param>
/// <param name="LastRunProvider">
/// Supplies the last run for continuous work that does not write run records (webhook delivery). Leave null for tasks
/// recorded through <see cref="ScheduledTaskRunner"/>.
/// </param>
public sealed record ScheduledTaskDescriptor(
    string Key,
    string Name,
    string Owner,
    string? PluginId,
    Func<string> ScheduleProvider,
    string SettingsPlace,
    bool IsContinuous = false,
    Func<CancellationToken, Task<ScheduledTaskRun?>>? LastRunProvider = null);

/// <summary>Every recurring task the Engine runs.</summary>
public interface IScheduledTaskRegistry
{
    /// <summary>Adds a task, replacing any earlier one with the same key.</summary>
    void Register(ScheduledTaskDescriptor descriptor);

    /// <summary>All registered tasks, ordered by name.</summary>
    IReadOnlyList<ScheduledTaskDescriptor> List();
}

public sealed class ScheduledTaskRegistry : IScheduledTaskRegistry
{
    private readonly ConcurrentDictionary<string, ScheduledTaskDescriptor> _tasks = new(StringComparer.Ordinal);

    public void Register(ScheduledTaskDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Key);

        _tasks[descriptor.Key] = descriptor;
    }

    public IReadOnlyList<ScheduledTaskDescriptor> List() =>
        _tasks.Values.OrderBy(task => task.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
