using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Configuration for all media types — both built-in and user-defined custom types.
/// Loaded from <c>config/media_types.json</c>.
///
/// <para>
/// Each media type defines its display name, icon, supported file extensions,
/// and the category folder used during file organisation. Built-in types
/// cannot be deleted; custom types can be added, edited, and removed freely.
/// </para>
/// </summary>
public sealed class MediaTypeConfiguration
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("types")]
    public List<MediaTypeDefinition> Types { get; set; } = DefaultTypes();

    /// <summary>
    /// Returns the default set of built-in media types. This is the authoritative extension
    /// superset — video, comics, and audio bypass sites across the Engine (AssetPathService,
    /// taggers, plugin segment detectors, playback capability detection, etc.) each declared
    /// their own narrower or divergent hardcoded extension list; this catalog now covers the
    /// union of what any of them recognized, so callers migrated onto
    /// <see cref="Services.MediaTypeExtensionCatalog"/> instead of a private list won't lose
    /// recognition for a format another part of the Engine already handled.
    /// </summary>
    public static List<MediaTypeDefinition> DefaultTypes() =>
    [
        new() { Key = "books",      DisplayName = "Books",      Icon = "MenuBook",    Extensions = [".epub", ".pdf", ".azw3"],                    CategoryFolder = "Books",    BuiltIn = true },
        new() { Key = "audiobooks", DisplayName = "Audiobooks", Icon = "Headphones",  Extensions = [".m4b", ".mp3", ".m4a"],                       CategoryFolder = "Audiobooks", BuiltIn = true },
        new() { Key = "comics",     DisplayName = "Comics",     Icon = "AutoStories", Extensions = [".cbz", ".cbr", ".cb7"],                       CategoryFolder = "Comics",   BuiltIn = true },
        new() { Key = "movies",     DisplayName = "Movies",     Icon = "Movie",       Extensions = [".mp4", ".mkv", ".webm", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".mpeg", ".mpg", ".m2ts", ".iso"], CategoryFolder = "Videos", BuiltIn = true },
        new() { Key = "tv_shows",   DisplayName = "TV Shows",   Icon = "Tv",          Extensions = [".mp4", ".mkv", ".webm", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".mpeg", ".mpg", ".m2ts"], CategoryFolder = "TV Shows", BuiltIn = true },
        new() { Key = "music",      DisplayName = "Music",      Icon = "MusicNote",   Extensions = [".mp3", ".flac", ".ogg", ".m4a", ".aac", ".wav", ".opus", ".wma"], CategoryFolder = "Music", BuiltIn = true },
        new() { Key = "videos",     DisplayName = "Videos",     Icon = "VideoLibrary", Extensions = [".mp4", ".mkv", ".webm", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".mpeg", ".mpg", ".m2ts"], CategoryFolder = "Videos", BuiltIn = true },
    ];
}

/// <summary>
/// A single media type definition — either built-in or user-created.
///
/// <para>
/// The <see cref="Key"/> is the stable identifier used internally.
/// The <see cref="DisplayName"/> is the user-visible label shown in tabs
/// and used as the key in provider slot assignments.
/// </para>
/// </summary>
public sealed class MediaTypeDefinition
{
    /// <summary>Stable internal identifier (e.g. "books", "tv_shows", "personal_docs").</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>User-visible label shown in media type tabs (e.g. "Books", "TV Shows").</summary>
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Icon identifier — either a Material Design icon name (e.g. "MenuBook")
    /// or a path to a custom uploaded icon file (e.g. "config/icons/custom.svg").
    /// </summary>
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "Description";

    /// <summary>File extensions handled by this media type (e.g. [".epub", ".pdf"]).</summary>
    [JsonPropertyName("extensions")]
    public List<string> Extensions { get; set; } = [];

    /// <summary>Folder name used during file organisation (e.g. "Books", "Videos").</summary>
    [JsonPropertyName("category_folder")]
    public string CategoryFolder { get; set; } = "Other";

    /// <summary>
    /// Whether this is a built-in media type. Built-in types cannot be deleted
    /// and have their display name locked.
    /// </summary>
    [JsonPropertyName("built_in")]
    public bool BuiltIn { get; set; }
}
