using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Tests;

public sealed class MediaTileHoverPolicyTests
{
    [Theory]
    [InlineData("Movie")]
    [InlineData("TV")]
    public void HomeAndWatchTilesOfTheSameKindResolveToTheSameHoverMode(string kind)
    {
        var tile = new MediaTileViewModel { Id = Guid.NewGuid(), Title = "Title", MediaKind = kind };
        var home = MediaTileHoverPolicy.Resolve(tile, MediaTileHoverPolicy.ForShelf(tile), isHomeSurface: true);
        // The Watch page shelf marks its tiles as a discovery surface exactly like Home does.
        var watch = MediaTileHoverPolicy.Resolve(tile, MediaTileHoverPolicy.ForShelf(tile), isHomeSurface: true);
        Assert.Equal(MediaTileHoverMode.Expanded, home);
        Assert.Equal(home, watch);
    }

    [Fact]
    public void WatchShelvesOptIntoTheSameSurfaceAsHome()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var shelf = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "MediaHub", "MediaShelf.razor"));
        Assert.Contains("IsHomeSurface=\"@(SectionType == MediaHubSectionType.Watch)\"", shelf);
        var css = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "MediaTiles", "MediaTile.razor.css"));
        Assert.DoesNotContain("transition: width", css);
        Assert.DoesNotContain("width: var(--media-tile-expanded-width) !important", css);
    }
}
