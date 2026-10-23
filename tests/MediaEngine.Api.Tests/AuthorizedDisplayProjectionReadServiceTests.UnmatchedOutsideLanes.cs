using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

// Unmatched files live only under the Unmatched filter (Library setup spec D64 to D66): never in a lane, on Home, in a shelf, a "View all"
// grid, Recently added, Continue, a personal list, a collection, a people page or a normal search, for an administrator and for anyone else.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private const string IdentifiedFilm = "Identified film";
    private const string UnmatchedFilm = "Unmatched film";

    private sealed record UnmatchedFilmWorld(
        Guid Account,
        DefaultHttpContext Context,
        (Guid WorkId, Guid AssetId) Identified,
        (Guid WorkId, Guid AssetId) Unmatched);

    // One identified film and one Unmatched film in the Movies library. The person watched half of each, saved each, liked each and gave
    // each a personal tag, so every list that could show them has something to show.
    private async Task<UnmatchedFilmWorld> SeedUnmatchedFilmWorldAsync(bool administrator, bool kept)
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>(), administrator);
        var identified = await InsertOwnedWorkWithIdAsync(MoviesLibrary, IdentifiedFilm, "Movie");
        var unmatched = await InsertOwnedWorkWithIdAsync(MoviesLibrary, UnmatchedFilm, "Movie");
        await MarkUnmatchedAsync(unmatched.AssetId, MoviesLibrary, kept);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO user_states(user_id, asset_id, progress_pct, last_accessed, extended_properties)
                VALUES (@profile, @identified, 40, CURRENT_TIMESTAMP, '{"position_seconds":300,"duration_seconds":900}'),
                       (@profile, @unmatched, 40, CURRENT_TIMESTAMP, '{"position_seconds":300,"duration_seconds":900}');
                INSERT INTO profile_work_preferences (profile_id, work_id, local_tags_json, revision, updated_at)
                VALUES (@profile, @identifiedWork, '["tag"]', 1, CURRENT_TIMESTAMP), (@profile, @unmatchedWork, '["tag"]', 1, CURRENT_TIMESTAMP);
                """,
                new
                {
                    profile = ProfileForLookups,
                    identified = identified.AssetId,
                    unmatched = unmatched.AssetId,
                    identifiedWork = identified.WorkId,
                    unmatchedWork = unmatched.WorkId,
                });
        }

        var state = new ProfileStateRepository(_database);
        foreach (var film in new[] { identified, unmatched })
        {
            await state.SaveItemAsync(ProfileForLookups, ProfileEntityKind.Movie, film.WorkId);
            await state.SetReactionAsync(ProfileForLookups, ProfileEntityKind.Movie, film.WorkId, ProfileReactionKind.Like);
        }

        return new UnmatchedFilmWorld(account, HumanContext(account, ProfileForLookups), identified, unmatched);
    }

    private static string[] CardTitles(DisplayPageDto page) => [.. page.Catalog
        .Concat(page.Shelves.SelectMany(shelf => shelf.Items))
        .Select(card => card.Title)
        .Concat(page.Hero is { } hero ? [hero.Title] : [])
        .Distinct()];

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AnUnmatchedFilm_OpenOrKept_IsAbsentFromTheWatchLaneAndBrowse_ForAnAdministratorAndForAProfileWithAccess(bool administrator, bool kept)
    {
        var world = await SeedUnmatchedFilmWorldAsync(administrator, kept);
        var composer = CreateGridComposer(world.Context);

        var lane = await composer.BuildBrowseAsync("watch", null, "all", null, 0, 48, true, ProfileForLookups);
        var movies = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, true, ProfileForLookups);
        var inLibrary = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, true, ProfileForLookups, libraryId: MoviesLibrary);

        Assert.Contains(IdentifiedFilm, CardTitles(lane));
        Assert.DoesNotContain(UnmatchedFilm, CardTitles(lane));
        Assert.Equal([IdentifiedFilm], CardTitles(movies));
        Assert.Equal([IdentifiedFilm], CardTitles(inLibrary));
        Assert.Equal(1, movies.TotalCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AnUnmatchedFilm_OpenOrKept_IsAbsentFromHomeAndItsShelves_ForAnAdministratorAndForAProfileWithAccess(bool administrator, bool kept)
    {
        var world = await SeedUnmatchedFilmWorldAsync(administrator, kept);
        var composer = CreateGridComposer(world.Context);

        var home = await composer.BuildHomeAsync(true, ProfileForLookups);
        var continueWatching = await composer.BuildContinueAsync("watch", 20, true, default, null, ProfileForLookups);

        Assert.Contains(IdentifiedFilm, CardTitles(home));
        Assert.DoesNotContain(UnmatchedFilm, CardTitles(home));
        Assert.Equal([IdentifiedFilm], CardTitles(continueWatching));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AnUnmatchedFilm_OpenOrKept_IsAbsentFromEveryViewAllGridAndPersonalList_ForAnAdministratorAndForAProfileWithAccess(bool administrator, bool kept)
    {
        var world = await SeedUnmatchedFilmWorldAsync(administrator, kept);
        var composer = CreateGridComposer(world.Context);

        foreach (var (lane, shelf) in new[]
                 {
                     ("watch", "movies"), ("watch", "continue-watching"), ("home", "watch-next"), ("home", "continue"),
                     ("personal", "my-list"), ("personal", "liked"),
                 })
        {
            var grid = await composer.BuildGridAsync(lane, shelf, new DisplayGridQuery(), ProfileForLookups);

            Assert.NotNull(grid);
            // The identified film is there, so an empty answer cannot pass for "left out".
            Assert.Contains(IdentifiedFilm, GridTitles(grid));
            Assert.DoesNotContain(UnmatchedFilm, GridTitles(grid));
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AnUnmatchedFilm_OpenOrKept_IsAbsentFromRecentlyAdded_ForAnAdministratorAndForAProfileWithAccess(bool administrator, bool kept)
    {
        var world = await SeedUnmatchedFilmWorldAsync(administrator, kept);
        var authorization = CreateService(world.Context, new StubRawProjection([], []));
        var recent = new RecentCatalogueReadService(new DisplayWorkProjectionReader(_database), authorization, new DisplayCardBuilder(), _database);

        var items = await recent.LoadAsync("all", ProfileForLookups, null, 50, default);

        Assert.Equal([IdentifiedFilm], items.Select(item => item.Catalogue!.Title).ToArray());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AnUnmatchedFilm_OpenOrKept_IsAbsentFromTheWorkReadsCollectionsUniversesAndPeoplePagesUse(bool administrator, bool kept)
    {
        var world = await SeedUnmatchedFilmWorldAsync(administrator, kept);
        var raw = RealReaders();
        var service = CreateService(world.Context, raw);

        var works = await service.LoadWorksAsync(default);
        var ids = await service.LoadVisibleWorkIdsAsync(default);
        var libraryRows = await service.LoadWorkLibraryRowsAsync(default);
        var favourites = await service.LoadFavoriteWorkIdsAsync(ProfileForLookups, default);
        await service.LoadHomeCollectionsAsync(ProfileForLookups, default);

        // Collections, universes and people pages read these (their owned titles come from the works' files).
        Assert.Equal([world.Identified.WorkId], works.Select(row => row.WorkId).ToArray());
        Assert.DoesNotContain(world.Unmatched.AssetId, works.Select(row => row.AssetId));
        Assert.Equal([world.Identified.WorkId], ids.ToArray());
        Assert.Equal([world.Identified.WorkId], libraryRows.Select(row => row.WorkId).ToArray());
        Assert.DoesNotContain(world.Unmatched.WorkId, ids);
        Assert.NotNull(raw.AllowedWorkIdsPassedToHomeCollections);
        Assert.DoesNotContain(world.Unmatched.WorkId, raw.AllowedWorkIdsPassedToHomeCollections!);
        Assert.Contains(world.Identified.WorkId, raw.AllowedWorkIdsPassedToHomeCollections!);
        Assert.DoesNotContain(world.Unmatched.WorkId, favourites);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task AnUnmatchedFilm_OpenOrKept_IsInTheUnmatchedFilter_ForAnAdministratorAndForAProfileWithAccess_AndCarriesTheKeptFlagOnlyWhenKept(bool administrator, bool kept)
    {
        var world = await SeedUnmatchedFilmWorldAsync(administrator, kept);
        var composer = BrowseComposer(world.Account);

        var page = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, unmatchedOnly: true);

        var card = Assert.Single(page.Catalog);
        Assert.Equal(UnmatchedFilm, card.Title);
        Assert.Equal(kept, card.Flags.KeptAsIs);
        Assert.DoesNotContain((await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48)).Catalog, other => other.Flags.KeptAsIs);
    }

    [Fact]
    public async Task AnUnmatchedFilm_IsStillInTheDetailReadOfItsOwnPage_SoItCanBePlayedFromTheUnmatchedFilter()
    {
        var world = await SeedUnmatchedFilmWorldAsync(administrator: false, kept: false);
        var service = CreateService(world.Context, RealReaders());

        var detail = await service.LoadDetailWorksAsync(world.Unmatched.WorkId, default);

        Assert.Contains(world.Unmatched.WorkId, detail.Select(row => row.WorkId));
    }

    [Fact]
    public async Task AFilmWithAnUnmatchedFileAndAnIdentifiedFileInTheSameLibrary_StaysInTheLanes()
    {
        var world = await SeedUnmatchedFilmWorldAsync(administrator: true, kept: false);
        await InsertAssetForWorkAsync(world.Unmatched.WorkId, Guid.NewGuid(), MoviesLibrary, "Identified copy", createWork: false);
        var composer = CreateGridComposer(world.Context);

        var movies = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, true, ProfileForLookups);
        var filter = await BrowseComposer(world.Account).BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, unmatchedOnly: true);

        // The work's one card takes its title from whichever file ranks first, so the two works are told apart by id.
        Assert.Equal(new[] { world.Identified.WorkId, world.Unmatched.WorkId }.Order(), movies.Catalog.Select(card => card.WorkId!.Value).Order());
        Assert.Equal([world.Unmatched.WorkId], filter.Catalog.Select(card => card.WorkId!.Value).ToArray());
    }

    [Fact]
    public async Task AnUnmatchedFilmInALibraryTheProfileCannotSee_IsNeverInItsUnmatchedFilter_OrItsSearch()
    {
        var world = await SeedUnmatchedFilmWorldAsync(administrator: false, kept: false);
        await HideLibraryFromAsync(world.Account, LecturesLibrary);
        var hidden = await InsertOwnedWorkWithIdAsync(LecturesLibrary, "Hidden lecture", "Movie");
        await MarkUnmatchedAsync(hidden.AssetId, LecturesLibrary, keep: false);

        var filter = await BrowseComposer(world.Account).BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, unmatchedOnly: true);
        var search = await CreateSearch(world.Context).SearchAsync("Hidden lecture", 20, default);

        Assert.Equal([UnmatchedFilm], filter.Catalog.Select(card => card.Title).ToArray());
        Assert.Empty(search.Sections);
    }

    // TV --------------------------------------------------------------------------------------------------------

    private (Guid Show, Guid IdentifiedEpisode, Guid UnmatchedEpisode) SeedShow(string name, bool withIdentifiedEpisode, bool unmatchedKept)
    {
        var show = Guid.NewGuid();
        var identified = Guid.NewGuid();
        var unmatched = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@show, 'TV', 'parent', 'accepted');" +
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@show, 'title', @name, CURRENT_TIMESTAMP);",
            new { show, name });
        void Episode(Guid work, string title, int number)
        {
            var edition = Guid.NewGuid();
            var asset = Guid.NewGuid();
            connection.Execute(
                """
                INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal, curator_state) VALUES (@work, 'TV', 'child', @show, @number, 'accepted');
                INSERT INTO editions (id, work_id) VALUES (@edition, @work);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
                VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP, @library);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@asset, 'title', @title, CURRENT_TIMESTAMP), (@asset, 'season_number', '1', CURRENT_TIMESTAMP), (@asset, 'episode_number', @numberText, CURRENT_TIMESTAMP);
                """,
                new { work, show, number, numberText = number.ToString(), edition, asset, title, hash = asset.ToString("N"), path = $"C:/library/{asset:N}.mkv", library = ShowsLibrary.ToString("D") });
            if (work == unmatched)
            {
                var unmatchedRepository = new UnmatchedAssetRepository(_database);
                var now = DateTimeOffset.UtcNow;
                unmatchedRepository.MarkUnmatchedAsync(asset, ShowsLibrary.ToString("D"), UnmatchedReasons.NoMatch, now, now.AddDays(7)).GetAwaiter().GetResult();
                if (unmatchedKept)
                {
                    unmatchedRepository.KeepAsIsAsync(asset, now).GetAwaiter().GetResult();
                }
            }
        }

        if (withIdentifiedEpisode)
        {
            Episode(identified, "Pilot", 1);
        }

        Episode(unmatched, "Mystery episode", 2);
        return (show, identified, unmatched);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnmatchedEpisode_IsHidden_WhileItsShowStaysBecauseOfItsIdentifiedEpisodes(bool kept)
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>(), administrator: true);
        var show = SeedShow("The Show", withIdentifiedEpisode: true, unmatchedKept: kept);
        var service = CreateService(HumanContext(account, ProfileForLookups), RealReaders());

        var rows = await service.LoadWorksAsync(default);
        var ids = await service.LoadVisibleWorkIdsAsync(default);

        var episode = Assert.Single(rows);
        Assert.Equal(show.IdentifiedEpisode, episode.WorkId);
        Assert.Equal(show.Show, episode.RootWorkId);
        Assert.Equal([show.IdentifiedEpisode], ids.ToArray());
        // The Unmatched filter still has the hidden episode, and only it.
        var filtered = await BrowseNarrowing.VisibleWorkIdsAsync(service, new UnmatchedAssetRepository(_database), null, true, default);
        Assert.Equal([show.UnmatchedEpisode], filtered.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AShowWithOnlyUnmatchedEpisodes_IsHiddenFromTheLanes_AndListedUnderTheUnmatchedFilter(bool kept)
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>(), administrator: true);
        var show = SeedShow("Lost Show", withIdentifiedEpisode: false, unmatchedKept: kept);
        var service = CreateService(HumanContext(account, ProfileForLookups), RealReaders());

        Assert.Empty(await service.LoadWorksAsync(default));
        Assert.Empty(await service.LoadVisibleWorkIdsAsync(default));
        var inFilter = await service.LoadWorksIncludingUnmatchedAsync(default);
        Assert.Equal([show.UnmatchedEpisode], inFilter.Select(row => row.WorkId).ToArray());
    }

    // Search -----------------------------------------------------------------------------------------------------

    private UniversalSearchReadService CreateSearch(DefaultHttpContext context)
    {
        var display = CreateService(context, RealReaders());
        return new UniversalSearchReadService(
            _database,
            display,
            new HttpContextAccessor { HttpContext = context },
            Authorities(),
            SpoilerTestKit.Create(display, _database),
            new ProfileRepository(_database),
            new MediaEngine.Storage.Backup.WaitingPersonalStateRepository(_database),
            new UnmatchedAssetRepository(_database));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Search_ReturnsAnUnmatchedFilmInItsOwnUnmatchedGroup_AndNotInTheNormalResults(bool kept)
    {
        var world = await SeedUnmatchedFilmWorldAsync(administrator: false, kept);

        var response = await CreateSearch(world.Context).SearchAsync("film", 20, default);

        var group = Assert.Single(response.Sections, section => section.Key == "unmatched");
        Assert.Equal("Unmatched", group.Title);
        var found = Assert.Single(group.Results);
        Assert.Equal(world.Unmatched.WorkId, found.Id);
        Assert.Equal(UnmatchedFilm, found.Title);
        var normal = response.Sections.Where(section => section.Key != "unmatched").SelectMany(section => section.Results).ToList();
        Assert.Contains(normal, result => result.Title == IdentifiedFilm);
        Assert.DoesNotContain(normal, result => result.Id == world.Unmatched.WorkId);
        Assert.NotEqual(world.Unmatched.WorkId, response.TopResult?.Id);
        Assert.Equal(normal.Count, response.TotalCount);
    }

    [Fact]
    public async Task Search_NeverReturnsAnUnmatchedFilm_ToAProfileThatHasItsLibraryHidden()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { MoviesLibrary });
        var film = await InsertOwnedWorkWithIdAsync(MoviesLibrary, UnmatchedFilm, "Movie");
        await MarkUnmatchedAsync(film.AssetId, MoviesLibrary, keep: false);

        var response = await CreateSearch(HumanContext(account, ProfileForLookups)).SearchAsync("Unmatched film", 20, default);

        Assert.Empty(response.Sections);
        Assert.Equal(0, response.TotalCount);
    }

    [Fact]
    public async Task Search_FindsAnUnmatchedFileByAnEmbeddedTagItAlreadyHas()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>(), administrator: true);
        var song = await InsertOwnedWorkWithIdAsync(MusicLibrary, "track01", "Music");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@asset, 'artist', 0, 'The Embedded Band');",
                new { asset = song.AssetId });
        }

        await MarkUnmatchedAsync(song.AssetId, MusicLibrary, keep: false);

        var response = await CreateSearch(HumanContext(account, ProfileForLookups)).SearchAsync("Embedded Band", 20, default);

        var group = Assert.Single(response.Sections);
        Assert.Equal("unmatched", group.Key);
        Assert.Equal("track01", Assert.Single(group.Results).Title);
    }

    // Personal state follows identification ----------------------------------------------------------------------

    [Fact]
    public async Task AnUnmatchedFilmThatIsLaterMatched_JoinsTheLanesWithItsProgressReactionSavedItemAndTagsIntact()
    {
        var world = await SeedUnmatchedFilmWorldAsync(administrator: false, kept: false);
        var composer = CreateGridComposer(world.Context);
        Assert.DoesNotContain(UnmatchedFilm, CardTitles(await composer.BuildHomeAsync(true, ProfileForLookups)));

        // The identity job leaving the no-match state is what removes the Unmatched row.
        Assert.True(await new UnmatchedAssetRepository(_database).ClearAsync(world.Unmatched.AssetId));

        // The next request: a read service keeps what it learned about Unmatched files for the one request it serves.
        composer = CreateGridComposer(world.Context);
        var home = await composer.BuildHomeAsync(true, ProfileForLookups);
        var movies = await composer.BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, true, ProfileForLookups);
        var continueWatching = await composer.BuildContinueAsync("watch", 20, true, default, null, ProfileForLookups);
        var saved = await composer.BuildGridAsync("personal", "my-list", new DisplayGridQuery(), ProfileForLookups);
        var liked = await composer.BuildGridAsync("personal", "liked", new DisplayGridQuery(), ProfileForLookups);
        Assert.Contains(UnmatchedFilm, CardTitles(home));
        Assert.Equal(new[] { IdentifiedFilm, UnmatchedFilm }.Order(), CardTitles(movies).Order());
        Assert.Contains(UnmatchedFilm, CardTitles(continueWatching));
        Assert.Contains(UnmatchedFilm, GridTitles(saved));
        Assert.Contains(UnmatchedFilm, GridTitles(liked));
        var progress = movies.Catalog.Single(card => card.Title == UnmatchedFilm).Progress;
        Assert.Equal(40, progress!.Percent);
        using var connection = _database.CreateConnection();
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM profile_work_preferences WHERE work_id = @work AND local_tags_json = '[\"tag\"]';", new { work = world.Unmatched.WorkId }));
    }

    [Fact]
    public async Task AnUnmatchedAudiobookThatIsLaterMatchedOntoAnExistingTitle_KeepsEveryKindOfPersonalStateOnTheSurvivor()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>(), administrator: true);
        var survivor = await InsertOwnedWorkWithIdAsync(BooksLibrary, "The Book", "Audiobooks");
        var duplicate = await InsertOwnedWorkWithIdAsync(BooksLibrary, "the book.m4b", "Audiobooks");
        await MarkUnmatchedAsync(duplicate.AssetId, BooksLibrary, keep: false);
        var bookmark = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                UPDATE works SET wikidata_qid = 'Q123456' WHERE id IN (@survivor, @duplicate);
                INSERT INTO user_states(user_id, asset_id, progress_pct, last_accessed) VALUES (@profile, @duplicateAsset, 55, CURRENT_TIMESTAMP);
                INSERT INTO audiobook_bookmarks (id, profile_id, work_id, asset_id, position_seconds, created_at)
                VALUES (@bookmark, @profile, @duplicate, @duplicateAsset, 90, CURRENT_TIMESTAMP);
                INSERT INTO audiobook_listen_history (id, profile_id, work_id, asset_id, title, position_seconds, device_id, client, started_at, ended_at)
                VALUES (@history, @profile, @duplicate, @duplicateAsset, 'Heard', 90, 'd', 'web', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES (@profile, 'Audiobook', @duplicate, CURRENT_TIMESTAMP);
                INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@profile, 'Audiobook', @duplicate, 'Love', CURRENT_TIMESTAMP);
                INSERT INTO profile_work_preferences (profile_id, work_id, local_tags_json, revision, updated_at) VALUES (@profile, @duplicate, '["car"]', 1, CURRENT_TIMESTAMP);
                """,
                new
                {
                    profile = ProfileForLookups,
                    survivor = survivor.WorkId,
                    duplicate = duplicate.WorkId,
                    duplicateAsset = duplicate.AssetId,
                    bookmark,
                    history = Guid.NewGuid(),
                });
        }

        // The lookup matches the file: the Engine merges the two works for the same title, then the Unmatched row goes.
        Assert.Equal(1, await new WorkIdentityReconciliationService(_database).MergeDuplicateReadWorksByQidAsync());
        await new UnmatchedAssetRepository(_database).ClearAsync(duplicate.AssetId);

        using var check = _database.CreateConnection();
        var target = check.ExecuteScalar<Guid>("SELECT work_id FROM editions WHERE id = (SELECT edition_id FROM media_assets WHERE id = @asset);", new { asset = duplicate.AssetId });
        Assert.Equal(1, check.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id IN (@survivor, @duplicate);", new { survivor = survivor.WorkId, duplicate = duplicate.WorkId }));
        Assert.Equal(55, check.ExecuteScalar<int>("SELECT progress_pct FROM user_states WHERE asset_id = @asset;", new { asset = duplicate.AssetId }));
        Assert.Equal(target, check.ExecuteScalar<Guid>("SELECT work_id FROM audiobook_bookmarks WHERE id = @bookmark;", new { bookmark }));
        Assert.Equal(target, check.ExecuteScalar<Guid>("SELECT work_id FROM audiobook_listen_history;"));
        Assert.Equal(target, check.ExecuteScalar<Guid>("SELECT entity_id FROM profile_saved_items WHERE profile_id = @profile;", new { profile = ProfileForLookups }));
        Assert.Equal(target, check.ExecuteScalar<Guid>("SELECT entity_id FROM profile_reactions WHERE profile_id = @profile;", new { profile = ProfileForLookups }));
        Assert.Equal(target, check.ExecuteScalar<Guid>("SELECT work_id FROM profile_work_preferences WHERE profile_id = @profile;", new { profile = ProfileForLookups }));
    }

    [Fact]
    public async Task TheSettingsUnmatchedCount_StillLeavesKeptFilesOut_WhileTheFilterListsThem()
    {
        var world = await SeedUnmatchedFilmWorldAsync(administrator: true, kept: true);
        var open = await InsertOwnedWorkWithIdAsync(MoviesLibrary, "Open film", "Movie");
        await MarkUnmatchedAsync(open.AssetId, MoviesLibrary, keep: false);

        var counts = await new UnmatchedAssetRepository(_database).CountByLibraryAsync();
        var filter = await BrowseComposer(world.Account).BuildBrowseAsync("watch", "Movie", "all", null, 0, 48, unmatchedOnly: true);

        Assert.Equal(1, counts[MoviesLibrary.ToString("D")]);
        Assert.Equal(2, filter.TotalCount);
    }

    // Personal state, playback and the detail page -------------------------------------------------------------------

    // A route table over the real authorized display projection, called the way a request would be, without the middleware.
    private async Task<(int Status, string Body)> CallAsync(
        Action<IEndpointRouteBuilder> map,
        Action<IServiceCollection> services,
        string endpointName,
        DefaultHttpContext user,
        string method,
        object? routeValues = null,
        string query = "")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(Authorities());
        services(builder.Services);
        var app = builder.Build();
        map(app);
        _routeApps.Add(app);
        var route = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == endpointName);
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { User = user.User, RequestServices = scope.ServiceProvider };
        context.Request.Method = method;
        context.Request.QueryString = new QueryString(query);
        if (routeValues is not null)
        {
            foreach (var property in routeValues.GetType().GetProperties())
            {
                context.Request.RouteValues[property.Name] = property.GetValue(routeValues)?.ToString();
            }
        }

        context.Response.Body = new MemoryStream();
        await route.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    private void ProfileStateServices(IServiceCollection services, DefaultHttpContext context)
    {
        var display = CreateService(context, RealReaders());
        var activity = new MediaEngine.Storage.PersonalActivityRepository(_database);
        services.AddSingleton<IDisplayProjectionReadService>(display);
        services.AddSingleton<MediaEngine.Domain.Contracts.IProfileStateRepository>(new ProfileStateRepository(_database));
        services.AddSingleton(new ProfileStateAccessService(display, CreateGridCatalog(), new ProfileRepository(_database)));
        services.AddSingleton(new MediaEngine.Api.Services.Activity.PersonalActivityRecorder(
            activity,
            new VaultLibraryRepository(_database),
            new MediaEngine.Api.Services.Activity.PersonalActivityArtworkService(
                new WorkRepository(_database), new EntityAssetRepository(_database), activity,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaEngine.Api.Services.Activity.PersonalActivityArtworkService>.Instance),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaEngine.Api.Services.Activity.PersonalActivityRecorder>.Instance));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProfileThatOpensAnUnmatchedFilmFromTheFilter_CanSaveAndLikeIt_WhileMyListAndLikedKeepLeavingItOut(bool kept)
    {
        var world = await SeedUnmatchedFilmWorldAsync(administrator: false, kept);
        // The seed already saved and liked both films for the lists; start the Unmatched film clean so the writes are the ones under test.
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM profile_saved_items WHERE entity_id = @film; DELETE FROM profile_reactions WHERE entity_id = @film;", new { film = world.Unmatched.WorkId });
        }

        void Services(IServiceCollection services) => ProfileStateServices(services, world.Context);
        void Map(IEndpointRouteBuilder app) => app.MapProfileStateEndpoints();
        var film = new { entityKind = "Movie", entityId = world.Unmatched.WorkId };

        var save = await CallAsync(Map, Services, "SaveProfileItem", world.Context, "PUT", film);
        var like = await CallAsync(Map, Services, "SetProfileReaction", world.Context, "PUT", new { film.entityKind, film.entityId, reaction = "Like" });
        var savedState = await CallAsync(Map, Services, "GetSavedProfileItem", world.Context, "GET", film);
        var savedList = await CallAsync(Map, Services, "GetSavedProfileItems", world.Context, "GET");
        var reactionList = await CallAsync(Map, Services, "GetProfileReactions", world.Context, "GET");

        Assert.Equal(StatusCodes.Status200OK, save.Status);
        Assert.Equal(StatusCodes.Status200OK, like.Status);
        Assert.Equal(StatusCodes.Status200OK, savedState.Status);
        Assert.Contains(world.Unmatched.WorkId.ToString("D"), savedState.Body, StringComparison.OrdinalIgnoreCase);
        // The rows are written, but the lists still leave an Unmatched film out (D64).
        using var verify = _database.CreateConnection();
        Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM profile_saved_items WHERE entity_id = @film;", new { film = world.Unmatched.WorkId }));
        Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM profile_reactions WHERE entity_id = @film;", new { film = world.Unmatched.WorkId }));
        Assert.DoesNotContain(world.Unmatched.WorkId.ToString("D"), savedList.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(world.Unmatched.WorkId.ToString("D"), reactionList.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(world.Identified.WorkId.ToString("D"), savedList.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AProfileThatHasTheLibraryHidden_CannotSaveOrLikeItsUnmatchedFilm()
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid> { MoviesLibrary });
        var film = await InsertOwnedWorkWithIdAsync(MoviesLibrary, UnmatchedFilm, "Movie");
        await MarkUnmatchedAsync(film.AssetId, MoviesLibrary, keep: false);
        var context = HumanContext(account, ProfileForLookups);

        var save = await CallAsync(
            app => app.MapProfileStateEndpoints(), services => ProfileStateServices(services, context),
            "SaveProfileItem", context, "PUT", new { entityKind = "Movie", entityId = film.WorkId });

        Assert.Equal(StatusCodes.Status404NotFound, save.Status);
        using var verify = _database.CreateConnection();
        Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM profile_saved_items WHERE entity_id = @film;", new { film = film.WorkId }));
    }

    // Up Next and the companion are playing, not browsing: they follow Unmatched episodes too ------------------------

    private async Task<(Guid Show, DefaultHttpContext Context, Guid FirstAsset, Guid SecondAsset, Guid ThirdAsset)> SeedThreeEpisodeShowAsync(int unmatchedEpisode)
    {
        var account = Guid.NewGuid();
        await CreateHumanAsync(account, EveryFeature, new HashSet<Guid>());
        var show = Guid.NewGuid();
        var assets = new List<Guid>();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@show, 'TV', 'parent', 'accepted');" +
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@show, 'title', 'Up Next Show', CURRENT_TIMESTAMP);",
                new { show });
            for (var number = 1; number <= 3; number++)
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
                    VALUES (@asset, 'title', @title, CURRENT_TIMESTAMP), (@asset, 'season_number', '1', CURRENT_TIMESTAMP), (@asset, 'episode_number', @numberText, CURRENT_TIMESTAMP);
                    """,
                    new { work, show, number, numberText = number.ToString(), edition, asset, title = $"Episode {number}", hash = asset.ToString("N"), path = $"C:/library/{asset:N}.mkv", library = ShowsLibrary.ToString("D") });
                if (number == unmatchedEpisode)
                {
                    var now = DateTimeOffset.UtcNow;
                    new UnmatchedAssetRepository(_database).MarkUnmatchedAsync(asset, ShowsLibrary.ToString("D"), UnmatchedReasons.NoMatch, now, now.AddDays(7)).GetAwaiter().GetResult();
                }
            }
        }

        return (show, HumanContext(account, ProfileForLookups), assets[0], assets[1], assets[2]);
    }

    private async Task<string> NextEpisodesBodyAsync(Guid show, DefaultHttpContext context, Guid after)
    {
        var result = await CallAsync(
            app => app.MapDetailEndpoints(),
            services =>
            {
                services.AddSingleton<IDisplayProjectionReadService>(CreateService(context, RealReaders()));
                foreach (var inferredOnly in new[] { typeof(MediaEngine.Api.Services.Details.DetailComposerService), typeof(MediaEngine.Api.Services.Details.ContributorReadiness), typeof(MediaEngine.Api.Services.Details.DetailUnmatchedStateService), typeof(MediaEngine.Domain.Contracts.ICanonicalValueRepository) })
                {
                    services.AddSingleton(inferredOnly, _ => null!);
                }
            },
            "GetShowNextEpisodes", context, "GET", new { id = show }, $"?after={after:D}");
        Assert.Equal(StatusCodes.Status200OK, result.Status);
        return result.Body;
    }

    [Fact]
    public async Task UpNext_AfterAnUnmatchedEpisode_StillWorks()
    {
        var world = await SeedThreeEpisodeShowAsync(unmatchedEpisode: 1);

        var body = await NextEpisodesBodyAsync(world.Show, world.Context, world.FirstAsset);

        Assert.Contains(world.SecondAsset.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(world.ThirdAsset.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpNext_AfterAnIdentifiedEpisode_DoesNotSkipItsUnmatchedSiblings()
    {
        var world = await SeedThreeEpisodeShowAsync(unmatchedEpisode: 2);

        var body = await NextEpisodesBodyAsync(world.Show, world.Context, world.FirstAsset);

        Assert.Contains(world.SecondAsset.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpNext_StillAppliesTheLibrariesHiddenFromTheProfile()
    {
        var world = await SeedThreeEpisodeShowAsync(unmatchedEpisode: 2);
        var stranger = Guid.NewGuid();
        await CreateHumanAsync(stranger, EveryFeature, new HashSet<Guid> { ShowsLibrary });

        var result = await CallAsync(
            app => app.MapDetailEndpoints(),
            services =>
            {
                services.AddSingleton<IDisplayProjectionReadService>(CreateService(HumanContext(stranger, ProfileForLookups), RealReaders()));
                foreach (var inferredOnly in new[] { typeof(MediaEngine.Api.Services.Details.DetailComposerService), typeof(MediaEngine.Api.Services.Details.ContributorReadiness), typeof(MediaEngine.Api.Services.Details.DetailUnmatchedStateService), typeof(MediaEngine.Domain.Contracts.ICanonicalValueRepository) })
                {
                    services.AddSingleton(inferredOnly, _ => null!);
                }
            },
            "GetShowNextEpisodes", HumanContext(stranger, ProfileForLookups), "GET", new { id = world.Show }, $"?after={world.FirstAsset:D}");

        Assert.Equal(StatusCodes.Status404NotFound, result.Status);
    }

    [Fact]
    public async Task AMatchedShowsDetailRead_ListsItsUnmatchedEpisodes_ByDesign()
    {
        var world = await SeedThreeEpisodeShowAsync(unmatchedEpisode: 2);
        var service = CreateService(world.Context, RealReaders());

        var detail = await service.LoadDetailWorksAsync(world.Show, default);
        var lanes = await service.LoadWorksAsync(default);

        // The show's own page keeps the Unmatched episode (it plays from there); the lanes do not.
        Assert.Equal(3, detail.Count);
        Assert.Equal(2, lanes.Count);
        Assert.Contains(world.SecondAsset, detail.Select(row => row.AssetId));
        Assert.DoesNotContain(world.SecondAsset, lanes.Select(row => row.AssetId));
    }

    [Fact]
    public async Task OneRequest_LooksTheUnmatchedFilesOfTheSameWorksUpOnce()
    {
        var world = await SeedUnmatchedFilmWorldAsync(administrator: true, kept: false);
        var counting = new CountingDatabase(_database);
        var service = new AuthorizedDisplayProjectionReadService(
            RealReaders(),
            new HttpContextAccessor { HttpContext = world.Context },
            Authorities(),
            _accounts,
            new AuthorizationEvaluator(
                new AccountAccessDecisionService(_accounts, new GrantAdminUnlockService(_accounts, new StubProfilePins(), TimeProvider.System), new LibraryVisibilityRule(_accounts, new VaultLibraryRepository(_database))),
                _applications,
                new PermissionRegistry(),
                new AuthorizationAuditWriter(_accounts),
                new HttpContextAccessor { HttpContext = world.Context }),
            counting);

        await service.LoadWorksAsync(default);
        var afterFirst = counting.Opened;
        await service.LoadWorksAsync(default);
        await service.LoadWorkLibraryRowsAsync(default);

        // The second and third reads open no connection to ask about Unmatched files again.
        Assert.True(counting.Opened - afterFirst <= 0, $"{counting.Opened - afterFirst} extra connections");
    }
}
