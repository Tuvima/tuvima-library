using System.Globalization;
using System.Text.Json;

namespace MediaEngine.Api.Services.Playback;

/// <summary>Reads saved player timing, including the string values persisted by UserStateRepository.</summary>
internal static class SavedPlaybackTiming
{
    internal static (double? PositionSeconds, double? DurationSeconds) Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return (null, null);
            return (ReadNumber(document.RootElement, "position_seconds", allowZero: true),
                ReadNumber(document.RootElement, "duration_seconds", allowZero: false));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static double? ReadNumber(JsonElement root, string key, bool allowZero)
    {
        if (!root.TryGetProperty(key, out var value)) return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
        var valid = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed);
        return valid && double.IsFinite(parsed) && (allowZero ? parsed >= 0 : parsed > 0) ? parsed : null;
    }

}
