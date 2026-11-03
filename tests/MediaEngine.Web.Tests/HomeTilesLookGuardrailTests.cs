using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Tests;

/// <summary>Keeps the Home, detail and tiles look (spec D1, D5, D17): square pills and tiles that never change size on hover.</summary>
public sealed class HomeTilesLookGuardrailTests
{
    private static string Read(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, relative));
    }

    [Fact]
    public void ThePillRadiusTokenIsSquareAndRoundHasItsOwnToken()
    {
        var tokens = Read("src/MediaEngine.Web/wwwroot/tuvima.tokens.css");
        Assert.Contains("--tl-radius-pill: 3px;", tokens, StringComparison.Ordinal);
        Assert.Contains("--tl-radius-round: 999px;", tokens, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Components/MediaTiles/MediaTile.razor.css")]
    [InlineData("Components/MediaTiles/MediaGroupTile.razor.css")]
    [InlineData("Components/Details/DetailPrimaryModule.razor.css")]
    [InlineData("Components/Shared/AppChoiceCard.razor.css")]
    public void LabelsOnTilesAndDetailPagesAreNotRoundEnded(string file)
    {
        var css = Read("src/MediaEngine.Web/" + file);
        Assert.DoesNotContain("999px", css, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Movie")]
    [InlineData("TV")]
    [InlineData("Book")]
    [InlineData("Music")]
    public void NoTileGrowsOnHover(string kind)
    {
        var tile = new MediaTileViewModel { Id = Guid.NewGuid(), Title = "Title", MediaKind = kind };
        Assert.NotEqual(MediaTileHoverMode.Expanded, MediaTileHoverPolicy.ForShelf(tile));
        Assert.NotEqual(MediaTileHoverMode.Expanded, MediaTileHoverPolicy.Resolve(tile, MediaTileHoverMode.Expanded, isHomeSurface: true));
        var tokens = Read("src/MediaEngine.Web/wwwroot/tuvima.tokens.css");
        Assert.Contains("--tl-hover-media-scale: 1;", tokens, StringComparison.Ordinal);
        Assert.Contains("--tl-hover-artwork-scale: 1;", tokens, StringComparison.Ordinal);
    }
}
