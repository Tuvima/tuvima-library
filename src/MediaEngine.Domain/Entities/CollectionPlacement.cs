namespace MediaEngine.Domain.Entities;

/// <summary>
/// Maps a Collection to a UI location with display constraints.
/// A collection can appear in multiple locations (home, media lane, detail, collections) with different limits.
/// Maps to <c>collection_placements</c> table.
/// </summary>
public sealed class CollectionPlacement
{
    public Guid Id { get; set; }
    public Guid CollectionId { get; set; }

    /// <summary>UI location: "home", "media_lane:Movie", "collections", "my_library", etc.</summary>
    public string Location { get; set; } = "";

    /// <summary>Sort order within the location (lower = higher on page).</summary>
    public int Position { get; set; }

    /// <summary>Max items to display at this location (0 = no limit).</summary>
    public int DisplayLimit { get; set; }

    /// <summary>How to render: "swimlane", "grid", "list", "accordion", "container_table".</summary>
    public string DisplayMode { get; set; } = "swimlane";

    /// <summary>User can hide a placement without deleting it.</summary>
    public bool IsVisible { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Null for the shared "Show on" choice every profile sees. Set for one profile's own choice about the same collection
    /// and location: an invisible row is that profile's "Hide this lane".
    /// </summary>
    public Guid? ProfileId { get; set; }
}
