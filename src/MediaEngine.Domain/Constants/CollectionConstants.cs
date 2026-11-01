namespace MediaEngine.Domain.Constants;

/// <summary>
/// Stable string values stored in collection-related SQLite TEXT columns.
/// </summary>
public static class CollectionTypeNames
{
    public const string Universe = "Universe";
    public const string Series = "Series";
    public const string ContentGroup = "ContentGroup";
    public const string System = "System";
    public const string Smart = "Smart";
    public const string Playlist = "Playlist";
    public const string PlaylistFolder = "PlaylistFolder";
    public const string Custom = "Custom";
    public const string Set = "Set";
}

public static class CollectionResolutionNames
{
    public const string Query = "query";
    public const string Materialized = "materialized";
}

public static class CollectionScopeNames
{
    public const string Library = "library";
    public const string User = "user";
}

public static class CollectionUniverseStatusNames
{
    public const string Unknown = "Unknown";
}
