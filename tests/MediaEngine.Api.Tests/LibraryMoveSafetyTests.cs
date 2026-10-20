using MediaEngine.Api.Services.Libraries.Moves;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Aggregates;

namespace MediaEngine.Api.Tests;

/// <summary>
/// What a move must never do: move a whole container or make a loop, throw away what people did with a title, lose details the person
/// set, reach into a private collection, leave stale links, or let an application do more than it holds permission for.
/// </summary>
public sealed class LibraryMoveSafetyTests : IDisposable
{
    private static readonly Guid Person = Profile.SeedProfileId;

    private readonly LibraryMoveTestWorld _world = new();

    public void Dispose() => _world.Dispose();

    // ----- containers and loops ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AWholeShow_CannotBeMoved_NotEvenIntoItsOwnSeason()
    {
        var show = _world.AddParent("TV", "Northwatch");
        var season = _world.AddParent("TV", string.Empty, show, ordinal: 1);
        _world.AddChild(season, "TV", "Pilot", 1);

        var refused = await PreviewAsync([show], season);

        Assert.Equal(LibraryMoveRefusalKind.Invalid, refused.Refusal!.Kind);
        Assert.Contains("can't be moved", refused.Refusal.Message, StringComparison.Ordinal);
        Assert.Equal(show, _world.Work(season).Parent);
        Assert.Null(_world.Work(show).Parent);
    }

    [Fact]
    public async Task AWholeAlbumOrSeries_CannotBeMovedIntoAnotherOne()
    {
        var album = _world.AddParent("Music", "The Best Of");
        _world.AddChild(album, "Music", "Airbag", 1);
        var other = _world.AddParent("Music", "OK Computer");
        _world.AddChild(other, "Music", "Karma Police", 1);
        var series = _world.AddParent("Books", "Dune");
        _world.AddChild(series, "Books", "Dune", 1);
        var otherSeries = _world.AddParent("Books", "Foundation");
        _world.AddChild(otherSeries, "Books", "Foundation", 1);

        Assert.Equal(LibraryMoveRefusalKind.Invalid, (await PreviewAsync([album], other)).Refusal!.Kind);
        Assert.Equal(LibraryMoveRefusalKind.Invalid, (await PreviewAsync([series], otherSeries)).Refusal!.Kind);
        Assert.Equal(album, _world.Scalar<Guid>("SELECT id FROM works WHERE id = @album;", new { album }));
    }

    [Fact]
    public async Task ATitleThatHoldsOtherItems_CannotBeMoved_EvenWhenItIsNotMarkedAsAContainer()
    {
        var album = _world.AddParent("Music", "The Best Of");
        var (song, _, _) = _world.AddChild(album, "Music", "Airbag", 1);
        _world.AddChild(song, "Music", "Hidden track", 2);
        var other = AlbumWith("OK Computer");

        var refused = await PreviewAsync([song], other);

        Assert.Equal(LibraryMoveRefusalKind.Invalid, refused.Refusal!.Kind);
        Assert.Equal(album, _world.Work(song).Parent);
    }

    // ----- what people did with a title is kept ----------------------------------------------------------------------------------

    [Fact]
    public async Task AHeartedSongWithPlays_KeepsItsHeartAndPlays_WhenMatchedOntoAnExistingEntry()
    {
        var bestOf = _world.AddParent("Music", "The Best Of");
        var (song, _, _) = _world.AddChild(bestOf, "Music", "Paranoid Android", 3);
        var okComputer = _world.AddParent("Music", "OK Computer");
        var (entry, _, _) = _world.AddChild(okComputer, "Music", "Paranoid Android", 2, hasFile: false, catalogOnly: true);
        _world.Execute(
            """
            INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES (@person, 'Song', @song, '2026-10-01T00:00:00Z');
            INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@person, 'Song', @song, 'Love', '2026-10-05T00:00:00Z');
            INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@person, 'Song', @entry, 'Dislike', '2026-10-02T00:00:00Z');
            INSERT INTO music_play_stats (profile_id, work_id, play_count, last_played_at) VALUES (@person, @song, 5, '2026-10-04T00:00:00Z');
            INSERT INTO music_play_stats (profile_id, work_id, play_count, last_played_at) VALUES (@person, @entry, 2, '2026-10-03T00:00:00Z');
            INSERT INTO profile_work_preferences (profile_id, work_id, local_tags_json, revision, updated_at) VALUES (@person, @song, '["mine"]', 1, '2026-10-01T00:00:00Z');
            INSERT INTO profile_work_preferences (profile_id, work_id, local_tags_json, revision, updated_at) VALUES (@person, @entry, '["entry"]', 1, '2026-10-01T00:00:00Z');
            """,
            new { person = Person, song, entry });

        var preview = await Preview([song], okComputer);
        var result = await Commit(preview, Match(song, preview.Entries.Single().EntryKey));

        Assert.True(result.IsSuccess, result.Refusal?.Message);
        Assert.False(_world.WorkExists(song));
        Assert.Equal(1, _world.Scalar<long>("SELECT COUNT(*) FROM profile_saved_items WHERE entity_id = @entry;", new { entry }));
        Assert.Equal("Love", _world.Scalar<string>("SELECT reaction FROM profile_reactions WHERE entity_id = @entry;", new { entry }));
        Assert.Equal(7, _world.Scalar<long>("SELECT play_count FROM music_play_stats WHERE work_id = @entry;", new { entry }));
        Assert.Equal("2026-10-04T00:00:00Z", _world.Scalar<string>("SELECT last_played_at FROM music_play_stats WHERE work_id = @entry;", new { entry }));
        Assert.Equal("[\"entry\"]", _world.Scalar<string>("SELECT local_tags_json FROM profile_work_preferences WHERE work_id = @entry;", new { entry }));
    }

