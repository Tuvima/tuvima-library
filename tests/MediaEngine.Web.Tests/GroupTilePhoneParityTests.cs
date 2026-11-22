using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A group tile is one tile at every width (issue 1260): a phone differs from a desktop only in the size the stylesheet gives the tile and
/// in how many tiles sit across, never in the shape, the collage, the name and count band, the "..." menu or the count slot. A video
/// collection used to draw its own tall card with parts that dropped out on a narrow tile; it now takes the shared landscape collage.
/// </summary>
public sealed class GroupTilePhoneParityTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public GroupTilePhoneParityTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
    }

    private Task Init(string formFactor) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, formFactor == "phone"));

    public static TheoryData<string> GroupKinds => new() { "series", "collection", "video-folder", "video-folder-picture" };

    [Theory]
    [MemberData(nameof(GroupKinds))]
    public async Task AGroupTile_DrawsTheSameMarkupOnAPhoneAsOnADesktop(string kind)
    {
        var item = Build(kind);

        await Init("desktop");
        var desktop = Render<MediaTile>(p => p.Add(c => c.Item, item));
        await Init("phone");
        var phone = Render<MediaTile>(p => p.Add(c => c.Item, item));

        Assert.Equal(Stable(desktop.Markup), Stable(phone.Markup));
    }

    [Theory]
    [MemberData(nameof(GroupKinds))]
    public async Task AGroupTile_OnAPhone_HasTheLandscapeShape_TheCollageTheNameAndCountBandAndTheMenu(string kind)
    {
        await Init("phone");

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Build(kind)));

        Assert.Contains("is-landscape", cut.Find("article.media-tile").ClassList);
        Assert.Contains("is-group", cut.Find("article.media-tile").ClassList);
        Assert.NotEmpty(cut.FindAll(".group-collage"));
        Assert.Equal("Group", cut.Find(".group-collage__name").TextContent.Trim());
        Assert.StartsWith("3 ", cut.Find(".group-collage__count").TextContent.Trim(), StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll(".media-tile-label--kind"));
        Assert.Empty(cut.FindAll(".media-tile-video-collection"));
    }

    [Fact]
    public async Task AVideoFolderTile_OnAPhone_KeepsItsKindWord_ThatTheOldCompactCardDroppedBelowAWidth()
    {
        await Init("phone");

        var cut = Render<MediaTile>(p => p.Add(c => c.Item, Build("video-folder")));

        Assert.Equal("Video collection", cut.Find(".media-tile-label--kind").TextContent.Trim());
    }

    [Fact]
    public async Task AVideoFolderWithAFolderPicture_ShowsItWhole_OnAPhoneAsOnADesktop()
    {
        foreach (var formFactor in new[] { "phone", "desktop" })
        {
            await Init(formFactor);

            var cut = Render<MediaTile>(p => p.Add(c => c.Item, Build("video-folder-picture")));

            Assert.Contains("folder.jpg", cut.Find("img.group-collage__custom").GetAttribute("src"), StringComparison.Ordinal);
            Assert.Empty(cut.FindAll(".group-collage__cover"));
        }
    }

    [Fact]
    public void TheGroupTileStylesheets_HaveNoPhoneOrWidthSpecificTileLayout()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components"));
        var tileCss = File.ReadAllText(Path.Combine(root, "MediaTiles", "MediaTile.razor.css"));
        var collageCss = File.ReadAllText(Path.Combine(root, "Shared", "GroupCoverCollage.razor.css"));

        // The tile's width alone sets how many covers show (the approved 3, 4, 6 rule, a container query on the collage); the tile's own
        // stylesheet carries no media query, container query or form-factor rule that would draw a group differently on a phone.
        Assert.DoesNotContain("@media (max-width", tileCss, StringComparison.Ordinal);
        Assert.DoesNotContain("@container", tileCss, StringComparison.Ordinal);
        Assert.DoesNotContain("form-factor", tileCss, StringComparison.Ordinal);
        Assert.DoesNotContain("@media", collageCss, StringComparison.Ordinal);
        Assert.DoesNotContain("form-factor", collageCss, StringComparison.Ordinal);
    }

    // Blazor numbers its event handlers and element references afresh per render; everything else must match character for character.
    private static string Stable(string markup) =>
        Regex.Replace(markup, "(blazor:[a-zA-Z:]+)=\"[^\"]*\"", "$1=\"\"");

    private static MediaTileViewModel Build(string kind)
    {
        var video = kind.StartsWith("video-folder", StringComparison.Ordinal);
        return new MediaTileViewModel
        {
            Id = Guid.Parse("00000000-0000-0000-0000-0000000000a1"),
            CollectionId = Guid.Parse("00000000-0000-0000-0000-0000000000a2"),
            Title = "Group",
            IsCollection = true,
            UseGroupTile = true,
            GroupKind = kind == "series" ? MediaTileGroupKind.Series : MediaTileGroupKind.Collection,
            Presentation = kind == "series" ? MediaTilePresentation.BookSeries : MediaTilePresentation.Default,
            MediaKind = video ? VideoFolderWording.FolderMediaType : kind == "series" ? "Book" : string.Empty,
            Shape = MediaTileShape.Landscape,
            TileImageUrl = kind == "video-folder-picture" ? "/v/folder.jpg" : null,
            ArtworkStackItems = Enumerable.Range(0, 3).Select(index => new ArtworkStackItem
            {
                Id = index.ToString(),
                Title = $"Item {index}",
                ImageUrl = $"/c/{index + 1}.jpg",
                MediaType = video ? "Video" : "Book",
                Shape = video ? ArtworkShape.Wide : ArtworkShape.Portrait,
            }).ToList(),
            NavigationUrl = "/details/collection/group",
            PreviewTotalCount = 3,
        };
    }
}
