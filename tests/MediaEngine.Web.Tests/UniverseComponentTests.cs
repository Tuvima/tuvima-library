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

    // The item page's Universe icon ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Dune", "Dune Universe")]
    [InlineData("Dune Universe", "Dune Universe")]
    [InlineData("The Expanse universe", "Expanse universe")]
    [InlineData("The Arrakis Saga", "Arrakis Saga Universe")]
    [InlineData("Theseus", "Theseus Universe")]
    [InlineData("  Discworld  ", "Discworld Universe")]
    public void TheUniverseName_NeverSaysUniverseTwice(string name, string expected) =>
        Assert.Equal(expected, UniversePageText.UniverseName(name));

    [Theory]
    [InlineData(3, 14, "3 Series · 14 Works in your Library")]
    [InlineData(1, 1, "1 Series · 1 Work in your Library")]
    [InlineData(0, 2, "2 Works in your Library")]
    public void TheIconCard_NamesWhatTheLibraryHolds(int series, int works, string expected) =>
        Assert.Equal(expected, UniversePageText.CardCounts(series, works));

    [Fact]
    public void AnItemPageWithAUniverseLink_ShowsTheIcon_ThatOpensTheUniversePage()
    {
        var cut = RenderHero(new UniverseLinkViewModel { Qid = "Q4242", Name = "Dune", SeriesCount = 2, WorkCount = 7 });

        var link = Assert.Single(cut.FindAll(".tl-universe-icon__button"));
        Assert.Equal("/universe/Q4242", link.GetAttribute("href"));
        Assert.Equal("Open the Dune Universe", link.GetAttribute("aria-label"));
        Assert.Contains("2 Series · 7 Works in your Library", cut.Find(".tl-universe-icon__card").TextContent);
        // The icon replaces the old quiet text line.
        Assert.Empty(cut.FindAll(".tl-detail-hero__universe-link"));
    }

    [Fact]
    public void AnItemPageWithoutAUniverseLink_ShowsNoIcon() =>
        Assert.Empty(RenderHero(null).FindAll(".tl-universe-icon"));

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
    public void AManifestThatRepeatsAnEntry_StillDrawsEachPlaceholder_WithoutClashingKeys()
    {
        var (shelf, works) = Shelf("One", "Two");
        var missing = new UniverseShelfMissingDto
        {
            CollectionId = shelf.CollectionId,
            ShowMissing = true,
            MissingCount = 3,
            Items =
            [
                new UniverseMissingItemDto { Label = "Same", RawOrdinal = "2", ItemQid = "Q7", AfterWorkId = works[0] },
                new UniverseMissingItemDto { Label = "Same", RawOrdinal = "2", ItemQid = "Q7", AfterWorkId = works[0] },
                new UniverseMissingItemDto { Label = "Same", RawOrdinal = "2", ItemQid = "Q7", AfterWorkId = works[1] },
            ],
        };

        var cut = Render<UniverseSeriesShelf>(parameters => parameters
            .Add(p => p.Shelf, shelf)
            .Add(p => p.Missing, missing));

        Assert.Equal(3, cut.FindAll(".universe-missing").Count);
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

    [Fact]
    public void TheSeriesPanelAndTheUniversePageReadTheMissingItemsRuleFromTheSamePlace()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var panel = File.ReadAllText(Path.Combine(repo, "src", "MediaEngine.Web", "Components", "Details", "SequencePlacementPanel.razor"));
        var page = File.ReadAllText(Path.Combine(repo, "src", "MediaEngine.Api", "Services", "ReadServices", "UniversePageReadService.cs"));

        Assert.Contains("SeriesMissingItemVisibility.PolicyKey(", panel, StringComparison.Ordinal);
        Assert.Contains("SeriesMissingItemVisibility.IsDefaultShown(", panel, StringComparison.Ordinal);
        Assert.Contains("SeriesMissingItemVisibility.Resolve(", page, StringComparison.Ordinal);
        Assert.Contains("SeriesMissingItemVisibility.OverrideKeys(", page, StringComparison.Ordinal);
        // Neither keeps its own copy of the "shown" default.
        Assert.DoesNotContain("\"shown\"", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("\"shown\"", page, StringComparison.Ordinal);
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
        Assert.Equal("Source: Wikidata", cut.Find(".universe-cast__source").TextContent);
    }

    [Fact]
    public void CastRows_RaiseTheCallback_WhenAHostListens()
    {
        var person = Guid.NewGuid();
        UniverseClientCastMemberDto? chosen = null;

        var cut = Render<UniverseCastList>(parameters => parameters
            .Add(p => p.Cast, [CastMember(person, "Ann", "Hero")])
            .Add(p => p.OnCastMemberSelected, member => chosen = member));
        var button = cut.Find("button.universe-cast__row");
        // The scoped styles of this component cannot reach the button inside AppNativeButton, so the button takes its
        // padding, hover and focus ring from the shared list-row class, and the frame is the list item's.
        Assert.Contains("tl-list-item-button", button.ClassName);
        Assert.Contains("universe-cast__item", button.ParentElement!.ClassName);
        button.Click();

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

    // The adaptations list ------------------------------------------------------------------------------------------

    [Fact]
    public void AdaptationRows_ShowTheirSourceAndTheMediaTypeTag()
    {
        var row = new UniverseSourcedAdaptationDto
        {
            Adaptation = new UniverseClientAdaptationDto { Qid = "a", Label = "Dune: Part One", Relationship = "based on", SourceQid = "Q9", SourceLabel = "Dune", MediaType = "Movie" },
            Sources = [new UniverseSourceDto { Label = "Wikidata" }],
        };

        var cut = Render<UniverseAdaptationsList>(parameters => parameters.Add(p => p.Adaptations, [row]));

        Assert.Equal("Dune: Part One — Based on Dune", cut.Find(".universe-adaptations__line").TextContent);
        Assert.Equal("Source: Wikidata", cut.Find(".universe-adaptations__source").TextContent);
        Assert.Equal("Movie", cut.Find(".universe-adaptations__kind").TextContent);
    }

    [Fact]
    public void AWorkAdaptedFromTwoSources_IsTwoRows_AndRendersAgainWithoutAKeyClash()
    {
        UniverseSourcedAdaptationDto Row(string relationship, string sourceQid, string sourceLabel) => new()
        {
            Adaptation = new UniverseClientAdaptationDto { Qid = "QM2", Label = "Dune: Part Two", Relationship = relationship, SourceQid = sourceQid, SourceLabel = sourceLabel },
            Sources = [new UniverseSourceDto { Label = "Wikidata" }],
        };
        IReadOnlyList<UniverseSourcedAdaptationDto> rows = [Row("based on", "QB1", "Dune"), Row("inspired by", "QB2", "Dune Messiah")];

        var cut = Render<UniverseAdaptationsList>(parameters => parameters.Add(p => p.Adaptations, rows));
        // A second render diffs the keyed rows; a key shared by both rows would throw here.
        cut.Render(parameters => parameters.Add(p => p.Adaptations, rows.Reverse().ToList()));

        Assert.Equal(
            ["Dune: Part Two — Inspired by Dune Messiah", "Dune: Part Two — Based on Dune"],
            cut.FindAll(".universe-adaptations__line").Select(line => line.TextContent));
    }

    // The source and works lines --------------------------------------------------------------------------------------

    [Fact]
    public void TheSourcesLine_NamesOneOrSeveralSourcesInOrder_AndIsNullWhenThereAreNone()
    {
        Assert.Equal("Source: Wikidata", UniversePageText.SourcesLine([new UniverseSourceDto { Label = "Wikidata" }]));
        Assert.Equal(
            "Sources: Wikidata, Dune Wiki",
            UniversePageText.SourcesLine([new UniverseSourceDto { Label = "Wikidata" }, new UniverseSourceDto { Label = "Dune Wiki" }]));
        Assert.Null(UniversePageText.SourcesLine([]));
    }

    // The explorer's adaptation tree --------------------------------------------------------------------------------

    [Fact]
    public void ANodeWithASource_NamesItAfterTheRelationship_AndARootAddsNothing()
    {
        var child = new UniverseAdaptationNodeDto("Q2", "Dune: Part One", 2021, "Movie", null, "based on", [], "Wikidata");
        var root = new UniverseAdaptationNodeDto("Q1", "Dune", 1965, "Book", null, null, [child]);

        var cut = Render<AdaptationTreeNode>(parameters => parameters.Add(p => p.Node, root));

        var text = cut.Markup;
        Assert.Contains("(based on · Wikidata)", Normalise(text));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Normalise(text), @"\(based on"));
        Assert.DoesNotContain("()", Normalise(text));
    }

    [Fact]
    public void AChildWithoutASource_ShowsOnlyTheRelationship()
    {
        var child = new UniverseAdaptationNodeDto("Q2", "Dune: Part One", 2021, "Movie", null, "based on", []);

        var cut = Render<AdaptationTreeNode>(parameters => parameters.Add(p => p.Node, child));

        Assert.Contains("(based on)", Normalise(cut.Markup));
    }

    [Fact]
    public void TheAdaptationTree_SaysHonestlyThatNothingIsRecorded_WhenTheWorksListIsEmpty()
    {
        Services.AddSingleton(EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetUniverseAdaptationsAsync),
            _ => Task.FromResult<UniverseAdaptationTreeResponse?>(new UniverseAdaptationTreeResponse("Q1", [])))));

        var cut = Render<AdaptationTree>(parameters => parameters.Add(p => p.UniverseQid, "Q1"));

        cut.WaitForAssertion(() => Assert.Contains(
            "No adaptation relationships recorded yet. Works appear here when Wikidata links them as \"based on\", \"derivative work\" or \"inspired by\".",
            Normalise(cut.Markup)));
        Assert.Empty(cut.FindAll(".adaptation-tree"));
    }

    [Fact]
    public void TheConnections_NoLongerListCharacters_BecauseTheCharactersRailOwnsThem()
    {
        var page = new UniversePageDto
        {
            Detail = new UniverseClientDetailDto { Qid = "Q1", Name = "Test World" },
            Connections = new UniverseConnectionsDto
            {
                Characters = [Character("Q2", "Paul", 2)],
                CharacterCount = 1,
                HiddenCharacterCount = 2,
                Cast = [],
            },
            Enrichment = new UniverseEnrichmentStateDto { State = UniverseEnrichmentStateDto.Ready },
        };

        var cut = Render<UniverseConnections>(parameters => parameters.Add(p => p.Page, page));

        Assert.Empty(cut.FindAll("[data-hidden-characters]"));
        Assert.DoesNotContain("Paul", cut.Markup);
        Assert.Contains("No cast or adaptations recorded", cut.Markup);
    }

    // Helpers -------------------------------------------------------------------------------------------------------

    private static string Normalise(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(System.Net.WebUtility.HtmlDecode(markup), "<[^>]+>", " "), @"\s+", " ").Replace("( ", "(").Replace(" )", ")");

    private static UniverseCharacterDto Character(string qid, string label, int workCount, bool noPlotDetails = false) => new()
    {
        Qid = qid,
        Label = label,
        WorkCount = workCount,
        NoPlotDetails = noPlotDetails,
        Sources = [new UniverseSourceDto { Kind = UniverseSourceDto.Wikidata, Label = "Wikidata" }],
    };

    // Component test helpers -------------------------------------------------------------------------------------------------------

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

    private static UniverseSourcedCastMemberDto CastMember(Guid person, string name, params string[] characters) => new()
    {
        Member = new UniverseClientCastMemberDto
        {
            PersonId = person,
            Name = name,
            WorkCount = 1,
            Characters = characters.Select((label, index) => new UniverseClientCastCharacterDto { Qid = $"Q{index}", Label = label }).ToList(),
        },
        Sources = [new UniverseSourceDto { Kind = UniverseSourceDto.Wikidata, Label = "Wikidata" }],
    };
}
