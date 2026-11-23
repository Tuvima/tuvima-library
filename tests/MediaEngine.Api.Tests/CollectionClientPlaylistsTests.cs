using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>
/// <c>GET /api/v1/collections/playlists</c> for paired apps: the paired profile's own playlists, listed the way the
/// Dashboard's Listen page lists them. Same host and library as the other collection client tests.
/// </summary>
public sealed partial class CollectionClientEndpointsTests
{
    private static readonly Guid Me = MediaEngine.Domain.Aggregates.Profile.SeedProfileId;

    private static readonly string MovieRule = JsonSerializer.Serialize(CollectionRuleDefinition.SingleGroup(
        [new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "Movies" }]));

    [Fact]
    public async Task Playlists_ListOnlyTheCallersOwnPlaylists_PinnedFirstThenTheSavedOrder_AndKeepEmptyAndSmartOnes()
    {
        var songs = AddThreeWorks();
        var roadTrip = AddPlaylist("Road trip", "Playlist", songs);
        var focus = AddPlaylist("Focus", "Playlist", songs[..1]);
        var empty = AddPlaylist("New ideas", "Playlist", [], createdAt: "2026-11-22 10:00:00");
        var smart = AddPlaylist("Recently played", "Playlist", [], resolution: "query", membership: "Smart", ruleJson: MovieRule);
        var pinned = AddPlaylist("Pinned one", "Playlist", songs[..1]);
        PinPlaylist(pinned);
        SaveNavigationConfig($$"""{"listenPlaylistOrder":["{{smart:D}}","{{focus:D}}","{{smart:D}}","{{Guid.NewGuid():D}}","not-a-guid",7]}""");

        // Things that are not this profile's own playlists, or not playlists at all.
        var othersPrivate = AddPlaylist("Someone else's", "Playlist", songs, profile: OtherProfile);
        var sharedWithMe = AddPlaylist("Shared with you", "Playlist", songs, profile: OtherProfile, audience: "SelectedProfiles");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO collection_profile_audience (collection_id, profile_id) VALUES (@id, @me)", new { id = sharedWithMe, me = Me });
        }

        var libraryPlaylist = AddPlaylist("Library mix", "Playlist", songs, scope: "library", owner: "Library", audience: "Everyone", profile: null);
        var ownCollection = AddPlaylist("A curated collection", "Custom", songs);
        var disabled = AddPlaylist("Retired", "Playlist", songs, enabled: false);
        // Older list types the Dashboard's own read never returns.
        var legacySmart = AddPlaylist("Old smart type", "Smart", songs);
        var legacyFolder = AddPlaylist("Old folder type", "PlaylistFolder", songs);

        await using var app = await StartAsync();
        var page = await GetJsonAsync(app, "/api/v1/collections/playlists");
        var items = page["items"]!.AsArray();
        var ids = items.Select(item => item!["id"]!.GetValue<Guid>()).ToList();

        // Pinned first, then the saved order (Recently played, Focus), then what the saved order does not know: newest first.
        Assert.Equal([pinned, smart, focus, empty, roadTrip], ids);
        Assert.Equal(5, page["total_count"]!.GetValue<int>());
        Assert.False(page["has_more"]!.GetValue<bool>());
        Assert.Null(page["next_cursor"]);
        foreach (var excluded in new[] { othersPrivate, sharedWithMe, libraryPlaylist, ownCollection, disabled, legacySmart, legacyFolder })
        {
            Assert.DoesNotContain(excluded, ids);
        }

        var emptyEntry = items.Single(item => item!["id"]!.GetValue<Guid>() == empty)!;
        Assert.Equal(0, emptyEntry["item_count"]!.GetValue<int>());
        Assert.Equal("Empty", emptyEntry["status"]!.GetValue<string>());
        var smartEntry = items.Single(item => item!["id"]!.GetValue<Guid>() == smart)!;
        Assert.Equal("Smart", smartEntry["membership_mode"]!.GetValue<string>());
        Assert.Equal("query", smartEntry["resolution"]!.GetValue<string>());
        Assert.Equal("Playlist", smartEntry["collection_type"]!.GetValue<string>());
        Assert.True(items.Single(item => item!["id"]!.GetValue<Guid>() == pinned)!["is_pinned"]!.GetValue<bool>());
        Assert.False(items.Single(item => item!["id"]!.GetValue<Guid>() == focus)!["is_pinned"]!.GetValue<bool>());
        Assert.Equal(3, items.Single(item => item!["id"]!.GetValue<Guid>() == roadTrip)!["item_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Playlists_CarryTheSummedLength_ButNeverAuthoringFieldsOrAnArtworkAddressAppsCannotOpen()
    {
        var songs = AddThreeWorks();
        var playlist = AddPlaylist("Road trip", "Playlist", songs, resolution: "query", membership: "Smart", ruleJson: MovieRule);
        var manual = AddPlaylist("Manual", "Playlist", songs);
        SetSongLength(songs[0], 60);
        SetSongLength(songs[1], 120);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                UPDATE collections
                   SET cover_artwork_path = 'C:/private/cover.jpg', rule_hash = 'abc',
                       match_mode = 'any', sort_field = 'title', sort_direction = 'asc',
                       secondary_sort_field = 'year', secondary_sort_direction = 'asc'
                 WHERE id IN (@playlist, @manual)
                """,
                new { playlist, manual });
        }

        await using var app = await StartAsync();
        var items = (await GetJsonAsync(app, "/api/v1/collections/playlists"))["items"]!.AsArray();
        var entry = items.Single(item => item!["id"]!.GetValue<Guid>() == playlist)!;
        var manualEntry = items.Single(item => item!["id"]!.GetValue<Guid>() == manual)!;

        // The rule of a smart playlist is the person's own authoring, so it is not sent.
        Assert.NotNull(entry["membership_mode"]);
        Assert.Null(entry["rule_json"]);
        Assert.Null(entry["rule_hash"]);
        // Nor are the rule's matching and sorting: they stay at their neutral values.
        Assert.Equal("all", entry["match_mode"]!.GetValue<string>());
        Assert.Null(entry["sort_field"]);
        Assert.Equal("desc", entry["sort_direction"]!.GetValue<string>());
        Assert.Null(entry["secondary_sort_field"]);
        Assert.Null(entry["secondary_sort_direction"]);
        Assert.Null(entry["profile_id"]);
        Assert.Empty(entry["selected_profile_ids"]!.AsArray());
        Assert.Null(entry["cover_artwork_url"]);
        Assert.Null(entry["background_artwork_url"]);
        Assert.Null(entry["logo_artwork_url"]);
        Assert.False(entry["can_edit"]!.GetValue<bool>());
        Assert.False(entry["can_share"]!.GetValue<bool>());
        // The person's own cover choice is still told, so an app knows whether to draw a mosaic.
        Assert.Equal("picture", entry["cover_mode"]!.GetValue<string>());
        Assert.DoesNotContain("C:/private", items.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(180d, manualEntry["total_duration_seconds"]!.GetValue<double>());
    }

    [Fact]
    public async Task Playlists_PageByCursor_WithoutRepeatsOrGaps()
    {
        var songs = AddThreeWorks();
        var made = new[] { "One", "Two", "Three", "Four", "Five" }
            .Select((name, index) => AddPlaylist(name, "Playlist", songs[..1], createdAt: $"2026-10-0{index + 1} 10:00:00"))
            .ToList();
        await using var app = await StartAsync();

        var first = await GetJsonAsync(app, "/api/v1/collections/playlists?limit=2");
        var second = await GetJsonAsync(app, $"/api/v1/collections/playlists?limit=2&cursor={first["next_cursor"]!.GetValue<string>()}");
        var third = await GetJsonAsync(app, $"/api/v1/collections/playlists?limit=2&cursor={second["next_cursor"]!.GetValue<string>()}");
        var all = await GetJsonAsync(app, "/api/v1/collections/playlists");

        Assert.True(first["has_more"]!.GetValue<bool>());
        Assert.True(second["has_more"]!.GetValue<bool>());
        Assert.False(third["has_more"]!.GetValue<bool>());
        Assert.Null(third["next_cursor"]);
        Assert.Equal(5, first["total_count"]!.GetValue<int>());
        var paged = first["items"]!.AsArray().Concat(second["items"]!.AsArray()).Concat(third["items"]!.AsArray())
            .Select(item => item!["id"]!.GetValue<Guid>()).ToList();
        Assert.Equal(all["items"]!.AsArray().Select(item => item!["id"]!.GetValue<Guid>()), paged);
        // Newest first, because nothing is pinned or saved in an order.
        Assert.Equal(Enumerable.Reverse(made), paged);

        // A cursor past the end is an empty page, not an error.
        var past = await GetJsonAsync(app, "/api/v1/collections/playlists?cursor=50");
        Assert.Empty(past["items"]!.AsArray());
        Assert.False(past["has_more"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("100001")]
    public async Task Playlists_ACursorThatIsNotAnOffset_IsABadRequest(string cursor)
    {
        await using var app = await StartAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await SnapshotAsync(app, $"/api/v1/collections/playlists?cursor={cursor}")).Status);
    }

    [Fact]
    public async Task Playlists_AProfileWithNoPlaylists_GetsAnEmptyPage()
    {
        await using var app = await StartAsync();

        var page = await GetJsonAsync(app, "/api/v1/collections/playlists");

        Assert.Empty(page["items"]!.AsArray());
        Assert.Equal(0, page["total_count"]!.GetValue<int>());
        Assert.False(page["has_more"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Playlists_KidsProfile_CountsLeaveOutItemsOverTheLimit_AndNoOtherPlaylistLeaksIn()
    {
        // Three hand-picked members: one G, one PG and one R. A Kids limit of PG leaves two of them in the count.
        var members = new[]
        {
            AddWork("Movies", "Kids G", "G", _alpha, ".mkv"),
            AddWork("Movies", "Kids PG", "PG", _alpha, ".mkv"),
            AddWork("Movies", "Grown-up R", "R", _alpha, ".mkv"),
        };
        var playlist = AddPlaylist("Movie night", "Playlist", members);
        var othersAdultList = AddPlaylist("Adults only picks", "Playlist", members, profile: OtherProfile);
        SetContentLimit("PG");
        await using var app = await StartAsync();

        var page = await GetJsonAsync(app, "/api/v1/collections/playlists");
        var entry = page["items"]!.AsArray().Single()!;

        Assert.Equal(playlist, entry["id"]!.GetValue<Guid>());
        Assert.Equal(2, entry["item_count"]!.GetValue<int>());
        Assert.DoesNotContain(othersAdultList.ToString("D"), page.ToJsonString(), StringComparison.OrdinalIgnoreCase);

        // The same device without the limit sees the whole count, so the limit is the only difference.
        SetContentLimit(null);
        Assert.Equal(3, (await GetJsonAsync(app, "/api/v1/collections/playlists"))["items"]!.AsArray().Single()!["item_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Playlists_AreReadOnly_AndNoWriteVerbReachesThem()
    {
        await using var app = await StartAsync();

        using var post = await app.Client.PostAsync("/api/v1/collections/playlists", new StringContent("{}"));
        using var put = await app.Client.PutAsync("/api/v1/collections/playlists", new StringContent("{}"));
        using var delete = await app.Client.DeleteAsync("/api/v1/collections/playlists");

        Assert.All(new[] { post, put, delete }, response =>
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed }));
    }

    [Fact]
    public async Task Playlists_LiveAnswerHasTheSamePropertiesAsTheCheckedInFixture()
    {
        var songs = AddThreeWorks();
        AddPlaylist("Road trip", "Playlist", songs);
        SetSongLength(songs[0], 90);
        await using var app = await StartAsync();

        var live = await GetJsonAsync(app, "/api/v1/collections/playlists");

        AssertSameShape(live, ReadFixture("playlists.json"), "$");
    }

    [Fact]
    public void PlaylistListOrder_IgnoresASavedOrderThatIsNotJson_OrNotAnObject_OrHasNoOrderList()
    {
        var older = Dto("Older", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = Dto("Newer", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

        foreach (var config in new string?[] { null, "", "   ", "{ this is not json", "[]", "\"text\"", "{}", "{\"listenPlaylistOrder\":\"x\"}" })
        {
            // Newest first, because there is no usable saved order.
            Assert.Equal([newer.Id, older.Id], PlaylistListOrder.Order([older, newer], config).Select(item => item.Id));
        }
    }

    [Fact]
    public void PlaylistListOrder_PutsPinnedFirst_ThenTheSavedOrder_ThenWhatItDoesNotKnow()
    {
        var a = Dto("A", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var b = Dto("B", new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero));
        var c = Dto("C", new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), pinned: true);
        var d = Dto("D", new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero));
        var config = $$"""{"listenPlaylistOrder":["{{b.Id:D}}","{{a.Id:D}}","{{b.Id:D}}"]}""";

        Assert.Equal([c.Id, b.Id, a.Id, d.Id], PlaylistListOrder.Order([a, b, c, d], config).Select(item => item.Id));
    }

    [Theory]
    [InlineData("Playlist", true)]
    [InlineData("playlist", true)]
    [InlineData("Smart", false)]
    [InlineData("PlaylistFolder", false)]
    [InlineData("Custom", false)]
    [InlineData("Series", false)]
    [InlineData("Universe", false)]
    public void PlaylistListOrder_ListsOnlyThePlaylistTypeTheDashboardDoes(string type, bool expected) =>
        Assert.Equal(expected, PlaylistListOrder.IsUserVisiblePlaylist(new ManagedCollectionDto { CollectionType = type }));

    // Seeds ------------------------------------------------------------------------------------------------------

    private Guid[] AddThreeWorks() =>
    [
        AddWork("Movies", "Playlist member one", "G", _alpha, ".mkv"),
        AddWork("Movies", "Playlist member two", "G", _alpha, ".mkv"),
        AddWork("Movies", "Playlist member three", "G", _alpha, ".mkv"),
    ];

    private Guid AddPlaylist(
        string name,
        string type,
        IReadOnlyList<Guid> works,
        Guid? profile = null,
        string scope = "user",
        string owner = "Profile",
        string audience = "Private",
        bool enabled = true,
        string createdAt = "2026-10-01 10:00:00",
        string resolution = "materialized",
        string membership = "Manual",
        string? ruleJson = null)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, membership_mode, owner_kind, audience, profile_id, created_at, rule_json)
            VALUES (@id, @name, @type, @scope, @resolution, @enabled, @membership, @owner, @audience, @profile, @createdAt, @ruleJson);
            """,
            new
            {
                id,
                name,
                type,
                scope,
                resolution,
                membership,
                owner,
                audience,
                enabled = enabled ? 1 : 0,
                createdAt,
                ruleJson,
                profile = scope == "library" ? (Guid?)null : profile ?? Me,
            });
        for (var index = 0; index < works.Count; index++)
        {
            connection.Execute(
                "INSERT INTO collection_items (id, collection_id, work_id, sort_order) VALUES (@item, @id, @work, @index)",
                new { item = Guid.NewGuid(), id, work = works[index], index });
        }

        return id;
    }

    private void PinPlaylist(Guid playlist)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profile_pinned_playlists (profile_id, collection_id, pinned_at) VALUES (@me, @playlist, CURRENT_TIMESTAMP)",
            new { me = Me, playlist });
    }

    // A song's length is stored on its file, as the playlist total reads it.
    private void SetSongLength(Guid work, double seconds)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            SELECT ma.id, 'duration_seconds', @seconds, CURRENT_TIMESTAMP
              FROM media_assets ma JOIN editions e ON e.id = ma.edition_id
             WHERE e.work_id = @work;
            """,
            new { work, seconds = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }

    private void SaveNavigationConfig(string json)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("UPDATE profiles SET navigation_config = @json WHERE id = @me", new { json, me = Me });
    }

    private static ManagedCollectionDto Dto(string name, DateTimeOffset created, bool pinned = false) =>
        new() { Id = Guid.NewGuid(), Name = name, CollectionType = "Playlist", CreatedAt = created, IsPinned = pinned };
}
