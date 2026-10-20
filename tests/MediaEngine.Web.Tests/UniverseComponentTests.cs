using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Universe;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The pieces of the Universe page on their own, and the "Part of" line on an item page.</summary>
public sealed class UniverseComponentTests : AsyncBunitContext
{
    public UniverseComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.CreateDefault());
    }

    // The item page's "Part of" line -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Dune", "Part of the Dune Universe")]
    [InlineData("Dune Universe", "Part of the Dune Universe")]
    [InlineData("The Expanse universe", "Part of the The Expanse universe")]
    [InlineData("  Discworld  ", "Part of the Discworld Universe")]
    public void ThePartOfLine_NeverSaysUniverseTwice(string name, string expected) =>
        Assert.Equal(expected, UniversePageText.PartOfLine(name));

    [Fact]
    public void AnItemPageWithAUniverseLink_ShowsOneQuietLine_ThatOpensTheUniversePage()
    {
        var cut = RenderHero(new UniverseLinkViewModel { Qid = "Q4242", Name = "Dune" });

        var link = Assert.Single(cut.FindAll(".tl-detail-hero__universe-link a"));
        Assert.Equal("Part of the Dune Universe", link.TextContent);
        Assert.Equal("/universe/Q4242", link.GetAttribute("href"));
        // A line under the title block, not a tab or a button row.
        Assert.Empty(cut.FindAll(".tl-detail-hero__universe-link button"));
    }

    [Fact]
    public void AnItemPageWithoutAUniverseLink_ShowsNoLine() =>
        Assert.Empty(RenderHero(null).FindAll(".tl-detail-hero__universe-link"));

    // A Series shelf and its missing entries ------------------------------------------------------------------------------

    [Fact]
    public void MissingEntries_AreDrawnInPlace_WhenThePersonChoseToSeeThem()
    {
        var (shelf, works) = Shelf("One", "Two", "Three");
        var missing = new UniverseShelfMissingDto
        {
            CollectionId = shelf.CollectionId,
            ShowMissing = true,
            MissingCount = 2,
            Items =
            [
                new UniverseMissingItemDto { Label = "Prologue", RawOrdinal = "0", ParsedOrdinal = 0, ItemQid = "Q1" },
                new UniverseMissingItemDto { Label = "Between", RawOrdinal = "1.5", ParsedOrdinal = 1.5, ItemQid = "Q2", AfterWorkId = works[0] },
            ],
        };

        var cut = Render<UniverseSeriesShelf>(parameters => parameters
            .Add(p => p.Shelf, shelf)
            .Add(p => p.Missing, missing));

        // In order: the entry before everything, the first work, the entry that follows it, then the rest.
        var row = cut.FindAll(".universe-missing, .media-tile");
        Assert.Equal(5, row.Count);
        Assert.Contains("universe-missing", row[0].ClassName);
        Assert.Contains("media-tile", row[1].ClassName);
        Assert.Contains("universe-missing", row[2].ClassName);
        Assert.Equal(2, cut.FindAll(".universe-missing").Count);
        Assert.Equal(["Prologue", "Between"], cut.FindAll(".universe-missing__title").Select(title => title.TextContent).ToArray());
        // The number is shown as the source gives it, decimals kept.
        Assert.Equal(["0", "1.5"], cut.FindAll(".universe-missing__ordinal").Select(ordinal => ordinal.TextContent).ToArray());
        Assert.All(cut.FindAll(".universe-missing__art"), art => Assert.Contains("Not in library", art.TextContent));
        Assert.Equal("Prologue, number 0, not in library", cut.FindAll(".universe-missing")[0].GetAttribute("aria-label"));
        Assert.DoesNotContain("missing", cut.Find(".universe-series__summary").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingEntries_AreNotDrawn_WhenThePersonHidesThem_ButTheCountStaysQuietly()
    {
        var (shelf, _) = Shelf("One", "Two", "Three");
        var hidden = new UniverseShelfMissingDto { CollectionId = shelf.CollectionId, ShowMissing = false, MissingCount = 2, Items = [] };

        var cut = Render<UniverseSeriesShelf>(parameters => parameters
            .Add(p => p.Shelf, shelf)
            .Add(p => p.Missing, hidden));

        Assert.Empty(cut.FindAll(".universe-missing"));
        Assert.Equal(3, cut.FindAll(".media-tile").Count);
        Assert.Equal("3 items · 2 missing", cut.Find(".universe-series__summary").TextContent);
    }

    [Fact]
    public void ASeriesWithNothingMissing_SaysNothingAboutIt()
    {
        var (shelf, _) = Shelf("One", "Two");

        var cut = Render<UniverseSeriesShelf>(parameters => parameters.Add(p => p.Shelf, shelf));

        Assert.Equal("2 items", cut.Find(".universe-series__summary").TextContent);
        Assert.Empty(cut.FindAll(".universe-missing"));
    }

    [Fact]
    public void AlsoInThisWorld_IsItsOwnRowUnderTheSeries()
    {
        var (shelf, _) = Shelf("One");
        var withAlso = new UniverseMemberShelfDto
        {
            CollectionId = shelf.CollectionId,
            GroupType = shelf.GroupType,
            ItemCount = shelf.ItemCount,
            Shelf = shelf.Shelf,
            AlsoInThisWorldCount = 1,
            AlsoInThisWorld = new DisplayShelfDto("also", "Also in this world", "1 item", [UniversePageTests.Card("Side Story", "Book")], null),
        };

        var cut = Render<UniverseSeriesShelf>(parameters => parameters.Add(p => p.Shelf, withAlso));

        Assert.Contains("Also in this world", cut.Markup);
        Assert.Contains("Side Story", cut.Markup);
    }

    // The cast list -------------------------------------------------------------------------------------------------

    [Fact]
    public void CastRows_LinkToThePersonPage_WhenNoHostListens()
    {
        var person = Guid.NewGuid();

        var cut = Render<UniverseCastList>(parameters => parameters.Add(p => p.Cast, [CastMember(person, "Ann", "Hero", "Villain")]));

        var row = cut.Find("a.universe-cast__row");
        Assert.Equal($"/details/person/{person:D}", row.GetAttribute("href"));
        Assert.Equal("as Hero, Villain", cut.Find(".universe-cast__characters").TextContent);
        Assert.Empty(cut.FindAll("button.universe-cast__row"));
    }

    [Fact]
    public void CastRows_RaiseTheCallback_WhenAHostListens()
    {
        var person = Guid.NewGuid();
        UniverseClientCastMemberDto? chosen = null;

        var cut = Render<UniverseCastList>(parameters => parameters
            .Add(p => p.Cast, [CastMember(person, "Ann", "Hero")])
            .Add(p => p.OnCastMemberSelected, member => chosen = member));
        cut.Find("button.universe-cast__row").Click();

        Assert.Empty(cut.FindAll("a.universe-cast__row"));
        Assert.Equal(person, chosen!.PersonId);
        Assert.Equal("Hero", Assert.Single(chosen.Characters).Label);
    }

    [Fact]
    public void ACastRowWithoutACharacter_ShowsOnlyTheName()
    {
        var cut = Render<UniverseCastList>(parameters => parameters.Add(p => p.Cast, [CastMember(Guid.NewGuid(), "Ann")]));

        Assert.Equal("Ann", cut.Find(".universe-cast__name").TextContent);
        Assert.Empty(cut.FindAll(".universe-cast__characters"));
    }

    // Helpers -------------------------------------------------------------------------------------------------------

    private IRenderedComponent<DetailHero> RenderHero(UniverseLinkViewModel? link)
    {
        var model = new DetailPageViewModel
        {
            Id = Guid.NewGuid().ToString("D"),
            EntityType = DetailEntityType.Movie,
            PresentationContext = DetailPresentationContext.Watch,
            Title = "Dune: Part One",
            UniverseLink = link,
        };
        return Render<DetailHero>(parameters => parameters.Add(p => p.Model, model));
    }

    private static (UniverseMemberShelfDto Shelf, IReadOnlyList<Guid> WorkIds) Shelf(params string[] titles)
    {
        var cards = titles.Select(title => UniversePageTests.Card(title, "Book")).ToList();
        var shelf = new UniverseMemberShelfDto
        {
            CollectionId = Guid.NewGuid(),
            GroupType = "series",
            ItemCount = cards.Count,
            Shelf = new DisplayShelfDto(Guid.NewGuid().ToString("D"), "The Novels", null, cards, null),
        };
        return (shelf, cards.Select(card => card.WorkId!.Value).ToList());
    }

    private static UniverseClientCastMemberDto CastMember(Guid person, string name, params string[] characters) => new()
    {
        PersonId = person,
        Name = name,
        WorkCount = 1,
        Characters = characters.Select((label, index) => new UniverseClientCastCharacterDto { Qid = $"Q{index}", Label = label }).ToList(),
    };
}
