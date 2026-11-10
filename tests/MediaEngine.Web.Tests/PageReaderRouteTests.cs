namespace MediaEngine.Web.Tests;

/// <summary>PDF and comic reader spec WP3: the reader address picks the page reader or the book reader from the Engine's answer.</summary>
public sealed class PageReaderRouteTests
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    [Fact]
    public void ReadRoute_AsksTheEngineAndOpensThePageReaderForComicsAndPdfs()
    {
        var route = Read("src/MediaEngine.Web/Components/Pages/EpubReader.razor");

        Assert.Contains("@page \"/read/{AssetId:guid}\"", route, StringComparison.Ordinal);
        Assert.Contains("GetReaderFormatAsync(AssetId)", route, StringComparison.Ordinal);
        Assert.Contains("\"comic\" or \"pdf\"", route, StringComparison.Ordinal);
        Assert.Contains("<PageReader", route, StringComparison.Ordinal);
        Assert.Contains("<BookReader", route, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadRoute_FallsBackToTheBookReaderWhenTheEngineCannotSay()
    {
        var route = Read("src/MediaEngine.Web/Components/Pages/EpubReader.razor");

        Assert.Contains("found ?? Epub", route, StringComparison.Ordinal);
    }

    [Fact]
    public void PageReader_ReadsPicturesOnlyThroughTheSignedInProxyAndNeverTheEngineDirectly()
    {
        var page = Read("src/MediaEngine.Web/Components/Pages/PageReader.razor");
        var script = Read("src/MediaEngine.Web/wwwroot/js/page-reader.js");

        Assert.Contains("EngineBookProxyPath.ToBookBase(AssetId)", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/read/", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/v1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PageReader_CarriesEveryStateTheSpecNames()
    {
        var page = Read("src/MediaEngine.Web/Components/Pages/PageReader.razor");

        foreach (var key in new[] { "PageReader_CantReachEngine", "PageReader_CantOpenFile", "PageReader_Opening", "PageReader_TryAgain", "PageReader_BackToLibrary" })
        {
            Assert.Contains(key, page, StringComparison.Ordinal);
        }
    }
}
