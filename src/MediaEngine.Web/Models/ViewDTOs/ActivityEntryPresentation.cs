using System.Text.Json;
using MediaEngine.Contracts.Activity;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// Dashboard-only presentation helpers for the shared activity wire contract.
/// </summary>
public static class ActivityEntryPresentation
{
    public static ActivityRichData? GetRichData(this ActivityEntryResponse entry)
    {
        if (entry.ActionType is not ("FileIngested" or "MediaAdded")
            || string.IsNullOrWhiteSpace(entry.ChangesJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ActivityRichData>(entry.ChangesJson);
        }
        catch (JsonException)
        {
            // Malformed historical activity payloads degrade to the plain-text entry.
            return null;
        }
    }

    public static ReviewRichData? GetReviewData(this ActivityEntryResponse entry)
    {
        if (entry.ActionType != "ReviewItemResolved"
            || string.IsNullOrWhiteSpace(entry.ChangesJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReviewRichData>(entry.ChangesJson);
        }
        catch (JsonException)
        {
            // Malformed historical activity payloads degrade to the plain-text entry.
            return null;
        }
    }

    public static string GetRelativeTime(this ActivityEntryResponse entry)
    {
        if (!DateTimeOffset.TryParse(entry.OccurredAt, out var timestamp))
        {
            return "just now";
        }

        var elapsed = DateTimeOffset.UtcNow - timestamp;
        return elapsed.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)elapsed.TotalMinutes}m ago",
            < 1440 => $"{(int)elapsed.TotalHours}h ago",
            _ => $"{(int)elapsed.TotalDays}d ago",
        };
    }
}
