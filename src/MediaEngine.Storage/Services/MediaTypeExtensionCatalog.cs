using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Services;

/// <summary>
/// Config-backed media extension catalog used by ingestion/runtime services.
/// </summary>
public sealed class MediaTypeExtensionCatalog : IMediaTypeExtensionCatalog
{
    private readonly IConfigurationLoader? _configLoader;

    public MediaTypeExtensionCatalog()
    {
    }

    public MediaTypeExtensionCatalog(IConfigurationLoader configLoader)
    {
        ArgumentNullException.ThrowIfNull(configLoader);
        _configLoader = configLoader;
    }

    public IReadOnlySet<string> GetAllMediaExtensions()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in LoadTypes())
        {
            foreach (var extension in type.Extensions)
            {
                var normalized = NormalizeExtension(extension);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    result.Add(normalized);
                }
            }
        }

        return result;
    }

    public IReadOnlySet<string> GetExtensionsFor(MediaType mediaType)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in LoadTypes())
        {
            if (!MatchesMediaType(type, mediaType))
            {
                continue;
            }

            foreach (var extension in type.Extensions)
            {
                var normalized = NormalizeExtension(extension);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    result.Add(normalized);
                }
            }
        }

        return result;
    }

    public bool IsKnownMediaExtension(string? extension) =>
        GetAllMediaExtensions().Contains(NormalizeExtension(extension));

    public bool IsUnambiguousExtension(string? extension)
    {
        var normalized = NormalizeExtension(extension);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return LoadTypes()
            .Count(type => type.Extensions.Any(value =>
                string.Equals(NormalizeExtension(value), normalized, StringComparison.OrdinalIgnoreCase))) == 1;
    }

    public bool IsStrongFormatExtension(string? extension)
    {
        var normalized = NormalizeExtension(extension);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        // PDF is intentionally not "strong" because it can be user documents
        // or books; keep root-folder review behavior conservative.
        return IsUnambiguousExtension(normalized)
            && !normalized.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsVideoExtension(string? extension)
    {
        var normalized = NormalizeExtension(extension);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return GetExtensionsFor(MediaType.Movies).Contains(normalized)
            || GetExtensionsFor(MediaType.TV).Contains(normalized);
    }

    public MediaType ResolveMediaTypeFromExtension(string? extension)
    {
        var normalized = NormalizeExtension(extension);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return MediaType.Unknown;
        }

        var matches = LoadTypes()
            .Where(type => type.Extensions.Any(value =>
                string.Equals(NormalizeExtension(value), normalized, StringComparison.OrdinalIgnoreCase)))
            .Select(ToMediaType)
            .Where(type => type != MediaType.Unknown)
            .Distinct()
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (normalized.Equals(".m4b", StringComparison.OrdinalIgnoreCase)
            && matches.Contains(MediaType.Audiobooks))
        {
            return MediaType.Audiobooks;
        }

        if (matches.Contains(MediaType.Music)
            && !normalized.Equals(".m4b", StringComparison.OrdinalIgnoreCase))
        {
            return MediaType.Music;
        }

        if (matches.Contains(MediaType.Movies))
        {
            return MediaType.Movies;
        }

        return MediaType.Unknown;
    }

    private IReadOnlyList<MediaTypeDefinition> LoadTypes()
    {
        try
        {
            var config = _configLoader?.LoadMediaTypes();
            if (config?.Types.Count > 0)
            {
                return config.Types;
            }
        }
        catch
        {
            // First-run or test stubs can omit media_types.json support.
        }

        return MediaTypeConfiguration.DefaultTypes();
    }

    private static bool MatchesMediaType(MediaTypeDefinition definition, MediaType mediaType) =>
        ToMediaType(definition) == mediaType;

    private static MediaType ToMediaType(MediaTypeDefinition definition)
    {
        var key = NormalizeMediaTypeName(definition.Key);
        var display = NormalizeMediaTypeName(definition.DisplayName);

        return key switch
        {
            "books" => MediaType.Books,
            "audiobooks" => MediaType.Audiobooks,
            "comics" => MediaType.Comics,
            "movies" => MediaType.Movies,
            "tv" or "tvshows" => MediaType.TV,
            "music" => MediaType.Music,
            "videos" => MediaType.Videos,
            _ => display switch
            {
                "books" => MediaType.Books,
                "audiobooks" => MediaType.Audiobooks,
                "comics" => MediaType.Comics,
                "movies" => MediaType.Movies,
                "tv" or "tvshows" => MediaType.TV,
                "music" => MediaType.Music,
                "videos" => MediaType.Videos,
                _ => MediaType.Unknown
            }
        };
    }

    private static string NormalizeMediaTypeName(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var trimmed = extension.Trim().ToLowerInvariant();
        return trimmed.StartsWith(".", StringComparison.Ordinal) ? trimmed : "." + trimmed;
    }
}
