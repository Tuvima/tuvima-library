using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Components.Settings.PlayingNow;

/// <summary>The plain words and small rules of the Playing now list, kept apart from the markup so they can be tested.</summary>
public static class PlayingNowText
{
    /// <summary>"1:42" for a countdown, "42:10" for a position, "2:35:00" past an hour.</summary>
    public static string Clock(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:00}";
    }

    /// <summary>"Dana · Dana's profile · Living room TV": the person, the profile (a Kids profile says so) and the device, whichever are known.</summary>
    public static string WhoLine(PlayingNowRowDto row, string kidsProfileWord)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.PersonName))
        {
            parts.Add(row.PersonName!);
        }

        if (!string.IsNullOrWhiteSpace(row.ProfileName) && !string.Equals(row.ProfileName, row.PersonName, StringComparison.Ordinal))
        {
            parts.Add(row.IsKidsProfile ? $"{row.ProfileName} ({kidsProfileWord})" : row.ProfileName!);
        }
        else if (row.IsKidsProfile)
        {
            parts.Add(kidsProfileWord);
        }

        if (!string.IsNullOrWhiteSpace(row.DeviceName))
        {
            parts.Add(row.DeviceName!);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Whether a row passes the People and Device filters. A person value is "a:{accountId}" or "p:{profileId}"; empty means everyone.</summary>
    public static bool Matches(PlayingNowRowDto row, string? person, string? device)
    {
        if (!string.IsNullOrEmpty(person))
        {
            var id = person[2..];
            var ok = person.StartsWith("a:", StringComparison.Ordinal)
                ? string.Equals(row.AccountId, id, StringComparison.OrdinalIgnoreCase)
                : string.Equals(row.ProfileId, id, StringComparison.OrdinalIgnoreCase);
            if (!ok)
            {
                return false;
            }
        }

        return string.IsNullOrEmpty(device) || string.Equals(row.DeviceId, device, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The words for how heavy an encode is: Light, Medium or Heavy.</summary>
    public static string HeavinessKey(string? heaviness) => heaviness switch
    {
        PlayingNowHeaviness.Heavy => "PlayingNow_Heavy",
        PlayingNowHeaviness.Medium => "PlayingNow_Medium",
        _ => "PlayingNow_Light",
    };

    /// <summary>The resource key for the label of what a row is doing.</summary>
    public static string StepKey(string step) => step switch
    {
        PlayingNowSteps.Repackaging => "PlayingNow_Repackaging",
        PlayingNowSteps.ConvertingAudio => "PlayingNow_ConvertingAudio",
        PlayingNowSteps.EncodingPicture => "PlayingNow_EncodingPicture",
        PlayingNowSteps.Preparing => "PlayingNow_Preparing",
        _ => "PlayingNow_PlayingDirectly",
    };

    /// <summary>The snapshot line for the Right now card: "3 watching · 1 preparing · Heavy on the processor", or null when nothing plays.</summary>
    public static bool IsIdle(PlayingNowDto? list) => list is not { Rows.Count: > 0 };
}
