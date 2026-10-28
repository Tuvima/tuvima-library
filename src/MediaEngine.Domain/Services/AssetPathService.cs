using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Services;

/// <summary>
/// Central policy-driven resolver for managed asset storage.
/// </summary>
public sealed class AssetPathService
{
    private readonly LibraryStoragePolicy _policy;

    public AssetPathService(
        string libraryRoot,
        LibraryStoragePolicy? policy = null,
        string? dataRoot = null)
    {
        LibraryRoot = string.IsNullOrWhiteSpace(libraryRoot)
            ? Path.Combine(Path.GetTempPath(), "tuvima-library-unset")
            : Path.GetFullPath(libraryRoot);

        DataRoot = string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(LibraryRoot, ".data")
            : Path.GetFullPath(dataRoot);

        _policy = policy ?? new LibraryStoragePolicy();
    }

    public string LibraryRoot { get; }

    public LibraryStoragePolicy Policy => _policy;

    public string DataRoot { get; }

    public string AssetsRoot => Path.Combine(DataRoot, "assets");

    public string ArtworkRoot => Path.Combine(AssetsRoot, "artwork");

    public string DerivedRoot => Path.Combine(AssetsRoot, "derived");

    public string MetadataRoot => Path.Combine(AssetsRoot, "metadata");

    public string TranscriptsRoot => Path.Combine(AssetsRoot, "transcripts");

    public string SubtitleCacheRoot => Path.Combine(AssetsRoot, "subtitle-cache");

    public string TextTracksRoot => Path.Combine(AssetsRoot, "text-tracks");

    public string PeopleRoot => Path.Combine(AssetsRoot, "people");

    public bool IsHybridMode => _policy.Mode == StorageMode.Hybrid;

    public bool IsArtworkCentralized => _policy.Mode != StorageMode.CoLocated;

    public bool ShouldExportArtwork => _policy.Mode == StorageMode.CoLocated || _policy.ArtworkExport || _policy.ExportProfile.Artwork;

    public bool ShouldKeepPreferredSubtitlesLocal => _policy.Mode != StorageMode.Centralized || _policy.SubtitleExport || _policy.ExportProfile.PreferredSubtitles;

    public bool ShouldExportMetadataSidecars => _policy.MetadataSidecarExport || _policy.ExportProfile.MetadataSidecars;

    public string GetCentralAssetPath(
        string ownerKind,
        Guid ownerId,
        string assetType,
        Guid variantId,
        string extension) =>
        GetCentralAssetPath(ownerKind, ownerId.ToString("D"), assetType, variantId, extension);

    public string GetCentralAssetPath(
        string ownerKind,
        string ownerKey,
        string assetType,
        Guid variantId,
        string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetType);
        if (variantId == Guid.Empty)
        {
            throw new ArgumentException("Variant id is required.", nameof(variantId));
        }

