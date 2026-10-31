using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Api.Tests;

// The direct id lookup behind My Library (POST /api/v1/display/items): the same authorized projection the lane browse pages read,
// run against a real temp SQLite store.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private static readonly Guid ProfileForLookups = MediaEngine.Domain.Aggregates.Profile.SeedProfileId;

    private DisplayComposerService CreateItemsComposer(DefaultHttpContext context)
    {
        // A fresh composer (and raw projection cache) per call, so rows inserted by a test are always read.
        var authorized = CreateService(context, new LiveProjection(new DisplayWorkProjectionReader(_database), new DisplayJourneyProjectionReader(_database)));
        var cards = new DisplayCardBuilder();
        return new DisplayComposerService(authorized, cards, new DisplayShelfBuilder(cards));
    }

    private Task<(DefaultHttpContext Context, Guid Library)> SignInForLookupsAsync(params AccountFeatureId[] features) =>
        SignInForLookupsAsync(new HashSet<Guid>(), features);

    // The libraries in <paramref name="hiddenLibraries"/> are hidden from the person; every other library is visible through the areas.
    private async Task<(DefaultHttpContext Context, Guid Library)> SignInForLookupsAsync(IReadOnlySet<Guid> hiddenLibraries, params AccountFeatureId[] features)
    {
        var account = Guid.NewGuid();
        var library = Guid.NewGuid();
        await CreateHumanAsync(account, features.ToHashSet(), hiddenLibraries);
        return (HumanContext(account, ProfileForLookups), library);
    }

    // The real work and progress readers over the temp store, with nothing cached, standing in for the shared raw projection.
    private sealed class LiveProjection(DisplayWorkProjectionReader works, DisplayJourneyProjectionReader journey) : IRawDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => works.LoadAsync(ct);
        public Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct) => works.LoadAsync(ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => journey.LoadAsync(profileId, lane, ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => journey.LoadAsync(profileId, lane, ct, includeCompleted: true);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }

    private static string[] Titles(DisplayPageDto page) => page.Catalog.Select(card => card.Title).ToArray();

    [Fact]
    public async Task ItemLookup_ReturnsTheLaneCardForEachFoundIdInTheOrderAsked()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch, AccountFeatureId.Read);
        var film = await InsertOwnedWorkWithIdAsync(library, "Arrival", "Movie");
        var book = await InsertOwnedWorkWithIdAsync(library, "Dune", "Book");

        var page = await CreateItemsComposer(context).BuildItemsAsync([book.WorkId, film.WorkId], ProfileForLookups);

        Assert.Equal(["Dune", "Arrival"], Titles(page));
        Assert.Equal([book.WorkId, film.WorkId], page.Catalog.Select(card => card.Id));
        // Same card the lane builds for that Work (the added-at stamp is read again from the store, so it is left out of the comparison).
        var rows = await new DisplayWorkProjectionReader(_database).LoadAsync(default);
        var expected = new DisplayCardBuilder().FromWork(rows.Single(row => row.WorkId == film.WorkId), "watch", null);
        var options = new JsonSerializerOptions();
        Assert.Equal(
            JsonSerializer.Serialize(expected with { SortTimestamp = default }, options),
            JsonSerializer.Serialize(page.Catalog.Single(card => card.Id == film.WorkId) with { SortTimestamp = default }, options));
    }

    [Fact]
    public async Task ItemLookup_LeavesOutMissingEmptyAndRepeatedIds()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var film = await InsertOwnedWorkWithIdAsync(library, "Arrival", "Movie");

        var page = await CreateItemsComposer(context).BuildItemsAsync(
            [Guid.NewGuid(), Guid.Empty, film.WorkId, film.WorkId, Guid.NewGuid()], ProfileForLookups);

        Assert.Equal(["Arrival"], Titles(page));
        Assert.Empty((await CreateItemsComposer(context).BuildItemsAsync([], ProfileForLookups)).Catalog);
    }

    [Fact]
    public async Task ItemLookup_AppliesTheProfilesContentLimitLikeABrowsePage()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var family = await InsertOwnedWorkWithIdAsync(library, "Family film", "Movie");
        var adult = await InsertOwnedWorkWithIdAsync(library, "Adult film", "Movie");
        var unrated = await InsertOwnedWorkWithIdAsync(library, "Home video", "Movie");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@family, 'content_rating', 'PG', CURRENT_TIMESTAMP),
                       (@adult, 'content_rating', 'R', CURRENT_TIMESTAMP);
                """, new { family = family.WorkId, adult = adult.WorkId });
        }
        Guid[] all = [family.WorkId, adult.WorkId, unrated.WorkId];

        SetContentLimit("PG");
        var limited = await CreateItemsComposer(context).BuildItemsAsync(all, ProfileForLookups);
        Assert.Equal(["Family film"], Titles(limited));
        // The lane browse page for the same profile shows exactly the same set.
        var browse = await CreateItemsComposer(context).BuildBrowseAsync("watch", null, "all", null, 0, 48, profileId: ProfileForLookups);
        Assert.Equal(Titles(limited), Titles(browse));

        SetContentLimit("R", allowUnrated: true);
        var raised = await CreateItemsComposer(context).BuildItemsAsync(all, ProfileForLookups);
        Assert.Equal(["Adult film", "Family film", "Home video"], Titles(raised).Order());
    }

    [Fact]
    public async Task ItemLookup_NeverTellsAProfileWithoutDocumentsThatADocumentExists()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var film = await InsertOwnedWorkWithIdAsync(library, "Arrival", "Movie");
        var document = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            // A Shared item must sit in a household's Shared library.
            connection.Execute("""
                INSERT OR IGNORE INTO households (id, name, created_at)
                SELECT @household, 'Test household', '2026-10-13T00:00:00Z' WHERE NOT EXISTS (SELECT 1 FROM households);
                INSERT OR IGNORE INTO view_shared_library (household_id, library_id, created_at, updated_at)
                SELECT id, @sharedLibrary, '2026-10-13T00:00:00Z', '2026-10-13T00:00:00Z' FROM households ORDER BY rowid LIMIT 1;
                INSERT INTO local_items (id, scope_kind, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
                VALUES (@document, 'shared', (SELECT library_id FROM view_shared_library LIMIT 1), 'document', 'tax-return.pdf', 'application/pdf', '2026-10-13T00:00:00Z', '2026-10-13T00:00:00Z');
                INSERT INTO profile_view_policies (profile_id, documents_enabled, updated_at) VALUES (@profile, 0, '2026-10-13T00:00:00Z')
                ON CONFLICT(profile_id) DO UPDATE SET documents_enabled = 0;
                """, new { document, household = Guid.NewGuid(), sharedLibrary = Guid.NewGuid(), profile = ProfileForLookups });
        }

        var withDocument = await CreateItemsComposer(context).BuildItemsAsync([document, film.WorkId], ProfileForLookups);
        var withUnknown = await CreateItemsComposer(context).BuildItemsAsync([Guid.NewGuid(), film.WorkId], ProfileForLookups);

        // A document id answers exactly like an id that does not exist: no card, and nothing else in the reply differs.
        Assert.Equal(["Arrival"], Titles(withDocument));
        Assert.Equal(Titles(withUnknown), Titles(withDocument));
        Assert.Equal((withUnknown.Key, withUnknown.Title, withUnknown.Shelves.Count, withUnknown.Catalog.Count),
            (withDocument.Key, withDocument.Title, withDocument.Shelves.Count, withDocument.Catalog.Count));
        Assert.Empty((await CreateItemsComposer(context).BuildItemsAsync([document], ProfileForLookups)).Catalog);

        // Turning Documents on does not make a document a catalogue card either: the lookup only ever reads catalogue Works.
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profile_view_policies SET documents_enabled = 1 WHERE profile_id = @profile", new { profile = ProfileForLookups });
        }
        Assert.Empty((await CreateItemsComposer(context).BuildItemsAsync([document], ProfileForLookups)).Catalog);
    }

    [Fact]
    public async Task ItemLookup_AppliesTheHouseholdsHiddenLibrariesAcrossMixedLanes()
    {
        // Every profile reaches Watch, Listen and Read; one of the two libraries is turned off for the household.
        var otherLibrary = Guid.NewGuid();
        var (context, library) = await SignInForLookupsAsync(new HashSet<Guid> { otherLibrary }, AccountFeatureId.Watch, AccountFeatureId.Read);
        var film = await InsertOwnedWorkWithIdAsync(library, "Arrival", "Movie");
        var book = await InsertOwnedWorkWithIdAsync(library, "Dune", "Book");
        var song = await InsertOwnedWorkWithIdAsync(library, "Roads", "Music");
        var filmElsewhere = await InsertOwnedWorkWithIdAsync(otherLibrary, "Elsewhere", "Movie");

        var page = await CreateItemsComposer(context).BuildItemsAsync(
            [film.WorkId, book.WorkId, song.WorkId, filmElsewhere.WorkId], ProfileForLookups);

        Assert.Equal(["Arrival", "Dune", "Roads"], Titles(page));
        // The same hidden libraries leave the same items on the lane pages.
        var watch = await CreateItemsComposer(context).BuildBrowseAsync("watch", null, "all", null, 0, 48, profileId: ProfileForLookups);
        var read = await CreateItemsComposer(context).BuildBrowseAsync("read", null, "all", null, 0, 48, profileId: ProfileForLookups);
        var listen = await CreateItemsComposer(context).BuildBrowseAsync("listen", null, "all", null, 0, 48, profileId: ProfileForLookups);
        Assert.Equal(["Arrival"], Titles(watch));
        Assert.Equal(["Dune"], Titles(read));
        Assert.Equal(["Roads"], Titles(listen));
    }

    [Fact]
    public async Task ItemLookup_AnswersNothingForACallerWhoseAccountIsNotValid()
    {
        var (_, library) = await SignInForLookupsAsync(AccountFeatureId.Watch);
        var film = await InsertOwnedWorkWithIdAsync(library, "Arrival", "Movie");
        var stranger = HumanContext(Guid.NewGuid(), ProfileForLookups);

        var page = await CreateItemsComposer(stranger).BuildItemsAsync([film.WorkId], ProfileForLookups);

        Assert.Empty(page.Catalog);
    }

    [Fact]
    public async Task ItemLookup_FindsAShowAndAnAlbumByTheirRootIdAndAnEpisodeByItsOwnId()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Watch, AccountFeatureId.Listen);
        var show = Guid.NewGuid();
        var album = Guid.NewGuid();
        var episode = Guid.Empty;
        using (var connection = _database.CreateConnection())
        {
            void Parent(Guid id, string mediaType, string title) => connection.Execute(
                "INSERT INTO works(id,media_type,work_kind,curator_state) VALUES(@id,@mediaType,'parent','accepted');INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES(@id,'title',@title,CURRENT_TIMESTAMP);",
                new { id, mediaType, title });
            Guid Child(Guid parent, string mediaType, string title, string? season, string? number, string? albumName)
            {
                var work = Guid.NewGuid(); var edition = Guid.NewGuid(); var asset = Guid.NewGuid();
                connection.Execute("""
                    INSERT INTO works(id,media_type,work_kind,parent_work_id,curator_state) VALUES(@work,@mediaType,'child',@parent,'accepted');
                    INSERT INTO editions(id,work_id) VALUES(@edition,@work);
                    INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,presented_at,library_id) VALUES(@asset,@edition,@hash,@path,CURRENT_TIMESTAMP,@library);
                    INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES(@asset,'title',@title,CURRENT_TIMESTAMP);
                    """, new { work, mediaType, parent, edition, asset, hash = asset.ToString("N"), path = $"C:/library/{asset:N}.{(mediaType == "Music" ? "mp3" : "mkv")}", library = library.ToString("D"), title });
                if (season is not null)
                {
                    connection.Execute("INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES(@asset,'season_number',@season,CURRENT_TIMESTAMP),(@asset,'episode_number',@number,CURRENT_TIMESTAMP),(@work,'show_name','Long show',CURRENT_TIMESTAMP);", new { asset, work, season, number });
                }
                if (albumName is not null)
                {
                    connection.Execute("INSERT INTO canonical_values(entity_id,key,value,last_scored_at) VALUES(@asset,'album',@albumName,CURRENT_TIMESTAMP),(@asset,'artist','Artist',CURRENT_TIMESTAMP);", new { asset, albumName });
                }
                return work;
            }
            Parent(show, "TV", "Long show");
            Parent(album, "Music", "Road album");
            episode = Child(show, "TV", "Pilot", "1", "1", null);
            Child(show, "TV", "Second", "1", "2", null);
            Child(album, "Music", "Track one", null, null, "Road album");
            Child(album, "Music", "Track two", null, null, "Road album");
        }

        var page = await CreateItemsComposer(context).BuildItemsAsync([show, album, episode], ProfileForLookups);

        Assert.Equal(3, page.Catalog.Count);
        Assert.Equal(show, page.Catalog[0].Id);
        Assert.Equal(DisplaySubjectKind.TvShow, page.Catalog[0].Subject);
        Assert.Equal(album, page.Catalog[1].Id);
        Assert.Equal(DisplaySubjectKind.Album, page.Catalog[1].Subject);
        Assert.Equal(episode, page.Catalog[2].Id);
        Assert.Equal(DisplaySubjectKind.TvEpisode, page.Catalog[2].Subject);
    }

    [Fact]
    public async Task ItemLookup_FindsItemsPastTheFirstTwoHundredFiftyAThatALanePageWouldStopAt()
    {
        var (context, library) = await SignInForLookupsAsync(AccountFeatureId.Read);
        var ids = new List<Guid>();
        for (var i = 0; i < 300; i++)
        {
            ids.Add((await InsertOwnedWorkWithIdAsync(library, $"Book {i:000}", "Book")).WorkId);
        }

        // Ask for the last 200: every one of them sits past position 250 in a 300-item library.
        var tail = ids.Skip(100).ToList();
        var page = await CreateItemsComposer(context).BuildItemsAsync(tail, ProfileForLookups);

        Assert.Equal(DisplayItemsRequest.MaxIds, page.Catalog.Count);
        Assert.Equal(tail, page.Catalog.Select(card => card.Id));
    }

    [Fact]
    public async Task ItemLookup_RefusesMoreIdsThanTheCapInsteadOfSilentlyCuttingTheList()
    {
        var (context, _) = await SignInForLookupsAsync(AccountFeatureId.Read);
        var composer = CreateItemsComposer(context);

        Assert.Equal(200, DisplayItemsRequest.MaxIds);
        await composer.BuildItemsAsync(Enumerable.Range(0, DisplayItemsRequest.MaxIds).Select(_ => Guid.NewGuid()).ToList(), ProfileForLookups);
        await Assert.ThrowsAsync<ArgumentException>(() => composer.BuildItemsAsync(
            Enumerable.Range(0, DisplayItemsRequest.MaxIds + 1).Select(_ => Guid.NewGuid()).ToList(), ProfileForLookups));
    }
}
