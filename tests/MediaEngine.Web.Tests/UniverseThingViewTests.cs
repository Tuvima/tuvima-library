using Bunit;
using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Components.Universe;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>One thing of a Universe on its own page (spec universe-contents, section 4.2), and the tiles that open it.</summary>
public sealed class UniverseThingViewTests : AsyncBunitContext
{
    public UniverseThingViewTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
    }

    [Fact]
    public void ShowsThePictureNameKindAndWhereTheDescriptionCameFrom()
    {
        var cut = Render(new UniverseThingDto
        {
            Id = Guid.NewGuid(),
            UniverseQid = "Q1",
            UniverseLabel = "Dune universe",
            Label = "House Atreides",
            KindLabel = "Faction",
            KindKey = "factions",
            LogoUrl = "/stream/artwork/1?size=m",
            Description = "A noble house.",
            DescriptionSource = "wikidata",
        });

        Assert.Equal("House Atreides", cut.Find("h1").TextContent);
        Assert.Contains("Faction", cut.Find(".universe-thing__kind").TextContent);
        Assert.Equal("A noble house.", cut.Find("[data-thing-description]").TextContent);
        Assert.Equal("From Wikidata", cut.Find("[data-thing-source]").TextContent);
        // A logo is shown whole on a plain tile, not cropped.
        Assert.NotEmpty(cut.FindAll(".universe-thing__picture--logo"));
        Assert.Equal("Back to Dune universe", cut.Find("[data-thing-back]").TextContent);
        Assert.Equal("/universe/Q1", cut.Find("[data-thing-back]").GetAttribute("href"));
    }

    [Theory]
    [InlineData("fandom", "From Fandom")]
    [InlineData("you", "Written by you")]
    public void NamesFandomAndYourOwnWordingAsTheSource(string source, string expected)
    {
        var cut = Render(Thing(description: "Text.", source: source));

        Assert.Equal(expected, cut.Find("[data-thing-source]").TextContent);
    }

    [Fact]
    public void AThingTheLibraryCannotPlaceSaysItIsHidden_AndShowsNoDescriptionOrWorks()
    {
        var cut = Render(new UniverseThingDto
        {
            Id = Guid.NewGuid(),
            UniverseQid = "Q1",
            Label = "Cora",
            KindLabel = "Character",
            DetailsHidden = true,
            AppearsIn = [new UniverseThingWorkDto { WorkQid = "QW1", Label = "Should Not Show" }],
        });

        Assert.Equal("Hidden until you reach it", cut.Find("[data-details-hidden]").TextContent);
        Assert.Empty(cut.FindAll("[data-thing-description]"));
        Assert.Empty(cut.FindAll("[data-thing-aliases]"));
    }

    [Fact]
    public void AThingWithNoDescription_SaysSoQuietly_AndAHandAddedOneIsMarked()
    {
        var cut = Render(new UniverseThingDto
        {
            Id = Guid.NewGuid(),
            UniverseQid = "Q1",
            Label = "Spacing Guild",
            KindLabel = "Faction",
            AddedByUser = true,
            Aliases = ["The Guild"],
        });

        Assert.Equal("Nothing is known about this yet.", cut.Find("[data-thing-no-description]").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-added-by-you]"));
        Assert.Equal("Also known as The Guild", cut.Find("[data-thing-aliases]").TextContent);
    }

    [Fact]
    public void ListsTheWorksItAppearsIn_AndEveryConnectedGroupWithLinksToThoseThings()
    {
        var friend = Guid.NewGuid();
        var cut = Render(new UniverseThingDto
        {
            Id = Guid.NewGuid(),
            UniverseQid = "Q1",
            Label = "Cora",
            KindLabel = "Character",
            AppearsIn = [new UniverseThingWorkDto { WorkQid = "QW1", Label = "Test Book", Role = "Protagonist" }],
            Related =
            [
                new UniverseThingRelatedGroupDto
                {
                    Key = "member_of",
                    Label = "Member of",
                    Count = 1,
                    Things = [new UniverseThingLinkDto { Id = friend, Label = "House Test", KindKey = "factions" }],
                },
            ],
        });

        Assert.Contains("Test Book", cut.Find("[data-thing-appears-in]").TextContent);
        Assert.Contains("Protagonist", cut.Find("[data-thing-appears-in]").TextContent);
        var link = cut.Find("[data-thing-related='member_of'] a");
        Assert.Equal($"/universe/Q1/things/{friend:D}", link.GetAttribute("href"));
        Assert.Contains("House Test", link.TextContent);
    }

    [Fact]
    public void EditAppearsOnlyWhenThePageSaysThePersonMayEdit()
    {
        var thing = Thing(description: "Text.", source: "wikidata");

        Assert.Empty(Render(thing, canEdit: false).FindAll(".universe-thing__actions"));
        Assert.NotEmpty(Render(thing, canEdit: true).FindAll(".universe-thing__actions"));
    }

    [Fact]
    public void RailTilesAndTimelineStopsOpenTheThingsOwnPage_WhenTheUniverseIsKnown()
    {
        var id = Guid.NewGuid();
        var stop = Guid.NewGuid();
        var contents = new UniverseContentsDto
        {
            Sections = [new UniverseContentsSectionDto
            {
                Key = "factions",
                Label = "Factions",
                Count = 1,
                Tiles = [new UniverseContentsTileDto { Id = id, Label = "House Test" }],
            }],
            Timeline = new UniverseTimelineDto
            {
                DefaultOrder = UniverseTimelineDto.ReleaseOrder,
                Stops = [new UniverseTimelineStopDto { Id = stop, Label = "The Battle", Kind = "events", ReleaseYear = 2001 }],
            },
        };

        var linked = Render<UniverseContents>(parameters => parameters
            .Add(p => p.Contents, contents)
            .Add(p => p.UniverseQid, "Q1"));
        var plain = Render<UniverseContents>(parameters => parameters.Add(p => p.Contents, contents));

        Assert.Equal(
            [$"/universe/Q1/things/{id:D}", $"/universe/Q1/things/{stop:D}"],
            linked.FindAll("[data-thing-link]").Select(link => link.GetAttribute("href")));
        Assert.All(plain.FindAll("[data-thing-link]"), link => Assert.Null(link.GetAttribute("href")));
    }

    private IRenderedComponent<UniverseThingView> Render(UniverseThingDto thing, bool canEdit = false) =>
        Render<UniverseThingView>(parameters => parameters
            .Add(p => p.Thing, thing)
            .Add(p => p.CanEdit, canEdit));

    private static UniverseThingDto Thing(string description, string source) => new()
    {
        Id = Guid.NewGuid(),
        UniverseQid = "Q1",
        Label = "Cora",
        KindLabel = "Character",
        Description = description,
        DescriptionSource = source,
    };
}
