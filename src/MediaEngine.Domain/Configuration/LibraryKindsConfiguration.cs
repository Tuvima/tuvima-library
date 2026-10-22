using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// The one list of library kinds ("recipes"): every type of library the Library can hold, loaded from
/// <c>config/library_kinds.json</c>. Each screen that offers a type (the front door, group pages, Add folder, the import
/// folder) is drawn from this list, and a declarative plugin can add kinds to it (see <c>docs/guides/building-a-plugin.md</c>).
/// </summary>
public sealed class LibraryKindsConfiguration
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("kinds")]
    public List<LibraryKindDefinition> Kinds { get; set; } = BuiltIn();

    /// <summary>
    /// The kinds that ship with the Library, in the order they are listed. This is the same list as the shipped
    /// <c>config/library_kinds.json</c>; it stands in when that file is missing and is what the import folder is drawn from.
    /// </summary>
    public static List<LibraryKindDefinition> BuiltIn() =>
    [
        Kind("movies", LibraryAreas.Watch, "Movies", "Movie", MediaType.Movies, ["tmdb", "subdl"], LibraryKindGroupings.Movie, "Movies",
            LibraryKindMisfits.Unmatched, "Feature films, one file or folder per film.", 1),
        Kind("tv", LibraryAreas.Watch, "TV Shows", "Tv", MediaType.TV, ["tvdb", "subdl"], LibraryKindGroupings.TvShowSeason, "TV",
            LibraryKindMisfits.Unmatched, "Series with seasons and episodes, one folder per show.", 2),
        Kind("youtube", LibraryAreas.Watch, "YouTube", "PlayCircle", MediaType.Videos, ["youtube"], LibraryKindGroupings.VideoSeriesFolder, "Videos",
            LibraryKindMisfits.Unmatched, "Videos saved from YouTube, found by the video ID in the file name.", 3, available: false),
        Kind("other_videos", LibraryAreas.Watch, "Videos", "VideoLibrary", MediaType.Videos, null, LibraryKindGroupings.VideoSeriesFolder, "Videos",
            LibraryKindMisfits.Keep, "Courses, talks and downloads. YouTube lookup can be switched on in the last step.", 4),
        Kind("music", LibraryAreas.Listen, "Music", "MusicNote", MediaType.Music, ["musicbrainz", "apple_api", "lrclib"], LibraryKindGroupings.MusicAlbum, "Music",
            LibraryKindMisfits.Unmatched, "Albums and songs, tagged or in artist and album folders.", 1),
        Kind("audiobooks", LibraryAreas.Listen, "Audiobooks", "Headphones", MediaType.Audiobooks, ["apple_api"], LibraryKindGroupings.Audiobook, "Audiobooks",
            LibraryKindMisfits.Unmatched, "Spoken books, one file or folder per book.", 2),
        Kind("other_audio", LibraryAreas.Listen, "Other audio", "AudioFile", MediaType.Music, null, LibraryKindGroupings.FolderAndTags, "default",
            LibraryKindMisfits.Keep, "Random audio files, recordings and lectures, shown by folder and the tags inside the files.", 3, available: false),
        Kind("books", LibraryAreas.Read, "Books", "MenuBook", MediaType.Books, ["apple_api"], LibraryKindGroupings.BookSeries, "Books",
            LibraryKindMisfits.Unmatched, "EPUB and PDF books.", 1),
        Kind("comics", LibraryAreas.Read, "Comics", "AutoStories", MediaType.Comics, ["comicvine"], LibraryKindGroupings.ComicSeries, "Comics",
            LibraryKindMisfits.Unmatched, "CBZ and CBR comics and manga.", 2),
        Kind("other_reading", LibraryAreas.Read, "Other reading", "Description", MediaType.Books, null, LibraryKindGroupings.FolderAndTags, "default",
            LibraryKindMisfits.Keep, "Loose books and documents with no match, shown by folder and the details inside the files.", 3, available: false),
        new LibraryKindDefinition
        {
            Key = "photos",
            Area = LibraryAreas.View,
            Label = "Photos & personal videos",
            Icon = "PhotoLibrary",
            Format = null,
            Lookup = LibraryKindLookup.None,
            Grouping = LibraryKindGroupings.ViewGallery,
            NamingTemplate = null,
            Misfit = LibraryKindMisfits.Keep,
            DefaultHandling = LibraryKindHandling.ReadOnly,
            Tooltip = "Photos and phone or camera videos. View sorts them by date and place.",
            Order = 1,
            BuiltIn = true,
            Available = true,
        },
    ];

    private static LibraryKindDefinition Kind(
        string key,
        string area,
        string label,
        string icon,
        MediaType format,
        string[]? providers,
        string grouping,
        string namingTemplate,
        string misfit,
        string tooltip,
        int order,
        bool available = true) => new()
        {
            Key = key,
            Area = area,
            Label = label,
            Icon = icon,
            Format = format.ToString(),
            Lookup = providers is null ? LibraryKindLookup.None : new LibraryKindLookup { Providers = [.. providers] },
            Grouping = grouping,
            NamingTemplate = namingTemplate,
            Misfit = misfit,
            DefaultHandling = LibraryKindHandling.ReadOnly,
            Tooltip = tooltip,
            Order = order,
            BuiltIn = true,
            Available = available,
        };
}

