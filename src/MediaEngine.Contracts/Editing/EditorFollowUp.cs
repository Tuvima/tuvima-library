using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Editing;

/// <summary>The states a follow-up step can be in, as words the Dashboard can draw (a tick, a spinner, a dot, amber).</summary>
public static class EditorFollowUpStates
{
    public const string Done = "done";

    public const string Running = "running";

    public const string Waiting = "waiting";

    public const string Failed = "failed";
}

/// <summary>The fixed, named steps of the Engine's work after a match is saved, in the order they happen.</summary>
public static class EditorFollowUpSteps
{
    public const string Matched = "matched";

    public const string Details = "details";

    public const string PicturesAndCast = "pictures_and_cast";

    public const string Related = "related";

    public const string Files = "files";
}

/// <summary>
/// The Engine's finishing work for one saved item, by named step (universal editor v3, D7 and D8 item 1). Read from the item's durable
/// identity job, so it is the same whether the Dashboard was watching or came back later. A read; nothing changes.
/// </summary>
public sealed class EditorFollowUpDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    /// <summary>True when nothing more will happen on its own: every step is done, or one failed. False while work is still going.</summary>
    [JsonPropertyName("finished")]
    public bool Finished { get; set; }

    /// <summary>The steps in order. Empty when the item has no finishing work (a details-only save).</summary>
    [JsonPropertyName("steps")]
    public List<EditorFollowUpStepDto> Steps { get; set; } = [];
}

public sealed class EditorFollowUpStepDto
{
    /// <summary>One of <see cref="EditorFollowUpSteps"/>.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>The step in plain words ("Details from the providers").</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>One of <see cref="EditorFollowUpStates"/>.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = EditorFollowUpStates.Waiting;

    /// <summary>For a failed step, why, in plain words; otherwise null.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// "Re-match seasons and episodes" (universal editor v3, D8 item 3): after a show is re-matched, the library's seasons and episodes of
/// that show are matched again against the new show by their own season and episode numbers. Whatever the new show does not have is sent
/// to Needs Review instead of being left on the old match. The target names the new TheTVDB show.
/// </summary>
public sealed class ShowChildRematchTarget
{
    [JsonPropertyName("series_id")]
    public string SeriesId { get; set; } = string.Empty;
}

/// <summary>What "Re-match seasons and episodes" would cover for a show and a chosen TheTVDB show (a read, so the switch can say how many).</summary>
public sealed class ShowChildRematchPreviewDto
{
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("series_id")]
    public string SeriesId { get; set; } = string.Empty;

    [JsonPropertyName("show_name")]
    public string ShowName { get; set; } = string.Empty;

    /// <summary>Episodes of the library show that have a season and episode number to match by.</summary>
    [JsonPropertyName("episode_count")]
    public int EpisodeCount { get; set; }

    /// <summary>Of those, how many the chosen show has an episode for.</summary>
    [JsonPropertyName("matchable_count")]
    public int MatchableCount { get; set; }

    /// <summary>Of those, how many the chosen show does not have (they go to Needs Review).</summary>
    [JsonPropertyName("not_found_count")]
    public int NotFoundCount { get; set; }
}

public static class EditSessionChildRematchTarget
{
    public static string Serialize(ShowChildRematchTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return JsonSerializer.Serialize(target);
    }

    public static bool TryParse(string? targetId, out ShowChildRematchTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return false;
        }

        try
        {
            target = JsonSerializer.Deserialize<ShowChildRematchTarget>(targetId);
        }
        catch (JsonException)
        {
            // Not JSON, or not a re-match target: the caller says the target could not be read.
            return false;
        }

        if (target is not null && target.SeriesId.Length is >= 1 and <= 12 && target.SeriesId.All(char.IsAsciiDigit))
        {
            return true;
        }

        target = null;
        return false;
    }
}
