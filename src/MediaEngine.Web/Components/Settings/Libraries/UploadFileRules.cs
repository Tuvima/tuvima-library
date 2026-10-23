namespace MediaEngine.Web.Components.Settings;

/// <summary>What a file looks like it is, judged only by its extension and the type the browser reports (the Engine checks the real file later).</summary>
public enum UploadFileClass
{
    Unknown,
    Video,
    Audio,
    Book,
    Comic,
    Image,
}

/// <summary>
/// The one small mapping from a file's type to the kinds it fits (Library setup D44): video goes to Watch kinds, audio to Listen kinds,
/// epub and pdf to Read's books, cbz to Read's comics, and pictures to View Photos (which has its own upload, so they are not sent here).
/// </summary>
public static class UploadFileRules
{
    private static readonly Dictionary<string, UploadFileClass> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mkv"] = UploadFileClass.Video,
        [".mp4"] = UploadFileClass.Video,
        [".m4v"] = UploadFileClass.Video,
        [".avi"] = UploadFileClass.Video,
        [".mov"] = UploadFileClass.Video,
        [".wmv"] = UploadFileClass.Video,
        [".webm"] = UploadFileClass.Video,
        [".ts"] = UploadFileClass.Video,
        [".m2ts"] = UploadFileClass.Video,
        [".mpg"] = UploadFileClass.Video,
        [".mpeg"] = UploadFileClass.Video,
        [".flv"] = UploadFileClass.Video,
        [".ogv"] = UploadFileClass.Video,
        [".mp3"] = UploadFileClass.Audio,
        [".flac"] = UploadFileClass.Audio,
        [".m4a"] = UploadFileClass.Audio,
        [".m4b"] = UploadFileClass.Audio,
        [".aac"] = UploadFileClass.Audio,
        [".ogg"] = UploadFileClass.Audio,
        [".opus"] = UploadFileClass.Audio,
        [".wav"] = UploadFileClass.Audio,
        [".wma"] = UploadFileClass.Audio,
        [".aiff"] = UploadFileClass.Audio,
        [".aif"] = UploadFileClass.Audio,
        [".epub"] = UploadFileClass.Book,
        [".pdf"] = UploadFileClass.Book,
        [".cbz"] = UploadFileClass.Comic,
        [".jpg"] = UploadFileClass.Image,
        [".jpeg"] = UploadFileClass.Image,
        [".png"] = UploadFileClass.Image,
        [".gif"] = UploadFileClass.Image,
        [".webp"] = UploadFileClass.Image,
        [".heic"] = UploadFileClass.Image,
        [".heif"] = UploadFileClass.Image,
        [".avif"] = UploadFileClass.Image,
        [".bmp"] = UploadFileClass.Image,
        [".tif"] = UploadFileClass.Image,
        [".tiff"] = UploadFileClass.Image,
    };

    // The kind to suggest first for each class, when the page does not already say.
    private static readonly Dictionary<UploadFileClass, string> LikelyKind = new()
    {
        [UploadFileClass.Video] = "movies",
        [UploadFileClass.Audio] = "music",
        [UploadFileClass.Book] = "books",
        [UploadFileClass.Comic] = "comics",
    };

    /// <summary>What the file looks like: its extension decides, and the browser's type is used only when the extension says nothing.</summary>
    public static UploadFileClass Classify(string? fileName, string? mimeType)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (!string.IsNullOrEmpty(extension) && ByExtension.TryGetValue(extension, out var known))
        {
            return known;
        }

        var mime = (mimeType ?? string.Empty).Trim().ToLowerInvariant();
        if (mime.StartsWith("video/", StringComparison.Ordinal))
        {
            return UploadFileClass.Video;
        }

        if (mime.StartsWith("audio/", StringComparison.Ordinal))
        {
            return UploadFileClass.Audio;
        }

        if (mime.StartsWith("image/", StringComparison.Ordinal))
        {
            return UploadFileClass.Image;
        }

        return mime switch
        {
            "application/epub+zip" or "application/pdf" => UploadFileClass.Book,
            "application/vnd.comicbook+zip" or "application/x-cbz" => UploadFileClass.Comic,
            _ => UploadFileClass.Unknown,
        };
    }

    /// <summary>The group (area) whose kinds take this class, or null when no kind does.</summary>
    public static string? AreaOf(UploadFileClass fileClass) => fileClass switch
    {
        UploadFileClass.Video => "watch",
        UploadFileClass.Audio => "listen",
        UploadFileClass.Book or UploadFileClass.Comic => "read",
        UploadFileClass.Image => LibraryTypeCatalog.ViewArea,
        _ => null,
    };

    /// <summary>The kinds, in the list's order, that take this class. Pictures and unknown files fit none (View has its own upload).</summary>
    public static IReadOnlyList<LibraryTypeOption> FittingKinds(LibraryKindCatalog kinds, UploadFileClass fileClass)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (AreaOf(fileClass) is not { } area || area == LibraryTypeCatalog.ViewArea)
        {
            return [];
        }

        var inArea = kinds.Types.Where(kind => string.Equals(kind.Area, area, StringComparison.OrdinalIgnoreCase));
        return fileClass switch
        {
            // Books and comics are both Read, but a comic is not a book: the kind's format says which it takes.
            UploadFileClass.Book => [.. inArea.Where(kind => kind.MediaTypes.Contains("Books", StringComparer.OrdinalIgnoreCase))],
            UploadFileClass.Comic => [.. inArea.Where(kind => kind.MediaTypes.Contains("Comics", StringComparer.OrdinalIgnoreCase))],
            _ => [.. inArea],
        };
    }

    /// <summary>True when a page for <paramref name="pageArea"/> takes this class (and, on a type page, that kind itself fits).</summary>
    public static bool Fits(LibraryKindCatalog kinds, UploadFileClass fileClass, string pageArea, string? pageKindKey) =>
        FittingKinds(kinds, fileClass) is { Count: > 0 } fitting
        && string.Equals(AreaOf(fileClass), pageArea, StringComparison.OrdinalIgnoreCase)
        && (pageKindKey is null || fitting.Any(kind => string.Equals(kind.Key, pageKindKey, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The kind to suggest first among <paramref name="fitting"/>: the usual one for the class, else the first.</summary>
    public static LibraryTypeOption? MostLikely(UploadFileClass fileClass, IReadOnlyList<LibraryTypeOption> fitting) =>
        LikelyKind.TryGetValue(fileClass, out var key)
            ? fitting.FirstOrDefault(kind => string.Equals(kind.Key, key, StringComparison.OrdinalIgnoreCase)) ?? fitting.FirstOrDefault()
            : fitting.FirstOrDefault();

    /// <summary>"videos", "audio", "books" and so on: what a dropped class reads as in a sentence.</summary>
    public static string Noun(UploadFileClass fileClass) => fileClass switch
    {
        UploadFileClass.Video => "videos",
        UploadFileClass.Audio => "audio",
        UploadFileClass.Book => "books",
        UploadFileClass.Comic => "comics",
        UploadFileClass.Image => "photos",
        _ => "files",
    };

    /// <summary>What a page for this group is about, in a sentence ("videos" for Watch).</summary>
    public static string AreaNoun(string? area) => area?.ToLowerInvariant() switch
    {
        "watch" => "videos",
        "listen" => "audio",
        "read" => "books",
        _ => "this page",
    };

    /// <summary>A kind's or group's name as it reads in the middle of a sentence ("Music" is "music", "TV shows" stays as it is).</summary>
    public static string InSentence(string label) =>
        label.Length > 1 && char.IsUpper(label[0]) && !char.IsUpper(label[1]) ? char.ToLowerInvariant(label[0]) + label[1..] : label;
}

/// <summary>What to tell the person when a start is refused: one plain message per reason, and a link when there is a fix to go to.</summary>
public static class UploadRefusalMessages
{
    public const string SetImportFolder = "Set an Import folder first.";
    public const string VaultNotAvailable = "Adding files to the Vault by drag and drop isn't available yet.";
    public const string Unreachable = "The Library could not be reached. Try again in a moment.";
    public const string Fallback = "This file could not be added.";
    public const string ImportAddress = "/settings/libraries/import";

    /// <summary>The words to show, the link address (or null) and the link's words.</summary>
    public static (string Text, string? Href, string? LinkText) Describe(string? code, string? serverMessage, string? kindAddress)
    {
        var message = string.IsNullOrWhiteSpace(serverMessage) ? Fallback : serverMessage.Trim();
        return code switch
        {
            "no_import_folder" => (SetImportFolder, ImportAddress, "Set the Import folder"),
            "destination_unavailable" => (message, ImportAddress, "Check the Import folder"),
            "no_managed_destination" => (message, kindAddress, kindAddress is null ? null : "Set up a folder"),
            "vault_not_available" => (VaultNotAvailable, null, null),
            "engine_unreachable" => (Unreachable, null, null),
            _ => (message, null, null),
        };
    }
}
