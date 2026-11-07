using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Resources;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Playback;

/// <summary>The audiobook timeline labels (unified player v2 D9): chapter mode and whole-book mode.</summary>
public static class PlaybackTimelineLabels
{
    // The bar draws these in the mockup's order (elapsed, label, rail, time left) through PlaybackSeekRail; Summary is the same line as one
    // sentence ("Chapter 8 · 12:08 · −18:32"), which is what a screen reader hears.
    private const string Separator = " \u00B7 ";

    /// <summary>"Chapter 8" (chapter mode).</summary>
    public static string ChapterLabel(IStringLocalizer<SharedStrings> L, int number) => L["PlaybackTimeline_Chapter", number].Value;

    /// <summary>"Chapter 8 · 12:08 · −18:32" (chapter mode): the chapter, the time into it, and the time left in it.</summary>
    public static string ChapterSummary(IStringLocalizer<SharedStrings> L, int number, double elapsedSeconds, double remainingSeconds) =>
        string.Join(Separator, ChapterLabel(L, number), PlaybackSeekRail.Time(elapsedSeconds), PlaybackSeekRail.RemainingTime(remainingSeconds));

    /// <summary>"Book · 32%" (whole-book mode). The percentage rounds down, so it reads 100% only when the book is done.</summary>
    public static string BookLabel(IStringLocalizer<SharedStrings> L, double positionSeconds, double durationSeconds) =>
        $"{L["PlaybackTimeline_Book"].Value}{Separator}{BookPercent(positionSeconds, durationSeconds)}%";

    /// <summary>"Book · 32% complete" (the Chapters panel's line under the book).</summary>
    public static string BookComplete(IStringLocalizer<SharedStrings> L, double positionSeconds, double durationSeconds) =>
        L["PlaybackTimeline_BookComplete", BookPercent(positionSeconds, durationSeconds)].Value;

    /// <summary>"Book · 32% · 6h 12m left" (whole-book mode).</summary>
    public static string BookSummary(IStringLocalizer<SharedStrings> L, double positionSeconds, double durationSeconds) =>
        string.Join(Separator, BookLabel(L, positionSeconds, durationSeconds), TimeLeft(L, durationSeconds - positionSeconds));

    /// <summary>"6h 12m left", "42m left" or "Less than a minute left".</summary>
    public static string TimeLeft(IStringLocalizer<SharedStrings> L, double seconds)
    {
        var total = (int)Math.Max(0, double.IsFinite(seconds) ? seconds : 0);
        var hours = total / 3600;
        var minutes = total / 60 % 60;
        return hours > 0 ? L["PlaybackTimeline_TimeLeftHours", hours, minutes].Value
            : minutes > 0 ? L["PlaybackTimeline_TimeLeftMinutes", minutes].Value
            : L["PlaybackTimeline_TimeLeftUnderMinute"].Value;
    }

    private static int BookPercent(double positionSeconds, double durationSeconds) =>
        durationSeconds > 0 && double.IsFinite(durationSeconds) && double.IsFinite(positionSeconds)
            ? (int)Math.Clamp(Math.Floor(positionSeconds / durationSeconds * 100d), 0d, 100d)
            : 0;
}
