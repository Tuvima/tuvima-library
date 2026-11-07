namespace MediaEngine.Web.Services.Playback;

/// <summary>
/// The lengths an audiobook sleep timer may be set to (unified player v2 D16): any whole number of minutes from one to twelve hours, so the Custom
/// duration field and "+5 minutes" can reach lengths the four presets do not. The session controller and the Sleep timer panel read the same range.
/// </summary>
public static class AudiobookSleepTimerLimits
{
    public const int MinimumMinutes = 1;
    public const int MaximumMinutes = 720;

    public static bool IsValidMinutes(int minutes) => minutes is >= MinimumMinutes and <= MaximumMinutes;
}
