using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;

namespace MediaEngine.Web.Tests;

/// <summary>About this on a Series: owned of total, first and latest year, author; nothing invented.</summary>
public sealed class DetailFileFactsSeriesTests
{
    private static SequenceItemViewModel Item(string title, string? date, bool owned) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Title = title,
        PublicationDate = date,
        IsOwned = owned,
    };

    private static IReadOnlyList<DetailFileFactRow> Build(DetailPageViewModel model) =>
        DetailFileFacts.BuildSeriesFacts(model, "In your library", "First published", "Latest", "Author", "{0} of {1} {2}");

    [Fact]
    public void SeriesWithMissingBooks_ShowsOwnedOfTotalYearsAndAuthor()
    {
        var model = new DetailPageViewModel
        {
            EntityType = DetailEntityType.BookSeries,
            SequencePlacement = new SequencePlacementViewModel
            {
                ItemPluralLabel = "Books",
                TotalKnownItems = 3,
                OrderedItems =
                [
                    Item("One", "2011-06-15", true),
                    Item("Two", "2012", true),
                    Item("Three", "2021-12-01", false),
                ],
            },
            ContributorGroups =
            [
                new CreditGroupViewModel
                {
                    GroupType = CreditGroupType.Authors,
                    Credits = [new EntityCreditViewModel { DisplayName = "James S. A. Corey" }],
                },
            ],
        };

        var rows = Build(model);

        Assert.Equal("2 of 3 books", rows.Single(row => row.Label == "In your library").Value);
        Assert.Equal("2011", rows.Single(row => row.Label == "First published").Value);
        Assert.Equal("2021", rows.Single(row => row.Label == "Latest").Value);
        Assert.Equal("James S. A. Corey", rows.Single(row => row.Label == "Author").Value);
    }

    [Fact]
    public void SeriesWithNothingKnown_ShowsNoRows()
    {
        var rows = Build(new DetailPageViewModel { EntityType = DetailEntityType.ComicSeries });

        Assert.Empty(rows);
    }

    [Fact]
    public void SeriesWithAllBooksOwned_ShowsPlainCount()
    {
        var model = new DetailPageViewModel
        {
            EntityType = DetailEntityType.MovieSeries,
            SequencePlacement = new SequencePlacementViewModel
            {
                ItemPluralLabel = "Films",
                OrderedItems = [Item("A", "2001", true), Item("B", "2001", true)],
            },
        };

        var rows = Build(model);

        Assert.Equal("2 films", rows.Single(row => row.Label == "In your library").Value);
        Assert.DoesNotContain(rows, row => row.Label == "Latest");
    }
}
