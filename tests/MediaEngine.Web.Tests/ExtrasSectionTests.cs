using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Extras row on a title's page and the viewer a card opens (Extras spec): nothing shows when there are none, a picture folder
/// is one card, a missing file stays as a warning, and each kind opens in the right viewer.
/// </summary>
public sealed class ExtrasSectionTests : AsyncBunitContext
{
    private static readonly string DocId = Guid.NewGuid().ToString("D");
    private static readonly string PictureId = Guid.NewGuid().ToString("D");
    private static readonly string ClipId = Guid.NewGuid().ToString("D");

    public ExtrasSectionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
    }

    [Fact]
    public void WithoutExtras_TheSectionShowsNothing()
    {
        var cut = Render<ExtrasSection>(p => p.Add(c => c.Extras, []));

        Assert.Empty(cut.FindAll("[data-testid=extras-section]"));
    }

    [Fact]
    public void EachCardLinksToTheViewer_AndAPictureFolderIsOneCard()
    {
        var cut = Render<ExtrasSection>(p => p.Add(c => c.Extras,
        [
            new DetailExtraCardViewModel { Id = DocId, Kind = DetailExtraCardKinds.Document, Title = "Season Guide", Subtitle = "PDF, 42 pages" },
            new DetailExtraCardViewModel
            {
                Id = PictureId, Kind = DetailExtraCardKinds.Pictures, Title = "Stills", Subtitle = "24 pictures",
                ItemCount = 24, PreviewIds = [PictureId, Guid.NewGuid().ToString("D")],
            },
            new DetailExtraCardViewModel { Id = ClipId, Kind = DetailExtraCardKinds.Clip, Title = "Trailer", Subtitle = "2:10" },
        ]));

        var links = cut.FindAll("a.tl-extras-card");
        Assert.Equal(3, links.Count);
        Assert.Equal($"/extras/{DocId}", links[0].GetAttribute("href"));
        Assert.Contains("24 pictures", cut.Markup);
        Assert.Equal(2, cut.FindAll(".tl-extras-card__mosaic img").Count);
    }

    [Fact]
    public void AMissingFile_StaysAsAWarning_WithoutALink()
    {
        var cut = Render<ExtrasSection>(p => p.Add(c => c.Extras,
        [
            new DetailExtraCardViewModel { Id = DocId, Title = "Season Guide", IsMissing = true },
        ]));

        Assert.Empty(cut.FindAll("a.tl-extras-card"));
        Assert.Contains("File not found", cut.Markup);
    }

    [Fact]
    public async Task APdf_OpensInTheBrowsersReader()
    {
        var cut = Viewer(DocId, View(DocId, "application/pdf", DetailExtraCardKinds.Document));

        await cut.WaitForStateAsync(() => cut.FindAll("[data-testid=extra-document]").Count == 1);
        Assert.Equal($"/engine-extra/{DocId}/file", cut.Find("iframe").GetAttribute("src"));
    }

    [Fact]
    public async Task AnEpub_IsDownloadOnly()
    {
        var cut = Viewer(DocId, View(DocId, "application/epub+zip", DetailExtraCardKinds.Document));

        await cut.WaitForStateAsync(() => cut.FindAll("[data-testid=extra-download-only]").Count == 1);
        Assert.Empty(cut.FindAll("iframe"));
    }

    [Fact]
    public async Task AClip_PlaysInAPlainPlayer()
    {
        var cut = Viewer(ClipId, View(ClipId, "video/mp4", DetailExtraCardKinds.Clip));

        await cut.WaitForStateAsync(() => cut.FindAll("video").Count == 1);
        Assert.Equal($"/engine-extra/{ClipId}/file", cut.Find("video").GetAttribute("src"));
    }

    [Fact]
    public async Task APictureFolder_OpensAGalleryAndAFullSizeView()
    {
        var cut = Viewer(PictureId, View(PictureId, "image/jpeg", DetailExtraCardKinds.Pictures));

        await cut.WaitForStateAsync(() => cut.FindAll(".tl-extra-viewer__thumb").Count == 1);
        cut.Find(".tl-extra-viewer__thumb").Click();

        Assert.Equal($"/engine-extra/{PictureId}/file", cut.Find(".tl-extra-viewer__lightbox img").GetAttribute("src"));
    }

    [Fact]
    public async Task AnExtraThatIsGone_SaysSo()
    {
        var cut = Viewer(DocId, null);

        await cut.WaitForStateAsync(() => cut.FindAll("[data-testid=extra-unavailable]").Count == 1);
    }

    private IRenderedComponent<ExtraViewerPage> Viewer(string id, ExtraViewDto? view)
    {
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetExtraAsync), _ => Task.FromResult(view))));
        return Render<ExtraViewerPage>(p => p.Add(c => c.ExtraId, id));
    }

    private static ExtraViewDto View(string id, string mime, string kind) => new()
    {
        Id = id,
        Kind = kind,
        Title = "Season Guide",
        Items = [new ExtraViewItemDto { Id = id, Title = "Season Guide", FileName = "guide.bin", MimeType = mime }],
    };
}
