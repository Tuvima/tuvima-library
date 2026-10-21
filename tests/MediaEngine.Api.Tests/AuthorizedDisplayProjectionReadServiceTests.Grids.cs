using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Playback;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

// The "View all" grid read against a real temp SQLite store and the authorized projection the lane pages read.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private CollectionCatalogReadService CreateGridCatalog() => new(
        new CollectionRepository(_database),
        new SeriesManifestRepository(_database),
        new PersonRepository(_database),
        new ArtworkPaletteService(),
        new CollectionMediaLookupReadService(_database),
        _database,
        NullLogger<CollectionCatalogReadService>.Instance);

    // `collections` stands in for the home collections; `realCollections` reads them from the store the way the Engine does.
    private DisplayComposerService CreateGridComposer(
        DefaultHttpContext context,
        IReadOnlyList<DisplayHomeCollectionRow>? collections = null,
        MusicPlayStatsRepository? musicStats = null,
        bool realCollections = false)
    {
        var catalog = CreateGridCatalog();
        var homeCollections = realCollections
            ? new DisplayHomeCollectionProjectionReader(new CollectionPlacementRepository(_database), catalog, new ProfileRepository(_database))
            : null;
        var authorized = CreateService(context, new GridProjection(
            new DisplayWorkProjectionReader(_database),
            new DisplayJourneyProjectionReader(_database),
            collections ?? [],
            homeCollections));
        var cards = new DisplayCardBuilder();
        return new DisplayComposerService(
            authorized,
            cards,
            new DisplayShelfBuilder(cards),
            musicStats: musicStats,
            formats: new DisplayFormatReader(_database, new MemoryCache(new MemoryCacheOptions())),
            personal: new DisplayGridPersonalReader(new ProfileStateRepository(_database), new ProfileRepository(_database), catalog));
    }

    private sealed class GridProjection(
        DisplayWorkProjectionReader works,
        DisplayJourneyProjectionReader journey,
        IReadOnlyList<DisplayHomeCollectionRow> collections,
        DisplayHomeCollectionProjectionReader? homeCollections = null) : IRawDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => works.LoadAsync(ct);
        public Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct) => works.LoadAsync(ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => journey.LoadAsync(profileId, lane, ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => journey.LoadAsync(profileId, lane, ct, includeCompleted: true);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) =>
            homeCollections is null ? Task.FromResult(collections) : homeCollections.LoadAsync(profileId, ct);
    }

    // Counts the connections a repository opens, so a test can tell whether a read happened at all.
    private sealed class CountingDatabase(IDatabaseConnection inner) : IDatabaseConnection
    {
        public int Opened { get; private set; }

        public Microsoft.Data.Sqlite.SqliteConnection CreateConnection()
        {
            Opened++;
            return inner.CreateConnection();
        }

        public Microsoft.Data.Sqlite.SqliteConnection Open() => inner.Open();
        public Task<T> ExecuteReadAsync<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default) => inner.ExecuteReadAsync(body, ct);
        public void InitializeSchema() => inner.InitializeSchema();
        public void RunStartupChecks() => inner.RunStartupChecks();
        public Task AcquireWriteLockAsync(CancellationToken ct = default, string holderFile = "", string holderMember = "") => inner.AcquireWriteLockAsync(ct, holderFile, holderMember);
        public void ReleaseWriteLock() => inner.ReleaseWriteLock();
        public string? DescribeWriteLockHolder() => inner.DescribeWriteLockHolder();
        public Task<T> ExecuteWriteAsync<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, CancellationToken, T> body, CancellationToken ct = default, string holderFile = "", string holderMember = "") => inner.ExecuteWriteAsync(body, ct, holderFile, holderMember);
        public Task ExecuteWriteAsync(Action<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, CancellationToken> body, CancellationToken ct = default, string holderFile = "", string holderMember = "") => inner.ExecuteWriteAsync(body, ct, holderFile, holderMember);
        public void Dispose() { }
    }

    // A second file of the same Work, owned in another library (it carries the title too, as a real second copy would).
    private void AddMovieFileInLibrary(Guid workId, Guid libraryId, int videoHeight, string title)
    {
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO editions (id, work_id) VALUES (@edition, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
                VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP, @library);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@asset, 'title', @title, CURRENT_TIMESTAMP);
                """, new { edition, workId, asset, hash = asset.ToString("N"), path = $"C:/library/{asset:N}.mkv", library = libraryId.ToString("D"), title });
        }

        SetVideoHeight(asset, videoHeight);
    }

    private void SaveDirectly(Guid profile, string kind, Guid entity)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES (@profile, @kind, @entity, '2026-10-05T10:00:00.0000000+00:00')",
            new { profile, kind, entity });
    }

    private void SetOwnedPath(Guid assetId, string path)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("UPDATE media_assets SET file_path_root=@path WHERE id=@assetId", new { path, assetId });
    }

    private void SetVideoHeight(Guid assetId, int height)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES(@assetId,'video_height',@height,CURRENT_TIMESTAMP)",
            new { assetId, height = height.ToString() });
    }

    private Guid InsertOtherProfile()
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles (id, display_name, avatar_color, role, created_at) VALUES (@id, 'Other', '#7C4DFF', 'StandardUser', CURRENT_TIMESTAMP);",
            new { id });
        return id;
    }

    private void StampSaved(Guid profile, Guid entity, string savedAt)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "UPDATE profile_saved_items SET saved_at=@savedAt WHERE profile_id=@profile AND entity_id=@entity",
            new { savedAt, profile, entity });
    }

    private void StampReaction(Guid profile, Guid entity, string updatedAt)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "UPDATE profile_reactions SET updated_at=@updatedAt WHERE profile_id=@profile AND entity_id=@entity",
            new { updatedAt, profile, entity });
    }

    private static string[] GridTitles(DisplayGridPageDto? page) => page!.Items.Select(card => card.Title).ToArray();

    [Fact]
    public async Task Grid_HidesItemsOverTheProfilesContentLimit()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var family = await InsertOwnedWorkWithIdAsync(library, "Family film", "Movie");
        var adult = await InsertOwnedWorkWithIdAsync(library, "Adult film", "Movie");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@family, 'content_rating', 'PG', CURRENT_TIMESTAMP),
                       (@adult, 'content_rating', 'R', CURRENT_TIMESTAMP);
                """, new { family = family.WorkId, adult = adult.WorkId });
        }

        SetContentLimit("PG");
        var limited = await CreateGridComposer(context).BuildGridAsync("watch", "movies", new DisplayGridQuery(), ProfileForLookups);
        SetContentLimit("R");
        var raised = await CreateGridComposer(context).BuildGridAsync("watch", "movies", new DisplayGridQuery(), ProfileForLookups);

        Assert.Equal(["Family film"], GridTitles(limited));
        Assert.Equal(1, limited!.TotalCount);
        Assert.Equal(["Adult film", "Family film"], GridTitles(raised).Order());
    }

    [Fact]
    public async Task Grid_NeverShowsAProfileWithoutDocumentsADocument()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        await InsertOwnedWorkWithIdAsync(library, "Arrival", "Movie");
        var document = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT OR IGNORE INTO households (id, name, created_at)
                SELECT @household, 'Test household', '2026-10-13T00:00:00Z' WHERE NOT EXISTS (SELECT 1 FROM households);
                INSERT OR IGNORE INTO view_shared_library (household_id, library_id, created_at, updated_at)
                SELECT id, @sharedLibrary, '2026-10-13T00:00:00Z', '2026-10-13T00:00:00Z' FROM households ORDER BY rowid LIMIT 1;
                INSERT INTO local_items (id, scope_kind, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
                VALUES (@document, 'shared', (SELECT library_id FROM view_shared_library LIMIT 1), 'document', 'tax-return.pdf', 'application/pdf', '2026-10-13T00:00:00Z', '2026-10-13T00:00:00Z');
                INSERT INTO profile_view_policies (profile_id, documents_enabled, updated_at) VALUES (@profile, 0, '2026-10-13T00:00:00Z')
                ON CONFLICT(profile_id) DO UPDATE SET documents_enabled = 0;
                INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES (@profile, 'Book', @document, '2026-10-13T00:00:00Z');
                """, new { document, household = Guid.NewGuid(), sharedLibrary = Guid.NewGuid(), profile = ProfileForLookups });
        }

        var composer = CreateGridComposer(context);
        var movies = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(), ProfileForLookups);
        var saved = await composer.BuildGridAsync("personal", "my-list", new DisplayGridQuery(), ProfileForLookups);

        Assert.Equal(["Arrival"], GridTitles(movies));
        Assert.Empty(saved!.Items);
        Assert.Equal(0, saved.TotalCount);
    }

    [Fact]
    public async Task Grid_AppliesLaneGrantsSoAViewerWithoutListenSeesNoAlbums()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        await InsertOwnedWorkWithIdAsync(library, "Arrival", "Movie");
        await InsertOwnedWorkWithIdAsync(library, "Roads", "Music");

        var composer = CreateGridComposer(context);
        var watchNext = await composer.BuildGridAsync("home", "watch-next", new DisplayGridQuery(), ProfileForLookups);
        var listenNext = await composer.BuildGridAsync("home", "listen-next", new DisplayGridQuery(), ProfileForLookups);

        Assert.Equal(["Arrival"], GridTitles(watchNext));
        Assert.Empty(listenNext!.Items);
    }

    [Fact]
    public async Task Grid_MyListShowsOnlyTheSignedInProfilesSavedItemsNewestSavedFirst()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch, AccountFeatureId.Read);
        var first = await InsertOwnedWorkWithIdAsync(library, "Saved first", "Movie");
        var second = await InsertOwnedWorkWithIdAsync(library, "Saved second", "Book");
        var others = await InsertOwnedWorkWithIdAsync(library, "Saved by someone else", "Movie");
        await InsertOwnedWorkWithIdAsync(library, "Never saved", "Movie");
        var other = InsertOtherProfile();
        var state = new ProfileStateRepository(_database);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, first.WorkId);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Book, second.WorkId);
        await state.SaveItemAsync(other, ProfileEntityKind.Movie, others.WorkId);
        StampSaved(ProfileForLookups, first.WorkId, "2026-10-01T10:00:00.0000000+00:00");
        StampSaved(ProfileForLookups, second.WorkId, "2026-10-02T10:00:00.0000000+00:00");

        var composer = CreateGridComposer(context);
        var mine = await composer.BuildGridAsync("personal", "my-list", new DisplayGridQuery(), ProfileForLookups);
        var withoutProfile = await composer.BuildGridAsync("personal", "my-list", new DisplayGridQuery(), null);

        Assert.Equal(["Saved second", "Saved first"], GridTitles(mine));
        Assert.Equal(DisplayGridValues.RememberMyList, mine!.RememberKey);
        Assert.Empty(withoutProfile!.Items);
    }

    [Fact]
    public async Task Grid_MyListLeavesOutSongsAndIncludesCollectionsAndPlaylists()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch, AccountFeatureId.Listen);
        var film = await InsertOwnedWorkWithIdAsync(library, "Arrival", "Movie");
        var collection = Guid.NewGuid();
        var playlist = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO collections (id, display_name, collection_type, created_at) VALUES (@collection, 'Heist films', 'Custom', CURRENT_TIMESTAMP);
                INSERT INTO collections (id, display_name, collection_type, created_at) VALUES (@playlist, 'Road trip', 'Playlist', CURRENT_TIMESTAMP);
                INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES
                    (@profile, 'Collection', @collection, '2026-10-02T10:00:00.0000000+00:00'),
                    (@profile, 'Playlist', @playlist, '2026-10-03T10:00:00.0000000+00:00'),
                    (@profile, 'Song', @film, '2026-10-04T10:00:00.0000000+00:00'),
                    (@profile, 'Movie', @film, '2026-10-01T10:00:00.0000000+00:00');
                """, new { collection, playlist, film = film.WorkId, profile = ProfileForLookups });
        }
        var row = new DisplayHomeCollectionRow { CollectionId = collection, Title = "Heist films", ItemCount = 3, CreatedAt = DateTimeOffset.UtcNow };

        var page = await CreateGridComposer(context, [row]).BuildGridAsync("personal", "my-list", new DisplayGridQuery(), ProfileForLookups);

        Assert.Equal(["Road trip", "Heist films", "Arrival"], GridTitles(page));
        Assert.Equal(3, page!.TotalCount);
    }

    [Fact]
    public async Task Grid_LikedIncludesLikeAndLoveButNotDislikeNewestFirst()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var liked = await InsertOwnedWorkWithIdAsync(library, "Liked film", "Movie");
        var loved = await InsertOwnedWorkWithIdAsync(library, "Loved film", "Movie");
        var disliked = await InsertOwnedWorkWithIdAsync(library, "Disliked film", "Movie");
        var state = new ProfileStateRepository(_database);
        await state.SetReactionAsync(ProfileForLookups, ProfileEntityKind.Movie, liked.WorkId, ProfileReactionKind.Like);
        await state.SetReactionAsync(ProfileForLookups, ProfileEntityKind.Movie, loved.WorkId, ProfileReactionKind.Love);
        await state.SetReactionAsync(ProfileForLookups, ProfileEntityKind.Movie, disliked.WorkId, ProfileReactionKind.Dislike);
        StampReaction(ProfileForLookups, liked.WorkId, "2026-10-01T10:00:00.0000000+00:00");
        StampReaction(ProfileForLookups, loved.WorkId, "2026-10-02T10:00:00.0000000+00:00");

        var page = await CreateGridComposer(context).BuildGridAsync("personal", "liked", new DisplayGridQuery(), ProfileForLookups);

        Assert.Equal(["Loved film", "Liked film"], GridTitles(page));
        Assert.Equal(DisplayGridValues.RememberLiked, page!.RememberKey);
    }

    [Fact]
    public async Task Grid_FiltersWatchMediaByResolutionAndReadMediaByFileFormat()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch, AccountFeatureId.Read);
        var uhd = await InsertOwnedWorkWithIdAsync(library, "Ultra film", "Movie");
        var hd = await InsertOwnedWorkWithIdAsync(library, "Sharp film", "Movie");
        var sd = await InsertOwnedWorkWithIdAsync(library, "Old film", "Movie");
        var epub = await InsertOwnedWorkWithIdAsync(library, "Epub book", "Book");
        var pdf = await InsertOwnedWorkWithIdAsync(library, "Pdf book", "Book");
        var comic = await InsertOwnedWorkWithIdAsync(library, "Comic issue", "Comic");
        foreach (var film in new[] { uhd, hd, sd })
        {
            SetOwnedPath(film.AssetId, $"C:/library/{film.AssetId:N}.mkv");
        }

        SetVideoHeight(uhd.AssetId, 2160);
        SetVideoHeight(hd.AssetId, 1080);
        SetVideoHeight(sd.AssetId, 480);
        SetOwnedPath(pdf.AssetId, $"C:/library/{pdf.AssetId:N}.pdf");
        SetOwnedPath(comic.AssetId, $"C:/library/{comic.AssetId:N}.cbz");
        var composer = CreateGridComposer(context);

        var movies = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery());
        var onlyHd = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Format: "hd"));
        var only4k = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Format: "4k"));
        var books = await composer.BuildGridAsync("read", "recently-added", new DisplayGridQuery());
        var onlyPdf = await composer.BuildGridAsync("read", "recently-added", new DisplayGridQuery(Format: "pdf"));
        var onlyComic = await composer.BuildGridAsync("read", "recently-added", new DisplayGridQuery(Format: "comic"));

        var movieFormats = Assert.Single(movies!.Filters, group => group.Key == DisplayGridValues.FilterFormat);
        Assert.Equal([("4k", 1), ("hd", 1), ("sd", 1)], movieFormats.Options.Select(option => (option.Value, option.Count)));
        Assert.Equal(["Sharp film"], GridTitles(onlyHd));
        Assert.Equal(["Ultra film"], GridTitles(only4k));
        var bookFormats = Assert.Single(books!.Filters, group => group.Key == DisplayGridValues.FilterFormat);
        Assert.Equal(["EPUB", "PDF", "Comic file"], bookFormats.Options.Select(option => option.Label));
        Assert.Equal(["Pdf book"], GridTitles(onlyPdf));
        Assert.Equal(["Comic issue"], GridTitles(onlyComic));
    }

    [Fact]
    public async Task Grid_MatchesAShowToAFormatWhenAnyOwnedEpisodeHasIt()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var show = Guid.NewGuid();
        Guid pilot = Guid.Empty;
        Guid second = Guid.Empty;
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO works(id,media_type,work_kind,curator_state) VALUES(@show,'TV','parent','accepted');INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES(@show,'title','Long show',CURRENT_TIMESTAMP);", new { show });
            Guid Episode(string title, string number)
            {
                var work = Guid.NewGuid(); var edition = Guid.NewGuid(); var asset = Guid.NewGuid();
                connection.Execute("""
                    INSERT INTO works(id,media_type,work_kind,parent_work_id,curator_state) VALUES(@work,'TV','child',@show,'accepted');
                    INSERT INTO editions(id,work_id) VALUES(@edition,@work);
                    INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,presented_at,library_id) VALUES(@asset,@edition,@hash,@path,CURRENT_TIMESTAMP,@library);
                    INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES(@asset,'title',@title,CURRENT_TIMESTAMP),(@asset,'season_number','1',CURRENT_TIMESTAMP),(@asset,'episode_number',@number,CURRENT_TIMESTAMP),(@work,'show_name','Long show',CURRENT_TIMESTAMP);
                    """, new { work, show, edition, asset, hash = asset.ToString("N"), path = $"C:/library/{asset:N}.mkv", library = library.ToString("D"), title, number });
                return asset;
            }
            pilot = Episode("Pilot", "1");
            second = Episode("Second", "2");
        }
        SetVideoHeight(pilot, 480);
        SetVideoHeight(second, 2160);
        var composer = CreateGridComposer(context);

        var shows = await composer.BuildGridAsync("watch", "tv-shows", new DisplayGridQuery());
        var sd = await composer.BuildGridAsync("watch", "tv-shows", new DisplayGridQuery(Format: "sd"));
        var uhd = await composer.BuildGridAsync("watch", "tv-shows", new DisplayGridQuery(Format: "4k"));
        var hd = await composer.BuildGridAsync("watch", "tv-shows", new DisplayGridQuery(Format: "hd"));

        Assert.Single(shows!.Items);
        Assert.Equal(["Long show"], GridTitles(sd));
        Assert.Equal(["Long show"], GridTitles(uhd));
        Assert.Empty(hd!.Items);
    }

    [Fact]
    public async Task Grid_FormatFilterIgnoresFilesOwnedInLibrariesTheViewerHasNoGrantFor()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var split = await InsertOwnedWorkWithIdAsync(library, "Split film", "Movie");
        var control = await InsertOwnedWorkWithIdAsync(library, "Control film", "Movie");
        SetOwnedPath(split.AssetId, $"C:/library/{split.AssetId:N}.mkv");
        SetOwnedPath(control.AssetId, $"C:/library/{control.AssetId:N}.mkv");
        SetVideoHeight(split.AssetId, 480);
        SetVideoHeight(control.AssetId, 1080);
        // The same Work also has a 4K file, but in a library this account has no grant for.
        AddMovieFileInLibrary(split.WorkId, Guid.NewGuid(), 2160, "Split film");
        var composer = CreateGridComposer(context);

        var movies = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery());
        var only4k = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Format: "4k"));
        var onlySd = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Format: "sd"));
        var onlyHd = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Format: "hd"));

        var formats = Assert.Single(movies!.Filters, group => group.Key == DisplayGridValues.FilterFormat);
        Assert.Equal([("hd", 1), ("sd", 1)], formats.Options.Select(option => (option.Value, option.Count)));
        Assert.Empty(only4k!.Items);
        Assert.Equal(["Split film"], GridTitles(onlySd));
        Assert.Equal(["Control film"], GridTitles(onlyHd));
    }

    [Fact]
    public async Task Grid_FormatFilterCountsFilesInEveryLibraryTheViewerIsGrantedFor()
    {
        var account = Guid.NewGuid();
        var library = Guid.NewGuid();
        var other = Guid.NewGuid();
        await CreateHumanAsync(account, new HashSet<AccountFeatureId> { AccountFeatureId.Watch }, new HashSet<Guid> { library, other });
        var context = HumanContext(account, ProfileForLookups);
        var split = await InsertOwnedWorkWithIdAsync(library, "Split film", "Movie");
        SetOwnedPath(split.AssetId, $"C:/library/{split.AssetId:N}.mkv");
        SetVideoHeight(split.AssetId, 480);
        AddMovieFileInLibrary(split.WorkId, other, 2160, "Split film");
        var composer = CreateGridComposer(context);

        var movies = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery());
        var only4k = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Format: "4k"));
        var onlySd = await composer.BuildGridAsync("watch", "movies", new DisplayGridQuery(Format: "sd"));

        var formats = Assert.Single(movies!.Filters, group => group.Key == DisplayGridValues.FilterFormat);
        Assert.Equal([("4k", 1), ("sd", 1)], formats.Options.Select(option => (option.Value, option.Count)));
        Assert.Equal(["Split film"], GridTitles(only4k));
        Assert.Equal(["Split film"], GridTitles(onlySd));
    }

    [Fact]
    public async Task Grid_MyListLeavesOutSavedItemsOverTheContentLimitAndInLibrariesWithoutAGrant()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var family = await InsertOwnedWorkWithIdAsync(library, "Family film", "Movie");
        var adult = await InsertOwnedWorkWithIdAsync(library, "Adult film", "Movie");
        var elsewhere = await InsertOwnedWorkWithIdAsync(Guid.NewGuid(), "Other library film", "Movie");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@family, 'content_rating', 'PG', CURRENT_TIMESTAMP),
                       (@adult, 'content_rating', 'R', CURRENT_TIMESTAMP);
                """, new { family = family.WorkId, adult = adult.WorkId });
        }

        var state = new ProfileStateRepository(_database);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, family.WorkId);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, adult.WorkId);
        await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, elsewhere.WorkId);

        SetContentLimit("R");
        var allowed = await CreateGridComposer(context).BuildGridAsync("personal", "my-list", new DisplayGridQuery(), ProfileForLookups);
        SetContentLimit("PG");
        var limited = await CreateGridComposer(context).BuildGridAsync("personal", "my-list", new DisplayGridQuery(), ProfileForLookups);

        Assert.Equal(["Adult film", "Family film"], GridTitles(allowed).Order());
        Assert.Equal(["Family film"], GridTitles(limited));
        Assert.Equal(1, limited!.TotalCount);
    }

    [Fact]
    public async Task Grid_MyListLeavesOutAnotherProfilesPrivatePlaylistAndCollectionEvenWhenSaved()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch, AccountFeatureId.Listen);
        await InsertOwnedWorkWithIdAsync(library, "Arrival", "Movie");
        var other = InsertOtherProfile();
        var othersPlaylist = Guid.NewGuid();
        var minePlaylist = Guid.NewGuid();
        var othersCollection = Guid.NewGuid();
        var sharedCollection = Guid.NewGuid();
        var member = await InsertOwnedWorkWithIdAsync(library, "Member film", "Movie");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO collections (id, display_name, collection_type, scope, profile_id, owner_kind, audience, created_at)
                VALUES (@othersPlaylist, 'Their road trip', 'Playlist', 'user', @other, 'Profile', 'Private', CURRENT_TIMESTAMP),
                       (@minePlaylist, 'My road trip', 'Playlist', 'user', @profile, 'Profile', 'Private', CURRENT_TIMESTAMP);
                INSERT INTO collections (id, display_name, collection_type, scope, profile_id, owner_kind, audience, membership_mode, resolution, created_at)
                VALUES (@othersCollection, 'Their heists', 'Custom', 'user', @other, 'Profile', 'Private', 'Manual', 'materialized', CURRENT_TIMESTAMP),
                       (@sharedCollection, 'Shared heists', 'Custom', 'library', NULL, 'Library', 'Everyone', 'Manual', 'materialized', CURRENT_TIMESTAMP);
                INSERT INTO collection_items (id, collection_id, work_id) VALUES
                    (@itemA, @othersCollection, @member), (@itemB, @sharedCollection, @member);
                INSERT INTO collection_placements (id, collection_id, location, position) VALUES
                    (@placeA, @othersCollection, 'home', 0), (@placeB, @sharedCollection, 'home', 1);
                """, new
            {
                othersPlaylist,
                minePlaylist,
                othersCollection,
                sharedCollection,
                other,
                profile = ProfileForLookups,
                member = member.WorkId,
                itemA = Guid.NewGuid(),
                itemB = Guid.NewGuid(),
                placeA = Guid.NewGuid(),
                placeB = Guid.NewGuid(),
            });
        }

        // The profile somehow holds saved rows for another profile's private playlist and collection.
        SaveDirectly(ProfileForLookups, "Playlist", othersPlaylist);
        SaveDirectly(ProfileForLookups, "Playlist", minePlaylist);
        SaveDirectly(ProfileForLookups, "Collection", othersCollection);
        SaveDirectly(ProfileForLookups, "Collection", sharedCollection);

        var page = await CreateGridComposer(context, realCollections: true)
            .BuildGridAsync("personal", "my-list", new DisplayGridQuery(), ProfileForLookups);

        // The profile's own playlist and the shared collection prove the lists load; the private ones are not there.
        Assert.Equal(["My road trip", "Shared heists"], GridTitles(page).Order());
        Assert.Equal(2, page!.TotalCount);
    }

    [Fact]
    public async Task Grid_ReadsMusicPlayStatsOnlyWhenSortedByRecentlyPlayed()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Listen);
        var track = await InsertOwnedWorkWithIdAsync(library, "Track one", "Music");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@asset, 'album', 'First album', CURRENT_TIMESTAMP)",
                new { asset = track.AssetId });
        }

        var stats = new CountingDatabase(_database);
        var composer = CreateGridComposer(context, musicStats: new MusicPlayStatsRepository(stats));

        var added = await composer.BuildGridAsync("listen", "albums", new DisplayGridQuery(), ProfileForLookups);
        var byTitle = await composer.BuildGridAsync("listen", "albums", new DisplayGridQuery(Sort: "title"), ProfileForLookups);

        Assert.Equal(["First album"], GridTitles(added));
        Assert.Equal(["First album"], GridTitles(byTitle));
        Assert.Equal(0, stats.Opened);

        var played = await composer.BuildGridAsync("listen", "albums", new DisplayGridQuery(Sort: "played"), ProfileForLookups);

        Assert.Equal(["First album"], GridTitles(played));
        Assert.True(stats.Opened > 0);
    }

    [Theory]
    [InlineData("book.epub", null, "epub")]
    [InlineData("BOOK.PDF", null, "pdf")]
    [InlineData("issue.cbz", null, "comic")]
    [InlineData("issue.cbr", null, "comic")]
    [InlineData("issue.cb7", null, "comic")]
    [InlineData("film.mkv", "2160", "4k")]
    [InlineData("film.mkv", "2000", "4k")]
    [InlineData("film.mkv", "1080", "hd")]
    [InlineData("film.mkv", "700", "hd")]
    [InlineData("film.mkv", "699", "sd")]
    [InlineData("film.mkv", null, null)]
    [InlineData("song.mp3", null, null)]
    public void FormatReader_NamesTheFormatOfOneOwnedFile(string path, string? height, string? expected)
    {
        Assert.Equal(expected, DisplayFormatReader.FormatFor(path, height));
    }
}
