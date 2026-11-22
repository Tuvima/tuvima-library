using Dapper;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Playlist actions that must work at any playlist size (Listen playlists spec WP1): moving one item, adding with an exact
/// "already there" count, the per-profile pin and the summed length. Real temp SQLite; a playlist of 1,200 songs is past
/// the 1,000 that the old reads stopped at.
/// </summary>
public sealed class PlaylistRepositoryTests : IDisposable
{
    private static readonly Guid FirstProfile = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid SecondProfile = Guid.Parse("00000000-0000-0000-0000-0000000000c2");

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_playlist_repo_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly PlaylistRepository _repository;

    public PlaylistRepositoryTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repository = new PlaylistRepository(_db);
        using var connection = _db.CreateConnection();
        connection.Execute(
            """
            INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
            VALUES (@first, 'First', '#7C4DFF', 'StandardUser', CURRENT_TIMESTAMP),
                   (@second, 'Second', '#7C4DFF', 'StandardUser', CURRENT_TIMESTAMP);
            """,
            new { first = FirstProfile, second = SecondProfile });
    }

    [Fact]
    public async Task MovingAnItemInA1200SongPlaylist_PutsItInItsNewPlaceAndKeepsEveryoneElseInOrder()
    {
        var playlist = SeedPlaylist("Big", 1200, out var songs);
        var items = ItemIdsInOrder(playlist);

        // Item 1,150 (past the old 1,000 cap) goes to place 3, then an early item goes to place 900.
        Assert.True(await _repository.MoveItemAsync(playlist, items[1149], 3));
        var expected = items.ToList();
        expected.RemoveAt(1149);
        expected.Insert(2, items[1149]);
        Assert.Equal(expected, ItemIdsInOrder(playlist));

        Assert.True(await _repository.MoveItemAsync(playlist, items[0], 900));
        expected.Remove(items[0]);
        expected.Insert(899, items[0]);
        Assert.Equal(expected, ItemIdsInOrder(playlist));
        Assert.Equal(Enumerable.Range(1, 1200), SortOrders(playlist));
        Assert.Equal(1200, songs.Count);
    }

    [Fact]
    public async Task MovingAnItemToTheTopAndToTheBottomOfA1200SongPlaylist_Works()
    {
        var playlist = SeedPlaylist("Big", 1200, out _);
        var items = ItemIdsInOrder(playlist);

        Assert.True(await _repository.MoveItemAsync(playlist, items[700], 1));
        Assert.Equal(items[700], ItemIdsInOrder(playlist)[0]);

        // Past the end means the bottom: the caller never needs to know the playlist's size.
        Assert.True(await _repository.MoveItemAsync(playlist, items[5], int.MaxValue));
        var order = ItemIdsInOrder(playlist);
        Assert.Equal(items[5], order[^1]);
        Assert.Equal(1200, order.Count);
        Assert.Equal(Enumerable.Range(1, 1200), SortOrders(playlist));
    }

    [Fact]
    public async Task MovingAnItem_LeavesOtherPlaylistsAlone()
    {
        var playlist = SeedPlaylist("Mine", 5, out _);
        var other = SeedPlaylist("Other", 5, out _);
        var otherBefore = ItemIdsInOrder(other);

        await _repository.MoveItemAsync(playlist, ItemIdsInOrder(playlist)[4], 1);

        Assert.Equal(otherBefore, ItemIdsInOrder(other));
    }

    [Fact]
    public async Task MovingAnItemThatIsNotInThePlaylist_ChangesNothingAndSaysSo()
    {
        var playlist = SeedPlaylist("Mine", 4, out _);
        var other = SeedPlaylist("Other", 4, out _);
        var before = ItemIdsInOrder(playlist);

        var moved = await _repository.MoveItemAsync(playlist, ItemIdsInOrder(other)[0], 1);

        Assert.False(moved);
        Assert.Equal(before, ItemIdsInOrder(playlist));
    }

    [Fact]
    public async Task MovingAnItemWhoseSortOrdersTie_StillGivesEveryItemItsOwnPlace()
    {
        var playlist = SeedPlaylist("Ties", 6, out _);
        using (var connection = _db.CreateConnection())
        {
            connection.Execute("UPDATE collection_items SET sort_order = 1 WHERE collection_id = @playlist", new { playlist });
        }

        var items = ItemIdsInOrder(playlist);
        await _repository.MoveItemAsync(playlist, items[3], 1);

        Assert.Equal(Enumerable.Range(1, 6), SortOrders(playlist));
        Assert.Equal(items[3], ItemIdsInOrder(playlist)[0]);
    }

    [Fact]
    public async Task AddingSongsThatAreAlreadyThere_AddsNothingAndCountsThem_EvenPastTheFirstThousand()
    {
        var playlist = SeedPlaylist("Big", 1200, out var songs);

        var result = await _repository.AddWorksAsync(playlist, [songs[1150], songs[2], songs[1199]]);

        Assert.Equal(new MediaEngine.Domain.Contracts.PlaylistAddResult(0, 3), result);
        Assert.Equal(1200, ItemIdsInOrder(playlist).Count);
    }

    [Fact]
    public async Task AddingSomeNewSongsAndSomeAlreadyThere_AddsTheNewOnesAtTheEndInOrderAndCountsBoth()
    {
        var playlist = SeedPlaylist("Mix", 3, out var songs);
        var fresh = SeedSongs(2);

        // The same new song listed twice counts once.
        var result = await _repository.AddWorksAsync(playlist, [fresh[0], songs[1], fresh[1], fresh[0]]);

        Assert.Equal(new MediaEngine.Domain.Contracts.PlaylistAddResult(2, 1), result);
        Assert.Equal([songs[0], songs[1], songs[2], fresh[0], fresh[1]], WorkIdsInOrder(playlist));
        Assert.Equal(Enumerable.Range(1, 5), SortOrders(playlist));
    }

    [Fact]
    public async Task APinBelongsToOneProfile_AndOtherProfilesSeeThePlaylistUnpinned()
    {
        var playlist = SeedPlaylist("Road trip", 2, out _);

        await _repository.SetPinnedAsync(FirstProfile, playlist, true);
        await _repository.SetPinnedAsync(FirstProfile, playlist, true);

        Assert.Contains(playlist, await _repository.GetPinnedCollectionIdsAsync(FirstProfile));
        Assert.DoesNotContain(playlist, await _repository.GetPinnedCollectionIdsAsync(SecondProfile));

        await _repository.SetPinnedAsync(SecondProfile, playlist, true);
        await _repository.SetPinnedAsync(FirstProfile, playlist, false);

        Assert.Empty(await _repository.GetPinnedCollectionIdsAsync(FirstProfile));
        Assert.Contains(playlist, await _repository.GetPinnedCollectionIdsAsync(SecondProfile));
    }

    [Fact]
    public async Task DeletingAPlaylistOrAProfile_TakesItsPinsWithIt()
    {
        var playlist = SeedPlaylist("Gone", 1, out _);
        var kept = SeedPlaylist("Kept", 1, out _);
        await _repository.SetPinnedAsync(FirstProfile, playlist, true);
        await _repository.SetPinnedAsync(FirstProfile, kept, true);
        await _repository.SetPinnedAsync(SecondProfile, kept, true);

        using (var connection = _db.CreateConnection())
        {
            connection.Execute("PRAGMA foreign_keys = ON; DELETE FROM collections WHERE id = @playlist", new { playlist });
            connection.Execute("PRAGMA foreign_keys = ON; DELETE FROM profiles WHERE id = @second", new { second = SecondProfile });
        }

        Assert.Equal([kept], (await _repository.GetPinnedCollectionIdsAsync(FirstProfile)).ToArray());
        Assert.Empty(await _repository.GetPinnedCollectionIdsAsync(SecondProfile));
    }

    [Fact]
    public async Task TheTotalLength_SumsTheSongsWhoseLengthIsKnown_AndLeavesOutAPlaylistWithNone()
    {
        var timed = SeedPlaylist("Timed", 3, out var songs);
        var untimed = SeedPlaylist("Untimed", 2, out _);
        SetDuration(songs[0], 180.5);
        SetDuration(songs[1], 200);
        // The third song has no stored length: it adds nothing.

        var totals = await _repository.GetTotalDurationSecondsAsync([timed, untimed]);

        Assert.Equal(380.5, totals[timed], 3);
        Assert.False(totals.ContainsKey(untimed));
    }

    private Guid SeedPlaylist(string name, int songCount, out List<Guid> songs)
    {
        songs = SeedSongs(songCount);
        var playlist = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        using var transaction = connection.BeginTransaction();
        connection.Execute(
            """
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, membership_mode, primary_area, owner_kind, audience)
            VALUES (@playlist, @name, 'Playlist', 'library', 'materialized', 'Manual', 'Listen', 'Library', 'Everyone')
            """,
            new { playlist, name },
            transaction);
        var order = 0;
        foreach (var song in songs)
        {
            connection.Execute(
                "INSERT INTO collection_items (id, collection_id, work_id, sort_order) VALUES (@id, @playlist, @song, @order)",
                new { id = Guid.NewGuid(), playlist, song, order = ++order },
                transaction);
        }

        transaction.Commit();
        return playlist;
    }

    private List<Guid> SeedSongs(int count)
    {
        var songs = new List<Guid>(count);
        using var connection = _db.CreateConnection();
        using var transaction = connection.BeginTransaction();
        for (var index = 0; index < count; index++)
        {
            var song = Guid.NewGuid();
            var edition = Guid.NewGuid();
            var asset = Guid.NewGuid();
            connection.Execute(
                """
                INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@song, 'Music', 'standalone', 'accepted');
                INSERT INTO editions (id, work_id) VALUES (@edition, @song);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
                VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP);
                """,
                new { song, edition, asset, hash = asset.ToString("N"), path = $"C:/music/{asset:N}.flac" },
                transaction);
            songs.Add(song);
        }

        transaction.Commit();
        return songs;
    }

    private void SetDuration(Guid song, double seconds)
    {
        using var connection = _db.CreateConnection();
        connection.Execute(
            """
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            SELECT ma.id, 'duration_seconds', @seconds, CURRENT_TIMESTAMP
            FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @song
            """,
            new { song, seconds = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }

    private List<Guid> ItemIdsInOrder(Guid playlist)
    {
        using var connection = _db.CreateConnection();
        return connection.Query<Guid>(
            "SELECT id FROM collection_items WHERE collection_id = @playlist ORDER BY sort_order, rowid", new { playlist }).ToList();
    }

    private List<Guid> WorkIdsInOrder(Guid playlist)
    {
        using var connection = _db.CreateConnection();
        return connection.Query<Guid>(
            "SELECT work_id FROM collection_items WHERE collection_id = @playlist ORDER BY sort_order, rowid", new { playlist }).ToList();
    }

    private List<int> SortOrders(Guid playlist)
    {
        using var connection = _db.CreateConnection();
        return connection.Query<int>(
            "SELECT sort_order FROM collection_items WHERE collection_id = @playlist ORDER BY sort_order", new { playlist }).ToList();
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }
}
