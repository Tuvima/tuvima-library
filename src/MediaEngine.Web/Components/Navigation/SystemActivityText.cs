using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Components.Navigation;

/// <summary>
/// The words the menu indicator uses for the Library's activity. The ingestion wording comes from
/// IngestionQueueProgressText, the live page's formatter, so every screen describes the same work the same way.
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
        // The snapshot's ingestion item exists only while the queue is working and its numbers are fresh.
        if (snapshot.Queue is { } queue && snapshot.Items.Any(item => item.Kind == ShellActivityKind.Ingestion))
        {
            parts.Add(Ingestion(queue, now));
        }

        parts.AddRange(snapshot.Items
            .Where(item => item.Kind != ShellActivityKind.Ingestion)
            .Select(FormatActivity));

        return string.Join(" • ", parts.Take(MaxTooltipItems));
    }

    /// <summary>The live page's headline, status and time on one line: "Found 508 files · 120 of 508 done · About 4 min left".</summary>
    public static string Ingestion(IngestionQueueProgressDto queue, DateTimeOffset now)
    {
        var text = $"{IngestionQueueProgressText.Headline(queue)} · {IngestionQueueProgressText.StatusLine(queue)}";
        return IngestionQueueProgressText.TimeLine(queue, now) is { } time ? $"{text} · {time}" : text;
    }

    /// <summary>
    /// The idle line. While tools are missing the indicator stays green but says why, in the live page's words:
    /// "Waiting for media tools · 3 files are waiting".
    /// </summary>
    public static string Idle(IngestionQueueProgressDto? queue, DateTimeOffset now)
    {
        if (queue is not null && IngestionQueueProgressText.IsWaiting(queue))
        {
            return $"{IngestionQueueProgressText.WaitingHeadline(queue)} · {IngestionQueueProgressText.WaitingLine(queue)}";
        }

        return queue is not null && IngestionQueueProgressText.LastActivityLine(queue, now) is { } last
            ? $"{IdleLabel} · {last}"
            : IdleLabel;
    }

    private static string FormatActivity(ShellActivityItem activity) => activity.ProgressPercent is { } progress
        ? $"{activity.Label} ({progress}%)"
        : activity.Label;
}
