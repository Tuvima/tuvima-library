using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Collections;

namespace MediaEngine.Web.Tests;

public sealed class AutomaticCollectionMembershipTests : BunitContext
{
    [Fact]
    public void Membership_GroupsOwnedItemsByLaneAndUsesBoundedImages()
    {
        var book = Item(DetailEntityType.Book, "Book");
        var movie = Item(DetailEntityType.Movie, "Movie");
        var album = Item(DetailEntityType.MusicAlbum, "Album");
        var cut = Render<AutomaticCollectionMembership>(parameters => parameters.Add(component => component.Model,
            new DetailPageViewModel
            {
                MediaGroups = [new MediaGroupingViewModel { Key = "items", Items = [book, movie, album, book,
                    new MediaGroupingItemViewModel { Id = Guid.NewGuid().ToString(), Title = "Missing", IsOwned = false }] },
                    new MediaGroupingViewModel { Key = "related", Items = [Item(DetailEntityType.Book, "Recommendation")] }],
            }));
        Assert.Equal(new[] { "Read membership", "Watch membership", "Listen membership" },
            cut.FindAll("section").Select(section => section.GetAttribute("aria-label")));
        Assert.Equal(3, cut.FindAll("a").Count);
        Assert.DoesNotContain("Missing", cut.Markup);
        Assert.DoesNotContain("Recommendation", cut.Markup);
        Assert.All(cut.FindAll("img"), image =>
        {
            Assert.EndsWith("size=s", image.GetAttribute("src"));
            Assert.Contains("320w", image.GetAttribute("srcset"));
            Assert.Equal("(max-width: 600px) 96px, 112px", image.GetAttribute("sizes"));
        });
        Assert.Single(cut.FindAll(".is-square"));
    }

    [Fact]
    public void Membership_UsesOwnedSequenceItemsWhenGroupsAreAbsent()
    {
        var cut = Render<AutomaticCollectionMembership>(parameters => parameters.Add(component => component.Model,
            new DetailPageViewModel
            {
                SequencePlacement = new SequencePlacementViewModel { OrderedItems = [
                    new SequenceItemViewModel { Id = Guid.NewGuid().ToString(), Title = "Film", EntityType = DetailEntityType.Movie, IsOwned = true },
                    new SequenceItemViewModel { Title = "Missing film", EntityType = DetailEntityType.Movie, IsOwned = false } ] },
            }));
        Assert.Equal("Watch membership", Assert.Single(cut.FindAll("section")).GetAttribute("aria-label"));
        Assert.Single(cut.FindAll("a"));
        Assert.DoesNotContain("Missing film", cut.Markup);
    }

    [Fact]
    public void Membership_LegacyArtworkUsesBoundedWorkCoverEndpoint()
    {
        var id = Guid.NewGuid();
        var cut = Render<AutomaticCollectionMembership>(parameters => parameters.Add(component => component.Model,
            new DetailPageViewModel { MediaGroups = [new MediaGroupingViewModel { Items = [
                new MediaGroupingItemViewModel { Id = id.ToString(), EntityType = DetailEntityType.Book,
                    Title = "Book", ArtworkUrl = $"/engine-image/stream/{Guid.NewGuid():D}/cover" } ] }] }));
        Assert.Equal($"/engine-image/stream/entity/work/{id:D}/cover?size=s", cut.Find("img").GetAttribute("src"));
    }

    private static MediaGroupingItemViewModel Item(DetailEntityType type, string title) => new()
    {
        Id = Guid.NewGuid().ToString(), EntityType = type, Title = title,
        ArtworkUrl = $"/engine-image/stream/artwork/{Guid.NewGuid():D}",
    };
}
