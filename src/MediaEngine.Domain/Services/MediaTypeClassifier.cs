using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Services;

/// <summary>
/// Single source of truth for classifying format/type strings into <see cref="MediaType"/> values.
/// All UI and engine code should use this classifier instead of local switch statements.
/// </summary>
public static class MediaTypeClassifier
{
    /// <summary>
    /// Normalises a format string, media type name, or file extension hint
    /// into a <see cref="MediaType"/> enum value.
    /// </summary>
    public static MediaType Classify(string? formatOrType)
    {
        if (string.IsNullOrWhiteSpace(formatOrType))
        {
            return MediaType.Unknown;
        }

        var t = formatOrType.Trim().ToLowerInvariant();

        // Exact enum name matches
        if (t is "books" or "book" or "epub")
        {
            return MediaType.Books;
        }

        if (t is "audiobook" or "audiobooks" or "m4b")
        {
            return MediaType.Audiobooks;
        }

        if (t is "movies" or "movie")
        {
            return MediaType.Movies;
        }

        if (t is "tv")
        {
            return MediaType.TV;
        }

        if (t is "music")
        {
            return MediaType.Music;
        }

        if (t is "comics" or "comic")
        {
            return MediaType.Comics;
        }

        // Extension / format substring matches
        if (t.Contains("epub"))
        {
            return MediaType.Books;
        }

        if (t.Contains("audiobook"))
        {
            return MediaType.Audiobooks;
        }

        if (t.Contains("m4b"))
        {
            return MediaType.Audiobooks;
        }

        if (t.Contains("video") || t.Contains("mkv") || t.Contains("mp4") || t.Contains("avi") || t.Contains("webm"))
        {
            return MediaType.Movies;
        }

        if (t.Contains("comic") || t.Contains("cbz") || t.Contains("cbr"))
        {
            return MediaType.Comics;
        }

        if (t.Contains("book"))
        {
            return MediaType.Books;
        }

        if (t.Contains("mp3") || t.Contains("flac") || t.Contains("aac") || t.Contains("m4a") || t.Contains("ogg") || t.Contains("wav"))
        {
            return MediaType.Music;
        }

        if (t.Contains("audio"))
        {
            return MediaType.Music;
        }

        return MediaType.Unknown;
    }

    /// <summary>Returns the user-facing display label for a media type.</summary>
    public static string GetDisplayLabel(MediaType type) => type switch
    {
        MediaType.Books => "Book",
        MediaType.Audiobooks => "Audiobook",
        MediaType.Movies => "Movie",
        MediaType.TV => "TV",
        MediaType.Music => "Music",
        MediaType.Comics => "Comic",
        _ => "Unknown",
    };

    /// <summary>Returns the user-facing display label from a raw format/type string.</summary>
    public static string GetDisplayLabel(string? formatOrType) =>
        GetDisplayLabel(Classify(formatOrType));

    /// <summary>
    /// Returns a Material Design icon name string for a media type.
    /// These are plain icon NAME strings since the Domain layer
    /// does not reference any UI library.
    /// </summary>
    public static string GetIconName(MediaType type) => type switch
    {
        MediaType.Books => "MenuBook",
        MediaType.Audiobooks => "Headphones",
        MediaType.Movies => "Movie",
        MediaType.TV => "Tv",
        MediaType.Music => "MusicNote",
        MediaType.Comics => "AutoStories",
        _ => "InsertDriveFile",
    };
}
