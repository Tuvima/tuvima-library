using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

// Bookmark packs (Videos spec D46): a pack is for one title, recognised by canonical IDs plus running time and edition. A different
// cut is reported, never silently accepted, and can be aligned by chapter name.
public sealed class BookmarkPackMatcherTests
{
    private static BookmarkPackTitle Title(string imdb, double? runningTime = 7200, string? edition = null) =>
        new(new Dictionary<string, string> { ["imdb_id"] = imdb }, runningTime, edition);

    [Fact]
    public void Compare_SameIdAndRunningTime_IsAMatch()
    {
        var result = BookmarkPackMatcher.Compare(Title("tt0120737", 7200), Title("TT0120737", 7201));

        Assert.Equal(BookmarkPackMatchKind.Match, result.Kind);
    }

    [Fact]
    public void Compare_ADifferentId_IsTheWrongTitle()
    {
        Assert.Equal(BookmarkPackMatchKind.WrongTitle, BookmarkPackMatcher.Compare(Title("tt0120737"), Title("tt0167261")).Kind);
    }

    [Fact]
    public void Compare_NoIdInCommon_IsTheWrongTitle_EvenWithTheSameRunningTime()
    {
        var pack = new BookmarkPackTitle(new Dictionary<string, string> { ["tmdb_id"] = "120" }, 7200, null);

        Assert.Equal(BookmarkPackMatchKind.WrongTitle, BookmarkPackMatcher.Compare(pack, Title("tt0120737")).Kind);
    }

    [Fact]
    public void Compare_AnIdOutsideTheRecognitionList_IsIgnored()
    {
        var pack = new BookmarkPackTitle(new Dictionary<string, string> { ["goodreads_id"] = "1", ["imdb_id"] = "tt1" }, 100, null);
        var local = new BookmarkPackTitle(new Dictionary<string, string> { ["goodreads_id"] = "2", ["imdb_id"] = "tt1" }, 100, null);

        Assert.Equal(BookmarkPackMatchKind.Match, BookmarkPackMatcher.Compare(pack, local).Kind);
    }

    [Fact]
    public void Compare_ADifferentRunningTime_IsADifferentCut_AndSaysSo()
    {
        var result = BookmarkPackMatcher.Compare(Title("tt1", 10_800), Title("tt1", 12_300));

        Assert.Equal(BookmarkPackMatchKind.DifferentCut, result.Kind);
        Assert.Contains("3:00:00", result.Reason);
        Assert.Contains("3:25:00", result.Reason);
    }

    [Fact]
    public void Compare_WithoutRunningTimes_ADifferentEditionIsADifferentCut()
    {
        Assert.Equal(BookmarkPackMatchKind.DifferentCut,
            BookmarkPackMatcher.Compare(Title("tt1", null, "Theatrical"), Title("tt1", null, "Extended")).Kind);
        Assert.Equal(BookmarkPackMatchKind.Match,
            BookmarkPackMatcher.Compare(Title("tt1", null, "Extended"), Title("tt1", null, "extended")).Kind);
    }

    [Fact]
    public void AlignByChapter_KeepsTheDistanceIntoTheChapter_AndSkipsWhatItCannotPlace()
    {
        var bookmarks = new[]
        {
            new BookmarkPackBookmark(3010, 3100, "The Siege", 3000, "n", null),
            new BookmarkPackBookmark(5000, null, null, null, "no chapter", null),
            new BookmarkPackBookmark(100, null, "Only in the short cut", 0, "gone", null),
        };
        var chapters = new[] { new BookmarkPackChapter("the siege", 3600), new BookmarkPackChapter("Opening", 0) };

        var result = BookmarkPackMatcher.AlignByChapter(bookmarks, chapters, 9000);

        var moved = Assert.Single(result.Aligned);
        Assert.Equal(3610, moved.Time);
        Assert.Equal(3700, moved.EndTime);
        Assert.Equal(3600, moved.ChapterStart);
        Assert.Equal(2, result.Skipped);
    }

    [Fact]
    public void AlignByChapter_DropsABookmarkThatFallsPastTheEndOfThisCopy()
    {
        var bookmarks = new[] { new BookmarkPackBookmark(3010, null, "The Siege", 3000, null, null) };

        var result = BookmarkPackMatcher.AlignByChapter(bookmarks, [new BookmarkPackChapter("The Siege", 3600)], 3605);

        Assert.Empty(result.Aligned);
        Assert.Equal(1, result.Skipped);
    }
}
