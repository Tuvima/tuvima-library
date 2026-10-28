using System.Text.Json;
using MediaEngine.Contracts.Matching;

namespace MediaEngine.Contracts.Editing;

/// <summary>
/// How an <see cref="EditSessionChangeKinds.Identity"/> change names the record the person chose. The change's
/// <see cref="EditSessionChange.TargetId"/> carries the very request the retail-match action takes
/// (<see cref="ReplaceRetailMatchRequestDto"/>) as JSON, so the Dashboard and the Engine agree on one shape and the Engine writes the
/// match through the same code the retail-match action uses.
/// </summary>
public static class EditSessionIdentityTarget
{
    /// <summary>Writes the chosen record into the text a staged identity change carries.</summary>
    public static string Serialize(ReplaceRetailMatchRequestDto match)
    {
        ArgumentNullException.ThrowIfNull(match);
        return JsonSerializer.Serialize(match);
    }

    /// <summary>Reads the chosen record back; false when the text is not one the Dashboard staged.</summary>
    public static bool TryParse(string? targetId, out ReplaceRetailMatchRequestDto? match)
    {
        match = null;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return false;
        }

        try
        {
            match = JsonSerializer.Deserialize<ReplaceRetailMatchRequestDto>(targetId);
        }
        catch (JsonException)
        {
            // Not JSON, or not a match: the caller says the record could not be read.
            return false;
        }

        return match is not null
            && !string.IsNullOrWhiteSpace(match.ProviderName)
            && !string.IsNullOrWhiteSpace(match.ProviderItemId);
    }
}
