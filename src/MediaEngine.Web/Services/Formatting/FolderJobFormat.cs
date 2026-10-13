using System.Globalization;
using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Services.Formatting;

/// <summary>Plain-English wording for a folder job and for the time it is expected to take.</summary>
public static class FolderJobFormat
{
    /// <summary>"about 3 h", "about 25 min" or "less than a minute"; a rough figure on purpose.</summary>
    public static string Duration(int seconds)
    {
        if (seconds < 60)
        {
            return "less than a minute";
        }

        var minutes = (int)Math.Round(seconds / 60d);
        if (minutes < 60)
        {
            return $"about {minutes.ToString(CultureInfo.CurrentCulture)} min";
        }

        var hours = seconds / 3600d;
        return hours < 10
            ? $"about {hours.ToString("0.#", CultureInfo.CurrentCulture)} h"
            : $"about {((int)Math.Round(hours)).ToString(CultureInfo.CurrentCulture)} h";
    }

    /// <summary>True while the job still has work to do or is waiting to.</summary>
    public static bool IsOpen(FolderJobStatusDto job) => job.State is "queued" or "running" or "paused";

    /// <summary>The one line shown on the folder row for a job.</summary>
    public static string Line(FolderJobStatusDto job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var done = DisplayFormat.FormatCount(job.ItemsCompleted);
        var total = DisplayFormat.FormatCount(job.ItemsTotal);
        return job.State switch
        {
            "queued" when !string.IsNullOrWhiteSpace(job.QueuedBehind) => $"Queued behind {job.QueuedBehind}",
            "queued" => job.ItemsTotal > 0 ? $"Waiting to organise {total} files" : "Waiting to organise",
            "running" => job.EstimatedSecondsRemaining is { } left
                ? $"Organising {done} of {total} \u00b7 {Duration(left)} left"
                : $"Organising {done} of {total}",
            "paused" => $"Paused at {done} of {total}",
            _ => job.Message ?? string.Empty,
        };
    }
}
