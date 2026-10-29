using Dapper;
using MediaEngine.Domain.Activity;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Each person's own activity record: snapshots that outlive the item, the music session rule, grouping by title,
/// filters, the date rail, and deleting history by hand. Real temp SQLite.
/// </summary>
public sealed class PersonalActivityRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-personal-activity-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly PersonalActivityRepository _repository;
    private readonly DateTimeOffset _start = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    public PersonalActivityRepositoryTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new PersonalActivityRepository(_database);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_path);
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    // ----- Writing -----

    [Fact]
    public async Task AddingTheSamePlaybackSessionTwice_KeepsOneRow()
    {
        var person = SeedProfile("Alex");
        var session = Guid.NewGuid();

        Assert.True(await AddWatchAsync(person, session, "Tremors", _start));
        Assert.False(await AddWatchAsync(person, session, "Tremors", _start));

        var item = Assert.Single((await Items(person)).Items);
        Assert.Equal("Tremors", Assert.Single(item.Entries).Title);
    }

    [Fact]
    public async Task UpdatingProgress_OnlyEverMovesForward()
    {
        var person = SeedProfile("Alex");
        var session = Guid.NewGuid();
        await AddWatchAsync(person, session, "Tremors", _start, played: 60, percent: 40);

        await _repository.UpdateProgressAsync(session, new PersonalActivityProgress(_start.AddMinutes(10), 600, 55, false));
        await _repository.UpdateProgressAsync(session, new PersonalActivityProgress(_start.AddMinutes(5), 300, 20, false));
        var middle = Assert.Single(Assert.Single((await Items(person)).Items).Entries);
        Assert.Equal(600, middle.PlayedSeconds);
        Assert.Equal(55, middle.ProgressPct);
        Assert.Equal(_start.AddMinutes(10), middle.EndedAt);
        Assert.False(middle.Finished);

        await _repository.UpdateProgressAsync(session, new PersonalActivityProgress(_start.AddMinutes(20), 1200, 100, true));
        await _repository.UpdateProgressAsync(session, new PersonalActivityProgress(_start.AddMinutes(21), 1200, 30, false));
        var done = Assert.Single(Assert.Single((await Items(person)).Items).Entries);
        Assert.True(done.Finished);
        Assert.Equal(100, done.ProgressPct);
    }

    [Fact]
    public async Task AnEntry_KeepsItsSnapshotAfterTheAssetAndTheWorkAreDeleted()
    {
        var person = SeedProfile("Alex");
        var (work, asset) = SeedWork("Movies");
        await _repository.SaveArtworkAsync(new PersonalActivityArtwork("a".PadRight(64, 'a'), [1, 2, 3], "image/jpeg", 160, 240));
        await _repository.AddPlaybackAsync(person, PersonalActivityKinds.Watch, false, Guid.NewGuid(), _start,
            new PersonalActivityDevice("Living room TV", "television"),
            new PersonalActivitySnapshot("Movies", "Tremors", "Directed cut", null, 1990, work, asset, "a".PadRight(64, 'a')),
            new PersonalActivityProgress(_start.AddMinutes(96), 5760, 100, true));

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM media_assets WHERE id = @asset;", new { asset });
            connection.Execute("DELETE FROM editions WHERE work_id = @work;", new { work });
            connection.Execute("DELETE FROM works WHERE id = @work;", new { work });
        }

        var entry = Assert.Single(Assert.Single((await Items(person)).Items).Entries);
        Assert.Equal("Tremors", entry.Title);
        Assert.Equal("Directed cut", entry.Subtitle);
        Assert.Equal(1990, entry.Year);
        Assert.Equal(work, entry.WorkId);
        Assert.Equal("a".PadRight(64, 'a'), entry.ArtworkKey);
        var picture = await _repository.GetArtworkAsync(entry.ArtworkKey!);
        Assert.Equal([1, 2, 3], picture!.Image);
    }

    [Fact]
    public async Task AddingToMyList_CreatesAnEntry_AndRemovingItFromMyListKeepsIt()
    {
        var person = SeedProfile("Alex");
        var (work, _) = SeedWork("Movies");
        var saved = new ProfileStateRepository(_database);
        await saved.SaveItemAsync(person, MediaEngine.Domain.Enums.ProfileEntityKind.Movie, work);
        await _repository.AddMyListAsync(person, _start,
            new PersonalActivitySnapshot("Movies", "Tremors", null, null, 1990, work, null, null));

        await saved.RemoveSavedItemAsync(person, MediaEngine.Domain.Enums.ProfileEntityKind.Movie, work);

        var entry = Assert.Single(Assert.Single((await Items(person)).Items).Entries);
        Assert.Equal(PersonalActivityKinds.MyList, entry.Kind);
        Assert.Equal("Tremors", entry.Title);
    }

    [Fact]
    public async Task DeletingAPerson_RemovesTheirActivityAndNobodyElses()
    {
        var alex = SeedProfile("Alex");
        var bea = SeedProfile("Bea");
        await AddWatchAsync(alex, Guid.NewGuid(), "Tremors", _start);
        await AddWatchAsync(bea, Guid.NewGuid(), "Dune", _start);

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM profiles WHERE id = @alex;", new { alex });
        }

        Assert.Empty((await Items(alex)).Items);
        Assert.Single((await Items(bea)).Items);
    }

    // ----- Music sessions -----

    [Fact]
    public async Task FiftySongsWithinThirtyMinutesOfEachOther_AreOneMusicSession()
    {
        var person = SeedProfile("Alex");
        for (var song = 0; song < 50; song++)
        {
            await AddSongAsync(person, $"Song {song}", $"Album {song % 12}", _start.AddMinutes(song * 4));
        }

        var item = Assert.Single((await Items(person)).Items);
        Assert.True(item.IsMusicSession);
        Assert.Equal(50, item.Entries.Count);
        Assert.Equal("Song 0", item.Entries[0].Title);
        Assert.Equal("Song 49", item.Entries[^1].Title);
    }

    [Fact]
    public async Task AGapOfThirtyOneMinutesBetweenSongs_StartsANewMusicSession()
    {
        var person = SeedProfile("Alex");
        await AddSongAsync(person, "One", "Album", _start, playedSeconds: 180);
        await AddSongAsync(person, "Two", "Album", _start.AddMinutes(5), playedSeconds: 200);
        // Thirty-one minutes after "Two" stopped playing.
        await AddSongAsync(person, "Three", "Album", _start.AddMinutes(5).AddSeconds(200).AddMinutes(31), playedSeconds: 180);

        var items = (await Items(person)).Items;

        Assert.Equal(2, items.Count);
        Assert.Equal(["Three"], items[0].Entries.Select(entry => entry.Title));
        Assert.Equal(["One", "Two"], items[1].Entries.Select(entry => entry.Title));
    }

    [Fact]
    public async Task AMovieBetweenSongs_EndsTheMusicSession()
    {
        var person = SeedProfile("Alex");
        await AddSongAsync(person, "One", "Album", _start);
        await AddWatchAsync(person, Guid.NewGuid(), "Tremors", _start.AddMinutes(5));
        await AddSongAsync(person, "Two", "Album", _start.AddMinutes(10));

        var items = (await Items(person)).Items;

        Assert.Equal(3, items.Count);
        Assert.All(items, item => Assert.Single(item.Entries));
        Assert.Equal(2, items.Count(item => item.IsMusicSession));
    }

    [Fact]
    public async Task AddingToMyListBetweenSongs_DoesNotEndTheMusicSession()
    {
        var person = SeedProfile("Alex");
        await AddSongAsync(person, "One", "Album", _start);
        await _repository.AddMyListAsync(person, _start.AddMinutes(2),
            new PersonalActivitySnapshot("Movies", "Tremors", null, null, 1990, Guid.NewGuid(), null, null));
        await AddSongAsync(person, "Two", "Album", _start.AddMinutes(4));

        var music = Assert.Single((await Items(person)).Items, item => item.IsMusicSession);

        Assert.Equal(2, music.Entries.Count);
    }

    [Fact]
    public async Task TwoPeopleListeningAtOnce_HaveTheirOwnMusicSessions()
    {
        var alex = SeedProfile("Alex");
        var bea = SeedProfile("Bea");
        await AddSongAsync(alex, "One", "Album", _start);
        await AddSongAsync(bea, "Uno", "Album", _start.AddMinutes(1));
        await AddSongAsync(alex, "Two", "Album", _start.AddMinutes(2));

        Assert.Equal(2, Assert.Single((await Items(alex)).Items).Entries.Count);
        Assert.Single(Assert.Single((await Items(bea)).Items).Entries);
    }

    // ----- Grouping, search, filters -----

    [Fact]
    public async Task GroupingByTitle_ReturnsOneGroupWithEveryAttemptInOrder()
    {
        var person = SeedProfile("Alex");
        var (work, _) = SeedWork("Movies");
        await AddWatchAsync(person, Guid.NewGuid(), "Tremors", _start, work: work, percent: 31, played: 1800);
        await AddWatchAsync(person, Guid.NewGuid(), "Tremors", _start.AddDays(3), work: work, percent: 43, played: 720);
        await AddWatchAsync(person, Guid.NewGuid(), "Tremors", _start.AddDays(7), work: work, percent: 100, played: 5760, finished: true);
        await AddWatchAsync(person, Guid.NewGuid(), "Dune", _start.AddDays(8));

        var page = await _repository.GetItemsAsync(
            new PersonalActivityFilter { ProfileIds = Ids(person), Query = "Tremors" }, PersonalActivityGrouping.Title, null, 50);

        var group = Assert.Single(page.Items);
        Assert.True(group.IsTitleGroup);
        Assert.Equal([31d, 43d, 100d], group.Entries.Select(entry => entry.ProgressPct!.Value));
        Assert.Equal(3, group.Entries.Count);
    }

    [Fact]
    public async Task GroupingByTitle_KeepsTwoPeoplesWatchesOfTheSameTitleApart()
    {
        var alex = SeedProfile("Alex");
        var bea = SeedProfile("Bea");
        var (work, _) = SeedWork("Movies");
        await AddWatchAsync(alex, Guid.NewGuid(), "Tremors", _start, work: work);
        await AddWatchAsync(bea, Guid.NewGuid(), "Tremors", _start.AddDays(1), work: work);

        var page = await _repository.GetItemsAsync(
            new PersonalActivityFilter { ProfileIds = Ids(alex, bea) }, PersonalActivityGrouping.Title, null, 50);

        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task GroupingByTitle_GroupsADeletedItemByItsTitle()
    {
        var person = SeedProfile("Alex");
        await AddWatchAsync(person, Guid.NewGuid(), "Tremors", _start, withoutWork: true);
        await AddWatchAsync(person, Guid.NewGuid(), "tremors", _start.AddDays(1), withoutWork: true);

        var page = await _repository.GetItemsAsync(
            new PersonalActivityFilter { ProfileIds = Ids(person) }, PersonalActivityGrouping.Title, null, 50);

        Assert.Equal(2, Assert.Single(page.Items).Entries.Count);
    }

    [Fact]
    public async Task FiltersNarrowByKindOutcomeDeviceDatesAndText()
    {
        var person = SeedProfile("Alex");
        await AddWatchAsync(person, Guid.NewGuid(), "Tremors", _start, finished: true, device: "Living room TV");
        await AddWatchAsync(person, Guid.NewGuid(), "Dune", _start.AddDays(2), finished: false, device: "Phone");
        await AddSongAsync(person, "Wish You Were Here", "Wish You Were Here", _start.AddDays(4));
        await _repository.AddMyListAsync(person, _start.AddDays(5),
            new PersonalActivitySnapshot("Books", "Hyperion", "Dan Simmons", null, 1989, Guid.NewGuid(), null, null));

        Assert.Equal(["Hyperion"], await Titles(new PersonalActivityFilter { Kinds = [PersonalActivityKinds.MyList] }, person));
        Assert.Equal(["Tremors"], await Titles(new PersonalActivityFilter { Finished = true }, person));
        Assert.Equal(["Dune"], await Titles(new PersonalActivityFilter { Finished = false, Kinds = [PersonalActivityKinds.Watch] }, person));
        Assert.Equal(["Dune"], await Titles(new PersonalActivityFilter { Device = "phone" }, person));
        Assert.Equal(["Dune"], await Titles(new PersonalActivityFilter { From = _start.AddDays(1), To = _start.AddDays(3) }, person));
        Assert.Equal(["Hyperion"], await Titles(new PersonalActivityFilter { Query = "simmons" }, person));
        Assert.Equal(["Music"], await Titles(new PersonalActivityFilter { Query = "were here" }, person, collapse: true));
    }

    [Fact]
    public async Task SearchingForPercentOrUnderscoreTextMatchesThemLiterally()
    {
        var person = SeedProfile("Alex");
        await AddWatchAsync(person, Guid.NewGuid(), "100% True", _start);
        await AddWatchAsync(person, Guid.NewGuid(), "Dune", _start.AddDays(1));

        Assert.Equal(["100% True"], await Titles(new PersonalActivityFilter { Query = "100%" }, person));
        Assert.Empty(await Titles(new PersonalActivityFilter { Query = "D_ne" }, person));
    }

    [Fact]
    public async Task SearchingByPersonName_OnlyMatchesWhenAskedToSearchPeople()
    {
        var alex = SeedProfile("Alex");
        var bea = SeedProfile("Bea");
        await AddWatchAsync(alex, Guid.NewGuid(), "Tremors", _start);
        await AddWatchAsync(bea, Guid.NewGuid(), "Dune", _start.AddDays(1));
        var both = Ids(alex, bea);

        var without = await _repository.GetItemsAsync(
            new PersonalActivityFilter { ProfileIds = both, Query = "bea" }, PersonalActivityGrouping.None, null, 50);
        var with = await _repository.GetItemsAsync(
            new PersonalActivityFilter { ProfileIds = both, Query = "bea", SearchPeople = true }, PersonalActivityGrouping.None, null, 50);

        Assert.Empty(without.Items);
        var found = Assert.Single(Assert.Single(with.Items).Entries);
        Assert.Equal("Bea", found.PersonName);
    }

    [Fact]
    public async Task EveryoneIsNullProfilesAndAnEmptySetIsNobody()
    {
        var alex = SeedProfile("Alex");
        var bea = SeedProfile("Bea");
        await AddWatchAsync(alex, Guid.NewGuid(), "Tremors", _start);
        await AddWatchAsync(bea, Guid.NewGuid(), "Dune", _start.AddDays(1));

        var everyone = await _repository.GetItemsAsync(new PersonalActivityFilter { ProfileIds = null }, PersonalActivityGrouping.None, null, 50);
        var nobody = await _repository.GetItemsAsync(new PersonalActivityFilter { ProfileIds = new HashSet<Guid>() }, PersonalActivityGrouping.None, null, 50);

        Assert.Equal(2, everyone.Items.Count);
        Assert.Empty(nobody.Items);
    }

    [Fact]
    public async Task Paging_WalksEveryItemOnceNewestFirstAndNeverSplitsAMusicSession()
    {
        var person = SeedProfile("Alex");
        for (var day = 0; day < 5; day++)
        {
            await AddWatchAsync(person, Guid.NewGuid(), $"Film {day}", _start.AddDays(day * 2));
        }

        await AddSongAsync(person, "One", "Album", _start.AddDays(3));
        await AddSongAsync(person, "Two", "Album", _start.AddDays(3).AddMinutes(4));
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await _repository.GetItemsAsync(
                new PersonalActivityFilter { ProfileIds = Ids(person) }, PersonalActivityGrouping.None, cursor, 2);
            seen.AddRange(page.Items.Select(item => item.IsMusicSession ? "Music:" + item.Entries.Count : item.Entries[0].Title));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(["Film 4", "Film 3", "Film 2", "Music:2", "Film 1", "Film 0"], seen);
    }

    [Fact]
    public async Task Paging_WithAnInvalidCursor_IsRefusedAsABadArgument()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _repository.GetItemsAsync(
            new PersonalActivityFilter { ProfileIds = null }, PersonalActivityGrouping.None, "not-a-cursor", 10));
    }

    [Fact]
    public async Task EntriesOneByOne_ExpandMusicSessionsIntoTheirSongs()
    {
        var person = SeedProfile("Alex");
        await AddSongAsync(person, "One", "Album", _start);
        await AddSongAsync(person, "Two", "Album", _start.AddMinutes(4));
        await AddWatchAsync(person, Guid.NewGuid(), "Tremors", _start.AddDays(1));

        var (entries, next) = await _repository.GetEntriesAsync(new PersonalActivityFilter { ProfileIds = Ids(person) }, null, 10);

        Assert.Equal(["Tremors", "Two", "One"], entries.Select(entry => entry.Title));
        Assert.Null(next);
    }

    // ----- The date rail -----

    [Fact]
    public async Task Periods_CountItemsPerYearAndMonthAndFollowTheFilters()
    {
        var person = SeedProfile("Alex");
        await AddWatchAsync(person, Guid.NewGuid(), "A", new DateTimeOffset(2024, 3, 5, 10, 0, 0, TimeSpan.Zero));
        await AddWatchAsync(person, Guid.NewGuid(), "B", new DateTimeOffset(2024, 3, 20, 10, 0, 0, TimeSpan.Zero));
        await AddWatchAsync(person, Guid.NewGuid(), "C", new DateTimeOffset(2025, 12, 31, 23, 59, 0, TimeSpan.Zero));
        await _repository.AddMyListAsync(person, new DateTimeOffset(2025, 12, 1, 8, 0, 0, TimeSpan.Zero),
            new PersonalActivitySnapshot("Books", "D", null, null, null, Guid.NewGuid(), null, null));

        var all = await _repository.GetPeriodsAsync(new PersonalActivityFilter { ProfileIds = Ids(person) }, PersonalActivityGrouping.None);
        var onlyMyList = await _repository.GetPeriodsAsync(
            new PersonalActivityFilter { ProfileIds = Ids(person), Kinds = [PersonalActivityKinds.MyList] }, PersonalActivityGrouping.None);

        Assert.Equal([new PersonalActivityPeriodCount(2025, 12, 2), new PersonalActivityPeriodCount(2024, 3, 2)], all);
        Assert.Equal([new PersonalActivityPeriodCount(2025, 12, 1)], onlyMyList);
    }

    // ----- Deleting history -----

    [Fact]
    public async Task NothingDeletesActivityAutomatically()
    {
        var person = SeedProfile("Alex");
        await AddWatchAsync(person, Guid.NewGuid(), "Ancient", new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero));

        // A restart runs the startup checks again; nothing there prunes history.
        using var restarted = new DatabaseConnection(_path);
        restarted.InitializeSchema();
        restarted.RunStartupChecks();
        var page = await new PersonalActivityRepository(restarted).GetItemsAsync(
            new PersonalActivityFilter { ProfileIds = Ids(person) }, PersonalActivityGrouping.None, null, 10);

        Assert.Single(page.Items);
    }

    [Fact]
    public async Task PreviewAndDelete_CountAndRemoveOnlyEntriesBeforeTheDateInTheScope()
    {
        var alex = SeedProfile("Alex");
        var bea = SeedProfile("Bea");
        var cutoff = _start.AddDays(10);
        await AddWatchAsync(alex, Guid.NewGuid(), "Old Alex", _start);
        await AddWatchAsync(alex, Guid.NewGuid(), "New Alex", cutoff.AddDays(1));
        await AddWatchAsync(bea, Guid.NewGuid(), "Old Bea", _start);

        Assert.Equal(1, await _repository.CountBeforeAsync(cutoff, Ids(alex)));
        Assert.Equal(2, await _repository.CountBeforeAsync(cutoff, null));
        Assert.Equal(0, await _repository.CountBeforeAsync(cutoff, new HashSet<Guid>()));

        Assert.Equal(1, await _repository.DeleteBeforeAsync(cutoff, Ids(alex), Audit));

        Assert.Equal(["New Alex"], await Titles(new PersonalActivityFilter(), alex));
        Assert.Equal(["Old Bea"], await Titles(new PersonalActivityFilter(), bea));
    }

    [Fact]
    public async Task Delete_RemovesPicturesNothingUsesAnyMore_AndKeepsTheOnesStillUsed()
    {
        var alex = SeedProfile("Alex");
        var bea = SeedProfile("Bea");
        var shared = new string('b', 64);
        var onlyOld = new string('c', 64);
        foreach (var key in new[] { shared, onlyOld })
        {
            await _repository.SaveArtworkAsync(new PersonalActivityArtwork(key, [9], "image/jpeg", 10, 10));
        }

        await AddWatchAsync(alex, Guid.NewGuid(), "Old", _start, artwork: shared);
        await AddWatchAsync(alex, Guid.NewGuid(), "Older", _start.AddDays(1), artwork: onlyOld);
        await AddWatchAsync(bea, Guid.NewGuid(), "New", _start.AddDays(30), artwork: shared);

        await _repository.DeleteBeforeAsync(_start.AddDays(10), null, Audit);

        Assert.True(await _repository.HasArtworkAsync(shared));
        Assert.False(await _repository.HasArtworkAsync(onlyOld));
    }

    [Fact]
    public async Task Delete_WritesItsAuditEntryInTheSameTransaction_WithTheNumberRemoved()
    {
        var alex = SeedProfile("Alex");
        await AddWatchAsync(alex, Guid.NewGuid(), "Old", _start);
        await AddWatchAsync(alex, Guid.NewGuid(), "Older", _start.AddDays(1));

        var removed = await _repository.DeleteBeforeAsync(_start.AddDays(10), Ids(alex), Audit);

        Assert.Equal(2, removed);
        using var connection = _database.CreateConnection();
        var changes = connection.ExecuteScalar<string>(
            "SELECT changes_json FROM authorization_audit_events WHERE event_type = 'activity.history_deleted';");
        Assert.Contains("\"count\":\"2\"", changes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_WhoseAuditEntryCannotBeWritten_DeletesNothing()
    {
        var alex = SeedProfile("Alex");
        var key = new string('7', 64);
        await _repository.SaveArtworkAsync(new PersonalActivityArtwork(key, [9], "image/jpeg", 10, 10));
        await AddWatchAsync(alex, Guid.NewGuid(), "Old", _start, artwork: key);

        // An actor account that does not exist breaks the audit table's reference, as a real write failure would.
        await Assert.ThrowsAnyAsync<Exception>(() => _repository.DeleteBeforeAsync(
            _start.AddDays(10), Ids(alex),
            count => new AuthorizationAuditEvent("activity.history_deleted", _start, Guid.NewGuid(), null, null, "everyone", "everyone",
                new Dictionary<string, string?> { ["count"] = count.ToString() })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.DeleteBeforeAsync(
            _start.AddDays(10), Ids(alex), _ => throw new InvalidOperationException("The audit log is unavailable.")));

        Assert.Single((await Items(alex)).Items);
        Assert.True(await _repository.HasArtworkAsync(key));
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM authorization_audit_events;"));
    }

    [Fact]
    public async Task Delete_NeverRemovesAPictureSavedForAnEntryThatIsNotWrittenYet()
    {
        var alex = SeedProfile("Alex");
        var pending = new string('8', 64);
        await _repository.SaveArtworkAsync(new PersonalActivityArtwork(pending, [9], "image/jpeg", 10, 10));
        await AddWatchAsync(alex, Guid.NewGuid(), "Old", _start);

        await _repository.DeleteBeforeAsync(_start.AddDays(10), null, Audit);
        await AddWatchAsync(alex, Guid.NewGuid(), "New", _start.AddDays(20), artwork: pending);

        Assert.True(await _repository.HasArtworkAsync(pending));
        Assert.Equal(pending, Assert.Single(Assert.Single((await Items(alex)).Items).Entries).ArtworkKey);
    }

    [Fact]
    public async Task AnEntryWhosePictureIsGone_IsKeptWithoutAPictureNotWithADanglingKey()
    {
        var alex = SeedProfile("Alex");

        await AddWatchAsync(alex, Guid.NewGuid(), "Tremors", _start, artwork: new string('9', 64));
        await _repository.AddMyListAsync(alex, _start.AddDays(1),
            new PersonalActivitySnapshot("Movies", "Dune", null, null, 2021, Guid.NewGuid(), null, new string('9', 64)));

        Assert.All((await Items(alex)).Items, item => Assert.Null(item.Entries[0].ArtworkKey));
    }

    [Fact]
    public async Task FindingAPersonsPage_UsesTheProfileIndexNotAScan()
    {
        var alex = SeedProfile("Alex");
        await AddWatchAsync(alex, Guid.NewGuid(), "Tremors", _start);
        var filter = new PersonalActivityFilter { ProfileIds = Ids(alex, SeedProfile("Bea")), Kinds = [PersonalActivityKinds.Watch] };
        var (where, parameters) = PersonalActivityRepository.Where(filter);
        parameters.Add("cursorAt", null);
        parameters.Add("cursorKey", null);
        parameters.Add("limit", 51);

        using var connection = _database.CreateConnection();
        var plan = (await connection.QueryAsync(
                "EXPLAIN QUERY PLAN " + PersonalActivityRepository.ItemKeysSql(where, PersonalActivityRepository.ItemKey(PersonalActivityGrouping.None)),
                parameters))
            .Select(row => (string)((IDictionary<string, object>)row)["detail"])
            .ToList();

        Assert.Contains(plan, line => line.Contains("idx_personal_activity_profile_", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN a", StringComparison.Ordinal) || line.StartsWith("SCAN personal_activity", StringComparison.Ordinal));
    }

    // ----- Summary -----

    [Fact]
    public async Task TheSummary_TotalsTimePlayedByKind_AndSplitsListeningIntoMusicAndAudiobooks()
    {
        var alex = SeedProfile("Alex");
        var bea = SeedProfile("Bea");
        await AddWatchAsync(alex, Guid.NewGuid(), "Tremors", _start, played: 5000);
        await AddWatchAsync(alex, Guid.NewGuid(), "Dune", _start.AddDays(1), played: 100);
        await _repository.AddPlaybackAsync(alex, PersonalActivityKinds.Read, false, Guid.NewGuid(), _start.AddDays(2),
            new PersonalActivityDevice("Phone", "mobile"),
            new PersonalActivitySnapshot("Books", "Hyperion", null, null, 1989, Guid.NewGuid(), Guid.NewGuid(), null),
            new PersonalActivityProgress(_start.AddDays(2), 3600, 50, false));
        await _repository.AddPlaybackAsync(alex, PersonalActivityKinds.Listen, false, Guid.NewGuid(), _start.AddDays(3),
            new PersonalActivityDevice("Phone", "mobile"),
            new PersonalActivitySnapshot("Audiobooks", "Dune (audio)", null, null, 1965, Guid.NewGuid(), Guid.NewGuid(), null),
            new PersonalActivityProgress(_start.AddDays(3), 7200, 20, false));
        await AddSongAsync(alex, "One", "Album", _start.AddDays(4), playedSeconds: 200);
        await AddSongAsync(alex, "Two", "Album", _start.AddDays(4).AddMinutes(5), playedSeconds: 300);
        await AddWatchAsync(bea, Guid.NewGuid(), "Someone else", _start, played: 99999);

        var summary = await _repository.GetSummaryAsync(alex);

        Assert.Equal(5100, summary.WatchSeconds);
        Assert.Equal(3600, summary.ReadSeconds);
        Assert.Equal(500, summary.MusicSeconds);
        Assert.Equal(7200, summary.AudiobookSeconds);
        Assert.Equal(_start, summary.FirstActivityAt);
    }

    [Fact]
    public async Task TheSummary_CountsTitlesFinishedAndInProgress_NotSongsOrMyListAdds()
    {
        var alex = SeedProfile("Alex");
        var tremors = Guid.NewGuid();
        var dune = Guid.NewGuid();
        var arrival = Guid.NewGuid();
        // Finished once, then started again and stopped: finished over a lifetime, in progress now.
        await AddWatchAsync(alex, Guid.NewGuid(), "Tremors", _start, work: tremors, finished: true, percent: 100);
        await AddWatchAsync(alex, Guid.NewGuid(), "Tremors", _start.AddDays(5), work: tremors, finished: false, percent: 30);
        // Stopped, then finished: finished, not in progress.
        await AddWatchAsync(alex, Guid.NewGuid(), "Dune", _start.AddDays(1), work: dune, finished: false, percent: 40);
        await AddWatchAsync(alex, Guid.NewGuid(), "Dune", _start.AddDays(2), work: dune, finished: true, percent: 100);
        // Only ever stopped.
        await AddWatchAsync(alex, Guid.NewGuid(), "Arrival", _start.AddDays(3), work: arrival, finished: false, percent: 10);
        // A title whose item was deleted is counted by its title.
        await AddWatchAsync(alex, Guid.NewGuid(), "Gone", _start.AddDays(4), finished: false, withoutWork: true);
        await AddWatchAsync(alex, Guid.NewGuid(), "gone", _start.AddDays(6), finished: true, withoutWork: true);
        await AddSongAsync(alex, "One", "Album", _start.AddDays(7));
        await _repository.AddMyListAsync(alex, _start.AddDays(8),
            new PersonalActivitySnapshot("Movies", "Saved", null, null, null, Guid.NewGuid(), null, null));

        var summary = await _repository.GetSummaryAsync(alex);

        Assert.Equal(3, summary.FinishedCount);
        Assert.Equal(2, summary.InProgressCount);
    }

    [Fact]
    public async Task TheSummaryOfSomeoneWithNoActivity_IsAllZero()
    {
        var summary = await _repository.GetSummaryAsync(SeedProfile("Alex"));

        Assert.Equal(new PersonalActivitySummary(0, 0, 0, 0, 0, 0, null), summary);
    }

    // ----- Rolling 30 days and finished by month -----

    [Fact]
    public async Task TheRollingWindow_CountsTitlesFinishedAndDaysActive_InsideThirtyDaysOnly()
    {
        var alex = SeedProfile("Alex");
        var now = new DateTimeOffset(2026, 10, 29, 20, 0, 0, TimeSpan.Zero);
        var tremors = Guid.NewGuid();
        // The first day of the window is 30 Sep (29 days before today); 29 Sep is outside it.
        await AddWatchAsync(alex, Guid.NewGuid(), "Old", new DateTimeOffset(2026, 9, 29, 23, 0, 0, TimeSpan.Zero), finished: true, played: 100);
        await AddWatchAsync(alex, Guid.NewGuid(), "Edge", new DateTimeOffset(2026, 9, 30, 0, 30, 0, TimeSpan.Zero), finished: true, played: 200);
        // The same title finished twice counts once; two entries on one day make one active day.
        await AddWatchAsync(alex, Guid.NewGuid(), "Tremors", now.AddDays(-3), work: tremors, finished: true, played: 300);
        await AddWatchAsync(alex, Guid.NewGuid(), "Tremors", now.AddDays(-3).AddHours(2), work: tremors, finished: true, played: 400);
        await AddWatchAsync(alex, Guid.NewGuid(), "Unfinished", now.AddDays(-1), finished: false, played: 50);
        await AddSongAsync(alex, "One", "Album", now);
        await _repository.AddMyListAsync(alex, now.AddDays(-2),
            new PersonalActivitySnapshot("Movies", "Saved", null, null, null, Guid.NewGuid(), null, null));

        var rolling = await _repository.GetRollingAsync(alex, now, 30);

        Assert.Equal(2, rolling.FinishedTitles);
        Assert.Equal(4, rolling.DaysActive);
        Assert.Equal(
            [new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 29)],
            rolling.Days.Select(day => day.Date));
        Assert.Equal(700, rolling.Days.Single(day => day.Date == new DateOnly(2026, 10, 26)).PlayedSeconds);
        Assert.Equal(2, rolling.Days.Single(day => day.Date == new DateOnly(2026, 10, 26)).Entries);
    }

    [Fact]
    public async Task TheRollingWindowOfSomeoneWithNoActivity_IsEmpty()
    {
        var rolling = await _repository.GetRollingAsync(SeedProfile("Alex"), _start, 30);

        Assert.Equal(0, rolling.FinishedTitles);
        Assert.Equal(0, rolling.DaysActive);
        Assert.Empty(rolling.Days);
    }

    [Fact]
    public async Task FinishedByMonth_GroupsTitlesByFirstFinishedMonthAndGenre_AndLeavesOutTheRest()
    {
        var alex = SeedProfile("Alex");
        var (scifi, _) = SeedWork("Movies");
        var (scifiToo, _) = SeedWork("Movies");
        var (drama, _) = SeedWork("Movies");
        SeedGenre(scifi, "sci-fi");
        SeedGenre(scifiToo, "sci-fi");
        SeedGenre(drama, "drama");
        await AddWatchAsync(alex, Guid.NewGuid(), "Arrival", _start, work: scifi, finished: true);
        // Finished again a month later: counted once, in the month it was first finished.
        await AddWatchAsync(alex, Guid.NewGuid(), "Arrival", _start.AddDays(40), work: scifi, finished: true);
        await AddWatchAsync(alex, Guid.NewGuid(), "Dune", _start.AddDays(41), work: scifiToo, finished: true);
        await AddWatchAsync(alex, Guid.NewGuid(), "Marty", _start.AddDays(42), work: drama, finished: true);
        await AddWatchAsync(alex, Guid.NewGuid(), "Gone", _start.AddDays(43), finished: true, withoutWork: true);
        await AddWatchAsync(alex, Guid.NewGuid(), "Unfinished", _start.AddDays(44), finished: false);
        await AddWatchAsync(alex, Guid.NewGuid(), "Too early", _start.AddMonths(-3), finished: true);
        await AddSongAsync(alex, "One", "Album", _start.AddDays(45));

        var rows = await _repository.GetFinishedByMonthAsync(alex, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains(new PersonalFinishedMonthGenre("2026-09", "sci-fi", 1), rows);
        Assert.Contains(new PersonalFinishedMonthGenre("2026-10", "sci-fi", 1), rows);
        Assert.Contains(new PersonalFinishedMonthGenre("2026-10", "drama", 1), rows);
        Assert.Contains(new PersonalFinishedMonthGenre("2026-10", null, 1), rows);
        Assert.Equal(4, rows.Sum(row => row.Titles));
    }

    private void SeedGenre(Guid work, string genre)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO canonical_value_arrays(entity_id, key, ordinal, value) VALUES (@work, 'genre', 0, @genre);",
            new { work, genre });
    }

    private static AuthorizationAuditEvent Audit(int removed) => new(
        "activity.history_deleted", DateTimeOffset.UtcNow, null, null, null, "everyone", "everyone",
        new Dictionary<string, string?> { ["count"] = removed.ToString() });

    [Fact]
    public async Task ArtworkIsOnlyUsedByThePeopleWhoseEntriesCarryIt()
    {
        var alex = SeedProfile("Alex");
        var bea = SeedProfile("Bea");
        var key = new string('d', 64);
        await _repository.SaveArtworkAsync(new PersonalActivityArtwork(key, [9], "image/jpeg", 10, 10));
        await AddWatchAsync(alex, Guid.NewGuid(), "Tremors", _start, artwork: key);

        Assert.True(await _repository.IsArtworkUsedAsync(key, Ids(alex)));
        Assert.True(await _repository.IsArtworkUsedAsync(key, null));
        Assert.False(await _repository.IsArtworkUsedAsync(key, Ids(bea)));
        Assert.False(await _repository.IsArtworkUsedAsync(key, new HashSet<Guid>()));
    }

    [Fact]
    public async Task SavingTheSameArtworkTwice_KeepsTheFirstCopy()
    {
        var key = new string('e', 64);

        await _repository.SaveArtworkAsync(new PersonalActivityArtwork(key, [1], "image/jpeg", 10, 10));
        await _repository.SaveArtworkAsync(new PersonalActivityArtwork(key, [2, 2], "image/webp", 20, 20));

        var kept = await _repository.GetArtworkAsync(key);
        Assert.Equal([1], kept!.Image);
        Assert.Equal("image/jpeg", kept.ContentType);
    }

    // The Vault never shows in anyone's activity (Videos spec D17): not as an item, not in the date rail, not in the totals.
    [Fact]
    public async Task VaultActivity_IsAbsentFromTheListTheDateRailAndTheSummary()
    {
        var person = SeedProfile("Alex");
        var (normalWork, normalAsset) = SeedWork("Movies");
        var (vaultWork, vaultAsset) = SeedWork("Videos");
        await _repository.AddPlaybackAsync(person, PersonalActivityKinds.Watch, false, Guid.NewGuid(), _start,
            new PersonalActivityDevice("TV", "television"),
            new PersonalActivitySnapshot("Movies", "Ordinary film", null, null, 2000, normalWork, normalAsset, null),
            new PersonalActivityProgress(_start.AddMinutes(10), 600, 100, true));
        await _repository.AddPlaybackAsync(person, PersonalActivityKinds.Watch, false, Guid.NewGuid(), _start.AddDays(1),
            new PersonalActivityDevice("TV", "television"),
            new PersonalActivitySnapshot("Videos", "Secret scene", null, null, 2020, vaultWork, vaultAsset, null),
            new PersonalActivityProgress(_start.AddDays(1).AddMinutes(10), 9000, 100, true));
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at)
                SELECT library_id, 'none', @now, @now, @now FROM media_assets WHERE id = @vaultAsset;
                """, new { vaultAsset, now = _start.ToString("O") });
        }

        var filter = new PersonalActivityFilter { ProfileIds = Ids(person) };
        var titles = (await Items(person)).Items.Select(item => item.Entries[0].Title).ToList();
        var periods = await _repository.GetPeriodsAsync(filter, PersonalActivityGrouping.None);
        var summary = await _repository.GetSummaryAsync(person);

        Assert.Equal(["Ordinary film"], titles);
        Assert.Equal(1, periods.Sum(period => period.Count));
        Assert.Equal(600, summary.WatchSeconds);
        Assert.Equal(1, summary.FinishedCount);
    }

    // ----- Helpers -----

    private async Task<PersonalActivityItemPage> Items(Guid person) => await _repository.GetItemsAsync(
        new PersonalActivityFilter { ProfileIds = Ids(person) }, PersonalActivityGrouping.None, null, 100);

    private async Task<IReadOnlyList<string>> Titles(PersonalActivityFilter filter, Guid person, bool collapse = false)
    {
        var page = await _repository.GetItemsAsync(
            filter with { ProfileIds = Ids(person) }, PersonalActivityGrouping.None, null, 100);
        return page.Items.Select(item => collapse && item.IsMusicSession ? "Music" : item.Entries[0].Title).ToList();
    }

    private static HashSet<Guid> Ids(params Guid[] people) => [.. people];

    private Task<bool> AddWatchAsync(
        Guid person,
        Guid session,
        string title,
        DateTimeOffset at,
        Guid? work = null,
        double? percent = 10,
        long played = 600,
        bool finished = false,
        string? device = "Dashboard",
        string? artwork = null,
        bool withoutWork = false) => _repository.AddPlaybackAsync(
            person, PersonalActivityKinds.Watch, false, session, at, new PersonalActivityDevice(device, "web"),
            new PersonalActivitySnapshot("Movies", title, null, null, 2000, withoutWork ? null : work ?? Guid.NewGuid(), Guid.NewGuid(), artwork),
            new PersonalActivityProgress(at.AddSeconds(played), played, percent, finished));

    private Task<bool> AddSongAsync(Guid person, string title, string album, DateTimeOffset at, long playedSeconds = 200) =>
        _repository.AddPlaybackAsync(
            person, PersonalActivityKinds.Listen, true, Guid.NewGuid(), at, new PersonalActivityDevice("Car", "automotive"),
            new PersonalActivitySnapshot("Music", title, "Pink Floyd", album, 1975, Guid.NewGuid(), Guid.NewGuid(), null),
            new PersonalActivityProgress(at.AddSeconds(playedSeconds), playedSeconds, 100, false));

    private Guid SeedProfile(string name)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles (id, display_name, created_at) VALUES (@id, @name, @now);",
            new { id, name, now = _start.ToString("O") });
        return id;
    }

    private (Guid Work, Guid Asset) SeedWork(string mediaType)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works(id,media_type,work_kind,is_catalog_only,ownership) VALUES(@work,@mediaType,'standalone',0,'Owned');
            INSERT INTO editions(id,work_id) VALUES(@edition,@work);
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,library_id) VALUES(@asset,@edition,@hash,@path,@library);
            """, new
        {
            work,
            edition,
            asset,
            mediaType,
            hash = asset.ToString("N"),
            path = $"C:/fixtures/{asset:N}.media",
            library = Guid.NewGuid().ToString("D"),
        });
        return (work, asset);
    }
}
