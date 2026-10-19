using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Components.Navigation;

/// <summary>
/// The words the menu indicator uses for the Library's activity. The ingestion wording must match
/// IngestionQueueProgressText (live page) so every screen describes the same work the same way.
/// </summary>
public static class SystemActivityText
{
    public const string IdleLabel = "Library is up to date";
    public const string WorkingState = "Working";
    public const string IdleState = "Idle";

    private const int MaxTooltipItems = 3;

    /// <summary>The tooltip: the ingestion line first, then other work kinds by label, or the idle line.</summary>
    public static string Tooltip(ShellActivitySnapshot snapshot, DateTimeOffset now)
    {
        if (!snapshot.IsWorking)
        {
            return Idle(snapshot.Queue, now);
        }

        var parts = new List<string>();
        if (snapshot.Queue is { } queue && ShellActivityState.IsQueueWorking(queue))
        {
            parts.Add(Ingestion(queue, now));
        }

        parts.AddRange(snapshot.Items
            .Where(item => item.Kind != ShellActivityKind.Ingestion)
            .Select(FormatActivity));

        return string.Join(" • ", parts.Take(MaxTooltipItems));
    }

    /// <summary>"Found 508 files · 120 done" plus the time left, or how long it has been running.</summary>
    public static string Ingestion(IngestionQueueProgressDto queue, DateTimeOffset now)
    {
        var text = $"Found {queue.FilesFound:N0} files · {queue.FilesDone:N0} done";
        if (queue.EstimatedSecondsRemaining is { } seconds)
        {
            return $"{text} · About {Math.Max(1, (int)Math.Ceiling(seconds / 60d))} min left";
        }

        return queue.StartedAt is { } started
            ? $"{text} · Running for {Elapsed(now - started)}"
            : text;
    }

    public static string Idle(IngestionQueueProgressDto? queue, DateTimeOffset now) =>
        queue?.LastActivityAt is { } last
            ? $"{IdleLabel} · last activity {Relative(now - last)}"
            : IdleLabel;

    private static string FormatActivity(ShellActivityItem activity) => activity.ProgressPercent is { } progress
        ? $"{activity.Label} ({progress}%)"
        : activity.Label;

    private static string Elapsed(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
        {
            return "under a minute";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{(int)span.TotalMinutes} min";
        }

        return $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    private static string Relative(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours} h ago";
        }

        return $"{(int)age.TotalDays} d ago";
    }
}
