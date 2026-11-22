using Bunit;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Tests;

public sealed class MediaViewerFaceBoxTests : BunitContext
{
    [Fact]
    public void Box_PlacesItselfAsPercentagesOfThePicture()
    {
        var cut = Render<MediaViewerFaceBox>(p => p.Add(c => c.X, 0.25).Add(c => c.Y, 0.1).Add(c => c.Width, 0.2).Add(c => c.Height, 0.3).Add(c => c.Name, "Sam"));

        var span = cut.Find("span.view-face-box");
        Assert.Contains("left:25%", span.GetAttribute("style"));
        Assert.Contains("width:20%", span.GetAttribute("style"));
        Assert.Equal("Sam", cut.Find("small").TextContent);
        Assert.DoesNotContain("unnamed", span.ClassName);
    }

    [Fact]
    public void Box_WithoutAName_IsMarkedUnnamedAndShowsNoLabel()
    {
        var cut = Render<MediaViewerFaceBox>(p => p.Add(c => c.Width, 0.2));

        Assert.Contains("view-face-box--unnamed", cut.Find("span").ClassName);
        Assert.Empty(cut.FindAll("small"));
    }
}
