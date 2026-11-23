using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// <c>GET /api/v1/collections/{id}/items</c> for a playlist: the paired profile's own playlist songs, read the way the
/// Dashboard's Listen page reads them, with what a phone needs to play each one. Same host and library as the other
/// collection client tests.
/// </summary>
public sealed partial class CollectionClientEndpointsTests
{
    [Fact]
    public async Task PlaylistItems_ComeInTheSavedOrder_WithWhatAPhoneNeedsToPlayEachSong()
    {
        var first = AddSong("Open Road", "Road Songs", artist: "The Drivers", seconds: 200.5);
        var second = AddSong("Night Lights", "City Songs", artist: "Skyline", seconds: 181);
        var third = AddSong("Last Mile", "Road Songs", artist: "The Drivers", seconds: 240);
        // The saved order, not the title order and not the order the songs were added to the library.
        var playlist = AddPlaylist("Road trip", "Playlist", [third, first, second]);
        await using var app = await StartAsync();

        var page = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items");
        var items = page["items"]!.AsArray();

        Assert.Equal(["Last Mile", "Open Road", "Night Lights"], items.Select(item => item!["title"]!.GetValue<string>()));
        Assert.Equal([0, 1, 2], items.Select(item => item!["sort_order"]!.GetValue<int>()));
        Assert.Equal(3, page["total_count"]!.GetValue<int>());
        Assert.False(page["has_more"]!.GetValue<bool>());

        var song = items[1]!;
        Assert.Equal(first, song["work_id"]!.GetValue<Guid>());
        Assert.Equal(AssetOf(first), song["asset_id"]!.GetValue<Guid>());
        Assert.Equal("The Drivers", song["creator"]!.GetValue<string>());
        Assert.Equal("Road Songs", song["album"]!.GetValue<string>());
        Assert.Equal(200.5, song["duration_seconds"]!.GetValue<double>());
        Assert.Equal("Music", song["media_type"]!.GetValue<string>());
    }

