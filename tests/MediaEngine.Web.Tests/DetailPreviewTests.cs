using Bunit;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class DetailPreviewTests : AsyncBunitContext
{
    private static DetailPreview Preview(Guid id, string? logo = null, string? cover = "/cover.jpg") =>
        new([id], "Dune", cover, logo, "/backdrop.jpg");

    [Fact]
    public void APreviewIsUsedOnceAndOnlyForTheItemItNames()
    {
        var state = new DetailPreviewState();
        var id = Guid.NewGuid();
        state.Set(Preview(id));
        Assert.Null(state.TryTake(Guid.NewGuid()));
        // A preview for another item is dropped, not kept for later.
        Assert.Null(state.TryTake(id));
        state.Set(Preview(id));
        Assert.NotNull(state.TryTake(id));
        Assert.Null(state.TryTake(id));
    }

    [Fact]
    public void SkeletonWithAPreviewDrawsTheRealCoverAndTitleInsteadOfGreyBoxes()
    {
        var cut = Render<DetailHeroSkeleton>(p => p.Add(c => c.Preview, Preview(Guid.NewGuid())));
        Assert.Equal("Dune", cut.Find("h1.tl-detail-skeleton__heading").TextContent);
        Assert.NotEmpty(cut.FindAll("img.tl-detail-skeleton__cover"));
        Assert.Empty(cut.FindAll(".tl-detail-skeleton__artwork"));
        Assert.Empty(cut.FindAll(".tl-detail-skeleton__title"));
    }

    [Fact]
    public void SkeletonWithAPreviewLogoShowsTheLogoAndNoPlainHeading()
    {
        var cut = Render<DetailHeroSkeleton>(p => p.Add(c => c.Preview, Preview(Guid.NewGuid(), logo: "/logo.png")));
        Assert.Equal("Dune", cut.Find("img.tl-detail-skeleton__logo").GetAttribute("alt"));
        Assert.Empty(cut.FindAll("h1"));
    }

    [Fact]
    public void SkeletonWithoutAPreviewKeepsTheGreyStandIn()
    {
        var cut = Render<DetailHeroSkeleton>();
        Assert.NotEmpty(cut.FindAll(".tl-detail-skeleton__artwork"));
        Assert.Empty(cut.FindAll("img"));
    }
}
