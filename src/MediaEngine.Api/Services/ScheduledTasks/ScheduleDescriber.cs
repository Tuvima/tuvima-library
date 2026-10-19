using System.Globalization;

namespace MediaEngine.Api.Services.ScheduledTasks;

/// <summary>
/// Turns a schedule into the plain words the Scheduled tasks list shows ("Every day at 3:00", "Every 6 hours").
/// Only the shapes the Engine's own schedules use are spelled out; anything else says it runs on a custom schedule.
/// </summary>
public static class ScheduleDescriber
{
    private static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    /// <summary>Describes a five-field cron expression ("0 3 * * *").</summary>
    public static string FromCron(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
        {
            return "On a custom schedule";
        }

        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            return "On a custom schedule";
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

        return "On a custom schedule";
    }

    /// <summary>Describes a fixed repeat interval ("Every minute", "Every 6 hours").</summary>
    public static string Every(TimeSpan interval)
    {
        if (interval < TimeSpan.FromMinutes(1))
        {
            var seconds = Math.Max(1, (int)Math.Round(interval.TotalSeconds));
            return seconds == 1 ? "Every second" : $"Every {seconds} seconds";
        }

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
}
