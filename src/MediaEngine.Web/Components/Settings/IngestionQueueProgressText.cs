using System.Globalization;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The one set of words for the Engine's ingestion queue snapshot. The live Ingestion page, the Overview and the menu
/// indicator all read it so the same snapshot always reads the same. It only formats: every number comes from the
/// snapshot, and the Dashboard never works out a percentage or an estimate of its own.
/// </summary>
public static class IngestionQueueProgressText
{
    public const string WorkingState = "working";
    public const string WaitingState = "waiting";
    public const string IdleState = "idle";

    /// <summary>The tool id the Engine reports while it cannot find FFmpeg.</summary>
    public const string FfmpegToolId = "ffmpeg";

    public static bool IsWorking(IngestionQueueProgressDto snapshot) => HasState(snapshot, WorkingState);

    public static bool IsWaiting(IngestionQueueProgressDto snapshot) => HasState(snapshot, WaitingState);

    public static bool IsIdle(IngestionQueueProgressDto snapshot) => !IsWorking(snapshot) && !IsWaiting(snapshot);

    /// <summary>The bar value, 0 to 100, taken from the Engine's 0 to 1 progress.</summary>
    public static double BarValue(IngestionQueueProgressDto snapshot) => snapshot.Progress * 100;

    /// <summary>The headline for the snapshot's state.</summary>
    public static string Headline(IngestionQueueProgressDto snapshot)
    {
        if (IsWaiting(snapshot))
        {
            return WaitingHeadline(snapshot);
        }

        if (IsWorking(snapshot))
        {
            return snapshot.FilesFound == 1
                ? "Found 1 file"
                : $"Found {DisplayFormat.FormatCount(snapshot.FilesFound)} files";
        }

        return "Library is up to date";
    }

    /// <summary>"Waiting for media tools", or "Waiting for {tool}" when only other tools are missing.</summary>
    public static string WaitingHeadline(IngestionQueueProgressDto snapshot)
    {
        if (snapshot.WaitingFor.Count == 0
            || snapshot.WaitingFor.Any(tool => string.Equals(tool, FfmpegToolId, StringComparison.OrdinalIgnoreCase)))
        {
            return "Waiting for media tools";
        }

        return $"Waiting for {string.Join(", ", snapshot.WaitingFor)}";
    }

    /// <summary>"212 movies, 140 episodes, 96 songs, 29 still being sorted", in the Engine's order; null when empty.</summary>
    public static string? TallyLine(IngestionQueueProgressDto snapshot)
    {
        var parts = new List<string>();
        foreach (var entry in snapshot.Tally)
        {
            if (entry.Files > 0)
            {
                parts.Add($"{DisplayFormat.FormatCount(entry.Files)} {TypeNoun(entry.MediaType, entry.Files)}");
            }
        }

        if (snapshot.FilesUnsorted > 0)
        {
            parts.Add($"{DisplayFormat.FormatCount(snapshot.FilesUnsorted)} still being sorted");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>"120 more found" for files that joined after the period began; null when none.</summary>
    public static string? MoreFoundLine(IngestionQueueProgressDto snapshot) =>
        snapshot.FilesMoreFound > 0 ? $"{DisplayFormat.FormatCount(snapshot.FilesMoreFound)} more found" : null;

    /// <summary>"312 of 508 done · 3 need review · 1 failed".</summary>
    public static string StatusLine(IngestionQueueProgressDto snapshot)
    {
        var parts = new List<string>
        {
            $"{DisplayFormat.FormatCount(snapshot.FilesDone)} of {DisplayFormat.FormatCount(snapshot.FilesFound)} done",
        };
        if (snapshot.Outcomes.NeedsReview > 0)
        {
            parts.Add($"{DisplayFormat.FormatCount(snapshot.Outcomes.NeedsReview)} need review");
        }

        if (snapshot.Outcomes.Failed > 0)
        {
            parts.Add($"{DisplayFormat.FormatCount(snapshot.Outcomes.Failed)} failed");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The estimate when the Engine sends one, otherwise how long the run has been going; null when neither is known.
    /// </summary>
    public static string? TimeLine(IngestionQueueProgressDto snapshot, DateTimeOffset now)
    {
        if (snapshot.EstimatedSecondsRemaining is { } seconds)
        {
            return seconds < 60
                ? "Less than a minute left"
                : $"About {(int)Math.Ceiling(seconds / 60d)} min left";
        }

        return snapshot.StartedAt is { } startedAt
            ? $"Running for {FormatDuration(now - startedAt.ToUniversalTime())}"
            : null;
    }

    /// <summary>"3 files are waiting" while tools are missing.</summary>
    public static string WaitingLine(IngestionQueueProgressDto snapshot) =>
        snapshot.FilesWaiting == 1
            ? "1 file is waiting"
            : $"{DisplayFormat.FormatCount(snapshot.FilesWaiting)} files are waiting";

    /// <summary>"Last activity 5 minutes ago"; null when the Engine has not seen any.</summary>
    public static string? LastActivityLine(IngestionQueueProgressDto snapshot, DateTimeOffset now) =>
        snapshot.LastActivityAt is { } at ? $"Last activity {FormatRelative(at, now)}" : null;

    /// <summary>One line for compact surfaces such as the Overview: the state's status and its time.</summary>
    public static string SummaryLine(IngestionQueueProgressDto snapshot, DateTimeOffset now)
    {
        if (IsWaiting(snapshot))
        {
            return $"{WaitingHeadline(snapshot)} · {WaitingLine(snapshot)}";
        }

        if (IsWorking(snapshot))
        {
            var time = TimeLine(snapshot, now);
            return time is null ? StatusLine(snapshot) : $"{StatusLine(snapshot)} · {time}";
        }

        return LastActivityLine(snapshot, now) is { } last
            ? $"{Headline(snapshot)} · {last}"
            : Headline(snapshot);
    }

    /// <summary>The noun for a count of one media type, for example "movie" or "audiobook files".</summary>
    public static string TypeNoun(string mediaType, int count)
    {
        var (singular, plural) = mediaType switch
        {
            "Movies" => ("movie", "movies"),
            "TV" => ("episode", "episodes"),
            "Music" => ("song", "songs"),
            "Books" => ("book", "books"),
            "Audiobooks" => ("audiobook file", "audiobook files"),
            "Comics" => ("comic", "comics"),
            _ => (mediaType.ToLowerInvariant(), mediaType.ToLowerInvariant()),
        };
        return count == 1 ? singular : plural;
    }

    public static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}h {value.Minutes}m"
            : $"{Math.Max(0, (int)value.TotalMinutes)}m {Math.Max(0, value.Seconds)}s";

    private static string FormatRelative(DateTimeOffset value, DateTimeOffset now)
    {
        var elapsed = now - value.ToUniversalTime();
        if (elapsed.TotalMinutes < 1)
        {
            return "just now";
        }

        if (elapsed.TotalHours < 1)
        {
            var minutes = (int)elapsed.TotalMinutes;
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        if (elapsed.TotalDays < 1)
        {
            var hours = (int)elapsed.TotalHours;
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        return value.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    private static bool HasState(IngestionQueueProgressDto snapshot, string state) =>
        string.Equals(snapshot.State, state, StringComparison.OrdinalIgnoreCase);
}
