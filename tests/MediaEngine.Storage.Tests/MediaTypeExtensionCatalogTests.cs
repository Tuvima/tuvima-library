using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Services;

namespace MediaEngine.Storage.Tests;

public sealed class MediaTypeExtensionCatalogTests
{
    [Fact]
    public void Catalog_ReadsMediaExtensionsFromConfiguration()
    {
        using var loader = new ConfigurationDirectoryLoader(FindRepoDir("config"));
        var catalog = new MediaTypeExtensionCatalog(loader);

        Assert.True(catalog.IsKnownMediaExtension(".flac"));
        Assert.True(catalog.IsKnownMediaExtension("epub"));
        Assert.True(catalog.IsUnambiguousExtension(".epub"));
        Assert.False(catalog.IsUnambiguousExtension(".mp3"));
        Assert.True(catalog.IsStrongFormatExtension(".cbz"));
        Assert.False(catalog.IsStrongFormatExtension(".pdf"));
        Assert.True(catalog.IsVideoExtension(".mkv"));

        Assert.Equal(MediaType.Audiobooks, catalog.ResolveMediaTypeFromExtension(".m4b"));
        Assert.Equal(MediaType.Music, catalog.ResolveMediaTypeFromExtension(".mp3"));
        Assert.Equal(MediaType.Movies, catalog.ResolveMediaTypeFromExtension(".mkv"));
        Assert.Equal(MediaType.Books, catalog.ResolveMediaTypeFromExtension(".epub"));
        Assert.Equal(MediaType.Comics, catalog.ResolveMediaTypeFromExtension(".cbz"));
    }

    // The Videos definition used to map to Unknown, so nothing could ask which extensions belong to Videos.
    [Fact]
    public void Catalog_MapsTheVideosDefinitionToTheVideosMediaType()
    {
        using var loader = new ConfigurationDirectoryLoader(FindRepoDir("config"));
        var catalog = new MediaTypeExtensionCatalog(loader);

        var extensions = catalog.GetExtensionsFor(MediaType.Videos);

        Assert.Contains(".mkv", extensions);
        Assert.Contains(".mp4", extensions);
        Assert.Contains(".webm", extensions);
    }

    // A bare video file is a movie by extension alone; only the library folder or the import sub-folder name makes it a Video.
    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    [InlineData(".webm")]
    public void Catalog_StillResolvesABareVideoExtensionToMovies(string extension)
    {
        using var loader = new ConfigurationDirectoryLoader(FindRepoDir("config"));
        var catalog = new MediaTypeExtensionCatalog(loader);

        Assert.Equal(MediaType.Movies, catalog.ResolveMediaTypeFromExtension(extension));
    }

    /// <summary>
    /// Pins the extension superset the catalog was extended to cover: formats that other
    /// bypass sites across the Engine (AssetPathService, VideoMetadataTagger,
    /// AudioMetadataTagger, the plugin segment detectors, PlaybackCapabilitiesService, etc.)
    /// recognized via their own private lists, but which the shared, config-backed catalog
    /// used to be missing. Sourced from <c>config/media_types.json</c>.
    /// </summary>
    [Theory]
    [InlineData(".cb7", MediaType.Comics)]
    [InlineData(".ts", MediaType.Movies)]
    [InlineData(".mpg", MediaType.Movies)]
    [InlineData(".mpeg", MediaType.Movies)]
    [InlineData(".m2ts", MediaType.Movies)]
    [InlineData(".mov", MediaType.Movies)]
    [InlineData(".wmv", MediaType.Movies)]
    [InlineData(".opus", MediaType.Music)]
    [InlineData(".wma", MediaType.Music)]
    public void Catalog_RecognizesFormatsPreviouslyOnlyHandledByBypassSites(string extension, MediaType expectedMediaType)
    {
        using var loader = new ConfigurationDirectoryLoader(FindRepoDir("config"));
        var catalog = new MediaTypeExtensionCatalog(loader);

        Assert.True(catalog.IsKnownMediaExtension(extension));
        Assert.Equal(expectedMediaType, catalog.ResolveMediaTypeFromExtension(extension));
    }

    [Theory]
    [InlineData(".ts")]
    [InlineData(".mpg")]
    [InlineData(".mpeg")]
    [InlineData(".m2ts")]
    [InlineData(".mov")]
    [InlineData(".wmv")]
    public void Catalog_TreatsNewVideoFormatsAsVideoExtensions(string extension)
    {
        using var loader = new ConfigurationDirectoryLoader(FindRepoDir("config"));
        var catalog = new MediaTypeExtensionCatalog(loader);

        Assert.True(catalog.IsVideoExtension(extension));
    }

    /// <summary>
    /// The hardcoded <see cref="MediaEngine.Domain.Configuration.MediaTypeConfiguration.DefaultTypes"/>
    /// fallback (used when <c>config/media_types.json</c> can't be loaded, e.g. first run) must
    /// carry the same extension superset as the JSON config, or a config-less catalog would
    /// silently lose recognition for these formats.
    /// </summary>
    [Theory]
    [InlineData(".cb7", MediaType.Comics)]
    [InlineData(".ts", MediaType.Movies)]
    [InlineData(".mpg", MediaType.Movies)]
    [InlineData(".mpeg", MediaType.Movies)]
    [InlineData(".m2ts", MediaType.Movies)]
    [InlineData(".mov", MediaType.Movies)]
    [InlineData(".wmv", MediaType.Movies)]
    [InlineData(".opus", MediaType.Music)]
    [InlineData(".wma", MediaType.Music)]
    public void Catalog_HardcodedFallback_MatchesConfigSuperset(string extension, MediaType expectedMediaType)
    {
        var catalog = new MediaTypeExtensionCatalog();

        Assert.True(catalog.IsKnownMediaExtension(extension));
        Assert.Equal(expectedMediaType, catalog.ResolveMediaTypeFromExtension(extension));
    }

    private static string FindRepoDir(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find {Path.Combine(parts)} from {AppContext.BaseDirectory}");
    }
}