/// <summary>
/// One library kind: its area, how it is named and drawn, where its details come from, how its files are grouped and
/// filed, what happens to files that do not fit, and how a new folder of this kind is handled by default. Everything here is
/// data; a kind that needs a new way of grouping files still needs a Library release.
/// </summary>
public sealed class LibraryKindDefinition
{
    /// <summary>Stable identifier in lower_snake case (for example <c>movies</c>, <c>other_videos</c>). Also the address of the kind's page.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>The area the kind sits under: watch, listen, read, view or vault.</summary>
    [JsonPropertyName("area")]
    public string Area { get; set; } = string.Empty;

    /// <summary>What a person sees (for example "TV Shows").</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>An <c>AppMaterialIcons</c> name the Dashboard draws (for example <c>MenuBook</c>).</summary>
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "Folder";

    /// <summary>The Engine media type the kind's files are filed under (Movies, TV, Videos, Music, Audiobooks, Books or Comics); null for View kinds.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>Where details come from: named providers, or none. Recorded for display; it does not change which provider is used.</summary>
    [JsonPropertyName("lookup")]
    public LibraryKindLookup Lookup { get; set; } = LibraryKindLookup.None;

    /// <summary>The built-in rule that groups files into works, series and albums (see <see cref="LibraryKindGroupings"/>).</summary>
    [JsonPropertyName("grouping")]
    public string Grouping { get; set; } = LibraryKindGroupings.FolderAndTags;

    /// <summary>The <c>organization_templates</c> key in <c>core.json</c> that names the kind's files and folders; null for View kinds.</summary>
    [JsonPropertyName("naming_template")]
    public string? NamingTemplate { get; set; }

    /// <summary>What happens to a file that does not fit: <c>unmatched</c> (it goes to Needs Review) or <c>keep</c> (it stays as it is).</summary>
    [JsonPropertyName("misfit")]
    public string Misfit { get; set; } = LibraryKindMisfits.Keep;

    /// <summary>The default for a new folder of this kind: <c>read_only</c> or <c>managed</c>.</summary>
    [JsonPropertyName("default_handling")]
    public string DefaultHandling { get; set; } = LibraryKindHandling.ReadOnly;

    /// <summary>The (i) text beside the kind in Add folder.</summary>
    [JsonPropertyName("tooltip")]
    public string Tooltip { get; set; } = string.Empty;

    /// <summary>Position among the kinds of its area.</summary>
    [JsonPropertyName("order")]
    public int Order { get; set; }

    /// <summary>True for the kinds that ship with the Library. Plugin kinds are never built in.</summary>
    [JsonPropertyName("built_in")]
    public bool BuiltIn { get; set; }