    [Fact]
    public async Task AnEarlierHeartOnTheEntry_IsNotOverwrittenByAnOlderOneOnTheMovedSong()
    {
        var bestOf = _world.AddParent("Music", "The Best Of");
        var (song, _, _) = _world.AddChild(bestOf, "Music", "Paranoid Android", 3);
        var okComputer = _world.AddParent("Music", "OK Computer");
        var (entry, _, _) = _world.AddChild(okComputer, "Music", "Paranoid Android", 2, hasFile: false, catalogOnly: true);
        _world.Execute(
            """
            INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@person, 'Song', @song, 'Dislike', '2026-10-01T00:00:00Z');
            INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@person, 'Song', @entry, 'Love', '2026-10-09T00:00:00Z');
            """,
            new { person = Person, song, entry });

        var preview = await Preview([song], okComputer);
        await Commit(preview, Match(song, preview.Entries.Single().EntryKey));

        Assert.Equal("Love", _world.Scalar<string>("SELECT reaction FROM profile_reactions WHERE entity_id = @entry;", new { entry }));
    }

    [Fact]
    public async Task AnAudiobookEmptiedByAMove_HandsItsBookmarksHistoryAndCollectionsToTheOneThatTookItsFiles()
    {
        var (target, _) = _world.AddAudiobook("Dune (audiobook)", [(1, "Part one")], partCount: 3);
        var (source, parts) = _world.AddAudiobook("Dune disc three", [(1, "Chapter three")]);
        var collection = _world.AddCollection("Road trip", source);
        _world.Execute(
            """
            INSERT INTO audiobook_bookmarks (id, profile_id, work_id, asset_id, position_seconds, created_at) VALUES (@bookmark, @person, @source, @asset, 12, '2026-10-01T00:00:00Z');
            INSERT INTO audiobook_listen_history (id, profile_id, work_id, asset_id, title, position_seconds, device_id, client, started_at, ended_at)
            VALUES (@history, @person, @source, @asset, 'Chapter three', 30, 'd', 'web', '2026-10-01T00:00:00Z', '2026-10-01T00:05:00Z');
            """,
            new { bookmark = Guid.NewGuid(), history = Guid.NewGuid(), person = Person, source, asset = parts[0].AssetId });

        var preview = await Preview([parts[0].EditionId], target);
        var free = preview.Entries.Single(entry => entry.Number == "2");
        var result = await Commit(preview, Match(parts[0].EditionId, free.EntryKey));

        Assert.True(result.IsSuccess, result.Refusal?.Message);
        Assert.False(_world.WorkExists(source));
        Assert.Equal(target, _world.Scalar<Guid>("SELECT work_id FROM audiobook_bookmarks;"));
        Assert.Equal(target, _world.Scalar<Guid>("SELECT work_id FROM audiobook_listen_history;"));
        Assert.Equal(1, _world.Scalar<long>("SELECT COUNT(*) FROM collection_items WHERE collection_id = @collection AND work_id = @target;", new { collection, target }));
        Assert.Equal(1, _world.Scalar<long>("SELECT COUNT(*) FROM collections WHERE id = @collection;", new { collection }));
        Assert.DoesNotContain(result.Value!.RemovedContainers, name => name == "Road trip");
    }

