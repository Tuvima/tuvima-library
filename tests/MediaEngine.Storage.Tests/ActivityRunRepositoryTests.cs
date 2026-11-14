using MediaEngine.Domain.Contracts;

namespace MediaEngine.Storage.Tests;

public sealed class ActivityRunRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    private const string Library = "11111111-1111-1111-1111-111111111111";
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ActivityRunRepository _runs;

    public ActivityRunRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_activity_runs_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _runs = new ActivityRunRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task ListRuns_AfterFourteenFilesInOnePass_ReturnsOneRunWithFourteenItemsAndAnEnd()
    {
        var run = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: false, Start);
        for (var index = 0; index < 14; index++)
        {
            await AddAsync(run, "Movies", $"Movie {index}", Start.AddSeconds(index * 10));
        }

        await _runs.CompleteQuietRunsAsync(Start.AddMinutes(30));

        var page = await _runs.ListRunsAsync(new ActivityRunQuery());
        var only = Assert.Single(page);
        Assert.Equal(14, only.Run.ItemCount);
        Assert.Equal(ActivityRunStatuses.Completed, only.Run.Status);
        Assert.Equal(Start.AddSeconds(130), only.Run.EndedAt);
        Assert.Equal(14, only.CountsByMediaType["Movies"]);
    }

    [Fact]
    public async Task ListRuns_AfterAFreshIngestOfSeveralRuns_ReturnsEveryRunWithItsCountsWhateverTheirStatus()
    {
        // The shape of a finished test library: several runs (one still open, one a first run), a few hundred items in all.
        var expected = new List<(Guid Id, int Items)>();
        for (var index = 0; index < 8; index++)
        {
            var at = Start.AddMinutes(index * 10);
            var run = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: index == 0, at);
            var items = 20 + index * 15;
            for (var item = 0; item < items; item++)
            {
                var show = $"Show {item % 3}";
                await AddAsync(
                    run, index % 2 == 0 ? "TV" : "Movies", $"Title {index}-{item}", at.AddSeconds(item),
                    groupKey: $"{index}|{show}", groupTitle: show, groupWorkId: Guid.NewGuid());
            }

            expected.Add((run.Id, items));
        }

        // Every run but the last has gone quiet and closed; the last is still active.
        await _runs.CompleteQuietRunsAsync(Start.AddMinutes(70));

        var page = await _runs.ListRunsAsync(new ActivityRunQuery(Limit: 51));

        Assert.Equal(8, page.Count);
        Assert.Equal(expected.AsEnumerable().Reverse().Select(entry => entry.Id), page.Select(entry => entry.Run.Id));
        Assert.All(page, entry => Assert.Equal(entry.Run.ItemCount, entry.CountsByMediaType.Values.Sum()));
        Assert.Contains(page, entry => entry.Run.Status == ActivityRunStatuses.Active);
        Assert.NotNull(await _runs.GetLastCompletedAsync(null));
        Assert.Equal(8, (await _runs.ListRunsAsync(new ActivityRunQuery(LibraryIds: [Library], Limit: 51))).Count);
    }

    [Fact]
    public async Task ListRuns_ForARemovalOfThreeItems_ReturnsOneRemovedRun()
    {
        var run = await OpenRunAsync(Library, ActivityRunKinds.Removed, isFirst: false, Start);
        for (var index = 0; index < 3; index++)
        {
            await AddAsync(run, string.Empty, $"file {index}", Start);
        }

        var only = Assert.Single(await _runs.ListRunsAsync(new ActivityRunQuery(Kinds: [ActivityRunKinds.Removed])));
        Assert.Equal(ActivityRunKinds.Removed, only.Run.Kind);
        Assert.Equal(3, only.Run.ItemCount);
    }

    [Fact]
    public async Task FindOpenRun_JoinsWithinTheQuietWindowButNotAfterIt()
    {
        var run = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: false, Start);
        await AddAsync(run, "TV", "Episode", Start);

        Assert.NotNull(await _runs.FindOpenRunAsync(Library, ActivityRunKinds.Added, Start.AddMinutes(-1)));
        Assert.Null(await _runs.FindOpenRunAsync(Library, ActivityRunKinds.Added, Start.AddMinutes(1)));
        Assert.Null(await _runs.FindOpenRunAsync(Library, ActivityRunKinds.Removed, Start.AddMinutes(-1)));
    }

    [Fact]
    public async Task LibraryHasRuns_IsTrueOnlyAfterTheFirstRunExists()
    {
        Assert.False(await _runs.LibraryHasRunsAsync(Library));
        await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: true, Start);
        Assert.True(await _runs.LibraryHasRunsAsync(Library));
        Assert.False(await _runs.LibraryHasRunsAsync("22222222-2222-2222-2222-222222222222"));
    }

    [Fact]
    public async Task NewWorks_NothingIsNewAfterTheFirstImport_ALaterImportIsNewForFourteenDaysThenNot()
    {
        var firstWork = Guid.NewGuid();
        var laterWork = Guid.NewGuid();
        var first = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: true, Start);
        await AddAsync(first, "Movies", "Original", Start, workId: firstWork);
        await _runs.CompleteQuietRunsAsync(Start.AddHours(1));

        var later = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: false, Start.AddDays(2));
        await AddAsync(later, "Movies", "Later", Start.AddDays(2), workId: laterWork);
        await _runs.CompleteQuietRunsAsync(Start.AddDays(2).AddHours(1));

        var within = await _runs.GetNewWorkIdsAsync([firstWork, laterWork], Start.AddDays(5).AddDays(-14));
        Assert.DoesNotContain(firstWork, within);
        Assert.Contains(laterWork, within);

        var expired = await _runs.GetNewWorkIdsAsync([firstWork, laterWork], Start.AddDays(20).AddDays(-14));
        Assert.Empty(expired);
    }

    [Fact]
    public async Task ListGroups_StacksEpisodesIntoOneTilePerShow()
    {
        var run = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: false, Start);
        var show = Guid.NewGuid();
        for (var index = 0; index < 4; index++)
        {
            await AddAsync(run, "TV", $"Episode {index}", Start, groupKey: "tv|the expanse", groupTitle: "The Expanse", groupWorkId: show);
        }

        await AddAsync(run, "Movies", "Dune", Start);

        var groups = await _runs.ListGroupsAsync(run.Id, null, 0, 20);
        Assert.Equal(2, groups.Count);
        var expanse = Assert.Single(groups, group => group.MediaType == "TV");
        Assert.Equal(4, expanse.ItemCount);
        Assert.Equal(show, expanse.GroupWorkId);

        var onlyMovies = await _runs.ListGroupsAsync(run.Id, new ActivityRunScope(MediaTypes: ["Movies"]), 0, 20);
        Assert.Single(onlyMovies);
    }

    [Fact]
    public async Task SearchTitles_FindsTheShowOncePerBatchWithItsOwnCounts()
    {
        var earlier = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: false, Start);
        for (var index = 0; index < 5; index++)
        {
            await AddAsync(earlier, "TV", $"E{index}", Start, groupKey: "tv|the expanse", groupTitle: "The Expanse");
        }

        var later = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: false, Start.AddDays(10));
        for (var index = 0; index < 4; index++)
        {
            await AddAsync(later, "TV", $"E{index + 5}", Start.AddDays(10), groupKey: "tv|the expanse", groupTitle: "The Expanse");
        }

        await AddAsync(later, "Movies", "Dune", Start.AddDays(10));

        var hits = await _runs.SearchTitlesAsync(new ActivityRunQuery(Search: "expanse"));

        Assert.Equal(2, hits.Count);
        Assert.Equal([4, 5], hits.Select(hit => hit.ItemCount).OrderBy(count => count));
        Assert.All(hits, hit => Assert.Equal("The Expanse", hit.GroupTitle));
    }

    [Fact]
    public async Task ListRuns_Search_OnlyReturnsRunsThatHoldAMatch()
    {
        var withMatch = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: false, Start);
        await AddAsync(withMatch, "Movies", "Dune");
        var without = await OpenRunAsync("33333333-3333-3333-3333-333333333333", ActivityRunKinds.Added, isFirst: false, Start.AddDays(1));
        await AddAsync(without, "Movies", "Arrival", Start.AddDays(1));

        var page = await _runs.ListRunsAsync(new ActivityRunQuery(Search: "dun"));

        Assert.Equal(withMatch.Id, Assert.Single(page).Run.Id);
    }

    [Fact]
    public async Task ListRuns_NewestFirstByDefault_AndOldestFirstWhenAsked()
    {
        var older = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: false, Start);
        var newer = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: false, Start.AddDays(1));

        Assert.Equal([newer.Id, older.Id], (await _runs.ListRunsAsync(new ActivityRunQuery())).Select(entry => entry.Run.Id));
        Assert.Equal([older.Id, newer.Id], (await _runs.ListRunsAsync(new ActivityRunQuery(NewestFirst: false))).Select(entry => entry.Run.Id));
    }

    [Fact]
    public async Task Purge_RemovesOldFinishedRunsAndTheirItemsButKeepsActiveOnes()
    {
        var old = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: false, Start.AddDays(-400));
        await AddAsync(old, "Movies", "Old", Start.AddDays(-400));
        await _runs.CompleteQuietRunsAsync(Start.AddDays(-399));
        var stillActive = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: false, Start.AddDays(-390));

        var removed = await _runs.PurgeAsync(Start.AddDays(-30));

        Assert.Equal(1, removed);
        Assert.Equal(stillActive.Id, Assert.Single(await _runs.ListRunsAsync(new ActivityRunQuery())).Run.Id);
        Assert.Empty(await _runs.ListGroupsAsync(old.Id, null, 0, 10));
    }

    [Fact]
    public async Task CompleteInterruptedRuns_ClosesEveryActiveRunAtItsLastTitle_AndReopenBringsOnlyThatKindBack()
    {
        var added = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: false, Start);
        await AddAsync(added, "Movies", "Cut off", Start.AddMinutes(2));
        await OpenRunAsync(Library, ActivityRunKinds.Removed, isFirst: false, Start);

        Assert.Equal(2, await _runs.CompleteInterruptedRunsAsync());
        var closed = (await _runs.ListRunsAsync(new ActivityRunQuery())).Select(entry => entry.Run).ToList();
        Assert.All(closed, run => Assert.Equal(ActivityRunStatuses.Completed, run.Status));
        Assert.Equal(Start.AddMinutes(2), closed.Single(run => run.Id == added.Id).EndedAt);

        Assert.Equal(1, await _runs.ReopenInterruptedRunsAsync(ActivityRunKinds.Added));

        var reopened = await _runs.FindOpenRunAsync(Library, ActivityRunKinds.Added, DateTimeOffset.MinValue);
        Assert.Equal(added.Id, reopened?.Id);
        Assert.Null(reopened?.EndedAt);
        Assert.Null(await _runs.FindOpenRunAsync(Library, ActivityRunKinds.Removed, DateTimeOffset.MinValue));
    }

    [Fact]
    public async Task ClearInterrupted_StopsTheCutOffRunsFromReopening()
    {
        var run = await OpenRunAsync(Library, ActivityRunKinds.Added, isFirst: false, Start);
        await AddAsync(run, "Movies", "Cut off", Start);
        await _runs.CompleteInterruptedRunsAsync();

        Assert.Equal(1, await _runs.ClearInterruptedAsync());

        Assert.Equal(0, await _runs.ReopenInterruptedRunsAsync(ActivityRunKinds.Added));
        Assert.Equal(ActivityRunStatuses.Completed, Assert.Single(await _runs.ListRunsAsync(new ActivityRunQuery())).Run.Status);
    }

    private async Task<ActivityRunRecord> OpenRunAsync(string library, string kind, bool isFirst, DateTimeOffset at)
    {
        var run = new ActivityRunRecord(Guid.NewGuid(), library, kind, ActivityRunStatuses.Active, isFirst, at, at, null, 0);
        await _runs.InsertRunAsync(run);
        return run;
    }

    private Task AddAsync(
        ActivityRunRecord run, string mediaType, string title, DateTimeOffset? at = null,
        Guid? workId = null, string? groupKey = null, string? groupTitle = null, Guid? groupWorkId = null)
        => _runs.AddItemAsync(
            new ActivityRunItemRecord(
                Guid.NewGuid(), run.Id, workId ?? Guid.NewGuid(), null, mediaType, title,
                groupKey ?? $"item|{Guid.NewGuid():N}", groupTitle ?? title, groupWorkId, at ?? Start),
            at ?? Start);
}
