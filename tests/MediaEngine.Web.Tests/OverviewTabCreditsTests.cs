using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Overview's two columns and the Credits tab (details-page-bottom D1 to D3): description with Released and Language on the left, "Cast and crew" on
/// the right with the director first, one tile for every person, and "No credits yet" with the left column spanning when there are none.
/// </summary>
public sealed class OverviewTabCreditsTests : AsyncBunitContext
{
    private static EntityCreditViewModel Person(string name, string role, string? character = null, int order = 0) => new()
    {
        EntityId = Guid.NewGuid().ToString("D"),
        EntityType = RelatedEntityType.Person,
        DisplayName = name,
        PrimaryRole = role,
        CharacterName = character,
        SortOrder = order,
    };

    private static DetailPageViewModel Movie(IReadOnlyList<CreditGroupViewModel> groups) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        EntityType = DetailEntityType.Movie,
        Title = "I Am Number Four",
        Description = "A teenage alien hiding on Earth must stay ahead of the hunters.",
        Facts = new DetailFactsViewModel { Year = "2011", Language = "English", ContentRating = "PG-13" },
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }, new DetailTab { Key = "credits", Label = "Credits" }],
        ContributorGroups = groups,
    };

    private static IReadOnlyList<CreditGroupViewModel> DirectorAndNineCast() =>
    [
        new CreditGroupViewModel
        {
            Title = "Director",
            GroupType = CreditGroupType.Directors,
            Credits = [Person("D. J. Caruso", "Director")],
        },
        new CreditGroupViewModel
        {
            Title = "Cast",
            GroupType = CreditGroupType.Cast,
            Credits = [.. Enumerable.Range(1, 9).Select(number => Person($"Actor {number}", "Actor", $"Character {number}", number))],
        },
    ];

    private IRenderedComponent<OverviewTab> RenderOverview(DetailPageViewModel model) =>
        Render<OverviewTab>(parameters => parameters.Add(component => component.Model, model));

    [Fact]
    public void TheRightColumn_IsCastAndCrew_WithTheDirectorFirstThenAGridOfPeople_AndViewFullCredits()
    {
        var requested = new List<string>();
        var cut = Render<OverviewTab>(parameters => parameters
            .Add(component => component.Model, Movie(DirectorAndNineCast()))
            .Add(component => component.OnTabRequested, EventCallback.Factory.Create<string>(this, tab => requested.Add(tab))));

        Assert.Equal("Cast and crew", cut.Find("[data-testid=overview-credits] h2").TextContent.Trim());
        Assert.Equal("D. J. Caruso", cut.Find(".tl-overview-credit-lead .tl-credit-card__name").TextContent.Trim());
        var grid = cut.FindAll(".tl-overview-credit-rows .tl-credit-card");
        Assert.Equal(8, grid.Count);
        Assert.Equal("Actor 1", grid[0].QuerySelector(".tl-credit-card__name")!.TextContent.Trim());
        Assert.Equal("Character 1", grid[0].QuerySelector(".tl-credit-card__role")!.TextContent.Trim());

        cut.Find("[data-testid=overview-view-full-credits]").Click();
        Assert.Equal(["credits"], requested);
        Assert.Equal("View full credits", cut.Find("[data-testid=overview-view-full-credits]").TextContent.Trim());
    }

    [Fact]
    public void TheLeftColumn_ShowsReleasedLanguageAndTheKindsOwnFact()
    {
        var cut = RenderOverview(Movie(DirectorAndNineCast()));

        var facts = cut.FindAll("[data-testid=overview-facts] > div")
            .Select(row => (row.QuerySelector("dt")!.TextContent.Trim(), row.QuerySelector("dd")!.TextContent.Trim()))
            .ToArray();
        Assert.Equal([("Released", "2011"), ("Language", "English"), ("Rated", "PG-13")], facts);
    }

    [Fact]
    public void WithoutCredits_SaysNoCreditsYet_AndTheDescriptionSpansTheWidth()
    {
        var cut = RenderOverview(Movie([]));

        Assert.Contains("tl-overview-surface--no-credits", cut.Find(".tl-overview-surface").ClassList);
        Assert.Equal("No credits yet", cut.Find("[data-testid=overview-credits] .tl-overview-section__empty").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=overview-view-full-credits]"));
    }

    [Fact]
    public void EveryPersonOnTheOverview_IsTheSameTile_WithNoSizeVariant()
    {
        var cut = RenderOverview(Movie(DirectorAndNineCast()));

        var tiles = cut.FindAll("[data-testid=overview-credits] .tl-credit-card");
        Assert.Equal(9, tiles.Count);
        Assert.All(tiles, tile => Assert.DoesNotContain("tl-credit-card--compact", tile.ClassList));
    }
}