    /// <summary>False while a kind is defined but not offered on any screen.</summary>
    [JsonPropertyName("available")]
    public bool Available { get; set; } = true;

    [JsonIgnore]
    public MediaType FormatMediaType => Services.MediaTypeParser.Parse(Format);
}

/// <summary>Where a kind's details come from: the named providers, or <see cref="None"/> (files by folder and file name only).</summary>
[JsonConverter(typeof(LibraryKindLookupConverter))]
public sealed class LibraryKindLookup
{
    public static LibraryKindLookup None { get; } = new();

    public IReadOnlyList<string> Providers { get; init; } = [];

    public bool IsNone => Providers.Count == 0;
}

/// <summary>Writes <c>"none"</c> or <c>{ "providers": [...] }</c>.</summary>
public sealed class LibraryKindLookupConverter : JsonConverter<LibraryKindLookup>
{
    public override LibraryKindLookup Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return string.Equals(reader.GetString(), "none", StringComparison.OrdinalIgnoreCase)
                ? LibraryKindLookup.None
                : throw new JsonException("lookup must be \"none\" or an object with a providers list.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("lookup must be \"none\" or an object with a providers list.");
        }

        var providers = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("lookup must be \"none\" or an object with a providers list.");
            }

            var name = reader.GetString();
            reader.Read();
            if (!string.Equals(name, "providers", StringComparison.Ordinal))
            {
                throw new JsonException($"lookup has an unknown property \"{name}\"; only providers is allowed.");
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("lookup.providers must be a list of provider names.");
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String || string.IsNullOrWhiteSpace(reader.GetString()))
                {
                    throw new JsonException("lookup.providers must be a list of provider names.");
                }

                providers.Add(reader.GetString()!.Trim());
            }
        }

        return providers.Count == 0 ? LibraryKindLookup.None : new LibraryKindLookup { Providers = providers };
    }

    public override void Write(Utf8JsonWriter writer, LibraryKindLookup value, JsonSerializerOptions options)
    {
        if (value.IsNone)
        {
            writer.WriteStringValue("none");
            return;
        }

        writer.WriteStartObject();
        writer.WriteStartArray("providers");
        foreach (var provider in value.Providers)
        {
            writer.WriteStringValue(provider);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

/// <summary>The named grouping rules that already exist in code. A kind picks one; it cannot define a new one.</summary>
public static class LibraryKindGroupings
{
    public const string Movie = "movie";
    public const string TvShowSeason = "tv_show_season";
    public const string MusicAlbum = "music_album";
    public const string Audiobook = "audiobook";
    public const string BookSeries = "book_series";
    public const string ComicSeries = "comic_series";
    public const string VideoSeriesFolder = "video_series_folder";
    public const string FolderAndTags = "folder_and_tags";
    public const string ViewGallery = "view_gallery";

    public static IReadOnlyList<string> All { get; } =
        [Movie, TvShowSeason, MusicAlbum, Audiobook, BookSeries, ComicSeries, VideoSeriesFolder, FolderAndTags, ViewGallery];

    public static bool IsValid(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>What happens to a file that does not fit its kind.</summary>
public static class LibraryKindMisfits
{
    public const string Unmatched = "unmatched";
    public const string Keep = "keep";
    public static bool IsValid(string? value) => value is Unmatched or Keep;
}

/// <summary>How a new folder of a kind is handled by default.</summary>
public static class LibraryKindHandling
{
    public const string ReadOnly = "read_only";
    public const string Managed = "managed";
    public static bool IsValid(string? value) => value is ReadOnly or Managed;
}

/// <summary>The Engine media types a kind can file under.</summary>
public static class LibraryKindFormats
{
    public static IReadOnlyList<string> All { get; } = [nameof(MediaType.Movies), nameof(MediaType.TV), nameof(MediaType.Videos), nameof(MediaType.Music), nameof(MediaType.Audiobooks), nameof(MediaType.Books), nameof(MediaType.Comics)];

    public static bool IsValid(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}
