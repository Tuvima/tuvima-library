using System.Globalization;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// What the Sleep timer panel offers and how a choice becomes a selection, in one place (unified player v2 D16): the four presets, a custom length,
/// "+5 minutes" on a running timer, the "14:32" countdown and the chapter named by an end-of-chapter timer. Every value is derived from the timer
/// state the session holds (its deadline), never from a clock of the panel's own, so the bar, Now playing and a reload agree.
/// </summary>
public static class PlaybackSleepTimerChoices
{
    /// <summary>The preset lengths, in minutes, in the order the panel lists them.</summary>
    public static IReadOnlyList<int> Presets { get; } = [15, 30, 45, 60];

    /// <summary>The minutes "+5 minutes" adds to what is left.</summary>
    public const int ExtensionMinutes = 5;

    public static bool IsRunning(AudiobookSleepTimerStateDto state) => state.Mode != AudiobookSleepTimerModes.Off;

    public static bool IsEndOfChapter(AudiobookSleepTimerStateDto state) =>
        state.Mode is AudiobookSleepTimerModes.EndCurrent or AudiobookSleepTimerModes.EndNext;

    /// <summary>The time left on a length timer, never negative; null when the timer is off, ends at a chapter or has no deadline.</summary>
    public static TimeSpan? Remaining(AudiobookSleepTimerStateDto state, DateTimeOffset now) =>
        state.Mode == AudiobookSleepTimerModes.Timer && state.DeadlineUtc is { } deadline
            ? (deadline > now ? deadline - now : TimeSpan.Zero)
            : null;

    /// <summary>The countdown as "14:32", or "1:04:32" from one hour up. A part of a second counts as the whole second, so it never shows 0:00 early.</summary>
    public static string FormatCountdown(TimeSpan remaining)
    {
        var seconds = (long)Math.Ceiling(Math.Max(0d, remaining.TotalSeconds));
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        var rest = seconds % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }

    /// <summary>The preset that is running (a length timer started from the grid), else null; a custom length highlights no preset.</summary>
    public static int? RunningPreset(AudiobookSleepTimerStateDto state) =>
        state.Mode == AudiobookSleepTimerModes.Timer && state.ChosenMinutes is { } chosen && Presets.Contains(chosen) ? chosen : null;

    public static AudiobookSleepTimerSelectionDto ForMinutes(int minutes) => new() { Mode = AudiobookSleepTimerModes.Timer, Minutes = minutes };

    public static AudiobookSleepTimerSelectionDto ForEndOfChapter() => new() { Mode = AudiobookSleepTimerModes.EndCurrent };

    public static AudiobookSleepTimerSelectionDto ForOff() => new() { Mode = AudiobookSleepTimerModes.Off };

    /// <summary>
    /// "+5 minutes": the selection that sets a new timer at what is left plus five minutes, through the same set action every other choice uses (no
    /// new Engine action). The session counts whole minutes, so the length rounds up and never shortens the five. Null when the timer is not a length
    /// timer (an end-of-chapter timer has no time left to add to).
    /// </summary>
    public static AudiobookSleepTimerSelectionDto? Extension(AudiobookSleepTimerStateDto state, DateTimeOffset now)
    {
        if (Remaining(state, now) is not { } remaining)
        {
            return null;
        }

        var seconds = remaining.TotalSeconds + ExtensionMinutes * 60d;
        var minutes = (int)Math.Min(AudiobookSleepTimerLimits.MaximumMinutes, Math.Ceiling(seconds / 60d));
        return ForMinutes(Math.Max(AudiobookSleepTimerLimits.MinimumMinutes, minutes));
    }

    /// <summary>Reads the Custom duration field: a whole number of minutes in the allowed range. Digits only, so "1e3", "-5" and "12.5" are all refused.</summary>
    public static bool TryReadCustomMinutes(string? text, out int minutes)
    {
        minutes = 0;
        var trimmed = text?.Trim();
        return !string.IsNullOrEmpty(trimmed)
            && int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out minutes)
            && AudiobookSleepTimerLimits.IsValidMinutes(minutes);
    }

    /// <summary>
    /// The number the chapter an end-of-chapter timer stops at has in the book ("8" for "Stopping at the end of chapter 8"), counted in book order the way
    /// the bar's timeline counts. Null when the chapter cannot be found, in which case the panel says "the chapter" without a number.
    /// </summary>
    public static int? TargetChapterNumber(AudiobookSleepTimerStateDto state, IReadOnlyList<PlaybackChapterDto>? chapters)
    {
        if (!IsEndOfChapter(state) || chapters is null || chapters.Count == 0)
        {
            return null;
        }

        var ordered = chapters.OrderBy(chapter => chapter.StartSeconds).ToList();
        var index = ordered.FindIndex(chapter => chapter.Index == state.TargetChapterIndex
            && (chapter.AssetId is null || chapter.AssetId == Guid.Empty || chapter.AssetId == state.TargetAssetId));
        return index < 0 ? null : index + 1;
    }
}
