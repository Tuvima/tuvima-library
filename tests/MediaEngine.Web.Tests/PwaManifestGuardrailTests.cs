using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

public sealed class PwaManifestGuardrailTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string WebRoot = Path.Combine(RepoRoot, "src", "MediaEngine.Web");

    [Fact]
    public void Manifest_IsValidJsonWithStableIdentity()
    {
        using var manifest = LoadManifest();
        var root = manifest.RootElement;

        Assert.Equal("/", root.GetProperty("id").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("description").GetString()));
        Assert.True(root.GetProperty("categories").GetArrayLength() > 0);
    }

    [Fact]
    public void Manifest_DeclaresFourShortcutsToRealRoutesWithExistingIcons()
    {
        using var manifest = LoadManifest();
        var shortcuts = manifest.RootElement.GetProperty("shortcuts").EnumerateArray().ToList();
        var urls = shortcuts.Select(shortcut => shortcut.GetProperty("url").GetString()!).ToList();
        var routes = PageRouteTemplates();

        Assert.Equal(new[] { "/watch", "/read", "/listen", "/search" }, urls);

        foreach (var shortcut in shortcuts)
        {
            var url = shortcut.GetProperty("url").GetString()!;
            Assert.Contains(routes, template => RouteMatches(template, url));

            var icons = shortcut.GetProperty("icons").EnumerateArray().ToList();
            Assert.NotEmpty(icons);
            foreach (var icon in icons)
            {
                Assert.Equal("96x96", icon.GetProperty("sizes").GetString());
                AssertPngIcon(icon.GetProperty("src").GetString()!, 96);
            }
        }
    }

    [Fact]
    public void Manifest_MaskableIconIsA512PixelPngThatExists()
    {
        using var manifest = LoadManifest();
        var maskable = manifest.RootElement.GetProperty("icons").EnumerateArray()
            .Single(icon => icon.GetProperty("purpose").GetString() == "maskable");

        Assert.Equal("512x512", maskable.GetProperty("sizes").GetString());
        AssertPngIcon(maskable.GetProperty("src").GetString()!, 512);
    }

    [Fact]
    public void HomeFirstShelf_LoadsItsFirstTilesEagerlyAndLaterShelvesLazily()
    {
        var home = Read("src/MediaEngine.Web/Components/Pages/LibraryBrowsePage.razor");
        var shelf = Read("src/MediaEngine.Web/Components/MediaTiles/MediaTileShelf.razor");
        var tile = Read("src/MediaEngine.Web/Components/MediaTiles/MediaTile.razor");

        Assert.Contains("const int FirstShelfEagerTileCount = 6;", home, StringComparison.Ordinal);
        Assert.Contains("EagerItemCount=\"@(shelfIndex == 0 ? FirstShelfEagerTileCount : 0)\"", home, StringComparison.Ordinal);
        Assert.Contains("EagerArtwork=\"@(index < EagerItemCount)\"", shelf, StringComparison.Ordinal);
        Assert.Contains("private string CardImageLoading => EagerArtwork ? \"eager\" : \"lazy\";", tile, StringComparison.Ordinal);
        Assert.Contains("loading=\"@CardImageLoading\"", tile, StringComparison.Ordinal);
    }

    [Fact]
    public void HeroComponents_DoNotDeferTheirArtworkWithLazyLoading()
    {
        var heroFiles = Directory.EnumerateFiles(Path.Combine(WebRoot, "Components"), "*.razor", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains("Hero", StringComparison.Ordinal)
                           || path.Contains(Path.Combine("Components", "Cinematic"), StringComparison.Ordinal))
            .ToList();

        Assert.Contains(heroFiles, path => Path.GetFileName(path) == "DetailHero.razor");
        Assert.Contains(heroFiles, path => Path.GetFileName(path) == "CinematicHeroCarousel.razor");

        foreach (var file in heroFiles)
        {
            Assert.DoesNotContain("loading=\"lazy\"", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    private static JsonDocument LoadManifest() =>
        JsonDocument.Parse(Read("src/MediaEngine.Web/wwwroot/manifest.webmanifest"));

    private static void AssertPngIcon(string src, int pixels)
    {
        var path = Path.Combine(WebRoot, "wwwroot", src.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Manifest icon is missing: {src}");

        var header = new byte[24];
        using (var stream = File.OpenRead(path))
        {
            stream.ReadExactly(header, 0, header.Length);
        }

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, header[..4]);
        Assert.Equal(pixels, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)));
        Assert.Equal(pixels, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)));
    }

    private static List<string> PageRouteTemplates()
    {
        var pattern = new Regex("@page\\s+\"([^\"]+)\"", RegexOptions.CultureInvariant);
        return Directory.EnumerateFiles(Path.Combine(WebRoot, "Components"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(file => pattern.Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value))
            .ToList();
    }

    private static bool RouteMatches(string template, string url)
    {
        var templateSegments = template.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var urlSegments = url.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (templateSegments.Length != urlSegments.Length)
        {
            return false;
        }

        for (var index = 0; index < templateSegments.Length; index++)
        {
            var segment = templateSegments[index];
            if (segment.StartsWith('{'))
            {
                continue;
            }

            if (!string.Equals(segment, urlSegments[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
