using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

// My List: the kind chips, the finished answer and the remove-many and restore actions, against a real temp store.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private static readonly JsonSerializerOptions MyListJson = new(JsonSerializerDefaults.Web);

    private async Task<Guid> SeedAlbumAsync(Guid library, string albumTitle)
    {
        var track = await InsertOwnedWorkWithIdAsync(library, $"{albumTitle} track", "Music");
        var album = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@album, 'Music', 'parent', 'accepted');
            UPDATE works SET parent_work_id = @album WHERE id = @track;
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@asset, 'album', @albumTitle, CURRENT_TIMESTAMP);
            """, new { album, track = track.WorkId, asset = track.AssetId, albumTitle });
        return album;
    }

    private (Guid Show, IReadOnlyList<Guid> Assets) SeedShowWithEpisodes(Guid library, string title, int episodes)
    {
        var show = Guid.NewGuid();
        var assets = new List<Guid>();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@show, 'TV', 'parent', 'accepted');" +
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@show, 'title', @title, CURRENT_TIMESTAMP);",
            new { show, title });
        for (var number = 1; number <= episodes; number++)
        {
            var (work, edition, asset) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            assets.Add(asset);
            connection.Execute(
                """
                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, curator_state) VALUES (@work, 'TV', 'child', @show, @number, 'accepted');
                INSERT INTO editions (id, work_id) VALUES (@edition, @work);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
                VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP, @library);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@asset, 'title', @episodeTitle, CURRENT_TIMESTAMP), (@asset, 'season_number', '1', CURRENT_TIMESTAMP), (@asset, 'episode_number', @numberText, CURRENT_TIMESTAMP);
                """,
                new { work, show, number, numberText = number.ToString(), edition, asset, episodeTitle = $"{title} episode {number}", hash = asset.ToString("N"), path = $"C:/library/{asset:N}.mkv", library = library.ToString("D") });
        }

        return (show, assets);
    }

    private void SetProgress(Guid profile, Guid assetId, int percent)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO user_states(user_id, asset_id, progress_pct, last_accessed) VALUES (@profile, @assetId, @percent, CURRENT_TIMESTAMP)",
            new { profile, assetId, percent });
    }

    private static TypeOption[] MyListKinds(DisplayGridPageDto? page) => page!.Filters
        .Single(group => group.Key == DisplayGridValues.FilterType).Options
        .Select(option => new TypeOption(option.Value, option.Label, option.Count)).ToArray();

    private sealed record TypeOption(string Value, string Label, int Count);

    [Fact]
    public async Task Grid_MyListOffersOneKindChipPerKindInTheFixedOrderWithAlbumsAndPlaylistsNamed()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch, AccountFeatureId.Read, AccountFeatureId.Listen);
        var film = await InsertOwnedWorkWithIdAsync(library, "Arrival", "Movie");
        var book = await InsertOwnedWorkWithIdAsync(library, "Dune", "Book");
        var comic = await InsertOwnedWorkWithIdAsync(library, "Saga", "Comic");
        var audiobook = await InsertOwnedWorkWithIdAsync(library, "Heard book", "Audiobook");
        var album = await SeedAlbumAsync(library, "Roads");
        var collection = Guid.NewGuid();
        var playlist = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO collections (id, display_name, collection_type, created_at) VALUES (@collection, 'Heist films', 'Custom', CURRENT_TIMESTAMP);
                INSERT INTO collections (id, display_name, collection_type, created_at) VALUES (@playlist, 'Road trip', 'Playlist', CURRENT_TIMESTAMP);
                """, new { collection, playlist });
        }

        // Saved newest-first in the opposite order to the chips, so the chip order cannot come from the saved order.
        var state = new ProfileStateRepository(_database);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Playlist, playlist);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Collection, collection);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Album, album);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Audiobook, audiobook.WorkId);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Comic, comic.WorkId);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Book, book.WorkId);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, film.WorkId);
        var row = new DisplayHomeCollectionRow { CollectionId = collection, Title = "Heist films", ItemCount = 3, CreatedAt = DateTimeOffset.UtcNow };

        var page = await CreateGridComposer(context, [row]).BuildGridAsync("personal", "my-list", new DisplayGridQuery(), ProfileForLookups);

        Assert.Equal(
            [
                new TypeOption("movie", "Movies", 1),
                new TypeOption("book", "Books", 1),
                new TypeOption("comic", "Comics", 1),
                new TypeOption("audiobook", "Audiobooks", 1),
                new TypeOption("music", "Albums", 1),
                new TypeOption("collection", "Collections", 1),
                new TypeOption("playlist", "Playlists", 1),
            ],
            MyListKinds(page));
    }

    [Fact]
    public async Task Grid_MyListKindFilterShowsOnlyThatKindAndKeepsTheOtherChips()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch, AccountFeatureId.Listen);
        var film = await InsertOwnedWorkWithIdAsync(library, "Arrival", "Movie");
        var album = await SeedAlbumAsync(library, "Roads");
        var playlist = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO collections (id, display_name, collection_type, created_at) VALUES (@playlist, 'Road trip', 'Playlist', CURRENT_TIMESTAMP);",
                new { playlist });
        }

        var state = new ProfileStateRepository(_database);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, film.WorkId);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Album, album);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Playlist, playlist);

        var composer = CreateGridComposer(context);
        var playlists = await composer.BuildGridAsync("personal", "my-list", new DisplayGridQuery(Type: "playlist"), ProfileForLookups);
        var albums = await composer.BuildGridAsync("personal", "my-list", new DisplayGridQuery(Type: "music"), ProfileForLookups);

        Assert.Equal(["Road trip"], GridTitles(playlists));
        Assert.Equal(["Roads"], GridTitles(albums));
        Assert.Equal(3, MyListKinds(playlists).Length);
        Assert.Single(MyListKinds(playlists), option => option.Value == "playlist" && option.Count == 1);
    }

    [Fact]
    public async Task Grid_OtherGridsKeepCallingTheMusicKindMusic()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Listen);
        await InsertOwnedWorkWithIdAsync(library, "Heard book", "Audiobook");
        await SeedAlbumAsync(library, "Roads");

        var page = await CreateGridComposer(context).BuildGridAsync("home", "listen-next", new DisplayGridQuery(), ProfileForLookups);

        Assert.Equal(["Audiobooks", "Music"], MyListKinds(page).Select(option => option.Label).ToArray());
    }

    [Fact]
    public async Task FinishedSaved_ListsFinishedMoviesBooksAndFullyWatchedShowsButNeverAlbumsCollectionsOrPlaylists()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch, AccountFeatureId.Read, AccountFeatureId.Listen);
        var finishedFilm = await InsertOwnedWorkWithIdAsync(library, "Finished film", "Movie");
        var startedFilm = await InsertOwnedWorkWithIdAsync(library, "Started film", "Movie");
        var untouchedFilm = await InsertOwnedWorkWithIdAsync(library, "Untouched film", "Movie");
        var finishedBook = await InsertOwnedWorkWithIdAsync(library, "Finished book", "Book");
        var watchedShow = SeedShowWithEpisodes(library, "Watched show", 2);
        var partlyWatchedShow = SeedShowWithEpisodes(library, "Partly watched show", 2);
        var album = await SeedAlbumAsync(library, "Roads");
        var collection = Guid.NewGuid();
        var playlist = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO collections (id, display_name, collection_type, created_at) VALUES (@collection, 'Heist films', 'Custom', CURRENT_TIMESTAMP);
                INSERT INTO collections (id, display_name, collection_type, created_at) VALUES (@playlist, 'Road trip', 'Playlist', CURRENT_TIMESTAMP);
                """, new { collection, playlist });
        }

        SetProgress(ProfileForLookups, finishedFilm.AssetId, 100);
        SetProgress(ProfileForLookups, startedFilm.AssetId, 40);
        SetProgress(ProfileForLookups, finishedBook.AssetId, 100);
        foreach (var asset in watchedShow.Assets)
        {
            SetProgress(ProfileForLookups, asset, 100);
        }

        SetProgress(ProfileForLookups, partlyWatchedShow.Assets[0], 100);
        var state = new ProfileStateRepository(_database);
        foreach (var (kind, id) in new[]
        {
            (ProfileEntityKind.Movie, finishedFilm.WorkId), (ProfileEntityKind.Movie, startedFilm.WorkId), (ProfileEntityKind.Movie, untouchedFilm.WorkId),
            (ProfileEntityKind.Book, finishedBook.WorkId), (ProfileEntityKind.TvShow, watchedShow.Show), (ProfileEntityKind.TvShow, partlyWatchedShow.Show),
            (ProfileEntityKind.Album, album), (ProfileEntityKind.Collection, collection), (ProfileEntityKind.Playlist, playlist),
        })
        {
            await state.SaveItemAsync(ProfileForLookups, kind, id);
        }

        var row = new DisplayHomeCollectionRow { CollectionId = collection, Title = "Heist films", ItemCount = 3, CreatedAt = DateTimeOffset.UtcNow };
        var finished = await CreateGridComposer(context, [row]).LoadFinishedSavedAsync(ProfileForLookups);

        Assert.Equal(
            new[] { finishedFilm.WorkId, finishedBook.WorkId, watchedShow.Show }.Order(),
            finished.Select(entry => entry.EntityId).Order());
        Assert.Contains(finished, entry => entry.Kind == ProfileEntityKind.TvShow && entry.EntityId == watchedShow.Show);
    }

    [Fact]
    public async Task FinishedSaved_LeavesOutAFinishedTitleTheProfileMayNoLongerSee()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var family = await InsertOwnedWorkWithIdAsync(library, "Family film", "Movie");
        var adult = await InsertOwnedWorkWithIdAsync(library, "Adult film", "Movie");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@family, 'content_rating', 'PG', CURRENT_TIMESTAMP), (@adult, 'content_rating', 'R', CURRENT_TIMESTAMP);
                """, new { family = family.WorkId, adult = adult.WorkId });
        }

        SetProgress(ProfileForLookups, family.AssetId, 100);
        SetProgress(ProfileForLookups, adult.AssetId, 100);
        var state = new ProfileStateRepository(_database);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, family.WorkId);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, adult.WorkId);

        SetContentLimit("R");
        var allowed = await CreateGridComposer(context).LoadFinishedSavedAsync(ProfileForLookups);
        SetContentLimit("PG");
        var limited = await CreateGridComposer(context).LoadFinishedSavedAsync(ProfileForLookups);

        Assert.Equal(2, allowed.Count);
        Assert.Equal([family.WorkId], limited.Select(entry => entry.EntityId).ToArray());
    }

    [Fact]
    public async Task FinishedSaved_NeverCountsWhatAnotherProfileFinishedOrSaved()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var mine = await InsertOwnedWorkWithIdAsync(library, "My film", "Movie");
        var shared = await InsertOwnedWorkWithIdAsync(library, "Shared film", "Movie");
        var other = InsertOtherProfile();
        // The other profile finished both and saved one; this profile finished only its own and saved both.
        SetProgress(other, mine.AssetId, 100);
        SetProgress(other, shared.AssetId, 100);
        SetProgress(ProfileForLookups, mine.AssetId, 100);
        var state = new ProfileStateRepository(_database);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, mine.WorkId);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, shared.WorkId);
        await state.SaveItemAsync(other, ProfileEntityKind.Movie, shared.WorkId);

        var composer = CreateGridComposer(context);
        var finished = await composer.LoadFinishedSavedAsync(ProfileForLookups);
        var withoutProfile = await composer.LoadFinishedSavedAsync(null);

        Assert.Equal([mine.WorkId], finished.Select(entry => entry.EntityId).ToArray());
        Assert.Empty(withoutProfile);
    }

    private Task<(int Status, string Body)> CallSavedAsync(
        DefaultHttpContext context,
        string endpointName,
        string method,
        string? jsonBody = null) => CallAsync(
            app => app.MapProfileStateEndpoints(),
            services =>
            {
                ProfileStateServices(services, context);
                services.AddSingleton(CreateGridComposer(context));
            },
            endpointName, context, method, jsonBody: jsonBody);

    private static string Body(object request) => JsonSerializer.Serialize(request, MyListJson);

    private static ProfileSavedItemDto[] Saved(string body) =>
        JsonSerializer.Deserialize<ProfileSavedItemDto[]>(body, MyListJson)!;

    [Fact]
    public async Task SavedFinishedAction_ReturnsTheProfilesFinishedEntriesWithTheirSavedDates()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var done = await InsertOwnedWorkWithIdAsync(library, "Done film", "Movie");
        var open = await InsertOwnedWorkWithIdAsync(library, "Open film", "Movie");
        SetProgress(ProfileForLookups, done.AssetId, 100);
        var state = new ProfileStateRepository(_database);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, done.WorkId);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, open.WorkId);
        StampSaved(ProfileForLookups, done.WorkId, "2026-10-01T10:00:00.0000000+00:00");

        var response = await CallSavedAsync(context, "GetFinishedSavedProfileItems", "GET");

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        var item = Assert.Single(Saved(response.Body));
        Assert.Equal(done.WorkId, item.EntityId);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T10:00:00Z"), item.SavedAt);
    }

    [Fact]
    public async Task SavedRemoveAction_ReturnsTheOriginalsSkipsSongsAndUnsavedItemsAndLeavesAnotherProfilesListAlone()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch, AccountFeatureId.Listen);
        var first = await InsertOwnedWorkWithIdAsync(library, "First film", "Movie");
        var second = await InsertOwnedWorkWithIdAsync(library, "Second film", "Movie");
        var song = await InsertOwnedWorkWithIdAsync(library, "A song", "Music");
        var notSaved = await InsertOwnedWorkWithIdAsync(library, "Not saved film", "Movie");
        var other = InsertOtherProfile();
        var state = new ProfileStateRepository(_database);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, first.WorkId, position: 3);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, second.WorkId);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Song, song.WorkId);
        await state.SaveItemAsync(other, ProfileEntityKind.Movie, first.WorkId);
        StampSaved(ProfileForLookups, first.WorkId, "2026-10-01T10:00:00.0000000+00:00");

        var request = new RemoveSavedItemsRequest(
        [
            new ProfileSavedItemKeyDto(ProfileEntityKind.Movie, first.WorkId),
            new ProfileSavedItemKeyDto(ProfileEntityKind.Movie, notSaved.WorkId),
            new ProfileSavedItemKeyDto(ProfileEntityKind.Movie, Guid.NewGuid()),
            new ProfileSavedItemKeyDto(ProfileEntityKind.Song, song.WorkId),
            new ProfileSavedItemKeyDto(ProfileEntityKind.Movie, first.WorkId),
        ]);
        var response = await CallSavedAsync(context, "RemoveSavedProfileItems", "POST", Body(request));

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        var removed = Assert.Single(Saved(response.Body));
        Assert.Equal(ProfileEntityKind.Movie, removed.EntityKind);
        Assert.Equal(first.WorkId, removed.EntityId);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T10:00:00Z"), removed.SavedAt);
        Assert.Equal(3, removed.Position);
        Assert.Null(await state.GetSavedItemAsync(ProfileForLookups, ProfileEntityKind.Movie, first.WorkId));
        Assert.NotNull(await state.GetSavedItemAsync(ProfileForLookups, ProfileEntityKind.Movie, second.WorkId));
        Assert.NotNull(await state.GetSavedItemAsync(ProfileForLookups, ProfileEntityKind.Song, song.WorkId));
        Assert.NotNull(await state.GetSavedItemAsync(other, ProfileEntityKind.Movie, first.WorkId));
    }

    [Fact]
    public async Task SavedRemoveAction_RefusesMoreThanFiveHundredItems()
    {
        var (context, _) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var request = new RemoveSavedItemsRequest(
            Enumerable.Range(0, RemoveSavedItemsRequest.MaxItems + 1).Select(_ => new ProfileSavedItemKeyDto(ProfileEntityKind.Movie, Guid.NewGuid())).ToList());

        var response = await CallSavedAsync(context, "RemoveSavedProfileItems", "POST", Body(request));

        Assert.Equal(StatusCodes.Status400BadRequest, response.Status);
    }

    [Fact]
    public async Task SavedRestoreAction_PutsRemovedItemsBackWithTheirSavedDatesAndOrder()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var older = await InsertOwnedWorkWithIdAsync(library, "Older film", "Movie");
        var newer = await InsertOwnedWorkWithIdAsync(library, "Newer film", "Movie");
        var state = new ProfileStateRepository(_database);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, older.WorkId, position: 2);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, newer.WorkId);
        StampSaved(ProfileForLookups, older.WorkId, "2026-10-01T10:00:00.0000000+00:00");
        StampSaved(ProfileForLookups, newer.WorkId, "2026-10-02T10:00:00.0000000+00:00");
        var removed = Saved((await CallSavedAsync(context, "RemoveSavedProfileItems", "POST", Body(new RemoveSavedItemsRequest(
        [
            new ProfileSavedItemKeyDto(ProfileEntityKind.Movie, older.WorkId),
            new ProfileSavedItemKeyDto(ProfileEntityKind.Movie, newer.WorkId),
        ]))))
            .Body);
        Assert.Empty(await state.GetSavedItemsAsync(ProfileForLookups));

        var response = await CallSavedAsync(context, "RestoreSavedProfileItems", "POST", Body(new RestoreSavedItemsRequest(removed)));

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        Assert.Equal(2, Saved(response.Body).Length);
        var older2 = await state.GetSavedItemAsync(ProfileForLookups, ProfileEntityKind.Movie, older.WorkId);
        var newer2 = await state.GetSavedItemAsync(ProfileForLookups, ProfileEntityKind.Movie, newer.WorkId);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T10:00:00Z"), older2!.SavedAt);
        Assert.Equal(2, older2.Position);
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T10:00:00Z"), newer2!.SavedAt);
        var grid = await CreateGridComposer(context).BuildGridAsync("personal", "my-list", new DisplayGridQuery(), ProfileForLookups);
        Assert.Equal(["Newer film", "Older film"], GridTitles(grid));
    }

    [Fact]
    public async Task SavedRestoreAction_RefusesItemsTheProfileMayNotSeeAndSongsAndSavesNothingForAnotherProfile()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch, AccountFeatureId.Listen);
        var family = await InsertOwnedWorkWithIdAsync(library, "Family film", "Movie");
        var adult = await InsertOwnedWorkWithIdAsync(library, "Adult film", "Movie");
        var song = await InsertOwnedWorkWithIdAsync(library, "A song", "Music");
        var other = InsertOtherProfile();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@family, 'content_rating', 'PG', CURRENT_TIMESTAMP), (@adult, 'content_rating', 'R', CURRENT_TIMESTAMP);
                """, new { family = family.WorkId, adult = adult.WorkId });
        }

        SetContentLimit("PG");
        var savedAt = DateTimeOffset.Parse("2026-09-01T08:00:00Z");
        var request = new RestoreSavedItemsRequest(
        [
            new ProfileSavedItemDto(ProfileEntityKind.Movie, family.WorkId, savedAt, null),
            new ProfileSavedItemDto(ProfileEntityKind.Movie, adult.WorkId, savedAt, null),
            new ProfileSavedItemDto(ProfileEntityKind.Song, song.WorkId, savedAt, null),
            new ProfileSavedItemDto(ProfileEntityKind.Movie, Guid.NewGuid(), savedAt, null),
        ]);
        var response = await CallSavedAsync(context, "RestoreSavedProfileItems", "POST", Body(request));

        Assert.Equal(StatusCodes.Status200OK, response.Status);
        var restored = Assert.Single(Saved(response.Body));
        Assert.Equal(family.WorkId, restored.EntityId);
        Assert.Equal(savedAt, restored.SavedAt);
        var state = new ProfileStateRepository(_database);
        Assert.Single(await state.GetSavedItemsAsync(ProfileForLookups));
        Assert.Empty(await state.GetSavedItemsAsync(other));
    }
}
