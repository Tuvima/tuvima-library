using System.Globalization;
using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Components.Shared;

/// <summary>
/// The audiobook sleep timer's choices and how a choice becomes a selection, in one place: Off, a few lengths, the end of this chapter and the
/// end of the next (unified player D10). The Settings menu's Sleep timer page and <see cref="PlaybackSleepTimerControl"/> both draw from it.
/// </summary>
public static class PlaybackSleepTimerChoices
{
    /// <summary>The value of the choice that matches the running timer, else the profile's default length, else Off.</summary>
    public static string SelectedValue(AudiobookSleepTimerStateDto state, int? defaultMinutes) => state.Mode switch
    {
        AudiobookSleepTimerModes.Timer when state.ChosenMinutes is > 0 => MinuteValue(state.ChosenMinutes.Value),
        AudiobookSleepTimerModes.EndCurrent => AudiobookSleepTimerModes.EndCurrent,
        AudiobookSleepTimerModes.EndNext => AudiobookSleepTimerModes.EndNext,
        _ when defaultMinutes is > 0 and <= 240 => MinuteValue(defaultMinutes.Value),
        _ => AudiobookSleepTimerModes.Off,
    };

    /// <summary>A short line for the current state ("Off", "30 minutes selected, 12 minutes remaining", "End of chapter: Part two").</summary>
    public static string StatusText(AudiobookSleepTimerStateDto state) => state.Mode switch
    {
        AudiobookSleepTimerModes.Timer => TimerStatus(state),
        AudiobookSleepTimerModes.EndCurrent => BoundaryStatus("End of chapter", state.TargetChapterTitle),
        AudiobookSleepTimerModes.EndNext => BoundaryStatus("End of next chapter", state.TargetChapterTitle),
        _ => "Off",
    };

    /// <summary>The value for the right side of the Settings row: "Off", "30 min", "End of chapter" or "End of next chapter".</summary>
    public static string RowValue(AudiobookSleepTimerStateDto state) => state.Mode switch
    {
        AudiobookSleepTimerModes.Timer when state.DeadlineUtc is { } deadline =>
            $"{Math.Max(0, (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalMinutes))} min left",
        AudiobookSleepTimerModes.Timer when state.ChosenMinutes is { } chosen => $"{chosen} min",
        AudiobookSleepTimerModes.EndCurrent => "End of chapter",
        AudiobookSleepTimerModes.EndNext => "End of next chapter",
        _ => "Off",
    };

    /// <summary>Off, the lengths on offer, then the end-of-chapter choices (disabled with a reason when no verified boundary exists).</summary>
    public static IReadOnlyList<AppSelectOption> Options(
        AudiobookSleepTimerStateDto state,
        IReadOnlyList<int> optionsMinutes,
        int? defaultMinutes,
        bool offersEndOfChapter,
        AudiobookSleepTimerAvailabilityDto availability)
    {
        var minuteOptions = optionsMinutes
            .Where(minutes => minutes is > 0 and <= 240)
            .Concat(state.Mode == AudiobookSleepTimerModes.Timer && state.ChosenMinutes is { } chosen ? [chosen] : [])
            .Concat(defaultMinutes is { } preferred ? [preferred] : [])
            .Append(15).Append(30).Append(45).Append(60)
            .Distinct()
            .Order()
            .Select(minutes => new AppSelectOption(MinuteValue(minutes), $"{minutes} min"))
            .ToList();

        var options = new List<AppSelectOption> { new(AudiobookSleepTimerModes.Off, "Off") };
        if (minuteOptions.Count > 0)
        {
            minuteOptions[0] = minuteOptions[0] with { SeparatorBefore = true };
        }
        options.AddRange(minuteOptions);
        if (!offersEndOfChapter)
        {
            return options;
        }

        options.Add(new AppSelectOption(
            AudiobookSleepTimerModes.EndCurrent,
            "End of chapter",
            AppMaterialIcons.Outlined.MenuBook,
            Disabled: !availability.CanEndCurrent,
            SeparatorBefore: true,
            Title: availability.CanEndCurrent ? null : availability.CurrentUnavailableReason ?? "No verified chapter boundary",
            AccessibleLabel: availability.CanEndCurrent ? null : $"End of chapter unavailable: {availability.CurrentUnavailableReason ?? "no verified chapter boundary"}"));
        options.Add(new AppSelectOption(
            AudiobookSleepTimerModes.EndNext,
            "End of next chapter",
            AppMaterialIcons.Outlined.SkipNext,
            Disabled: !availability.CanEndNext,
            Title: availability.CanEndNext ? null : availability.NextUnavailableReason ?? "No verified next chapter boundary",
            AccessibleLabel: availability.CanEndNext ? null : $"End of next chapter unavailable: {availability.NextUnavailableReason ?? "no verified next boundary"}"));
        return options;
    }

    /// <summary>Turns a chosen value into the selection to send, or null when the value is unknown or its chapter boundary is not available.</summary>
    public static AudiobookSleepTimerSelectionDto? SelectionFor(string value, AudiobookSleepTimerAvailabilityDto availability) => value switch
    {
        AudiobookSleepTimerModes.Off => new() { Mode = AudiobookSleepTimerModes.Off },
        AudiobookSleepTimerModes.EndCurrent when availability.CanEndCurrent => new() { Mode = AudiobookSleepTimerModes.EndCurrent },
        AudiobookSleepTimerModes.EndNext when availability.CanEndNext => new() { Mode = AudiobookSleepTimerModes.EndNext },
        _ when TryReadMinutes(value, out var minutes) => new() { Mode = AudiobookSleepTimerModes.Timer, Minutes = minutes },
        _ => null,
    };

    public static string MinuteValue(int minutes) => $"minutes:{minutes.ToString(CultureInfo.InvariantCulture)}";

    private static bool TryReadMinutes(string value, out int minutes)
    {
        minutes = 0;
        return value.StartsWith("minutes:", StringComparison.Ordinal)
            && int.TryParse(value.AsSpan("minutes:".Length), NumberStyles.None, CultureInfo.InvariantCulture, out minutes)
            && minutes is > 0 and <= 1440;
    }

    private static string TimerStatus(AudiobookSleepTimerStateDto state)
    {
        if (state.DeadlineUtc is not { } deadline)
        {
            return state.ChosenMinutes is { } chosen ? $"{chosen} minutes" : "Timer active";
        }

        var remaining = deadline - DateTimeOffset.UtcNow;
        var minutes = remaining <= TimeSpan.Zero ? 0 : Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return state.ChosenMinutes is { } preset
            ? $"{preset} minutes selected, {minutes} minutes remaining"
            : $"{minutes} minutes remaining";
    }

    private static string BoundaryStatus(string mode, string? title) =>
        string.IsNullOrWhiteSpace(title) ? mode : $"{mode}: {title}";
}
