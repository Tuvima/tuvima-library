using System.Text.Json;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;

namespace MediaEngine.Api.Tests;

public sealed class DisplayRecentTests
{
    private static readonly DateTimeOffset Added = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    [Theory]
    [InlineData(null, "all")][InlineData("ALL", "all")][InlineData("watch", "watch")][InlineData("read", "read")][InlineData("listen", "listen")][InlineData("view", "view")]
    public void TypesAreExplicit(string? input, string expected) => Assert.Equal(expected, DisplayRecentCursor.NormalizeType(input));
    [Theory][InlineData("")][InlineData("bad")][InlineData("watchlist")]
    public void UnknownTypesRejected(string input) => Assert.Throws<ArgumentException>(() => DisplayRecentCursor.NormalizeType(input));
    [Fact]
    public void CursorBindsProfileFilterAndVersion()
    {
        var profile = Guid.NewGuid();
        var cursor = DisplayRecentCursor.Encode("all", profile, new(Added, "view:" + Guid.NewGuid().ToString("N")));
        Assert.NotNull(DisplayRecentCursor.Decode(cursor, "all", profile));
        Assert.Throws<ArgumentException>(() => DisplayRecentCursor.Decode(cursor, "view", profile));
        Assert.Throws<ArgumentException>(() => DisplayRecentCursor.Decode(cursor, "all", Guid.NewGuid()));
        foreach (var value in new[] { "", "junk", Convert.ToBase64String("{}"u8.ToArray()), Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { Version = 2, Type = "all", ProfileId = profile, AddedAt = Added, Key = "view:" + Guid.NewGuid().ToString("N") })) })
            Assert.Throws<ArgumentException>(() => DisplayRecentCursor.Decode(value, "all", profile));
        var invalid = DisplayRecentCursor.Encode("view", profile, new(Added, "catalogue:" + Guid.NewGuid().ToString("N")));
        Assert.Throws<ArgumentException>(() => DisplayRecentCursor.Decode(invalid, "view", profile));
        Assert.Throws<ArgumentException>(() => DisplayRecentCursor.Decode(DisplayRecentCursor.Encode("all", profile, new(Added, "view:wrong")), "all", profile));
    }
    [Theory][InlineData(2, 15, 1)][InlineData(15, 2, 4)][InlineData(9, 9, 3)]
    public void LastEmittedBoundaryPreservesTiesAndSourceHeavyPages(int catalogueCount, int viewCount, int limit)
    {
        var profile = Guid.NewGuid();
        var all = Enumerable.Range(1, catalogueCount).Select(n => Item("catalogue", n))
            .Concat(Enumerable.Range(1, viewCount).Select(n => Item("view", n))).ToList();
        var emitted = new List<string>(); DisplayRecentBoundary? boundary = null;
        while (true)
        {
            // Each source independently re-queries from the sole global boundary.
            var candidates = all.GroupBy(i => i.Key.Split(':')[0]).SelectMany(g => g.Where(i => DisplayRecentCursor.IsAfter(i.AddedAt, i.Key, boundary))
                .OrderByDescending(i => i.AddedAt).ThenBy(i => i.Key, StringComparer.Ordinal).Take(limit + 1));
            var page = DisplayRecentCursor.Page("all", profile, candidates, limit);
            emitted.AddRange(page.Items.Select(i => i.Key));
            if (!page.HasMore) break;
            boundary = DisplayRecentCursor.Decode(page.NextCursor, "all", profile);
        }
        Assert.Equal(all.OrderByDescending(i => i.AddedAt).ThenBy(i => i.Key, StringComparer.Ordinal).Select(i => i.Key), emitted);
        Assert.Equal(all.Count, emitted.Distinct().Count());
    }
    [Theory][InlineData("all", 4)][InlineData("watch", 1)][InlineData("read", 1)][InlineData("listen", 2)][InlineData("view", 0)]
    public void CatalogueFiltersAndStructuralIdentities(string type, int count)
    {
        var show = Guid.NewGuid(); var album = Guid.NewGuid();
        var rows = new[] { Work("TV", show, "One"), Work("TV", show, "Two"), Work("Music", album, "Track1"), Work("Music", album, "Track2"), Work("Books", Guid.Empty, "Book"), Work("Audiobooks", Guid.Empty, "Audio") };
        rows[1].CreatedAt = Added.AddDays(1); rows[3].CreatedAt = Added.AddDays(2);
        var result = RecentCatalogueReadService.Compose(rows, new DisplayCardBuilder(), type, null, 100);
        Assert.Equal(count, result.Count);
        Assert.All(result, i => { Assert.NotNull(i.Catalogue); Assert.Null(i.ViewAsset); });
        if (type == "all") { Assert.Equal(Added.AddDays(2), result.Single(i => i.Catalogue!.Id == album).AddedAt); Assert.Equal(Added.AddDays(1), result.Single(i => i.Catalogue!.Id == show).AddedAt); }
    }
    [Fact]
    public void ViewKeysFollowRfcGuidOrder()
    {
        var a = Guid.Parse("00000001-ffff-ffff-ffff-ffffffffffff"); var b = Guid.Parse("00000100-0000-0000-0000-000000000000");
        Assert.True(string.CompareOrdinal(DisplayRecentCursor.ViewKey(a), DisplayRecentCursor.ViewKey(b)) < 0);
        Assert.Equal("view:00000001ffffffffffffffffffffffff", DisplayRecentCursor.ViewKey(a));
    }
    private static DisplayRecentItemDto Item(string source, int n) => new($"{source}:{n:x32}", n % 5 == 0 ? Added.AddSeconds(-1) : Added, null, null);
    private static DisplayWorkRow Work(string type, Guid root, string title) => new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), RootWorkId = root, MediaType = type, Title = title, ShowName = "Show", Album = "Album", CreatedAt = Added, SeasonNumber = "1", EpisodeNumber = "1" };
}
