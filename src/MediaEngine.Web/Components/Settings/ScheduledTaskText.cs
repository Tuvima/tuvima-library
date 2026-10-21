using System.Globalization;
using MediaEngine.Contracts.System;

namespace MediaEngine.Web.Components.Settings;

/// <summary>The words the Scheduled tasks list uses for a task's schedule, when it last ran and when it runs next.</summary>
public static class ScheduledTaskText
{
    public const string NeverRun = "Has not run yet";
    public const string RunsInBackground = "Runs quietly in the background";
    public const string NextUnknown = "Not scheduled yet";
    public const string CustomSchedule = "On a custom schedule";

    private static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    /// <summary>"Every day at 3:00", "Every 6 hours", or the task's own words for continuous work ("Every 5 seconds").</summary>
    public static string Schedule(ScheduledTaskDto task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.IsContinuous ? Capitalise(task.Schedule) : FromCron(task.Schedule);
    }

    /// <summary>
    /// "Done today at 3:00 AM", "Failed yesterday at 3:00 AM: the data store was busy", "Skipped ...", or
    /// <see cref="NeverRun"/>. A run still going says so.
    /// </summary>
    public static string LastRun(ScheduledTaskDto task, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.LastRun is not { } run)
        {
            return task.IsContinuous ? RunsInBackground : NeverRun;
        }

        var when = Moment(run.FinishedAt ?? run.StartedAt, now);
        var message = string.IsNullOrWhiteSpace(run.Message) ? null : run.Message.Trim().TrimEnd('.');
        return run.Outcome.ToLowerInvariant() switch
        {
            "running" => $"Running now, started {Moment(run.StartedAt, now)}",
            "failed" => message is null ? $"Failed {when}" : $"Failed {when}: {message}",
            "skipped" => message is null ? $"Skipped {when}" : $"Skipped {when}: {message}",
            "interrupted" => $"Stopped before it finished {when}",
            _ => $"Done {when}",
        };
    }

    /// <summary>True when the last run failed or was cut short, so the list can draw attention to it.</summary>
    public static bool LastRunNeedsAttention(ScheduledTaskDto task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.LastRun?.Outcome is { } outcome
            && (string.Equals(outcome, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outcome, "interrupted", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"Tomorrow at 3:00 AM", "All the time" for continuous work, or <see cref="NextUnknown"/>.</summary>
    public static string NextRun(ScheduledTaskDto task, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsContinuous)
        {
            return "Ongoing";
        }

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

    /// <summary>
    /// Describes a five-field cron expression in plain words. Only the shapes the Engine's own schedules use are spelled
    /// out; anything else says it runs on a custom schedule.
    /// </summary>
    public static string FromCron(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            return CustomSchedule;
        }

        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            return CustomSchedule;
        }

        var (minute, hour, dayOfMonth, month, dayOfWeek) = (fields[0], fields[1], fields[2], fields[3], fields[4]);

        // "*/15 * * * *" - every N minutes.
        if (TryStep(minute, out var minuteStep) && hour == "*" && dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
        {
            return Every(TimeSpan.FromMinutes(minuteStep));
        }

        // "0 */6 * * *" - every N hours.
        if (minute == "0" && TryStep(hour, out var hourStep) && dayOfMonth == "*" && month == "*" && dayOfWeek == "*")
        {
            return Every(TimeSpan.FromHours(hourStep));
        }

        if (int.TryParse(minute, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= 0 and <= 59
            && int.TryParse(hour, NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h is >= 0 and <= 23
            && dayOfMonth == "*" && month == "*")
        {
            var time = $"{h}:{m:00}";
            if (dayOfWeek == "*")
            {
                return $"Every day at {time}";
            }

            if (int.TryParse(dayOfWeek, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 0 and <= 7)
            {
                return $"Every {DayNames[d % 7]} at {time}";
            }
        }

        return CustomSchedule;
    }

    private static string Every(TimeSpan interval)
    {
        if (interval < TimeSpan.FromHours(1))
        {
            var minutes = (int)Math.Round(interval.TotalMinutes);
            return minutes == 1 ? "Every minute" : $"Every {minutes} minutes";
        }

        if (interval < TimeSpan.FromDays(1))
        {
            var hours = (int)Math.Round(interval.TotalHours);
            return hours == 1 ? "Every hour" : $"Every {hours} hours";
        }

        var days = (int)Math.Round(interval.TotalDays);
        return days == 1 ? "Every day" : $"Every {days} days";
    }

    private static bool TryStep(string field, out int step)
    {
        step = 0;
        return field.StartsWith("*/", StringComparison.Ordinal)
            && int.TryParse(field.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out step)
            && step > 0;
    }

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
}
