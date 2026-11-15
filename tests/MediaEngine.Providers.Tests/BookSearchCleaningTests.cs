using MediaEngine.Domain;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Workers;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// Issue 1050: audiobooks whose tags carry a position inside the text ("3.5 - The Churn", series "The Lord of the Rings #0") stayed in
/// Needs Review. The search text is cleaned, and a book that is still not accepted gets one more search by title and author alone.
/// </summary>
public sealed class BookSearchCleaningTests
{
    private static Dictionary<string, string> Hints(string title, string? series = null, string? isbn = null)
    {
        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MetadataFieldConstants.Title] = title,
            [MetadataFieldConstants.Author] = "J.R.R. Tolkien",
        };
        if (series is not null)
        {
            hints[MetadataFieldConstants.Series] = series;
        }

        if (isbn is not null)
        {
            hints[BridgeIdKeys.Isbn] = isbn;
        }

        return hints;
    }

    [Fact]
    public void SeriesNumberSuffix_IsDroppedFromTheSearchSeries()
    {
        var search = RetailMatchWorker.BuildSearchHints(MediaType.Audiobooks, Hints("The Silmarillion", "The Lord of the Rings #0"));
        Assert.Equal("The Lord of the Rings", search[MetadataFieldConstants.Series]);
    }

    [Fact]
    public void LeadingNumber_IsDroppedFromTheSearchTitle()
    {
        var search = RetailMatchWorker.BuildSearchHints(MediaType.Audiobooks, Hints("3.5 - The Churn"));
        Assert.Equal("The Churn", search[MetadataFieldConstants.Title]);
    }

    [Theory]
    [InlineData("Drive")]
    [InlineData("1984")]
    [InlineData("12 Years a Slave")]
    public void TitleWithoutANumberedPrefix_IsSearchedAsItIs(string title) =>
        Assert.Equal(title, RetailMatchWorker.BuildSearchHints(MediaType.Books, Hints(title))[MetadataFieldConstants.Title]);

    [Fact]
    public void ANumberedTitle_OfAMovie_IsNotChanged() =>
        Assert.Equal("3.5 - The Churn", RetailMatchWorker.BuildSearchHints(MediaType.Movies, Hints("3.5 - The Churn"))[MetadataFieldConstants.Title]);

    [Theory]
    [InlineData("Ambiguous")]
    [InlineData("Rejected")]
    public void ANotAcceptedBook_GetsATitleAndAuthorOnlyRetry(string outcome)
    {
        var retry = RetailMatchWorker.BuildTitleAuthorRetryHints(
            MediaType.Audiobooks, "file-hints", Hints("The Silmarillion", "The Lord of the Rings", "9780000000001"), outcome);
        Assert.NotNull(retry);
        Assert.Equal(2, retry.Count); // title and author only: no series, no ISBN
        Assert.Equal("The Silmarillion", retry[MetadataFieldConstants.Title]);
        Assert.DoesNotContain(MetadataFieldConstants.Series, retry.Keys);
        Assert.DoesNotContain(BridgeIdKeys.Isbn, retry.Keys);
    }

    [Fact]
    public void NoRetry_WhenAcceptedAlreadyARetryOrNothingWouldChange()
    {
        var hints = Hints("The Silmarillion", "The Lord of the Rings");
        Assert.Null(RetailMatchWorker.BuildTitleAuthorRetryHints(MediaType.Books, "file-hints", hints, "AutoAccepted"));
        Assert.Null(RetailMatchWorker.BuildTitleAuthorRetryHints(MediaType.Books, "title-author-retry", hints, "Rejected"));
        Assert.Null(RetailMatchWorker.BuildTitleAuthorRetryHints(MediaType.Movies, "file-hints", hints, "Rejected"));
        Assert.Null(RetailMatchWorker.BuildTitleAuthorRetryHints(MediaType.Books, "file-hints", Hints("Drive"), "Rejected"));
    }
}