    [Fact]
    public async Task PlaylistItems_ReadTheAlbumAndLengthTheWayTheDashboardDoes_FromTheSongThenItsAlbumThenItsFile()
    {
        var onWork = AddWork("Music", "Kept on the song", "G", _alpha, ".flac");
        SetValue(onWork, "album", "Song's own album");
        SetValue(onWork, "duration_sec", "100");
        SetValue(AssetOf(onWork), "album", "File album");
        SetValue(AssetOf(onWork), "duration_seconds", "999");

        var album = AddWork("Music", "An album", "G", _alpha, ".flac");
        SetValue(album, "album", "Album work's name");
        var onAlbum = AddWork("Music", "Kept on the album", "G", _alpha, ".flac");
        SetParent(onAlbum, album);

        var onFile = AddWork("Music", "Kept on the file", "G", _alpha, ".flac");
        SetValue(AssetOf(onFile), "album", "File album");
        SetValue(AssetOf(onFile), "duration_seconds", "61.25");

        var bare = AddWork("Music", "Nothing stored", "G", _alpha, ".flac");
        var playlist = AddPlaylist("Mixed", "Playlist", [onWork, onAlbum, onFile, bare]);
        await using var app = await StartAsync();

        var items = (await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items"))["items"]!.AsArray();

        Assert.Equal("Song's own album", items[0]!["album"]!.GetValue<string>());
        Assert.Equal(100d, items[0]!["duration_seconds"]!.GetValue<double>());
        Assert.Equal("Album work's name", items[1]!["album"]!.GetValue<string>());
        Assert.Equal("File album", items[2]!["album"]!.GetValue<string>());
        Assert.Equal(61.25, items[2]!["duration_seconds"]!.GetValue<double>());
        // A song with nothing stored keeps the two fields out of the answer, and still has the file that plays it.
        Assert.Null(items[3]!["album"]);
        Assert.Null(items[3]!["duration_seconds"]);
        Assert.Equal(AssetOf(bare), items[3]!["asset_id"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task PlaylistItems_PageByCursor_WithoutRepeatsOrGaps_AndTheSavedOrderKeepsRunningAcrossPages()
    {
        var songs = Enumerable.Range(1, 5).Select(number => AddSong($"Song {number}", "Album", seconds: 60)).ToArray();
        var playlist = AddPlaylist("Long one", "Playlist", songs);
        await using var app = await StartAsync();

        var first = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items?limit=2");
        var second = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items?limit=2&cursor={first["next_cursor"]!.GetValue<string>()}");
        var third = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items?limit=2&cursor={second["next_cursor"]!.GetValue<string>()}");

        Assert.True(first["has_more"]!.GetValue<bool>());
        Assert.True(second["has_more"]!.GetValue<bool>());
        Assert.False(third["has_more"]!.GetValue<bool>());
        Assert.Null(third["next_cursor"]);
        Assert.Equal(5, first["total_count"]!.GetValue<int>());
        var paged = first["items"]!.AsArray().Concat(second["items"]!.AsArray()).Concat(third["items"]!.AsArray()).ToList();
        Assert.Equal(songs, paged.Select(item => item!["work_id"]!.GetValue<Guid>()));
        Assert.Equal([0, 1, 2, 3, 4], paged.Select(item => item!["sort_order"]!.GetValue<int>()));
        // Every page carries the song details, not only the first.
        Assert.All(paged, item =>
        {
            Assert.Equal("Album", item!["album"]!.GetValue<string>());
            Assert.Equal(60d, item["duration_seconds"]!.GetValue<double>());
        });

        // A cursor past the end of a playlist that has songs is an empty page, not an error.
        var past = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items?cursor=50");
        Assert.Empty(past["items"]!.AsArray());
        Assert.False(past["has_more"]!.GetValue<bool>());
    }

    [Fact]
    public async Task PlaylistItems_DefaultToOneHundredPerPage_AndNeverMoreThanTwoHundred_SoALongPlaylistPagesInFewReads()
    {
        var songs = Enumerable.Range(1, 205).Select(number => AddWork("Music", $"Long {number:000}", "G", _alpha, ".flac")).ToArray();
        var playlist = AddPlaylist("Everything", "Playlist", songs);
        await using var app = await StartAsync();

        var byDefault = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items");
        var greedy = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items?limit=1000");
        var rest = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items?limit=200&cursor={greedy["next_cursor"]!.GetValue<string>()}");

        Assert.Equal(100, byDefault["items"]!.AsArray().Count);
        Assert.True(byDefault["has_more"]!.GetValue<bool>());
        Assert.Equal(200, greedy["items"]!.AsArray().Count);
        Assert.Equal(205, greedy["total_count"]!.GetValue<int>());
        Assert.Equal(5, rest["items"]!.AsArray().Count);
        Assert.False(rest["has_more"]!.GetValue<bool>());
        Assert.Equal(songs, greedy["items"]!.AsArray().Concat(rest["items"]!.AsArray()).Select(item => item!["work_id"]!.GetValue<Guid>()));
    }

    [Fact]
    public async Task PlaylistItems_OfAnotherPersonsSharedOrLibraryPlaylist_AreNotFound_TheSameAsAnUnknownPlaylist()
    {
        var songs = AddThreeWorks();
        var othersPrivate = AddPlaylist("Someone else's", "Playlist", songs, profile: OtherProfile);
        var sharedWithMe = AddPlaylist("Shared with you", "Playlist", songs, profile: OtherProfile, audience: "SelectedProfiles");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO collection_profile_audience (collection_id, profile_id) VALUES (@id, @me)", new { id = sharedWithMe, me = Me });
        }

        var libraryPlaylist = AddPlaylist("Library mix", "Playlist", songs, scope: "library", owner: "Library", audience: "Everyone", profile: null);
        var disabled = AddPlaylist("Retired", "Playlist", songs, enabled: false);
        var legacySmart = AddPlaylist("Old smart type", "Smart", songs, profile: OtherProfile);
        await using var app = await StartAsync();

        var unknown = await SnapshotAsync(app, $"/api/v1/collections/{Guid.NewGuid()}/items");

        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
        foreach (var playlist in new[] { othersPrivate, sharedWithMe, libraryPlaylist, disabled, legacySmart })
        {
            var answer = await SnapshotAsync(app, $"/api/v1/collections/{playlist}/items");
            Assert.Equal(HttpStatusCode.NotFound, answer.Status);
            Assert.Equal(unknown.Body, answer.Body);
            Assert.Equal(unknown.ContentType, answer.ContentType);
        }
    }

    [Fact]
    public async Task PlaylistItems_OfAnEmptyOwnPlaylist_AreAnEmptyPage_NotANotFound()
    {
        var playlist = AddPlaylist("New ideas", "Playlist", []);
        await using var app = await StartAsync();

        var page = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items");

        Assert.Empty(page["items"]!.AsArray());
        Assert.Equal(0, page["total_count"]!.GetValue<int>());
        Assert.False(page["has_more"]!.GetValue<bool>());
        Assert.Null(page["next_cursor"]);
    }

    [Fact]
    public async Task PlaylistItems_KidsProfile_LeaveOutItemsOverTheLimit_FromThePageAndTheTotal()
    {
        var members = new[]
        {
            AddWork("Movies", "Kids G", "G", _alpha, ".mkv"),
            AddWork("Movies", "Grown-up R", "R", _alpha, ".mkv"),
            AddWork("Movies", "Kids PG", "PG", _alpha, ".mkv"),
        };
        var playlist = AddPlaylist("Movie night", "Playlist", members);
        SetContentLimit("PG");
        await using var app = await StartAsync();

        var page = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items");

        Assert.Equal(["Kids G", "Kids PG"], page["items"]!.AsArray().Select(item => item!["title"]!.GetValue<string>()));
        Assert.Equal(2, page["total_count"]!.GetValue<int>());
        Assert.DoesNotContain("Grown-up R", page.ToJsonString(), StringComparison.Ordinal);

        // The same device without the limit sees all three, so the limit is the only difference.
        SetContentLimit(null);
        Assert.Equal(3, (await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items"))["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task PlaylistItems_OfASmartPlaylist_ResolveTheRuleTheSameWayTheDashboardDoes()
    {
        var movies = AddThreeWorks();
        var song = AddWork("Music", "Not a movie", "G", _alpha, ".flac");
        var smart = AddPlaylist("Movie picks", "Playlist", [], resolution: "query", membership: "Smart", ruleJson: MovieRule);
        await using var app = await StartAsync();

        var page = await GetJsonAsync(app, $"/api/v1/collections/{smart}/items");
        var ids = page["items"]!.AsArray().Select(item => item!["work_id"]!.GetValue<Guid>()).ToList();

        // The library's other films match the rule too; every one of the three added here is in, and the song is not.
        Assert.Equal(ids.Count, page["total_count"]!.GetValue<int>());
        Assert.All(movies, movie => Assert.Contains(movie, ids));
        Assert.DoesNotContain(song, ids);
        Assert.All(page["items"]!.AsArray(), item => Assert.NotNull(item!["asset_id"]));
    }

    [Fact]
    public async Task PlaylistItems_APlaylistSummaryStaysHiddenAndOtherCollectionsKeepTheirOwnPagingAndShape()
    {
        var songs = AddThreeWorks();
        var playlist = AddPlaylist("Road trip", "Playlist", songs);
        var curated = AddPlaylist("A curated collection", "Custom", songs);
        await using var app = await StartAsync();

        // The summary route does not serve a playlist: its entry is the playlist list's.
        Assert.Equal(HttpStatusCode.NotFound, (await SnapshotAsync(app, $"/api/v1/collections/{playlist}")).Status);

        // A collection that is not a playlist keeps its page size and leaves the playlist-only fields out.
        var page = await GetJsonAsync(app, $"/api/v1/collections/{curated}/items");
        Assert.All(page["items"]!.AsArray(), item =>
        {
            Assert.Null(item!["asset_id"]);
            Assert.Null(item["album"]);
            Assert.Null(item["duration_seconds"]);
        });
    }

    [Fact]
    public async Task PlaylistItems_LiveAnswerHasTheSamePropertiesAsTheCheckedInFixture()
    {
        var song = AddSong("Open Road", "Road Songs", artist: "The Drivers", seconds: 200.5);
        var playlist = AddPlaylist("Road trip", "Playlist", [song]);
        await using var app = await StartAsync();

        var live = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items");

        AssertSameShape(live, ReadFixture("playlist-items.json"), "$");
    }

    [Fact]
    public async Task PlaylistItems_SortOrderCountsFromZero_EvenWhenThePlaylistWasBuiltAndReorderedThroughTheRepository()
    {
        var songs = Enumerable.Range(1, 4).Select(number => AddSong($"Song {number}", "Album", seconds: 60)).ToArray();
        var playlist = AddPlaylist("Built by the app", "Playlist", []);
        var repository = new PlaylistRepository(_database);
        await repository.AddWorksAsync(playlist, songs);
        var lastItem = StoredItemOf(playlist, songs[3]);
        Assert.True(await repository.MoveItemAsync(playlist, lastItem, 1));
        Assert.Equal([1, 2, 3, 4], StoredOrders(playlist));
        await using var app = await StartAsync();

        var page = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items");
        var items = page["items"]!.AsArray();

        Assert.Equal([songs[3], songs[0], songs[1], songs[2]], items.Select(item => item!["work_id"]!.GetValue<Guid>()));
        // The stored numbers start at 1; the page counts from zero across the whole list.
        Assert.Equal([0, 1, 2, 3], items.Select(item => item!["sort_order"]!.GetValue<int>()));

        var second = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items?limit=2&cursor=2");
        Assert.Equal([2, 3], second["items"]!.AsArray().Select(item => item!["sort_order"]!.GetValue<int>()));
    }

    [Fact]
    public async Task PlaylistItems_KidsProfile_SortOrderHasNoGapsWhereAnItemOverTheLimitIsHidden()
    {
        var members = new[]
        {
            AddWork("Movies", "Kids G", "G", _alpha, ".mkv"),
            AddWork("Movies", "Grown-up R", "R", _alpha, ".mkv"),
            AddWork("Movies", "Kids PG", "PG", _alpha, ".mkv"),
            AddWork("Movies", "Another R", "R", _alpha, ".mkv"),
            AddWork("Movies", "Kids G again", "G", _alpha, ".mkv"),
        };
        var playlist = AddPlaylist("Movie night", "Playlist", []);
        await new PlaylistRepository(_database).AddWorksAsync(playlist, members);
        SetContentLimit("PG");
        await using var app = await StartAsync();

        var page = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items");
        Assert.Equal(["Kids G", "Kids PG", "Kids G again"], page["items"]!.AsArray().Select(item => item!["title"]!.GetValue<string>()));
        Assert.Equal([0, 1, 2], page["items"]!.AsArray().Select(item => item!["sort_order"]!.GetValue<int>()));

        // A later page carries on from the visible list, not from the stored numbers.
        var later = await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items?limit=1&cursor=1");
        Assert.Equal("Kids PG", later["items"]!.AsArray().Single()!["title"]!.GetValue<string>());
        Assert.Equal(1, later["items"]!.AsArray().Single()!["sort_order"]!.GetValue<int>());
        Assert.Equal(3, later["total_count"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("Smart")]
    [InlineData("PlaylistFolder")]
    public async Task PlaylistItems_OfAnOlderListTypeTheProfileOwns_AreNotFound_BecauseTheListRouteNeverReturnsOne(string type)
    {
        var songs = AddThreeWorks();
        var legacy = AddPlaylist("Old list type", type, songs);
        await using var app = await StartAsync();

        var unknown = await SnapshotAsync(app, $"/api/v1/collections/{Guid.NewGuid()}/items");
        var answer = await SnapshotAsync(app, $"/api/v1/collections/{legacy}/items");
        var list = await GetJsonAsync(app, "/api/v1/collections/playlists");

        Assert.Equal(HttpStatusCode.NotFound, answer.Status);
        Assert.Equal(unknown.Body, answer.Body);
        Assert.DoesNotContain(legacy, list["items"]!.AsArray().Select(item => item!["id"]!.GetValue<Guid>()));
    }

    [Fact]
    public async Task PlaylistItems_ARowThatIsAWholeAlbum_HasNoFileOrLength_WhileASongBesideItKeepsBoth()
    {
        var album = AddWork("Music", "Whole Album", "G", _alpha, ".flac");
        var track = AddSong("Album track", "Whole Album", seconds: 30);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE works SET work_kind = 'parent' WHERE id = @album", new { album });
            connection.Execute("UPDATE works SET work_kind = 'child', parent_work_id = @album WHERE id = @track", new { album, track });
        }

        SetValue(album, "album", "Whole Album");
        SetValue(AssetOf(album), "duration_seconds", "999");
        var song = AddSong("One Song", "Single", seconds: 75);
        var playlist = AddPlaylist("Albums and songs", "Playlist", [album, song]);
        await using var app = await StartAsync();

        var items = (await GetJsonAsync(app, $"/api/v1/collections/{playlist}/items"))["items"]!.AsArray();

        var albumRow = items.Single(item => item!["work_id"]!.GetValue<Guid>() == album)!;
        Assert.Null(albumRow["asset_id"]);
        Assert.Null(albumRow["duration_seconds"]);
        Assert.Equal("Whole Album", albumRow["album"]!.GetValue<string>());
        var songRow = items.Single(item => item!["work_id"]!.GetValue<Guid>() == song)!;
        Assert.Equal(AssetOf(song), songRow["asset_id"]!.GetValue<Guid>());
        Assert.Equal(75d, songRow["duration_seconds"]!.GetValue<double>());
    }

    // Seeds ------------------------------------------------------------------------------------------------------

    private Guid StoredItemOf(Guid playlist, Guid work)
    {
        using var connection = _database.CreateConnection();
        return connection.QuerySingle<Guid>(
            "SELECT id FROM collection_items WHERE collection_id = @playlist AND work_id = @work",
            new { playlist, work });
    }

    private List<int> StoredOrders(Guid playlist)
    {
        using var connection = _database.CreateConnection();
        return connection.Query<int>(
            "SELECT sort_order FROM collection_items WHERE collection_id = @playlist ORDER BY sort_order",
            new { playlist }).ToList();
    }

    // A song with its artist, album and stored length on the song itself.
    private Guid AddSong(string title, string album, string? artist = null, double? seconds = null)
    {
        var song = AddWork("Music", title, "G", _alpha, ".flac");
        SetValue(song, "album", album);
        if (artist is not null)
        {
            // The artist is a multi-valued fact, so it lives in the array table.
            using var connection = _database.CreateConnection();
            connection.Execute(
                "INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@song, 'artist', 0, @artist)",
                new { song, artist });
        }

        if (seconds is { } length)
        {
            SetValue(song, "duration_sec", length.ToString(CultureInfo.InvariantCulture));
        }

        return song;
    }

    private Guid AssetOf(Guid work)
    {
        using var connection = _database.CreateConnection();
        return connection.QuerySingle<Guid>(
            "SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @work",
            new { work });
    }

    private void SetValue(Guid entity, string key, string value)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@entity, @key, @value, CURRENT_TIMESTAMP)",
            new { entity, key, value });
    }

    private void SetParent(Guid child, Guid parent)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("UPDATE works SET parent_work_id = @parent WHERE id = @child", new { child, parent });
    }
}
