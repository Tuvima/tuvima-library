using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Shared;

public static class AppMediaPresentation
{
    public static string LabelFor(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return "Media";
        }

        var value = mediaType.ToLowerInvariant();
        if (value.Contains("m4b") || value.Contains("audiobook"))
        {
            return "Audiobook";
        }

        if (value.Contains("book") || value.Contains("epub"))
        {
            return "Book";
        }

        if (value.Contains("movie"))
        {
            return "Movie";
        }

        if (value.Contains("tv"))
        {
            return "TV";
        }

        if (value.Contains("music") || value.Contains("album"))
        {
            return "Music";
        }

        if (value.Contains("audio"))
        {
            return "Audio";
        }

        if (value.Contains("comic") || value.Contains("cbz") || value.Contains("cbr"))
        {
            return "Comic";
        }

        if (value.Contains("video") || value.Contains("mkv") || value.Contains("mp4"))
        {
            return "Video";
        }

        return mediaType;
    }

    public static string IconFor(string? mediaType)
    {
        var label = LabelFor(mediaType);
        return label switch
        {
            "Audiobook" or "Audio" => AppMaterialIcons.Filled.Headphones,
            "Book" or "Comic" => AppMaterialIcons.Filled.MenuBook,
            "Movie" or "TV" or "Video" => AppMaterialIcons.Filled.Movie,
            "Music" => AppMaterialIcons.Filled.MusicNote,
            _ => AppMaterialIcons.Filled.Folder,
        };
    }

    public static string IconKeyFor(string? mediaType)
    {
        var label = LabelFor(mediaType);
        return label switch
        {
            "Audiobook" or "Audio" => AppIcons.Audiobook,
            "Book" => AppIcons.Book,
            "Comic" => AppIcons.Comic,
            "Movie" or "Video" => AppIcons.Movie,
            "TV" => AppIcons.Television,
            "Music" => AppIcons.Music,
            _ => AppIcons.Library,
        };
    }

    public static string AccentFor(string? mediaType)
    {
        var label = LabelFor(mediaType);
        return label switch
        {
            "Book" => "var(--tl-media-books)",
            "Audiobook" or "Audio" => "var(--tl-media-audiobooks)",
            "Movie" or "Video" => "var(--tl-media-movies)",
            "TV" => "var(--tl-media-tv)",
            "Music" => "var(--tl-media-music)",
            "Comic" => "var(--tl-media-comics)",
            _ => "var(--tl-media-unknown)",
        };
    }

    public static AppMediaCardVariant VariantFor(string? mediaType)
    {
        var label = LabelFor(mediaType);
        return label is "Music" or "Audiobook" or "Audio"
            ? AppMediaCardVariant.Square
            : AppMediaCardVariant.Portrait;
    }
}