    // ----- details the person set --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MovingAllThreePartsOfARenamedAudiobook_IsRefused_ButTwoOfThreeIsFine()
    {
        var (target, _) = _world.AddAudiobook("Dune (audiobook)", [(1, "Part one")], partCount: 5);
        var (source, parts) = _world.AddAudiobook("Dune disc", [(1, "A"), (2, "B"), (3, "C")]);
        _world.Execute("UPDATE works SET display_overrides_json = '{\"title\":\"My own title\"}' WHERE id = @source;", new { source });

        var all = await Preview([.. parts.Select(part => part.EditionId)], target);
        Assert.All(all.Rows, row => Assert.Contains("last file can't be moved", row.BlockedReason, StringComparison.Ordinal));
        var rows = all.Rows.Select(row => AddAnyway(row.ItemId)).ToArray();
        var refused = await Commit(all, rows);
        Assert.Equal(LibraryMoveRefusalKind.Invalid, refused.Refusal!.Kind);
        Assert.True(_world.WorkExists(source));
        Assert.Equal(3, _world.EditionCount(source));

        var some = await Preview([parts[0].EditionId, parts[1].EditionId], target);
        Assert.All(some.Rows, row => Assert.Null(row.BlockedReason));
        var moved = await Commit(some, AddAnyway(parts[0].EditionId), AddAnyway(parts[1].EditionId));
        Assert.True(moved.IsSuccess, moved.Refusal?.Message);
        Assert.True(_world.WorkExists(source));
        Assert.Equal(1, _world.EditionCount(source));
    }

