using System.IO.Compression;
using System.Text;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Backup;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests.Backup;

/// <summary>
/// A backup restored onto a data store: settings go straight back, a person's own state waits for its title and is put back, exactly,
/// when the same title returns (even as a different file), and nothing is guessed. Each test builds a real source data store, exports
/// it, imports the export into a fresh target and then lets the title arrive.
/// </summary>
public sealed class BackupRestoreTests : IDisposable
{
    private static readonly DateTimeOffset RestoredAt = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly string Stamp = "2026-10-21T10:00:00Z";

    /// <summary>What the player saves with a file's progress: where it stopped and the length of the cut, plus facts about the device.</summary>
    private const string SavedTiming = """{"position_seconds":"4000.5","duration_seconds":"9300.5","playback_state":"paused","player_client":"web"}""";

    private readonly List<Store> _stores = [];

    // ---------------------------------------------------------------------------------------------------------
    // Restoring
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Import_PutsSettingsBackAndParksTitleStateUntilTheTitleReturns()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var movie = source.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: "438631", hash: "aaaa1111", seconds: 9300.5);
        source.AddUserState(profile, movie.Asset, 42.5);
        source.AddAudiobookBookmark(profile, movie, 120.0, "Spice");

        var result = Import(source, target);

        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM profiles WHERE display_name = 'Maya'"));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM user_states"));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM audiobook_bookmarks"));
        Assert.Equal(2, target.Scalar("SELECT COUNT(*) FROM waiting_personal_state"));
        Assert.Equal(2, result.WaitingRows);
        Assert.Equal(0, result.RowsDropped);
        Assert.True(result.AppliedRows > 0);
        Assert.True(result.ConfigurationTables > 0);

        var waiting = new WaitingPersonalStateRepository(target.Database);
        Assert.Equal(2, waiting.CountWaiting(includeWithheld: false));
        var summary = Assert.IsType<RestoreSummary>(waiting.GetLatestRestore());
        Assert.Equal("backup-2026-10-21.zip", summary.BackupFileName);
        Assert.Equal(2, summary.WaitingRows);
        Assert.Equal(0, summary.AttachedRows);

        // Each row records when it started waiting, and carries only how to recognise its title.
        Assert.Equal(2, target.Scalar($"SELECT COUNT(*) FROM waiting_personal_state WHERE created_at = '{RestoredRowSql.Timestamp(RestoredAt)}'"));
        var titles = target.Text("SELECT group_concat(titles_json, '') FROM waiting_personal_state");
        Assert.Contains("tt1160419", titles);
        Assert.Contains("aaaa1111", titles);
    }

    [Fact]
    public void Import_RestoresTheTwoStepSecretThroughTheCodec_AndRefusesWithoutOne()
    {
        var source = NewStore();
        source.AddTwoStepSecret("SECRET-MARKER");
        var export = new PersonalStateBackupRepository(source.Database, new FakeCodec()).Export(BackupScope.WholeLibrary);
        var target = NewStore();
        var refused = NewStore();

        var result = new BackupRestoreRepository(target.Database).Import(export, "backup.zip", null, new FakeCodec(), RestoredAt);

        Assert.Equal(1, result.PortableValuesRestored);
        Assert.Equal("sealed:readable:SECRET-MARKER", target.Text("SELECT secret_protected FROM account_two_step"));

        var restore = new BackupRestoreRepository(refused.Database);
        Assert.Throws<InvalidOperationException>(() => restore.Import(export, "backup.zip", null, null, RestoredAt));

        // A refused restore leaves nothing behind.
        Assert.Equal(0, refused.Scalar("SELECT COUNT(*) FROM account_two_step"));
        Assert.Equal(0, refused.Scalar("SELECT COUNT(*) FROM accounts"));
        Assert.Equal(0, refused.Scalar("SELECT COUNT(*) FROM backup_restores"));
    }

    [Fact]
    public void Import_KeepsTheItemsOfAWaitingBookmarkPackWithIt()
    {
        var source = NewStore();
        var profile = source.AddProfile("Maya");
        var movie = source.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: 9300.5);
        source.AddBookmarkPack(profile, movie, itemCount: 2);
        var target = NewStore();

        Import(source, target);

        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM bookmark_packs"));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM bookmark_pack_items"));
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM waiting_personal_state"));

        target.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "bbbb2222", seconds: 9300.5);
        var pass = new WaitingPersonalStateRepository(target.Database).RunPass(RestoredAt.AddMinutes(5), 100, 90);

        Assert.Equal(1, pass.Attached);
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM bookmark_packs"));
        Assert.Equal(2, target.Scalar("SELECT COUNT(*) FROM bookmark_pack_items"));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Coming back
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Reattach_BringsBookmarksAndPositionBack_WhenTheSameTitleReturnsWithADifferentFile()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var movie = source.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: "438631", hash: "aaaa1111", seconds: 9300.5);
        source.AddUserState(profile, movie.Asset, 42.5, SavedTiming);
        source.AddAudiobookBookmark(profile, movie, 120.0, "Spice");
        source.AddDevicePosition(profile, movie, "living-room", 3000.0);
        Import(source, target);
        var repository = new WaitingPersonalStateRepository(target.Database);

        // Nothing has arrived yet: everything keeps waiting.
        Assert.Equal(0, repository.RunPass(RestoredAt.AddMinutes(1), 100, 90).Attached);

        // The same title returns as a re-encoded file: another hash, the same ids and running time.
        var returned = target.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: "438631", hash: "bbbb2222", seconds: 9300.5);
        var pass = repository.RunPass(RestoredAt.AddMinutes(2), 100, 90);

        Assert.Equal(3, pass.Attached);
        Assert.Equal(0, pass.StillWaiting);
        Assert.Equal(42.5, target.Real("SELECT progress_pct FROM user_states WHERE asset_id = @a", ("a", returned.Asset)));

        // The row carries the fingerprint of the file it now sits on, not the one the backup was made from.
        Assert.Equal("bbbb2222", target.Text("SELECT content_hash FROM user_states WHERE asset_id = @a", ("a", returned.Asset)));

        // On the same cut the saved timing comes back whole.
        Assert.Equal(SavedTiming, target.Text("SELECT extended_properties FROM user_states WHERE asset_id = @a", ("a", returned.Asset)));
        Assert.Equal(120.0, target.Real("SELECT position_seconds FROM audiobook_bookmarks WHERE asset_id = @a AND work_id = @w", ("a", returned.Asset), ("w", returned.Work)));
        Assert.Equal(3000.0, target.Real("SELECT position_seconds FROM player_device_states WHERE asset_id = @a", ("a", returned.Asset)));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM waiting_personal_state"));
        var summary = Assert.IsType<RestoreSummary>(repository.GetLatestRestore());
        Assert.Equal(3, summary.AttachedRows);
    }

    [Fact]
    public void Reattach_DoesNotMatchAFileWithNoIdsAndADifferentHash()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var scan = source.AddMovie("Family Scan", "2001", imdb: null, tmdb: null, hash: "aaaa1111", seconds: 600.0);
        source.AddUserState(profile, scan.Asset, 50.0);
        Import(source, target);

        // Same name, same length, no ids and another file: nothing proves it is the same title.
        target.AddMovie("Family Scan", "2001", imdb: null, tmdb: null, hash: "cccc3333", seconds: 600.0);
        var pass = new WaitingPersonalStateRepository(target.Database).RunPass(RestoredAt.AddMinutes(1), 100, 90);

        Assert.Equal(0, pass.Attached);
        Assert.Equal(1, pass.StillWaiting);
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM user_states"));
    }

    [Fact]
    public void Reattach_UsesTheExactFileWhenTheTitleHasNoIds()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var scan = source.AddMovie("Family Scan", "2001", imdb: null, tmdb: null, hash: "aaaa1111", seconds: 600.0);
        source.AddUserState(profile, scan.Asset, 50.0);
        Import(source, target);

        var same = target.AddMovie("Family Scan", "2001", imdb: null, tmdb: null, hash: "aaaa1111", seconds: 600.0);
        var pass = new WaitingPersonalStateRepository(target.Database).RunPass(RestoredAt.AddMinutes(1), 100, 90);

        Assert.Equal(1, pass.Attached);
        Assert.Equal(50.0, target.Real("SELECT progress_pct FROM user_states WHERE asset_id = @a", ("a", same.Asset)));
    }

    [Fact]
    public void Reattach_LeavesAmbiguousTitlesWaiting()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var movie = source.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: 9300.5);
        source.AddUserState(profile, movie.Asset, 42.5);
        Import(source, target);

        // Two files that are both this title and this cut: which one is it? Nobody can say, so nothing moves.
        target.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "bbbb2222", seconds: 9300.5);
        target.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "cccc3333", seconds: 9300.5);
        var repository = new WaitingPersonalStateRepository(target.Database);
        var pass = repository.RunPass(RestoredAt.AddMinutes(1), 100, 90);

        Assert.Equal(0, pass.Attached);
        Assert.Equal(1, pass.StillWaiting);
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM user_states"));
    }

    [Fact]
    public void Reattach_AlignsBookmarksByChapterOnADifferentCut_AndLeavesUnalignableOnesWaiting()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var movie = source.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: 9300.5);
        source.AddAudiobookBookmark(profile, movie, 100.0, "Chapter 2");
        source.AddAudiobookBookmark(profile, movie, 200.0, "A chapter this cut lacks");
        source.AddDevicePosition(profile, movie, "living-room", 3000.0);
        source.AddUserState(profile, movie.Asset, 42.5, SavedTiming);
        source.AddConsumption(profile, movie.Asset, 30.0);
        Import(source, target);

        // The director's cut: the same title, a different running time, and the chapter somewhere else.
        var cut = target.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "bbbb2222", seconds: 8000.0);
        target.AddChapter(cut.Asset, 1, "Chapter 2", 50_000);
        var pass = new WaitingPersonalStateRepository(target.Database).RunPass(RestoredAt.AddMinutes(1), 100, 90);

        // The bookmark with a chapter this cut has lands in that chapter; a progress percent needs no alignment, and the saved timing
        // comes with its percent but without the seconds that belonged to the other cut.
        Assert.Equal(3, pass.Attached);
        Assert.Equal(2, pass.StillWaiting);
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM audiobook_bookmarks"));
        Assert.Equal(50.0, target.Real("SELECT position_seconds FROM audiobook_bookmarks WHERE chapter_title = 'Chapter 2'"));
        Assert.Equal(30.0, target.Real("SELECT progress_pct FROM consumption_history WHERE asset_id = @a", ("a", cut.Asset)));

        // A position with no chapter name means nothing on another cut: it keeps waiting.
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM player_device_states"));
    }

    [Fact]
    public void Reattach_BringsProgressBackOnADifferentCut_WithoutTheSecondsOfTheOtherCut()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var movie = source.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: 9300.5);
        source.AddUserState(profile, movie.Asset, 42.5, SavedTiming);
        Import(source, target);

        var cut = target.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "bbbb2222", seconds: 8000.0);
        var pass = new WaitingPersonalStateRepository(target.Database).RunPass(RestoredAt.AddMinutes(1), 100, 90);

        Assert.Equal(1, pass.Attached);
        Assert.Equal(0, pass.StillWaiting);
        Assert.Equal(42.5, target.Real("SELECT progress_pct FROM user_states WHERE asset_id = @a", ("a", cut.Asset)));

        // The seconds are gone; everything else the player saved stays.
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM user_states, json_each(extended_properties) WHERE key IN ('position_seconds', 'duration_seconds')"));
        Assert.Equal("paused", target.Text("SELECT json_extract(extended_properties, '$.playback_state') FROM user_states WHERE asset_id = @a", ("a", cut.Asset)));
        Assert.Equal("web", target.Text("SELECT json_extract(extended_properties, '$.player_client') FROM user_states WHERE asset_id = @a", ("a", cut.Asset)));
    }

    [Fact]
    public async Task Reattach_AppliesATypedCorrectionAndItWinsOverTheProvider()
    {
        var source = NewStore();
        var target = NewStore();
        var movie = source.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: 9300.5);
        await source.AddClaimAsync(movie.Work, WellKnownProviders.UserManual, "title", "My Own Title", locked: true);
        Import(source, target);

        var returned = target.AddMovie("Provider Title", "2021", imdb: "tt1160419", tmdb: null, hash: "bbbb2222", seconds: 9300.5);
        await target.AddClaimAsync(returned.Work, WellKnownProviders.Tmdb, "title", "Provider Title");
        var pass = new WaitingPersonalStateRepository(target.Database).RunPass(RestoredAt.AddMinutes(1), 100, 90);

        Assert.Equal(1, pass.Attached);
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM metadata_claims WHERE is_user_locked = 1 AND claim_value = 'My Own Title' AND entity_id = @w", ("w", returned.Work)));
        Assert.Equal("My Own Title", target.Text("SELECT value FROM canonical_values WHERE entity_id = @w AND key = 'title'", ("w", returned.Work)));
        Assert.Equal(1, target.Scalar(
            "SELECT COUNT(*) FROM canonical_values WHERE entity_id = @w AND key = 'title' AND winning_provider_id = @p",
            ("w", returned.Work),
            ("p", GuidSql.ToBlob(WellKnownProviders.UserManual))));
    }

    [Fact]
    public void Reattach_KeepsALiveRowThePersonMadeAfterTheRestore()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var movie = source.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: 9300.5);
        source.AddUserState(profile, movie.Asset, 42.5);
        Import(source, target);

        // The file returns and the person watches a little before the Library has looked for their old state.
        var returned = target.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "bbbb2222", seconds: 9300.5);
        target.AddUserState(profile, returned.Asset, 7.0);
        var repository = new WaitingPersonalStateRepository(target.Database);
        var pass = repository.RunPass(RestoredAt.AddMinutes(1), 100, 90);

        // Theirs stays, the waiting row is gone, and it is counted as yielded rather than attached.
        Assert.Equal(0, pass.Attached);
        Assert.Equal(1, pass.Yielded);
        Assert.Equal(7.0, target.Real("SELECT progress_pct FROM user_states WHERE asset_id = @a", ("a", returned.Asset)));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM waiting_personal_state"));
        var summary = Assert.IsType<RestoreSummary>(repository.GetLatestRestore());
        Assert.Equal(0, summary.AttachedRows);
        Assert.Equal(1, summary.YieldedRows);
    }

    [Fact]
    public void Reattach_YieldsAWaitingBookmarkPackWithItsItems_WhenThePackWasImportedAgainAfterTheRestore()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var movie = source.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: 9300.5);
        source.AddBookmarkPack(profile, movie, itemCount: 2);
        Import(source, target);

        // The person imports the same pack again once the title is back: the same person, title and pack fingerprint.
        var returned = target.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "bbbb2222", seconds: 9300.5);
        target.AddBookmarkPack(profile, returned, itemCount: 0);
        var repository = new WaitingPersonalStateRepository(target.Database);
        var pass = repository.RunPass(RestoredAt.AddMinutes(1), 100, 90);

        // The waiting pack yields with its items (they would point at a pack that was never made): no failure, no retry.
        Assert.Equal(0, pass.Attached);
        Assert.Equal(1, pass.Yielded);
        Assert.Equal(0, pass.FailedRows);
        Assert.Equal(0, pass.StillWaiting);
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM bookmark_packs"));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM bookmark_pack_items"));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM waiting_personal_state"));
        Assert.Equal(0, repository.RunPass(RestoredAt.AddMinutes(2), 100, 90).Checked);
    }

    [Fact]
    public void Reattach_ReportsARowThatCouldNotBeWrittenByItsTable_AndKeepsItWaiting()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var movie = source.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: 9300.5);
        source.AddUserState(profile, movie.Asset, 42.5);
        Import(source, target);
        target.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "bbbb2222", seconds: 9300.5);
        target.Execute("CREATE TRIGGER refuse_user_states BEFORE INSERT ON user_states BEGIN SELECT RAISE(ABORT, 'refused'); END");

        var pass = new WaitingPersonalStateRepository(target.Database).RunPass(RestoredAt.AddMinutes(1), 100, 90);

        Assert.Equal(0, pass.Attached);
        Assert.Equal(1, pass.FailedRows);
        Assert.Equal(["user_states"], pass.FailedTables);
        Assert.Equal(1, pass.StillWaiting);
    }

    [Fact]
    public void Reattach_RestoresAChoiceOnlyWhereTheColumnIsStillAtItsDefault()
    {
        var source = NewStore();
        var target = NewStore();
        var untouched = source.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: null);
        var changed = source.AddMovie("Arrival", "2016", imdb: "tt2543164", tmdb: null, hash: "dddd4444", seconds: null);
        source.SetDisplayOverrides(untouched.Work, """{"alias":"Dune (mine)"}""");
        source.SetDisplayOverrides(changed.Work, """{"alias":"Arrival (mine)"}""");
        Import(source, target);

        var first = target.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "bbbb2222", seconds: null);
        var second = target.AddMovie("Arrival", "2016", imdb: "tt2543164", tmdb: null, hash: "eeee5555", seconds: null);
        target.SetDisplayOverrides(second.Work, """{"alias":"set since the restore"}""");
        var pass = new WaitingPersonalStateRepository(target.Database).RunPass(RestoredAt.AddMinutes(1), 100, 90);

        Assert.Equal(2, pass.Attached);
        Assert.Equal("""{"alias":"Dune (mine)"}""", target.Text("SELECT display_overrides_json FROM works WHERE id = @w", ("w", first.Work)));
        Assert.Equal("""{"alias":"set since the restore"}""", target.Text("SELECT display_overrides_json FROM works WHERE id = @w", ("w", second.Work)));
    }

    [Fact]
    public void Reattach_BringsProgressBackOnAnotherEditionOfTheSameBook()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var hardcover = source.AddBook("Dune", qid: "Q190192", isbn: "9780441013593", hash: "aaaa1111", edition: "Hardcover");
        source.AddConsumption(profile, hardcover.Asset, 60.0);
        source.AddAudiobookBookmark(profile, hardcover, 100.0, null);
        Import(source, target);

        // The ebook of the same book: the same work, another ISBN, another edition and another file.
        var ebook = target.AddBook("Dune", qid: "Q190192", isbn: "9781473233959", hash: "bbbb2222", edition: "Ebook");
        var pass = new WaitingPersonalStateRepository(target.Database).RunPass(RestoredAt.AddMinutes(1), 100, 90);

        // A progress percent means the same on any edition; a position in seconds with no chapter name does not.
        Assert.Equal(1, pass.Attached);
        Assert.Equal(1, pass.StillWaiting);
        Assert.Equal(60.0, target.Real("SELECT progress_pct FROM consumption_history WHERE asset_id = @a", ("a", ebook.Asset)));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM audiobook_bookmarks"));
    }

    [Fact]
    public void Reattach_MatchesByISBNWhenThatIsTheOnlyId()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var book = source.AddBook("Dune", qid: null, isbn: "9780441013593", hash: "aaaa1111", edition: "Hardcover");
        source.AddConsumption(profile, book.Asset, 60.0);
        source.AddAudiobookBookmark(profile, book, 100.0, null);
        Import(source, target);

        // The same edition re-encoded under another file name: the ISBN agrees, so it is the same edition.
        var other = target.AddBook("Dune", qid: null, isbn: "9780441013593", hash: "bbbb2222", edition: "Hardcover");
        var wrong = target.AddBook("Another Book", qid: null, isbn: "9780000000002", hash: "cccc3333", edition: "Hardcover");
        var pass = new WaitingPersonalStateRepository(target.Database).RunPass(RestoredAt.AddMinutes(1), 100, 90);

        Assert.Equal(2, pass.Attached);
        Assert.Equal(60.0, target.Real("SELECT progress_pct FROM consumption_history WHERE asset_id = @a", ("a", other.Asset)));
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM audiobook_bookmarks WHERE asset_id = @a", ("a", other.Asset)));
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM consumption_history WHERE asset_id = @a", ("a", wrong.Asset)));
    }

    [Fact]
    public void Reattach_DoesNotMatchAnotherBookWhoseOnlyIdIsADifferentISBN()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var book = source.AddBook("Dune", qid: null, isbn: "9780441013593", hash: "aaaa1111", edition: "Hardcover");
        source.AddConsumption(profile, book.Asset, 60.0);
        Import(source, target);

        // With no title-level id in common, a different ISBN is a different book.
        target.AddBook("Dune", qid: null, isbn: "9781473233959", hash: "bbbb2222", edition: "Hardcover");
        var pass = new WaitingPersonalStateRepository(target.Database).RunPass(RestoredAt.AddMinutes(1), 100, 90);

        Assert.Equal(0, pass.Attached);
        Assert.Equal(1, pass.StillWaiting);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Waiting
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Purge_ClearsWaitingStateAfterTheWaitingPeriod_AndCountsIt()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var movie = source.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: 9300.5);
        source.AddUserState(profile, movie.Asset, 42.5);
        Import(source, target);
        var repository = new WaitingPersonalStateRepository(target.Database);

        var early = repository.RunPass(RestoredAt.AddDays(29), 100, 30);
        Assert.Equal(0, early.Purged);
        Assert.Equal(1, early.StillWaiting);

        var late = repository.RunPass(RestoredAt.AddDays(31), 100, 30);
        Assert.Equal(1, late.Purged);
        Assert.Equal(0, late.StillWaiting);
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM waiting_personal_state"));
        var summary = Assert.IsType<RestoreSummary>(repository.GetLatestRestore());
        Assert.Equal(1, summary.PurgedRows);
    }

    [Fact]
    public void Purge_ReadsTheWaitingPeriodLive_SoChangingItAffectsRowsAlreadyWaiting()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var movie = source.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: 9300.5);
        source.AddUserState(profile, movie.Asset, 42.5);
        Import(source, target);
        var repository = new WaitingPersonalStateRepository(target.Database);

        // Ten days in with a 30 day period: still waiting. The same day with the period shortened to 7: cleared.
        Assert.Equal(0, repository.RunPass(RestoredAt.AddDays(10), 100, 30).Purged);
        var shortened = repository.RunPass(RestoredAt.AddDays(10).AddMinutes(1), 100, 7);

        Assert.Equal(1, shortened.Purged);
        Assert.Equal(0, target.Scalar("SELECT COUNT(*) FROM waiting_personal_state"));
    }

    [Fact]
    public void WaitingRows_SurviveABackupTakenBeforeTheyAttach()
    {
        var source = NewStore();
        var middle = NewStore();
        var last = NewStore();
        var profile = source.AddProfile("Maya");
        var movie = source.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "aaaa1111", seconds: 9300.5);
        source.AddUserState(profile, movie.Asset, 42.5);
        Import(source, middle);
        var originalStart = middle.Text("SELECT created_at FROM waiting_personal_state");

        // A backup taken while the state still waits, restored later on another data store: the clock does not restart.
        var second = new BackupRestoreRepository(last.Database).Import(
            new PersonalStateBackupRepository(middle.Database).Export(BackupScope.WholeLibrary),
            "later.zip",
            null,
            null,
            RestoredAt.AddDays(10));

        Assert.Equal(1, second.WaitingRows);
        Assert.Equal(originalStart, last.Text("SELECT created_at FROM waiting_personal_state"));
        Assert.Equal(1, last.Scalar("SELECT COUNT(*) FROM profiles WHERE display_name = 'Maya'"));

        var returned = last.AddMovie("Dune", "2021", imdb: "tt1160419", tmdb: null, hash: "bbbb2222", seconds: 9300.5);
        var pass = new WaitingPersonalStateRepository(last.Database).RunPass(RestoredAt.AddDays(11), 100, 90);

        Assert.Equal(1, pass.Attached);
        Assert.Equal(42.5, last.Real("SELECT progress_pct FROM user_states WHERE asset_id = @a", ("a", returned.Asset)));
    }

    // ---------------------------------------------------------------------------------------------------------
    // The Vault
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task VaultTitles_ExportWithNoNameOrYear_KeepTheirStashBoxRecord_AndWaitOutOfSight()
    {
        var source = NewStore();
        var profile = source.AddProfile("Maya");
        source.AddVaultLibrary("vault-library");
        var video = source.AddMovie("Hidden Vault Title", "2019", imdb: null, tmdb: null, hash: "ffff6666", seconds: 5400.0, libraryId: "vault-library");
        source.AddVaultMatch(video.Asset, "stash-server", "record-77");
        source.AddAudiobookBookmark(profile, video, 120.0, "Scene 2");
        await source.AddClaimAsync(video.Work, WellKnownProviders.UserManual, "title", "Typed Vault Title", locked: true);

        var export = new PersonalStateBackupRepository(source.Database).Export(BackupScope.WholeLibrary);

        // The bookmark is kept against the video's Stash-box record, with no name or year; the typed correction on it is left out.
        var title = Assert.Single(export.Titles, candidate => candidate.Target == "MediaAsset");
        Assert.True(title.Withheld);
        Assert.Null(title.Name);
        Assert.Null(title.Year);
        Assert.Equal("stash-server|record-77", title.Ids["stash_box_id"]);
        Assert.Empty(export.Corrections);
        Assert.Equal(1, export.Contents.WithheldRows);
        var text = ArchiveText(export);
        Assert.DoesNotContain("Hidden Vault Title", text);
        Assert.DoesNotContain("Typed Vault Title", text);

        // After a restore the waiting row is counted only when asked, so nothing about the Vault shows by default.
        var target = NewStore();
        Import(export, target);
        var repository = new WaitingPersonalStateRepository(target.Database);
        Assert.Equal(0, repository.CountWaiting(includeWithheld: false));
        Assert.Equal(1, repository.CountWaiting(includeWithheld: true));
        Assert.Equal(1, target.Scalar("SELECT COUNT(*) FROM waiting_personal_state WHERE is_withheld = 1"));
    }

    [Fact]
    public void Reattach_BringsAVaultBookmarkBack_WhenTheSameStashBoxVideoReturnsWithADifferentFile_AndNotForAnotherRecord()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        source.AddVaultLibrary("vault-library");
        target.AddVaultLibrary("vault-library");
        var video = source.AddMovie("Hidden Vault Title", "2019", imdb: null, tmdb: null, hash: "ffff6666", seconds: 5400.0, libraryId: "vault-library");
        source.AddVaultMatch(video.Asset, "stash-server", "record-77");
        source.AddAudiobookBookmark(profile, video, 120.0, "Scene 2");
        Import(source, target);
        var repository = new WaitingPersonalStateRepository(target.Database);

        // A video with another record is not this title: the bookmark keeps waiting.
        var other = target.AddMovie("Other", "2020", imdb: null, tmdb: null, hash: "1111aaaa", seconds: 5400.0, libraryId: "vault-library");
        target.AddVaultMatch(other.Asset, "stash-server", "record-99");
        Assert.Equal(0, repository.RunPass(RestoredAt.AddMinutes(1), 100, 90).Attached);
        Assert.Equal(1, repository.CountWithheldWaiting());

        // The same record returns as a different file (another hash).
        var returned = target.AddMovie("Renamed", "2019", imdb: null, tmdb: null, hash: "2222bbbb", seconds: 5400.0, libraryId: "vault-library");
        target.AddVaultMatch(returned.Asset, "stash-server", "record-77");
        var pass = repository.RunPass(RestoredAt.AddMinutes(2), 100, 90);

        Assert.Equal(1, pass.Attached);
        Assert.Equal(0, repository.CountWithheldWaiting());
        Assert.Equal(120.0, target.Real("SELECT position_seconds FROM audiobook_bookmarks WHERE asset_id = @a", ("a", returned.Asset)));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Lookups use the indexes
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(LiveTitleResolver.AssetsByHashSql)]
    [InlineData("DISTINCT_PARENTS_WORK")]
    [InlineData("DISTINCT_PARENTS_EDITION")]
    [InlineData(LiveTitleResolver.LocalItemsByHashSql)]
    [InlineData(LiveTitleResolver.WorksByBridgeIdSql)]
    [InlineData(LiveTitleResolver.WorksByEditionWikidataIdSql)]
    [InlineData(LiveTitleResolver.WorksByWikidataIdSql)]
    public void TitleLookups_UseAnIndex_NotAScanOfATable(string sql)
    {
        var store = NewStore();
        sql = sql switch
        {
            "DISTINCT_PARENTS_WORK" => LiveTitleResolver.DistinctParentsSql("e.work_id"),
            "DISTINCT_PARENTS_EDITION" => LiveTitleResolver.DistinctParentsSql("e.id"),
            _ => sql,
        };

        var plan = store.Plan(sql);

        // Every table is reached by a search on an index; nothing is read end to end.
        var detail = string.Join(Environment.NewLine, plan);
        Assert.DoesNotContain(plan, line => line.StartsWith("SCAN", StringComparison.OrdinalIgnoreCase));
        Assert.True(plan.Any(line => line.Contains("USING INDEX", StringComparison.OrdinalIgnoreCase) || line.Contains("USING COVERING INDEX", StringComparison.OrdinalIgnoreCase)), detail);
    }

    [Fact]
    public void TitleLookups_FindAFileWhateverCaseTheBackupWroteItsFingerprintIn()
    {
        var source = NewStore();
        var target = NewStore();
        var profile = source.AddProfile("Maya");
        var scan = source.AddMovie("Family Scan", "2001", imdb: null, tmdb: null, hash: "ABCDEF12", seconds: 600.0);
        source.AddUserState(profile, scan.Asset, 50.0);
        Import(source, target);

        // The Library stores fingerprints in lower case; the backup's capitals still find the same file.
        var same = target.AddMovie("Family Scan", "2001", imdb: null, tmdb: null, hash: "abcdef12", seconds: 600.0);
        var pass = new WaitingPersonalStateRepository(target.Database).RunPass(RestoredAt.AddMinutes(1), 100, 90);

        Assert.Equal(1, pass.Attached);
        Assert.Equal(50.0, target.Real("SELECT progress_pct FROM user_states WHERE asset_id = @a", ("a", same.Asset)));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Guardrails
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void TimePositionTable_ListsEverySecondsColumnOfTheKeptTablesThatPointAtTitles()
    {
        var store = NewStore();
        using var connection = store.Database.CreateConnection();
        var problems = new List<string>();
        foreach (var rule in BackupTableClassification.Rules.Values.Where(rule => rule.Kind is BackupDataKind.PersonalState or BackupDataKind.Configuration && rule.CatalogueReferences.Count > 0))
        {
            var columns = connection.Query<string>("SELECT name FROM pragma_table_info(@table)", new { table = rule.Table }).ToList();
            foreach (var column in columns.Where(column => column.EndsWith("_seconds", StringComparison.OrdinalIgnoreCase) || column.EndsWith("_secs", StringComparison.OrdinalIgnoreCase)))
            {
                var listed = WaitingStateTimePositions.Tables.TryGetValue(rule.Table, out var time)
                    && time.Positions.Contains(column, StringComparer.OrdinalIgnoreCase);
                if (!listed && !WaitingStateTimePositions.NotPositions.Contains(rule.Table + "." + column))
                {
                    problems.Add($"{rule.Table}.{column} stores seconds: list it in WaitingStateTimePositions (a position) or NotPositions (a length).");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.All(WaitingStateTimePositions.Tables.Keys, table => Assert.NotNull(BackupTableClassification.Find(table)));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    private Store NewStore()
    {
        var store = new Store();
        _stores.Add(store);
        return store;
    }

    private static BackupRestoreResult Import(Store source, Store target) =>
        Import(new PersonalStateBackupRepository(source.Database).Export(BackupScope.WholeLibrary), target);

    private static BackupRestoreResult Import(BackupExport export, Store target) =>
        new BackupRestoreRepository(target.Database).Import(export, "backup-2026-10-21.zip", RestoredAt.AddDays(-1), null, RestoredAt);

    private static string ArchiveText(BackupExport export)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            BackupArchiveFormat.Write(archive, export);
        }

        stream.Position = 0;
        using var readable = new ZipArchive(stream, ZipArchiveMode.Read);
        var text = new StringBuilder();
        foreach (var entry in readable.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            text.AppendLine(reader.ReadToEnd());
        }

        return text.ToString();
    }

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }
    }

    private sealed class FakeCodec : IBackupPortableValueCodec
    {
        public string? Export(string table, string column, string protectedValue, IReadOnlyDictionary<string, BackupValue> row) => "readable:" + protectedValue;

        public string Import(string table, string column, string portableValue, IReadOnlyDictionary<string, BackupValue> row) => "sealed:" + portableValue;
    }

    private sealed record Movie(byte[] Work, byte[] Edition, byte[] Asset);

    /// <summary>One real data store in a temporary file, with the helpers that fill it.</summary>
    private sealed class Store : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_backup_restore_{Guid.NewGuid():N}.db");

        public Store()
        {
            DapperConfiguration.Configure();
            Database = new DatabaseConnection(_path);
            Database.InitializeSchema();
            Database.RunStartupChecks();
        }

        public DatabaseConnection Database { get; }

        public byte[] AddProfile(string name)
        {
            var id = GuidSql.ToBlob(Guid.NewGuid());
            Run("INSERT INTO profiles (id, display_name, created_at) VALUES (@id, @name, @now)", new { id, name, now = Stamp });
            return id;
        }

        public Movie AddMovie(string title, string year, string? imdb, string? tmdb, string hash, double? seconds, string edition = "4K Remux", string? libraryId = null)
        {
            var work = GuidSql.ToBlob(Guid.NewGuid());
            var editionId = GuidSql.ToBlob(Guid.NewGuid());
            var asset = GuidSql.ToBlob(Guid.NewGuid());
            using var connection = Database.CreateConnection();
            connection.Execute("INSERT INTO works (id, media_type) VALUES (@work, 'Movies')", new { work });
            connection.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @work, @edition)", new { editionId, work, edition });
            connection.Execute(
                "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id) VALUES (@asset, @editionId, @hash, @path, @libraryId)",
                new { asset, editionId, hash, path = $"/media/{hash}.mkv", libraryId });
            foreach (var (key, value) in new[] { ("title", title), ("year", year) })
            {
                connection.Execute(
                    "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@work, @key, @value, @now)",
                    new { work, key, value, now = Stamp });
            }

            foreach (var (type, value) in new[] { ("imdb_id", imdb), ("tmdb_id", tmdb) })
            {
                if (value is not null)
                {
                    connection.Execute(
                        "INSERT INTO bridge_ids (id, entity_id, id_type, id_value) VALUES (@id, @work, @type, @value)",
                        new { id = GuidSql.ToBlob(Guid.NewGuid()), work, type, value });
                }
            }

            if (seconds is { } duration)
            {
                connection.Execute(
                    "INSERT INTO playback_inspection_cache (asset_id, source_hash, inspected_at, duration_secs) VALUES (@asset, @hash, @now, @duration)",
                    new { asset, hash, now = Stamp, duration });
            }

            return new Movie(work, editionId, asset);
        }

        public void AddChapter(byte[] asset, int index, string title, long startMs) =>
            Run(
                "INSERT INTO media_asset_chapters (asset_id, chapter_index, start_ms, title) VALUES (@asset, @index, @startMs, @title)",
                new { asset, index, startMs, title });

        public void AddUserState(byte[] profile, byte[] asset, double progress, string? extendedProperties = null) =>
            Run(
                "INSERT INTO user_states (user_id, asset_id, content_hash, progress_pct, last_accessed, extended_properties) VALUES (@profile, @asset, (SELECT content_hash FROM media_assets WHERE id = @asset), @progress, @now, COALESCE(@extended, '{}'))",
                new { profile, asset, progress, now = Stamp, extended = extendedProperties });

        public void Execute(string sql) => Run(sql, new { });

        /// <summary>The rows of <c>EXPLAIN QUERY PLAN</c> for a query, as text. Parameters are bound to values that match nothing.</summary>
        public List<string> Plan(string sql)
        {
            using var connection = Database.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var name in new[] { "$hash", "$type", "$value" })
            {
                if (sql.Contains(name, StringComparison.Ordinal))
                {
                    command.Parameters.AddWithValue(name, "x");
                }
            }

            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add(reader.GetString(3));
            }

            return rows;
        }

        /// <summary>A book: the work (with its Wikidata id when given), one edition carrying the ISBN, and its file.</summary>
        public Movie AddBook(string title, string? qid, string isbn, string hash, string edition)
        {
            var work = GuidSql.ToBlob(Guid.NewGuid());
            var editionId = GuidSql.ToBlob(Guid.NewGuid());
            var asset = GuidSql.ToBlob(Guid.NewGuid());
            using var connection = Database.CreateConnection();
            connection.Execute("INSERT INTO works (id, media_type, wikidata_qid) VALUES (@work, 'Books', @qid)", new { work, qid });
            connection.Execute("INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @work, @edition)", new { editionId, work, edition });
            connection.Execute(
                "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@asset, @editionId, @hash, @path)",
                new { asset, editionId, hash, path = $"/books/{hash}.epub" });
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@work, 'title', @title, @now)",
                new { work, title, now = Stamp });
            connection.Execute(
                "INSERT INTO bridge_ids (id, entity_id, id_type, id_value) VALUES (@id, @editionId, 'isbn_13', @isbn)",
                new { id = GuidSql.ToBlob(Guid.NewGuid()), editionId, isbn });
            return new Movie(work, editionId, asset);
        }

        public void AddAudiobookBookmark(byte[] profile, Movie movie, double position, string? chapter) =>
            Run(
                """
                INSERT INTO audiobook_bookmarks (id, profile_id, work_id, asset_id, chapter_title, position_seconds, created_at)
                VALUES (@id, @profile, @work, @asset, @chapter, @position, @now)
                """,
                new { id = GuidSql.ToBlob(Guid.NewGuid()), profile, work = movie.Work, asset = movie.Asset, chapter, position, now = Stamp });

        public void AddDevicePosition(byte[] profile, Movie movie, string device, double position) =>
            Run(
                """
                INSERT INTO player_device_states (profile_id, device_id, client, asset_id, work_id, playback_state, position_seconds, updated_at)
                VALUES (@profile, @device, 'web', @asset, @work, 'paused', @position, @now)
                """,
                new { profile, device, asset = movie.Asset, work = movie.Work, position, now = Stamp });

        public void AddConsumption(byte[] profile, byte[] asset, double progress) =>
            Run(
                """
                INSERT INTO consumption_history (id, profile_id, asset_id, session_key, experience, started_at, updated_at, progress_pct)
                VALUES (@id, @profile, @asset, 'session-1', 'watch', @now, @now, @progress)
                """,
                new { id = GuidSql.ToBlob(Guid.NewGuid()), profile, asset, progress, now = Stamp });

        public void AddBookmarkPack(byte[] profile, Movie movie, int itemCount)
        {
            var pack = GuidSql.ToBlob(Guid.NewGuid());
            Run(
                """
                INSERT INTO bookmark_packs (id, profile_id, work_id, asset_id, name, content_hash, imported_at)
                VALUES (@pack, @profile, @work, @asset, 'Director notes', 'pack-hash-1', @now)
                """,
                new { pack, profile, work = movie.Work, asset = movie.Asset, now = Stamp });
            for (var i = 0; i < itemCount; i++)
            {
                Run(
                    "INSERT INTO bookmark_pack_items (id, pack_id, position_seconds) VALUES (@id, @pack, @position)",
                    new { id = GuidSql.ToBlob(Guid.NewGuid()), pack, position = 60.0 * (i + 1) });
            }
        }

        public void AddTwoStepSecret(string protectedValue)
        {
            var account = GuidSql.ToBlob(Guid.NewGuid());
            Run(
                """
                INSERT INTO accounts (id, created_at, updated_at) VALUES (@account, @now, @now);
                INSERT INTO account_two_step (account_id, secret_protected, created_at) VALUES (@account, @secret, @now);
                """,
                new { account, secret = protectedValue, now = Stamp });
        }

        public void SetDisplayOverrides(byte[] work, string json) =>
            Run("UPDATE works SET display_overrides_json = @json WHERE id = @work", new { json, work });

        public void AddVaultLibrary(string libraryId) =>
            Run(
                "INSERT INTO vault_libraries (library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES (@libraryId, 'none', @now, @now, @now)",
                new { libraryId, now = Stamp });

        public void AddVaultMatch(byte[] asset, string server, string record) =>
            Run(
                "INSERT INTO vault_matches (asset_id, server_id, record_id, matched_by, applied_via, matched_at) VALUES (@asset, @server, @record, 'fingerprint', 'automatic', @now)",
                new { asset, server, record, now = Stamp });

        public async Task AddClaimAsync(byte[] work, Guid provider, string key, string value, bool locked = false)
        {
            var claims = new MetadataClaimRepository(Database);
            await claims.InsertBatchAsync(
            [
                new MetadataClaim
                {
                    Id = Guid.NewGuid(),
                    EntityId = new Guid(work, bigEndian: true),
                    ProviderId = provider,
                    ClaimKey = key,
                    ClaimValue = value,
                    Confidence = 1.0,
                    ClaimedAt = DateTimeOffset.UtcNow,
                    IsUserLocked = locked,
                    IsCurrent = true,
                },
            ]);
        }

        public int Scalar(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = Database.CreateConnection();
            return connection.ExecuteScalar<int>(sql, ToParameters(parameters));
        }

        public double Real(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = Database.CreateConnection();
            return connection.ExecuteScalar<double>(sql, ToParameters(parameters));
        }

        public string Text(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = Database.CreateConnection();
            return connection.ExecuteScalar<string>(sql, ToParameters(parameters)) ?? string.Empty;
        }

        public void Dispose()
        {
            Database.Dispose();
            using (var pool = new SqliteConnection($"Data Source={_path}"))
            {
                SqliteConnection.ClearPool(pool);
            }

            // Best-effort cleanup of the temporary data store; a locked file must not fail the test run.
            foreach (var leftover in new[] { _path, _path + "-wal", _path + "-shm" })
            {
                try
                {
                    File.Delete(leftover);
                }
                catch (IOException)
                {
                }
            }
        }

        private void Run(string sql, object parameters)
        {
            using var connection = Database.CreateConnection();
            connection.Execute(sql, parameters);
        }

        private static DynamicParameters ToParameters((string Name, object Value)[] parameters)
        {
            var result = new DynamicParameters();
            foreach (var (name, value) in parameters)
            {
                result.Add(name, value);
            }

            return result;
        }
    }
}
