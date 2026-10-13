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

    /// <summary>True for a job that merges a folder into a managed folder; false for one that organises a folder.</summary>
    public static bool IsMerge(FolderJobStatusDto job) =>
        string.Equals(job.JobType, "merge_folder", StringComparison.OrdinalIgnoreCase);

    /// <summary>"merging" or "organising", for the accessible names of the job's buttons.</summary>
    public static string Verb(FolderJobStatusDto job) => IsMerge(job) ? "merging" : "organising";

    /// <summary>The one line shown on the folder row for a job.</summary>
    public static string Line(FolderJobStatusDto job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var done = DisplayFormat.FormatCount(job.ItemsCompleted);
        var total = DisplayFormat.FormatCount(job.ItemsTotal);
        var merge = IsMerge(job);
        return job.State switch
        {
            "queued" when !string.IsNullOrWhiteSpace(job.QueuedBehind) => $"Queued behind {job.QueuedBehind}",
            "queued" => job.ItemsTotal > 0
                ? $"Waiting to {(merge ? "merge" : "organise")} {total} {(job.ItemsTotal == 1 ? "file" : "files")}"
                : $"Waiting to {(merge ? "merge" : "organise")}",
            "running" => job.EstimatedSecondsRemaining is { } left
                ? $"{(merge ? "Merging" : "Organising")} {done} of {total} \u00b7 {Duration(left)} left"
                : $"{(merge ? "Merging" : "Organising")} {done} of {total}",
            "paused" => $"Paused at {done} of {total}",
            _ => job.Message ?? string.Empty,
        };
    }

    /// <summary>What a folder row says once its files were merged into another folder: "Merged into Movies on 13 Oct 2026".</summary>
    public static string MergedLine(string targetName, DateTimeOffset? mergedAt) => mergedAt is { } when
        ? $"Merged into {targetName} on {when.LocalDateTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}"
        : $"Merged into {targetName}";

    /// <summary>
    /// How the drive a path lives on is named in the sheet's "frees space on ..." line: "drive E:" for a Windows path, otherwise
    /// the folder itself, because the Dashboard cannot see the Engine's mounts.
    /// </summary>
    public static string DriveLabel(string path)
    {
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
        {
            return $"drive {char.ToUpperInvariant(path[0])}:";
        }

        return "the drive that holds this folder";
    }

    /// <summary>A rough size for people: "850 MB", "12.4 GB".</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value.ToString("N0", CultureInfo.CurrentCulture)} bytes"
            : $"{value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture)} {units[unit]}";
    }
}
