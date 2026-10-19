namespace MediaEngine.Contracts.System;

/// <summary>
/// The ranges a server administrator may choose for the Logs page retention settings. One place, so the Engine
/// action, the configuration validator and the Dashboard form agree.
/// </summary>
public static class LogRetentionLimits
{
    public const int MinDays = 1;
    public const int MaxDays = 3650;
    public const int MinActivityEvents = 1_000;
    public const int MaxActivityEvents = 10_000_000;

    /// <summary>Smallest total log size allowed: a single log file may grow to 50 MB, so the cap must leave room for a few.</summary>
    public const int MinLogTotalMegabytes = 100;
    public const int MaxLogTotalMegabytes = 102_400;

    /// <summary>Returns the sentence to show when a setting is out of range, or <see langword="null"/> when all four are valid.</summary>
    public static string? Validate(int activityDays, int activityMaxEvents, int logDays, int logMaxTotalMegabytes)
    {
        if (activityDays is < MinDays or > MaxDays)
        {
            return $"Keep events for between {MinDays} and {MaxDays} days.";
        }

        if (activityMaxEvents is < MinActivityEvents or > MaxActivityEvents)
        {
            return $"Keep between {MinActivityEvents:N0} and {MaxActivityEvents:N0} events.";
        }

        if (logDays is < MinDays or > MaxDays)
        {
            return $"Keep technical log files for between {MinDays} and {MaxDays} days.";
        }

        if (logMaxTotalMegabytes is < MinLogTotalMegabytes or > MaxLogTotalMegabytes)
        {
            return $"Keep between {MinLogTotalMegabytes:N0} MB and {MaxLogTotalMegabytes:N0} MB of technical log files.";
        }

        return null;
    }
}
