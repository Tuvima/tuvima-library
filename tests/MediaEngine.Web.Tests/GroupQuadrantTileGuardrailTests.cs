using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Keeps the quadrant tile the only group look (spec details-and-tile-fixes D1, D2): the fan, the single-cover group fallback and the
/// round person tile stay deleted, and the tile styles take their colours from design tokens.
/// </summary>
public sealed class GroupQuadrantTileGuardrailTests
{
    private static readonly string Tiles = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components", "MediaTiles"));

    [Fact]
    public void TheFannedCoverStackAndItsStyles_AreGone()
    {
        var markup = File.ReadAllText(Path.Combine(Tiles, "MediaTile.razor"));
        var css = File.ReadAllText(Path.Combine(Tiles, "MediaTile.razor.css"));

        Assert.DoesNotContain("media-tile-stack", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("media-tile-stack", css, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowPreviewStack", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("rotate(", css, StringComparison.Ordinal);
    }

    [Fact]
    public void AGroupNeverFallsBackToASingleCoverOfWhatItHolds()
    {
        var markup = File.ReadAllText(Path.Combine(Tiles, "MediaTile.razor"));

        Assert.DoesNotContain("ArtworkStackItems.Select", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Item.PreviewImages", markup, StringComparison.Ordinal);
        Assert.Contains("Item.CustomCoverUrl", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePersonTileIsNoLongerRound_AndTheCountLineThatBelongedToItIsGone()
    {
        var css = File.ReadAllText(Path.Combine(Tiles, "PersonTile.razor.css"));
        var shared = Path.GetFullPath(Path.Combine(Tiles, "..", "Shared"));

        Assert.DoesNotContain("border-radius: 50%", css, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(shared, "MediaCountLine.razor")));
        Assert.False(File.Exists(Path.Combine(Tiles, "PersonTileGrid.razor.css")));
    }

    [Theory]
    [InlineData("MediaTile.razor.css")]
    [InlineData("PersonTile.razor.css")]
    public void TheTileStylesTakeTheirColoursFromTokens(string file)
    {
        var css = File.ReadAllText(Path.Combine(Tiles, file));

        Assert.DoesNotMatch(new Regex(@"#[0-9a-fA-F]{3,8}\b"), css);
    }
}
