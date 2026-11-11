using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

// How Everyone's bookmarks are pooled from every profile's own (universal editor v5 D37): bookmarks at the same spot become one entry, and
// the entry is stood for by one already in Everyone's, else a named one, else the earliest made.
public sealed class BookmarkPoolingTests
{
    private static readonly Guid Asset = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static BookmarkPoolCandidate Time(double at, int madeMinutesLater = 0, bool inSet = false, bool named = false, Guid? asset = null) =>
        new(Guid.NewGuid(), asset ?? Asset, BookmarkPooling.TimeKind, at, null, null, inSet, named, Start.AddMinutes(madeMinutesLater));

    private static double Gap(string kind) => 3;

    [Fact]
    public void TwoBookmarksWithinTheGap_AreOneEntry_AndOnesFurtherApartAreSeparate()
    {
        var groups = BookmarkPooling.Merge([Time(100), Time(102), Time(500)], Gap);

        Assert.Equal([2, 1], groups.Select(group => group.Members.Count));
    }

    [Fact]
    public void ALongRunOfBookmarksAFewSecondsApart_DoesNotChainIntoOne()
    {
        var groups = BookmarkPooling.Merge([Time(100), Time(102), Time(104), Time(106)], Gap);

        Assert.Equal([2, 2], groups.Select(group => group.Members.Count));
    }

    [Fact]
    public void TheEntryIsStoodForByOneAlreadyInEveryones_ThenANamedOne_ThenTheEarliestMade()
    {
        var plainEarly = Time(100, madeMinutesLater: 0);
        var named = Time(101, madeMinutesLater: 5, named: true);
        var inSet = Time(102, madeMinutesLater: 9, inSet: true);

        Assert.Equal(inSet.Id, BookmarkPooling.Merge([plainEarly, named, inSet], Gap).Single().Representative.Id);
        Assert.Equal(named.Id, BookmarkPooling.Merge([plainEarly, named], Gap).Single().Representative.Id);
        Assert.Equal(plainEarly.Id, BookmarkPooling.Merge([plainEarly, Time(101, madeMinutesLater: 3)], Gap).Single().Representative.Id);
    }

    [Fact]
    public void BookmarksOnDifferentFiles_AreNeverTheSameSpot()
    {
        var groups = BookmarkPooling.Merge([Time(100), Time(100, asset: Guid.NewGuid())], Gap);

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void ABookPlace_IsTheSameSpotOnlyInTheSameChapterAndPlace()
    {
        BookmarkPoolCandidate Book(int chapter, string? locator) =>
            new(Guid.NewGuid(), Asset, BookmarkPooling.TextKind, 0, chapter, locator, false, false, Start);

        var groups = BookmarkPooling.Merge([Book(2, "epubcfi(/6/4)"), Book(2, "epubcfi(/6/4)"), Book(2, "epubcfi(/6/8)"), Book(3, "epubcfi(/6/4)")], Gap);

        Assert.Equal([2, 1, 1], groups.Select(group => group.Members.Count));
    }
}
