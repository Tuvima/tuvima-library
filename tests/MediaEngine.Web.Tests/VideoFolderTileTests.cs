using Bunit;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MediaEngine.Web.Tests;

/// <summary>A folder of videos is one portrait tile that sits beside single videos: the folder's own name, a stacked-videos mark and the count (issue 1327).</summary>
public sealed class VideoFolderTileTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public VideoFolderTileTests()
    {
        Services.AddSingleton(_device);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
    }

    [Fact]
    public void AVideoFolder_IsThePortraitCard_NamedForItsFolder_WithNoKindWordAndNoPlayButton()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Folder("/v/folder.jpg")));

        Assert.Equal("Spanish lessons", cut.Find(".media-tile-video-collection__title").TextContent.Trim());
        Assert.Empty(cut.FindAll(".media-tile-label--kind"));
        Assert.DoesNotContain("Video folder", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Video collection", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".group-collage"));
        Assert.Empty(cut.FindAll(".media-tile-video-collection__play"));
        Assert.Single(cut.FindAll(".media-tile-video-collection__group"));
        Assert.Equal("Group of videos", cut.Find(".media-tile-video-collection__group").GetAttribute("aria-label"));
    }

    [Fact]
    public void AVideoFolder_ShowsItsCountOnce()
    {
        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Folder("/v/folder.jpg")));

        Assert.Single(cut.FindAll(".media-tile-video-collection__count"));
        Assert.Empty(cut.FindAll(".media-tile-label--bottom"));
        Assert.Contains("88 videos", cut.Find(".media-tile-video-collection__count").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AVideoFolderWithNoPicture_DrawsAFrameFromItsFirstVideo()
    {
        var item = Folder(null, frame: "/v/frame.jpg");

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, item));

        Assert.Contains(cut.FindAll("img"), img => (img.GetAttribute("src") ?? string.Empty).Contains("frame.jpg", StringComparison.Ordinal));
    }

    [Fact]
    public void ASingleVideo_StaysTheStillTile_WithNoGroupMark()
    {
        var item = Folder("/v/one.jpg", single: true);

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, item));

        Assert.Empty(cut.FindAll(".media-tile-video-collection"));
        Assert.Empty(cut.FindAll(".media-tile-video-collection__group"));
    }

    private static MediaTileViewModel Folder(string? picture, string? frame = null, bool single = false) => new()
    {
        Id = Guid.Parse("00000000-0000-0000-0000-0000000000b1"),
        Title = single ? "Birthday 2024" : "Spanish lessons",
        IsCollection = !single,
        UseGroupTile = false,
        MediaKind = single ? "Video" : VideoFolderWording.FolderMediaType,
        ArtworkStackItems = frame is null ? [] : [new ArtworkStackItem { Id = "1", Title = "One", ImageUrl = frame, MediaType = "Video" }],
        Presentation = MediaTilePresentation.TvSeries,
        Shape = MediaTileShape.Portrait,
        TileImageUrl = picture,
        PreviewTotalCount = 88,
        MetaText = "88 videos",
        Subtitle = "88 videos",
        NavigationUrl = "/details/tvshow/b1",
    };
}
