using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.MediaTiles;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A collection's (and a Universe's) Items row on its details page sits in the shared shelf row, so every title can be
/// reached: wheel and touch swipe scroll it, and arrows appear when it overflows (spec details-and-tile-fixes D7).
/// </summary>
public sealed class CollectionItemsRowTests : AsyncBunitContext
{
    public CollectionItemsRowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
    }

    [Theory]
    [InlineData(DetailEntityType.Collection)]
    [InlineData(DetailEntityType.Universe)]
    public void TheItemsRow_IsTheSharedScrollingShelf_AndEveryItemIsInIt(DetailEntityType owner)
    {
        var cut = RenderRow(DetailPrimaryModuleKind.CollectionItems, owner, 14);

        Assert.Empty(cut.FindAll(".tl-detail-primary-module__rail"));
        cut.FindComponent<MediaShelfScroller>();
        Assert.Equal(14, cut.Find(".media-tile-shelf-scroll").QuerySelectorAll(".tl-detail-primary-module__item").Length);
        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "registerMediaShelfBoundaries");
        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "registerMediaTileShelfScrollGuard");
    }

    [Fact]
    public async Task TheItemsRow_ShowsNoArrowsUntilItOverflows_ThenOffersTheDirectionsThatExist()
    {
        var cut = RenderRow(DetailPrimaryModuleKind.CollectionItems, DetailEntityType.Collection, 14);
        var shelf = cut.FindComponent<MediaShelfScroller>();

        Assert.Empty(cut.FindAll(".media-tile-shelf-arrow"));

        // The browser reports the row is at its start with more to the right.
        await cut.InvokeAsync(() => shelf.Instance.OnScrollBoundaryChanged(true, false));
        Assert.Single(cut.FindAll(".media-tile-shelf-arrow"));

        // Part-way along, both directions are offered.
        await cut.InvokeAsync(() => shelf.Instance.OnScrollBoundaryChanged(false, false));
        Assert.Equal(2, cut.FindAll(".media-tile-shelf-arrow").Count);
    }

    [Fact]
    public void AnEmptyItemsRow_DrawsTheEmptyLine_NotAShelf()
    {
        var cut = RenderRow(DetailPrimaryModuleKind.CollectionItems, DetailEntityType.Collection, 0);

        Assert.Empty(cut.FindComponents<MediaShelfScroller>());
        Assert.Single(cut.FindAll(".tl-detail-primary-module__empty"));
    }

    [Fact]
    public void ThePersonWorksRow_KeepsItsOwnRail()
    {
        var cut = RenderRow(DetailPrimaryModuleKind.Works, DetailEntityType.Person, 4);

        Assert.Empty(cut.FindComponents<MediaShelfScroller>());
        Assert.Equal(4, cut.Find(".tl-detail-primary-module__rail").QuerySelectorAll(".tl-detail-primary-module__item").Length);
    }

    private IRenderedComponent<DetailPrimaryModule> RenderRow(DetailPrimaryModuleKind kind, DetailEntityType owner, int count)
    {
        var items = Enumerable.Range(1, count)
            .Select(number => new MediaGroupingItemViewModel
            {
                Id = Guid.NewGuid().ToString(),
                EntityType = DetailEntityType.Movie,
                Title = $"Movie {number}",
                Lane = "watch",
                IsOwned = true,
            })
            .ToList();
        var model = new DetailPageViewModel
        {
            Id = Guid.NewGuid().ToString(),
            EntityType = owner,
            Title = "A group",
            PrimaryModule = new DetailPrimaryModuleViewModel
            {
                Kind = kind,
                Title = kind == DetailPrimaryModuleKind.CollectionItems ? "Items" : "Works in Your Library",
                GroupKeys = ["items"],
            },
            MediaGroups = [new MediaGroupingViewModel { Key = "items", Title = "Items", Items = items, OwnedCount = count, TotalCount = count }],
        };

        return Render<DetailPrimaryModule>(p => p.Add(c => c.Model, model));
    }
}
