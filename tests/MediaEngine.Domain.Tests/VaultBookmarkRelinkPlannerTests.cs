using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class VaultBookmarkRelinkPlannerTests
{
    private static VaultRelinkIdentity Identity(
        IReadOnlyDictionary<string, string>? ids = null, double? time = 3600, string? edition = null, string? hash = null, params string[] prints) =>
        new(ids ?? new Dictionary<string, string>(), time, edition, hash, prints);

    private static VaultRelinkBookmark Saved(VaultRelinkIdentity identity, double at = 120, string? chapter = null, double? chapterStart = null) =>
        new(identity, new BookmarkPackBookmark(at, null, chapter, chapterStart, "note", null));

    private static VaultRelinkCandidate Video(VaultRelinkIdentity identity, params BookmarkPackChapter[] chapters) =>
        new(Guid.NewGuid(), Guid.NewGuid(), identity, chapters);

    private static Dictionary<string, string> Ids(string key, string value) => new() { [key] = value };

    [Fact]
    public void SameStashBoxRecord_AndRunningTime_LinksAtTheSameTime_EvenWithADifferentFile()
    {
        var saved = Saved(Identity(Ids("stash_box_id", "s|1"), hash: "old"));
        var video = Video(Identity(Ids("stash_box_id", "s|1"), time: 3601, hash: "new"));

        var decision = VaultBookmarkRelinkPlanner.Decide(saved, video);

        Assert.Equal(VaultRelinkKind.Exact, decision.Kind);
        Assert.Equal(120, decision.Bookmark!.Time);
    }

    [Fact]
    public void ADifferentExternalId_NeverLinks_EvenWhenTheFileHashIsTheSame()
    {
        var saved = Saved(Identity(Ids("imdb_id", "tt1"), hash: "same"));
        var video = Video(Identity(Ids("imdb_id", "tt2"), hash: "same"));

        Assert.Equal(VaultRelinkKind.NoMatch, VaultBookmarkRelinkPlanner.Decide(saved, video).Kind);
    }

    [Fact]
    public void ADifferentCut_IsMovedByChapterName()
    {
        var saved = Saved(Identity(Ids("tmdb_id", "9"), time: 3600), at: 130, chapter: "Part two", chapterStart: 100);
        var video = Video(Identity(Ids("tmdb_id", "9"), time: 4000), new BookmarkPackChapter("Part two", 160));

        var decision = VaultBookmarkRelinkPlanner.Decide(saved, video);

        Assert.Equal(VaultRelinkKind.Aligned, decision.Kind);
        Assert.Equal(190, decision.Bookmark!.Time);
    }

    [Fact]
    public void ADifferentCut_WithNoChapterToAlignTo_CannotBeRelinked()
    {
        var saved = Saved(Identity(Ids("tmdb_id", "9"), time: 3600));
        var video = Video(Identity(Ids("tmdb_id", "9"), time: 4000));

        Assert.Equal(VaultRelinkKind.NoMatch, VaultBookmarkRelinkPlanner.Decide(saved, video).Kind);
    }

    [Fact]
    public void WithNoCanonicalIdInCommon_TheExactFileFingerprintDecides()
    {
        var saved = Saved(Identity(hash: "abc", prints: "oshash-1"));

        Assert.Equal(VaultRelinkKind.Exact, VaultBookmarkRelinkPlanner.Decide(saved, Video(Identity(hash: "ABC"))).Kind);
        Assert.Equal(VaultRelinkKind.Exact, VaultBookmarkRelinkPlanner.Decide(saved, Video(Identity(hash: "zzz", prints: "OSHASH-1"))).Kind);
        Assert.Equal(VaultRelinkKind.NoMatch, VaultBookmarkRelinkPlanner.Decide(saved, Video(Identity(hash: "zzz", prints: "other"))).Kind);
    }

    [Fact]
    public void ABookmarkPastTheEndOfTheVideo_CannotBePlaced()
    {
        var saved = Saved(Identity(Ids("imdb_id", "tt1")), at: 5000);

        Assert.Equal(VaultRelinkKind.NoMatch, VaultBookmarkRelinkPlanner.Decide(saved, Video(Identity(Ids("imdb_id", "tt1"), time: 3600))).Kind);
    }
}
