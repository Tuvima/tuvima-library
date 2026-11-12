using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A store can answer a book's ISBN with the wrong book (Apple does for some Percy Jackson ISBNs).
/// The Library rejects that answer, then must search again by title and author, and must understand
/// that "Series, Book Two: Title" is the same book as "Title".
/// </summary>
public sealed class IsbnRejectedFallbackTests
{
    [Theory]
    [InlineData(MediaType.Books, "file-hints", true, "Rejected", true)]
    [InlineData(MediaType.Audiobooks, "file-hints", true, "Rejected", true)]
    public void RejectedIsbnAnswer_ForABook_RetriesByTitle(MediaType type, string source, bool hasIsbn, string outcome, bool expected) =>
        Assert.Equal(expected, RetailMatchWorker.ShouldRetryWithoutIsbn(type, source, hasIsbn, outcome));

    [Theory]
    [InlineData(MediaType.Books, "file-hints", false, "Rejected")]
    [InlineData(MediaType.Books, "file-hints", true, "AutoAccepted")]
    [InlineData(MediaType.Books, "file-hints", true, "Ambiguous")]
    [InlineData(MediaType.Books, "isbn-rejected-retry", true, "Rejected")]
    [InlineData(MediaType.Movies, "file-hints", true, "Rejected")]
    public void RetryDoesNotHappen_WhenThereIsNothingToRetryOrItAlreadyRetried(MediaType type, string source, bool hasIsbn, string outcome) =>
        Assert.False(RetailMatchWorker.ShouldRetryWithoutIsbn(type, source, hasIsbn, outcome));

    [Theory]
    [InlineData("Percy Jackson and the Olympians, Book Two: The Sea of Monsters", "The Sea of Monsters")]
    [InlineData("Percy Jackson and the Olympians, Book 5: The Last Olympian", "The Last Olympian")]
    [InlineData("The Sea of Monsters", "The Sea of Monsters")]
    [InlineData("Dune: Messiah", "Dune: Messiah")]
    public void SeriesAndBookNumberPrefix_IsDroppedFromTheTitle(string title, string expected) =>
        Assert.Equal(expected, RetailMatchScoringService.StripSeriesBookPrefix(title));
}
