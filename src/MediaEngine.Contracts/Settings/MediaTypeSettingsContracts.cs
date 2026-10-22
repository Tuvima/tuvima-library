using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

public sealed class MediaTypeConfigurationDto
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("types")]
    public List<MediaTypeDefinitionDto> Types { get; set; } = Defaults();

    public static List<MediaTypeDefinitionDto> Defaults() =>
    [
        new() { Key = "books", DisplayName = "Books", Icon = "MenuBook", Extensions = [".epub", ".pdf"], BuiltIn = true },
        new() { Key = "audiobooks", DisplayName = "Audiobooks", Icon = "Headphones", Extensions = [".m4b", ".mp3", ".m4a"], BuiltIn = true },
        new() { Key = "comics", DisplayName = "Comics", Icon = "AutoStories", Extensions = [".cbz", ".cbr", ".cb7"], BuiltIn = true },
        new() { Key = "movies", DisplayName = "Movies", Icon = "Movie", Extensions = [".mp4", ".mkv", ".webm", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".mpeg", ".mpg", ".m2ts"], BuiltIn = true },
        new() { Key = "tv_shows", DisplayName = "TV Shows", Icon = "Tv", Extensions = [".mp4", ".mkv", ".webm", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".mpeg", ".mpg", ".m2ts"], BuiltIn = true },
        new() { Key = "music", DisplayName = "Music", Icon = "MusicNote", Extensions = [".mp3", ".flac", ".ogg", ".m4a", ".aac", ".wav", ".opus", ".wma"], BuiltIn = true },
    ];
}

public sealed class MediaTypeDefinitionDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "Description";

    [JsonPropertyName("extensions")]
    public List<string> Extensions { get; set; } = [];

    [JsonPropertyName("built_in")]
    public bool BuiltIn { get; set; }
}