    // ----- the source collection ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APrivateProfileCollection_IsRefusedAsTheSourceOfAMove()
    {
        var bestOf = _world.AddParent("Music", "The Best Of");
        var (song, _, _) = _world.AddChild(bestOf, "Music", "Paranoid Android", 3);
        var favourites = _world.AddCollection("Favourites");
        var secret = _world.AddCollection("Somebody's private list", song);
        _world.Execute("UPDATE collections SET owner_kind = 'Profile', profile_id = @person, scope = 'profile' WHERE id = @secret;", new { person = Person, secret });

        var refused = await _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = [song], DestinationId = favourites, SourceCollectionId = secret },
            LibraryMoveTestWorld.Actor,
            LibraryMoveTestWorld.Everything);

        Assert.Equal(LibraryMoveRefusalKind.Invalid, refused.Refusal!.Kind);
        Assert.Equal(1, _world.Scalar<long>("SELECT COUNT(*) FROM collection_items WHERE collection_id = @secret;", new { secret }));
    }

    [Fact]
    public async Task ASourceCollectionTheCallerMayNotEdit_IsRefusedInThePreviewAndTheCommit()
    {
        var bestOf = _world.AddParent("Music", "The Best Of");
        var (song, _, _) = _world.AddChild(bestOf, "Music", "Paranoid Android", 3);
        var (other, _, _) = _world.AddChild(bestOf, "Music", "Airbag", 4);
        var favourites = _world.AddCollection("Favourites");
        var roadTrip = _world.AddCollection("Road trip", song, other);
        LibraryMoveAccess everythingButTheCollection = (id, _, _) => ValueTask.FromResult(id != roadTrip);
        var request = new LibraryMovePreviewRequest { ItemIds = [song], DestinationId = favourites, SourceCollectionId = roadTrip };

        var refused = await _world.Service.PreviewAsync(request, LibraryMoveTestWorld.Actor, everythingButTheCollection);
        var preview = (await _world.Service.PreviewAsync(request, LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.Everything)).Value!;
        var commit = await _world.Service.CommitAsync(
            new LibraryMoveCommitRequest { PreviewToken = preview.PreviewToken, Rows = [Free(song)] },
            LibraryMoveTestWorld.Actor,
            everythingButTheCollection);

        Assert.Equal(LibraryMoveRefusalKind.Unavailable, refused.Refusal!.Kind);
        Assert.Equal(LibraryMoveRefusalKind.Unavailable, commit.Refusal!.Kind);
        Assert.Equal(1, _world.Scalar<long>("SELECT COUNT(*) FROM collection_items WHERE collection_id = @roadTrip AND work_id = @song;", new { roadTrip, song }));
    }

    // ----- files Tuvima wrote ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APictureOnlyTheMergedAwayTitleUsed_IsRemovedAfterTheMove_AndASourceFileIsNeverTouched()
    {
        var bestOf = _world.AddParent("Music", "The Best Of");
        var (song, _, _) = _world.AddChild(bestOf, "Music", "Paranoid Android", 3);
        var okComputer = _world.AddParent("Music", "OK Computer");
        _world.AddChild(okComputer, "Music", "Paranoid Android", 2, hasFile: false, catalogOnly: true);
        var picture = Path.Combine(_world.AssetPaths.AssetsRoot, "artwork", "paranoid-android.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
        File.WriteAllBytes(picture, [1, 2, 3]);
        var sourceMedia = Path.Combine(_world.Root, "library", "Paranoid Android.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceMedia)!);
        File.WriteAllBytes(sourceMedia, [9, 9, 9]);
        _world.Execute(
            """
            INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, local_image_path) VALUES (@a, @song, 'Work', 'CoverArt', @picture);
            INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, local_image_path) VALUES (@b, @song, 'Work', 'Banner', @sourceMedia);
            """,
            new { a = Guid.NewGuid(), b = Guid.NewGuid(), song, picture, sourceMedia });

        var preview = await Preview([song], okComputer);
        var result = await Commit(preview, Match(song, preview.Entries.Single().EntryKey));

        Assert.True(result.IsSuccess, result.Refusal?.Message);
        Assert.False(File.Exists(picture));
        Assert.True(File.Exists(sourceMedia));
    }

    // ----- one definition of taken -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEntryOwnedElsewhereInTheSeriesList_IsTaken_ButCannotTakeAnotherVersion()
    {
        var elsewhere = _world.AddParent("Books", "Loose books");
        var (ninth, _, _) = _world.AddChild(elsewhere, "Books", "Book Nine", 9);
        var collection = _world.AddSeriesCollection("wd:Q9", [
            ("q1", "wd:Q9", 1, "Book One", null),
            ("q9", "wd:Q9", 9, "Book Nine", ninth)]);
        var series = _world.AddParent("Books", "The Nine");
        var (owned, _, _) = _world.AddChild(series, "Books", "Book Two", 2, collection: collection);
        var third = _world.AddParent("Books", "Third place");
        var (mover, _, _) = _world.AddChild(third, "Books", "Book Nine (second copy)", 5);

        var preview = await Preview([mover], series);

        var elsewhereEntry = preview.Entries.Single(entry => entry.Number == "9");
        var ownedEntry = preview.Entries.Single(entry => entry.TakenByItemId == owned);
        var free = preview.Entries.Single(entry => entry.Number == "1");
        Assert.Equal(ninth, elsewhereEntry.TakenByItemId);
        Assert.False(elsewhereEntry.CanAddVersion);
        Assert.True(ownedEntry.CanAddVersion);
        Assert.False(free.CanAddVersion);
        var refused = await Commit(preview, AsVersion(mover, elsewhereEntry.EntryKey));
        Assert.Equal(LibraryMoveRefusalKind.Invalid, refused.Refusal!.Kind);
        Assert.Contains("can't take another version", refused.Refusal.Message, StringComparison.Ordinal);
        Assert.True((await Commit(preview, AsVersion(mover, ownedEntry.EntryKey))).IsSuccess);
    }

    [Fact]
    public async Task AnEntryCoveredByAnotherFile_IsTaken_SoItIsNeverMatchedOverSilently()
    {
        var series = _world.AddParent("Books", "The Nine");
        var (host, _, hostAsset) = _world.AddChild(series, "Books", "Omnibus", 1);
        var (covered, _, _) = _world.AddChild(series, "Books", "Part two of the omnibus", 2, hasFile: false);
        _world.Execute(
            "INSERT INTO media_asset_coverage (asset_id, work_id, position, source) VALUES (@asset, @covered, 1, 'filename');",
            new { asset = hostAsset, covered });
        var elsewhere = _world.AddParent("Books", "Loose books");
        var (mover, _, _) = _world.AddChild(elsewhere, "Books", "Part two of the omnibus", 7);

        var preview = await Preview([mover], series);
        var entry = preview.Entries.Single(candidate => candidate.Title == "Part two of the omnibus");
        var refused = await Commit(preview, Match(mover, entry.EntryKey));

        Assert.Equal(covered, entry.TakenByItemId);
        Assert.Equal(LibraryMoveRefusalKind.Invalid, refused.Refusal!.Kind);
        Assert.NotEqual(host, covered);
    }

    // ----- links -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATitleAddedAnywayToASeriesWithNoSeriesLink_DoesNotStayTiedToTheSeriesItLeft()
    {
        var oldCollection = _world.AddSeriesCollection("wd:Q1", [("q1", "wd:Q1", 1, "Dune", null)]);
        var oldSeries = _world.AddParent("Books", "Dune");
        var (book, _, _) = _world.AddChild(oldSeries, "Books", "Dune Messiah", 2, collection: oldCollection);
        var newSeries = _world.AddParent("Books", "Loose books");
        _world.AddChild(newSeries, "Books", "Another", 1);
        Assert.Equal(oldCollection, _world.Work(book).Collection);

        var preview = await Preview([book], newSeries);
        var result = await Commit(preview, AddAnyway(book));

        Assert.True(result.IsSuccess, result.Refusal?.Message);
        Assert.Null(_world.Work(book).Collection);
    }

    // ----- permissions -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MatchingAndAddingAVersionNeedTheMatchRight_AddingAnywayAndFreeMovesNeedOnlyWrite()
    {
        var bestOf = _world.AddParent("Music", "The Best Of");
        var (first, _, _) = _world.AddChild(bestOf, "Music", "Paranoid Android", 3);
        var (second, _, _) = _world.AddChild(bestOf, "Music", "Airbag", 4);
        var okComputer = _world.AddParent("Music", "OK Computer", manifestJson: """{"tracks":[{"title":"Paranoid Android","track_number":2,"disc_number":1}]}""");
        var previewed = await _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = [first, second], DestinationId = okComputer },
            LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.WriteOnly);
        Assert.True(previewed.IsSuccess, previewed.Refusal?.Message);
        var preview = previewed.Value!;
        var entry = preview.Entries.Single();
        var matchRefused = await _world.Service.CommitAsync(
            new LibraryMoveCommitRequest { PreviewToken = preview.PreviewToken, Rows = [Match(first, entry.EntryKey), AddAnyway(second)] },
            LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.WriteOnly);
        var addAnyway = await _world.Service.CommitAsync(
            new LibraryMoveCommitRequest { PreviewToken = preview.PreviewToken, Rows = [AddAnyway(first), AddAnyway(second)] },
            LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.WriteOnly);

        Assert.Equal(LibraryMoveRefusalKind.Unavailable, matchRefused.Refusal!.Kind);
        Assert.True(addAnyway.IsSuccess, addAnyway.Refusal?.Message);

        var (third, _, _) = _world.AddChild(_world.AddParent("Music", "Other"), "Music", "Karma Police", 1);
        var (keeper, _, _) = _world.AddChild(_world.AddParent("Music", "Yet another"), "Music", "Lucky", 1);
        var favourites = _world.AddCollection("Favourites", keeper);
        var freePreview = await _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = [third], DestinationId = favourites },
            LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.WriteOnly);
        Assert.True(freePreview.IsSuccess, freePreview.Refusal?.Message);
        var free = freePreview.Value!;
        var moved = await _world.Service.CommitAsync(
            new LibraryMoveCommitRequest { PreviewToken = free.PreviewToken, Rows = [Free(third)] },
            LibraryMoveTestWorld.Actor, LibraryMoveTestWorld.WriteOnly);
        Assert.True(moved.IsSuccess, moved.Refusal?.Message);
    }

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private Guid AlbumWith(string name)
    {
        var album = _world.AddParent("Music", name);
        _world.AddChild(album, "Music", "Karma Police", 1);
        return album;
    }

    private Task<LibraryMoveResult<LibraryMovePreview>> PreviewAsync(IReadOnlyList<Guid> items, Guid destination) =>
        _world.Service.PreviewAsync(
            new LibraryMovePreviewRequest { ItemIds = items, DestinationId = destination },
            LibraryMoveTestWorld.Actor,
            LibraryMoveTestWorld.Everything);

    private async Task<LibraryMovePreview> Preview(IReadOnlyList<Guid> items, Guid destination)
    {
        var result = await PreviewAsync(items, destination);
        Assert.True(result.IsSuccess, result.Refusal?.Message);
        return result.Value!;
    }

    private Task<LibraryMoveResult<LibraryMoveCommitResult>> Commit(LibraryMovePreview preview, params LibraryMoveCommitRow[] rows) =>
        _world.Service.CommitAsync(
            new LibraryMoveCommitRequest { PreviewToken = preview.PreviewToken, Rows = rows },
            LibraryMoveTestWorld.Actor,
            LibraryMoveTestWorld.Everything);

    private static LibraryMoveCommitRow Match(Guid item, string entryKey) =>
        new() { ItemId = item, Mode = LibraryMoveModes.Match, EntryKey = entryKey };

    private static LibraryMoveCommitRow AsVersion(Guid item, string entryKey) =>
        new() { ItemId = item, Mode = LibraryMoveModes.AddAsVersion, EntryKey = entryKey };

    private static LibraryMoveCommitRow AddAnyway(Guid item) =>
        new() { ItemId = item, Mode = LibraryMoveModes.AddAnyway };

    private static LibraryMoveCommitRow Free(Guid item) =>
        new() { ItemId = item, Mode = LibraryMoveModes.Match };
}
