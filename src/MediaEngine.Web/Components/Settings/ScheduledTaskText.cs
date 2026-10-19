using System.Globalization;
using MediaEngine.Contracts.System;

namespace MediaEngine.Web.Components.Settings;

/// <summary>The words the Scheduled tasks list uses for when a task last ran and runs next.</summary>
public static class ScheduledTaskText
{
    public const string NeverRun = "Not run since the Engine started";
    public const string NextUnknown = "Not scheduled yet";

    /// <summary>"Done today at 3:00 AM", "Failed yesterday at 3:00 AM: the data store was busy", or <see cref="NeverRun"/>.</summary>
    public static string LastRun(ScheduledTaskDto task, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.LastRunAt is not { } at)
        {
            return NeverRun;
        }

        var when = Moment(at, now);
        if (string.Equals(task.LastRunResult, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(task.LastRunReason)
                ? $"Failed {when}"
                : $"Failed {when}: {task.LastRunReason.TrimEnd('.')}";
        }

        return $"Done {when}";
    }

    /// <summary>"Tomorrow at 3:00 AM", or <see cref="NextUnknown"/> when the task does not know.</summary>
    public static string NextRun(ScheduledTaskDto task, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.NextRunAt is { } at ? Capitalise(Moment(at, now)) : NextUnknown;
    }

    /// <summary>A moment in the viewer's local time relative to today: "today at 3:00 AM", "tomorrow at …", "Oct 21 at …".</summary>
    public static string Moment(DateTimeOffset moment, DateTimeOffset now)
    {
        var local = moment.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        var days = (local.Date - today).Days;
        return days switch
        {
            0 => $"today at {time}",
            1 => $"tomorrow at {time}",
            -1 => $"yesterday at {time}",
            _ => $"{local.ToString("MMM d", CultureInfo.CurrentCulture)} at {time}",
        };
    }

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
}
