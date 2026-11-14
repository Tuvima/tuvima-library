using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

public sealed class LibraryLaneGroupDisplaySettings
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("shelf_key")]
    public string? ShelfKey { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("see_all_route")]
    public string? SeeAllRoute { get; set; }

    [JsonPropertyName("minimum_series_items")]
    public int? MinimumSeriesItems { get; set; }
}

public sealed class LibraryPreferencesSettings
{
    [JsonPropertyName("view_modes")]
    public Dictionary<string, string> ViewModes { get; set; } = new()
    {
        ["movies"] = "all",
        ["tv"] = "shows",
        ["music"] = "artists",
        ["books"] = "all",
        ["audiobooks"] = "all",
        ["comics"] = "series",
    };

    [JsonPropertyName("lane_group_display")]
    public Dictionary<string, LibraryLaneGroupDisplaySettings> LaneGroupDisplay { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["watch"] = new()
        {
            Enabled = true,
            ShelfKey = "series",
            Title = "Series",
            Subtitle = "Movies dynamically aligned into series from your library metadata",
            SeeAllRoute = "/watch/movies?grouping=series",
            MinimumSeriesItems = 2,
        },
        ["read"] = new()
        {
            Enabled = true,
            ShelfKey = "series-and-reading-lists",
            Title = "Series & Reading Lists",
            Subtitle = "Book series, comic runs, and grouped reading",
            SeeAllRoute = "/read/books?grouping=series",
            MinimumSeriesItems = 2,
        },
    };
}