        return Path.Combine(
            ArtworkRoot,
            NormalizeSegment(ownerKind),
            NormalizeSegment(ownerKey),
            NormalizeAssetTypeSegment(assetType),
            $"{variantId:N}{NormalizeExtension(extension)}");
    }

    public string GetCentralDerivedPath(
        string ownerKind,
        Guid ownerId,
        string artifactType,
        string fileName) =>
        GetCentralDerivedPath(ownerKind, ownerId.ToString("D"), artifactType, fileName);

    public string GetCentralDerivedPath(
        string ownerKind,
        string ownerKey,
        string artifactType,
        string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactType);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        return Path.Combine(
            DerivedRoot,
            NormalizeSegment(ownerKind),
            NormalizeSegment(ownerKey),
            NormalizeSegment(artifactType),
            fileName);
    }

    public string GetPersonRoot(Guid personId)
    {
        if (personId == Guid.Empty)
        {
            throw new ArgumentException("Person id is required.", nameof(personId));
        }

        return Path.Combine(PeopleRoot, personId.ToString("D"));
    }

    public string GetPersonHeadshotPath(Guid personId, string extension = ".jpg") =>
        Path.Combine(GetPersonRoot(personId), "headshot" + NormalizeExtension(extension));

    public string GetCharacterPortraitPath(Guid personId, Guid fictionalEntityId, string extension = ".jpg")
    {
        if (fictionalEntityId == Guid.Empty)
        {
            throw new ArgumentException("Fictional entity id is required.", nameof(fictionalEntityId));
        }

        return Path.Combine(
            GetPersonRoot(personId),
            "characters",
            fictionalEntityId.ToString("D"),
            "portrait" + NormalizeExtension(extension));
    }

    public string GetLocalSidecarPath(string mediaFilePath, string assetType, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFilePath);
        return assetType switch
        {
            "Subtitle" => BuildSubtitleSidecarPath(mediaFilePath, extension),
            "Lyrics" => BuildLyricsSidecarPath(mediaFilePath, extension),
            _ => BuildArtworkExportPath(mediaFilePath, assetType, extension),
        };
    }

    public string GetCentralTextTrackPath(Guid assetId, string kind, string provider, string language, string extension)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("Asset id is required.", nameof(assetId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        var root = kind.Equals("Lyrics", StringComparison.OrdinalIgnoreCase)
            ? "lyrics"
            : "subtitles";

        return Path.Combine(
            TextTracksRoot,
            root,
            assetId.ToString("D"),
            $"{NormalizeSegment(provider)}-{NormalizeSegment(language)}{NormalizeExtension(extension)}");
    }

    public string GetExportedSidecarPath(string ownerPath, string assetType, string extension, Guid? variantId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPath);
        if (Directory.Exists(ownerPath))
        {
            return BuildFolderArtworkExportPath(ownerPath, assetType, extension, variantId);
        }

        return BuildArtworkExportPath(ownerPath, assetType, extension, variantId);
    }

    public static void EnsureDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string BuildArtworkExportPath(string mediaFilePath, string assetType, string extension, Guid? variantId = null)
    {
        var normalizedExtension = NormalizeExtension(extension);

        if (variantId.HasValue && variantId.Value != Guid.Empty)
        {
            return GetMediaFileArtworkVariantPath(mediaFilePath, assetType, variantId.Value, normalizedExtension);
        }

        return assetType switch
        {
            "CoverArt" => GetMediaFilePosterPath(mediaFilePath),
            "Background" => GetMediaFileFanartPath(mediaFilePath),
            "Logo" => GetMediaFileLogoPath(mediaFilePath),
            "EpisodeStill" => GetMediaFileThumbPath(mediaFilePath),
            _ => throw new ArgumentOutOfRangeException(nameof(assetType), assetType, "Unsupported export artwork type."),
        };
    }

    private static string BuildFolderArtworkExportPath(string folderPath, string assetType, string extension, Guid? variantId = null)
    {
        var normalizedExtension = NormalizeExtension(extension);
        var canonicalName = assetType switch
        {
            "CoverArt" => "poster",
            "Background" => "fanart",
            "Logo" => "logo",
            "SeasonPoster" => "poster",
            "SeasonThumb" => "thumb",
            _ => throw new ArgumentOutOfRangeException(nameof(assetType), assetType, "Unsupported folder artwork type."),
        };

        return variantId.HasValue && variantId.Value != Guid.Empty
            ? Path.Combine(folderPath, $"{canonicalName}-{variantId.Value:N}{normalizedExtension}")
            : Path.Combine(folderPath, $"{canonicalName}{normalizedExtension}");
    }

    private static string BuildSubtitleSidecarPath(string mediaFilePath, string extension)
    {
        var normalizedExtension = NormalizeExtension(extension);
        var directory = Path.GetDirectoryName(mediaFilePath) ?? ".";
        var basename = Path.GetFileNameWithoutExtension(mediaFilePath);
        return Path.Combine(directory, $"{basename}{normalizedExtension}");
    }

    public static string BuildSubtitleSidecarPath(string mediaFilePath, string language, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaFilePath);
        var normalizedExtension = NormalizeExtension(extension);
        var directory = Path.GetDirectoryName(mediaFilePath) ?? ".";
        var basename = Path.GetFileNameWithoutExtension(mediaFilePath);
        var suffix = string.IsNullOrWhiteSpace(language) ? "und" : NormalizeSegment(language);
        return Path.Combine(directory, $"{basename}.{suffix}{normalizedExtension}");
    }

    public static string BuildLyricsSidecarPath(string mediaFilePath, string extension)
    {
        var normalizedExtension = NormalizeExtension(extension);
        var directory = Path.GetDirectoryName(mediaFilePath) ?? ".";
        var basename = Path.GetFileNameWithoutExtension(mediaFilePath);
        return Path.Combine(directory, $"{basename}{normalizedExtension}");
    }

    /// <summary>
    /// Resolves whether sidecar artwork should use folder-level names or
    /// file-prefixed names for a media file's current directory.
    /// </summary>
    public static MediaFileArtScope GetMediaFileArtScope(string mediaFilePath)
    {
        if (string.IsNullOrWhiteSpace(mediaFilePath))
        {
            return MediaFileArtScope.Dedicated;
        }

        var dir = Path.GetDirectoryName(mediaFilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return MediaFileArtScope.Dedicated;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (string.Equals(file, mediaFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsMediaExtension(Path.GetExtension(file)))
                {
                    return MediaFileArtScope.Shared;
                }
            }
        }
        catch
        {
            return MediaFileArtScope.Dedicated;
        }

        return MediaFileArtScope.Dedicated;
    }

    public static string GetMediaFilePosterPath(string mediaFilePath) =>
        BuildSiblingPath(mediaFilePath, "poster", ".jpg");

    public static string GetMediaFileFanartPath(string mediaFilePath) =>
        BuildSiblingPath(mediaFilePath, "fanart", ".jpg");

    public static string GetMediaFileLogoPath(string mediaFilePath) =>
        BuildSiblingPath(mediaFilePath, "logo", ".png");

    public static string GetMediaFileHeroPath(string mediaFilePath) =>
        BuildSiblingPath(mediaFilePath, "hero", ".jpg");

    public static string GetMediaFileThumbPath(string mediaFilePath) =>
        BuildSiblingPath(mediaFilePath, "poster-thumb", ".jpg");

    public static string GetMediaFileArtworkVariantPath(
        string mediaFilePath,
        string assetType,
        Guid variantId,
        string extension)
    {
        if (string.IsNullOrWhiteSpace(mediaFilePath))
        {
            throw new ArgumentException("Media file path is required.", nameof(mediaFilePath));
        }

        if (variantId == Guid.Empty)
        {
            throw new ArgumentException("Variant id is required.", nameof(variantId));
        }

        if (string.IsNullOrWhiteSpace(extension))
        {
            throw new ArgumentException("File extension is required.", nameof(extension));
        }

        var artKind = assetType.Trim() switch
        {
            "CoverArt" => "poster",
            "Background" => "fanart",
            "Logo" => "logo",
            "EpisodeStill" => "thumb",
            _ => throw new ArgumentOutOfRangeException(nameof(assetType), assetType, "Unsupported artwork type."),
        };

        var normalizedExtension = NormalizeExtension(extension);
        return BuildSiblingPath(mediaFilePath, $"{artKind}-{variantId:N}", normalizedExtension);
    }

    public static string GetFolderArtworkVariantPath(
        string folderPath,
        string assetType,
        Guid variantId,
        string extension)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("Folder path is required.", nameof(folderPath));
        }

        if (variantId == Guid.Empty)
        {
            throw new ArgumentException("Variant id is required.", nameof(variantId));
        }

        if (string.IsNullOrWhiteSpace(extension))
        {
            throw new ArgumentException("File extension is required.", nameof(extension));
        }

        var artKind = assetType.Trim() switch
        {
            "CoverArt" => "poster",
            "Background" => "fanart",
            "Logo" => "logo",
            "SeasonPoster" => "poster",
            "SeasonThumb" => "thumb",
            _ => throw new ArgumentOutOfRangeException(nameof(assetType), assetType, "Unsupported folder artwork type."),
        };

        return Path.Combine(folderPath, $"{artKind}-{variantId:N}{NormalizeExtension(extension)}");
    }

    private static string BuildSiblingPath(string mediaFilePath, string artKind, string extension)
    {
        if (string.IsNullOrWhiteSpace(mediaFilePath))
        {
            throw new ArgumentException("Media file path is required.", nameof(mediaFilePath));
        }

        var dir = Path.GetDirectoryName(mediaFilePath) ?? ".";
        if (GetMediaFileArtScope(mediaFilePath) == MediaFileArtScope.Dedicated)
        {
            return Path.Combine(dir, artKind + extension);
        }

        var basename = Path.GetFileNameWithoutExtension(mediaFilePath);
        return Path.Combine(dir, $"{basename}-{artKind}{extension}");
    }

    private static bool IsMediaExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        return extension.ToLowerInvariant() switch
        {
            ".mkv" or ".mp4" or ".m4v" or ".avi" or ".mov" or ".wmv" or ".webm" or ".ts" => true,
            ".m4b" or ".mp3" or ".flac" or ".m4a" or ".ogg" or ".opus" or ".wav" or ".aac" => true,
            ".epub" or ".pdf" => true,
            ".cbz" or ".cbr" or ".cb7" => true,
            _ => false,
        };
    }

    private static string NormalizeAssetTypeSegment(string assetType) =>
        assetType.Trim().ToLowerInvariant() switch
        {
            "coverart" => "cover",
            "background" => "background",
            "logo" => "logo",
            "seasonposter" => "season-poster",
            "seasonthumb" => "season-thumb",
            "episodestill" => "episode-still",
            _ => NormalizeSegment(assetType),
        };

    private static string NormalizeSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var filtered = new string(value.Trim().ToLowerInvariant().Select(ch => invalidChars.Contains(ch) ? '-' : ch).ToArray());
        return string.IsNullOrWhiteSpace(filtered) ? "unknown" : filtered.Replace(' ', '-');
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            throw new ArgumentException("File extension is required.", nameof(extension));
        }

        return extension.StartsWith(".", StringComparison.Ordinal) ? extension : "." + extension;
    }
}

public enum MediaFileArtScope
{
    Dedicated,
    Shared,
}
