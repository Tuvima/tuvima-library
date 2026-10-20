using MediaEngine.Api.DevSupport;

namespace MediaEngine.Api.Tests;

public sealed class BaselineComparerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_baseline_compare_{Guid.NewGuid():N}");

    public BaselineComparerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Left behind only if a scanner holds a temp file; the unique folder name keeps runs apart.
        }
    }

    private static BaselineItem Item(string path, string title, string qid = "Q1")
    {
        var item = new BaselineItem
        {
            Path = path,
            MediaType = "Books",
            Identity = new BaselineIdentity { JobState = "Ready", Title = title, WikidataQid = qid },
            Work = new BaselineWork { Kind = "standalone", Title = title, Qid = qid, Assets = [path] },
            Edition = new BaselineEdition { Assets = [path] },
        };
        item.Values["work"] = new BaselineValueSet();
        item.Values["work"].Scalars["title"] = new BaselineValue { Value = title, Provider = "apple_api" };
        item.Values["work"].Arrays["author"] = ["Frank Herbert"];
        item.ExternalIds["apple_books_id"] = ["123"];
        item.Artwork["work/Primary"] = new BaselineArtwork { Provider = "provider", SourceUrl = "https://cdn/x.jpg", ContentHash = "abc", Width = 10, Height = 20 };
        return item;
    }

    private static BaselineRecord Record(params BaselineItem[] items)
    {
        var record = new BaselineRecord { Items = [.. items] };
        record.Counts.ByMediaType["Books"] = new BaselineTypeCounts { Assets = items.Length, Works = items.Length, WithWikidataQid = items.Length };
        return record;
    }

    [Fact]
    public void Compare_OfARecordWithItself_HasNoDifferences()
    {
        var record = Record(Item("books/Dune.epub", "Dune"), Item("books/Hobbit.epub", "The Hobbit", "Q2"));

        var comparison = BaselineComparer.Compare(record, Record(Item("books/Dune.epub", "Dune"), Item("books/Hobbit.epub", "The Hobbit", "Q2")));

        Assert.True(comparison.IsIdentical);
        Assert.Empty(comparison.Differences);
    }

    [Fact]
    public void Report_ListsEveryRequestTheRecordingDidNotHold_OnEachSide()
    {
        var before = Record(Item("books/Dune.epub", "Dune"));
        var after = Record(Item("books/Dune.epub", "Dune"));
        after.Metadata.ProviderAnswersNotRecorded = ["GET https://itunes.apple.com/search?term=Dune -"];

        var report = BaselineComparer.RenderReport(before, after, BaselineComparer.Compare(before, after));

        Assert.Contains("## After: requests not in the recording (1)", report, StringComparison.Ordinal);
        Assert.Contains("- `GET https://itunes.apple.com/search?term=Dune -`", report, StringComparison.Ordinal);
        Assert.DoesNotContain("## Before: requests not in the recording", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_DoesNotCareWhichOrderTheItemsWereExportedIn()
    {
        var left = Record(Item("books/A.epub", "A"), Item("books/B.epub", "B"));
        var right = Record(Item("books/B.epub", "B"), Item("books/A.epub", "A"));

        Assert.True(BaselineComparer.Compare(left, right).IsIdentical);
    }

    [Fact]
    public void Compare_ReportsAChangedValueWithBothSides()
    {
        var before = Record(Item("books/Dune.epub", "Dune"));
        var after = Record(Item("books/Dune.epub", "Dune"));
        after.Items[0].Values["work"].Scalars["title"] = new BaselineValue { Value = "Dune (1965)", Provider = "apple_api" };

        var comparison = BaselineComparer.Compare(before, after);

        var difference = Assert.Single(comparison.Differences);
        Assert.Equal("item: books/Dune.epub", difference.Subject);
        Assert.Equal("work value.title", difference.Field);
        Assert.Equal(BaselineDifferenceKind.Changed, difference.Kind);
        Assert.Contains("Dune (won by apple_api)", difference.Before, StringComparison.Ordinal);
        Assert.Contains("Dune (1965) (won by apple_api)", difference.After, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_NamesTheWinningProviderAsPartOfTheValue()
    {
        var before = Record(Item("books/Dune.epub", "Dune"));
        var after = Record(Item("books/Dune.epub", "Dune"));
        after.Items[0].Values["work"].Scalars["title"] = new BaselineValue { Value = "Dune", Provider = "wikidata" };

        var difference = Assert.Single(BaselineComparer.Compare(before, after).Differences);

        Assert.Contains("apple_api", difference.Before, StringComparison.Ordinal);
        Assert.Contains("wikidata", difference.After, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_ReportsAddedAndRemovedItems()
    {
        var before = Record(Item("books/A.epub", "A"), Item("books/B.epub", "B"));
        var after = Record(Item("books/B.epub", "B"), Item("books/C.epub", "C"));

        var comparison = BaselineComparer.Compare(before, after);

        Assert.Contains(comparison.Differences, d => d.Subject == "item: books/A.epub" && d.Kind == BaselineDifferenceKind.Removed);
        Assert.Contains(comparison.Differences, d => d.Subject == "item: books/C.epub" && d.Kind == BaselineDifferenceKind.Added);
        Assert.DoesNotContain(comparison.Differences, d => d.Subject == "item: books/B.epub");
    }

    [Fact]
    public void Compare_ReportsAMovedGroupingAndAChangedExternalIdPerItem()
    {
        var before = Record(Item("books/A.epub", "A"));
        var after = Record(Item("books/A.epub", "A"));
        after.Items[0].Work.Assets = ["books/A.epub", "books/A2.epub"];
        after.Items[0].ExternalIds["apple_books_id"] = ["999"];
        after.Items[0].ExternalIds["isbn"] = ["9780441013593"];

        var fields = BaselineComparer.Compare(before, after).Differences.Select(d => d.Field).ToList();

        Assert.Contains("work.grouped assets", fields);
        Assert.Contains("external id.apple_books_id", fields);
        Assert.Contains("external id.isbn", fields);
    }

    [Fact]
    public void Compare_ReportsAChangedArtworkChoiceAndAChangedReviewOutcome()
    {
        var before = Record(Item("books/A.epub", "A"));
        var after = Record(Item("books/A.epub", "A"));
        after.Items[0].Artwork["work/Primary"].ContentHash = "def";
        after.Items[0].Review = [new BaselineReview { Subject = "books/A.epub", Trigger = "RetailMatchAmbiguous", Status = "Pending" }];

        var comparison = BaselineComparer.Compare(before, after);

        Assert.Contains(comparison.Differences, d => d.Field == "artwork.work/Primary");
        Assert.Contains(comparison.Differences, d => d.Field == "review" && d.After!.Contains("RetailMatchAmbiguous (Pending)", StringComparison.Ordinal));
    }

    [Fact]
    public void Compare_ReportsChangedCountsPerMediaType()
    {
        var before = Record(Item("books/A.epub", "A"));
        var after = Record(Item("books/A.epub", "A"));
        after.Counts.ByMediaType["Books"].Assets = 2;

        var difference = Assert.Single(BaselineComparer.Compare(before, after).Differences);

        Assert.Equal("count: Books", difference.Subject);
        Assert.Equal("assets", difference.Field);
    }

    [Fact]
    public void Compare_NeverCountsTimingsAsDifferencesButListsThoseBeyondTheMargin()
    {
        var before = Record(Item("books/A.epub", "A"), Item("books/B.epub", "B"));
        var after = Record(Item("books/A.epub", "A"), Item("books/B.epub", "B"));
        before.Timings.TotalSeconds = 100;
        after.Timings.TotalSeconds = 150;
        before.Timings.RegisteredToIdentified["books/A.epub"] = 10;
        after.Timings.RegisteredToIdentified["books/A.epub"] = 11;
        before.Timings.RegisteredToIdentified["books/B.epub"] = 10;
        after.Timings.RegisteredToIdentified["books/B.epub"] = 30;

        var comparison = BaselineComparer.Compare(before, after);

        Assert.True(comparison.IsIdentical);
        Assert.Equal(["books/B.epub", "total"], comparison.Timings.Where(t => t.BeyondMargin).Select(t => t.Subject).Order(StringComparer.Ordinal));
        Assert.False(comparison.Timings.Single(t => t.Subject == "books/A.epub").BeyondMargin);
    }

    [Fact]
    public void Compare_UsesTheMarginItIsGiven()
    {
        var before = Record();
        var after = Record();
        before.Timings.TotalSeconds = 100;
        after.Timings.TotalSeconds = 130;

        Assert.True(BaselineComparer.Compare(before, after, margin: 0.2).Timings.Single().BeyondMargin);
        Assert.False(BaselineComparer.Compare(before, after, margin: 0.5).Timings.Single().BeyondMargin);
    }

    [Fact]
    public void Compare_IgnoresTimingStepsThatMovedByUnderASecond()
    {
        var before = Record();
        var after = Record();
        before.Timings.RegisteredToIdentified["books/A.epub"] = 0.4;
        after.Timings.RegisteredToIdentified["books/A.epub"] = 0.9;

        Assert.False(BaselineComparer.Compare(before, after).Timings.Single().BeyondMargin);
    }

    [Fact]
    public void Compare_IgnoresRunMetadata()
    {
        var before = Record(Item("books/A.epub", "A"));
        var after = Record(Item("books/A.epub", "A"));
        before.Metadata.BuildCommit = "aaaa";
        after.Metadata.BuildCommit = "bbbb";
        after.Metadata.ProviderClaims["tmdb"] = 5;

        Assert.True(BaselineComparer.Compare(before, after).IsIdentical);
    }

    [Fact]
    public void RenderReport_GroupsTheSameChangeOnManyItemsIntoOneLine()
    {
        var before = Record(Item("tv/E1.mp4", "E1"), Item("tv/E2.mp4", "E2"), Item("tv/E3.mp4", "E3"));
        var after = Record(Item("tv/E1.mp4", "E1"), Item("tv/E2.mp4", "E2"), Item("tv/E3.mp4", "E3"));
        foreach (var item in after.Items)
        {
            item.Groups = [new BaselineGroupRef { Type = "Series", Name = "Breaking Bad", Qid = "Q1079" }];
        }

        var report = BaselineComparer.RenderReport(before, after, BaselineComparer.Compare(before, after));

        Assert.Contains("3 entries", report, StringComparison.Ordinal);
        Assert.Equal(1, report.Split('\n').Count(line => line.Contains("**groups**", StringComparison.Ordinal)));
    }

    [Fact]
    public void RenderReport_ForIdenticalRecordsSaysSo()
    {
        var record = Record(Item("books/A.epub", "A"));

        var report = BaselineComparer.RenderReport(record, record, BaselineComparer.Compare(record, record));

        Assert.Contains("no comparable differences", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReturnsZeroForMatchingFilesAndOneForDifferingFilesAndWritesTheReport()
    {
        var before = Record(Item("books/A.epub", "A"));
        var after = Record(Item("books/A.epub", "B"));
        var beforePath = Path.Combine(_root, "before.json");
        var afterPath = Path.Combine(_root, "after.json");
        var sameReport = Path.Combine(_root, "same.md");
        var differentReport = Path.Combine(_root, "different.md");
        await File.WriteAllTextAsync(beforePath, BaselineRecordJson.Serialize(before));
        await File.WriteAllTextAsync(afterPath, BaselineRecordJson.Serialize(after));

        var matching = await BaselineComparer.RunAsync(beforePath, beforePath, sameReport, []);
        var differing = await BaselineComparer.RunAsync(beforePath, afterPath, differentReport, ["--margin", "0.5"]);

        Assert.Equal(0, matching);
        Assert.Equal(1, differing);
        Assert.Contains("no comparable differences", await File.ReadAllTextAsync(sameReport), StringComparison.Ordinal);
        Assert.Contains("work value.title", await File.ReadAllTextAsync(differentReport), StringComparison.Ordinal);
    }

    [Fact]
    public void RecordJson_RoundTripsWithoutLosingAnyComparableFact()
    {
        var record = Record(Item("books/Dune.epub", "Dune"));
        record.ViewItems.Add(new BaselineViewItem { Path = "personal_photos/a.png", MediaKind = "image", ByteSize = 5, ContentHash = "h" });
        record.SeriesMembers.Add(new BaselineSeriesMember { SeriesQid = "Q1", WorkQid = "Q2", Position = "1", Owned = true });

        var loaded = BaselineRecordJson.Deserialize(BaselineRecordJson.Serialize(record));

        Assert.True(BaselineComparer.Compare(record, loaded).IsIdentical);
    }
}
