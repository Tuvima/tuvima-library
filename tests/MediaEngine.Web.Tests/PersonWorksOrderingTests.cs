using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using Xunit;

namespace MediaEngine.Web.Tests;

public sealed class PersonWorksOrderingTests
{
    private static MediaGroupingItemViewModel Work(string title, string? year = null, string? subtitle = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Title = title,
        Year = year,
        Subtitle = subtitle,
    };

    private static readonly MediaGroupingItemViewModel[] Works =
    [
        Work("The Hobbit", "1937"),
        Work("Dune", "1965"),
        Work("Arrival", "2016"),
        Work("Untitled"),
    ];

    private static string[] Titles(IEnumerable<MediaGroupingItemViewModel> items) => items.Select(i => i.Title).ToArray();

    [Fact]
    public void Newest_PutsLatestYearFirst_AndWorksWithoutAYearLast()
        => Assert.Equal(["Arrival", "Dune", "The Hobbit", "Untitled"], Titles(PersonWorksOrdering.Apply(Works, PersonWorksOrdering.Newest, null)));

    [Fact]
    public void Oldest_PutsEarliestYearFirst_AndWorksWithoutAYearLast()
        => Assert.Equal(["The Hobbit", "Dune", "Arrival", "Untitled"], Titles(PersonWorksOrdering.Apply(Works, PersonWorksOrdering.Oldest, null)));

    [Fact]
    public void TitleAz_IgnoresLeadingArticles()
        => Assert.Equal(["Arrival", "Dune", "The Hobbit", "Untitled"], Titles(PersonWorksOrdering.Apply(Works, PersonWorksOrdering.TitleAz, null)));

    [Fact]
    public void UnknownSort_FallsBackToNewest()
        => Assert.Equal(PersonWorksOrdering.Newest, PersonWorksOrdering.Normalize("sideways"));

    [Fact]
    public void Find_NarrowsByTitleOrSubtitle_IgnoringCase()
    {
        var items = new[] { Work("Heat", "1995", "Director · 1995"), Work("Ronin", "1998", "Actor as Sam") };

        Assert.Equal(["Heat"], Titles(PersonWorksOrdering.Apply(items, null, "hEaT")));
        Assert.Equal(["Ronin"], Titles(PersonWorksOrdering.Apply(items, null, "sam")));
        Assert.Equal(2, PersonWorksOrdering.Apply(items, null, "  ").Count);
    }
}
