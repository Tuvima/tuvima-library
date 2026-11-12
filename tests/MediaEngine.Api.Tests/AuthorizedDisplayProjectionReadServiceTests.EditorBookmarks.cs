using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

// The editor's Bookmarks tab (universal editor v5 D37, WP15; global bookmarks v5 D1-D7). Only an administrator reads or changes a title's Global
// bookmarks, the Engine checks it on the read and on the commit, and a Vault title is refused. Global starts empty: the first change marks the
// set as made and flags nothing, so a bookmark is promoted with Add before it can be renamed or moved; Clear Global unflags everything and
// leaves every profile's bookmarks; Change time queues a new picture; and the show's own overview and file are keyed by season and episode
// number. Only movies and TV have a Global set.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private sealed record EditorBookmarkKit(
        EditorBookmarksService Service, PlayerCatalogueScope Scope, SystemActivityRepository Activity, BookmarkSetService Sets, PlayerService Player);

    private EditorBookmarkKit EditorBookmarksFor(Guid account, Guid? session)
    {
        // The shared seed names a film "Movie"; the data store holds "Movies", which is what Global bookmarks (movies and TV only) look for.
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE works SET media_type = 'Movies' WHERE media_type = 'Movie';");
        }

        var context = VaultContext(account, session);
        var services = new ServiceCollection()
            .AddSingleton<IRequestAuthorityResolver>(new RequestAuthorityResolver(_accounts, _applications))
            .AddSingleton(CreateService(context, RealReaders()))
            .BuildServiceProvider();
        context.RequestServices = services;
        var scope = new PlayerCatalogueScope(new HttpContextAccessor { HttpContext = context });
        var closeness = new BookmarkClosenessProvider(() => null);
        var bookmarks = new AudiobookBookmarkRepository(_database);
        var vault = new VaultLibraryRepository(_database);
        var sets = new BookmarkSetService(scope, new BookmarkRecognitionRepository(_database), vault, closeness, frames: FrameService());
        var readerSets = new ReaderBookmarkSetService(scope, new BookmarkRecognitionRepository(_database), vault, closeness);
        var activity = new SystemActivityRepository(_database);
        var pageSets = new PageBookmarkSetService(scope, new BookmarkRecognitionRepository(_database), vault, closeness, new MediaAssetRepository(_database));
        var service = new EditorBookmarksService(
            scope, new EditorBookmarkRepository(_database), closeness, sets, readerSets, pageSets, FrameService(), vault, activity, NullLogger<EditorBookmarksService>.Instance);
        var player = new PlayerService(null!, null!, null!, null!, null!, null!, null!, bookmarks, null!, scope, null!, null!, frames: FrameService(), bookmarkVisibility: BookmarkVisibility());
        return new EditorBookmarkKit(service, scope, activity, sets, player);
    }

    private static EditorBookmarkOp EditorOp(
        string kind, Guid? bookmark = null, string? name = null, double? seconds = null, IReadOnlyList<EditorBookmarkImportItem>? items = null) =>
        new() { Id = Guid.NewGuid().ToString("N"), Kind = kind, BookmarkId = bookmark, Name = name, PositionSeconds = seconds, Items = items ?? [] };

    private static EditorBookmarkChangeDto ChangeOf(params EditorBookmarkOp[] ops) => new() { Ops = ops };

    [Fact]
    public async Task EditorBookmarks_AnAdministratorPromotesAProfilesBookmark_ReviewSeesItAndSaveMakesItTheGlobalSetThePlayerShows()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var ben = AddOtherProfile();
        var opening = await BookmarkOf(ben, shape.Normal.WorkId, shape.Normal.AssetId, 724);
        var elsewhere = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 3000);

        var before = await kit.Service.ReadAsync(shape.Normal.WorkId);
        Assert.Equal(EditorBookmarkStates.Pooled, before.State);
        Assert.Empty(before.Everyone);
        Assert.Equal(2, before.FromProfiles.Count);
        Assert.Equal("Someone else", before.FromProfiles.First(row => row.Id == opening.Id).OwnerName);

        var change = ChangeOf(
            EditorOp(EditorBookmarkOpKinds.Add, elsewhere.Id),
            EditorOp(EditorBookmarkOpKinds.Add, opening.Id),
            EditorOp(EditorBookmarkOpKinds.Remove, elsewhere.Id),
            EditorOp(EditorBookmarkOpKinds.Rename, opening.Id, name: "Opening"));
        Assert.Null(await kit.Service.CheckAsync(shape.Normal.WorkId, change));
        await kit.Service.ApplyAsync(shape.Normal.WorkId, change);

        var after = await kit.Service.ReadAsync(shape.Normal.WorkId);
        Assert.Equal(EditorBookmarkStates.Definitive, after.State);
        var everyone = Assert.Single(after.Everyone);
        Assert.Equal(opening.Id, everyone.Id);
        Assert.Equal("Opening", everyone.Name);
        // The bookmark that left Global stays with its owner, under Mine.
        Assert.Contains(await kit.Player.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId), row => row.Id == elsewhere.Id && !row.IsGlobal);
        Assert.Contains(await kit.Player.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId), row => row.Id == opening.Id && row.IsGlobal);
    }

    [Fact]
    public async Task EditorBookmarks_AddingABookmarkAtTheSameSpotAsOneInGlobal_MergesThem_AndOnlyTheReplacedOnesPictureGoes()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var ben = AddOtherProfile();
        var standing = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 724);
        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(
            EditorOp(EditorBookmarkOpKinds.Add, standing.Id),
            EditorOp(EditorBookmarkOpKinds.Rename, standing.Id, name: "Opening")));
        var sameSpot = await BookmarkOf(ben, shape.Normal.WorkId, shape.Normal.AssetId, 726);
        var untouched = await BookmarkOf(ben, shape.Normal.WorkId, shape.Normal.AssetId, 4000);
        var frames = FrameCache();
        frames.Write(shape.Normal.AssetId, sameSpot.Id, [0xFF, 0xD8, 0xFF, 0xD9]);
        frames.Write(shape.Normal.AssetId, untouched.Id, [0xFF, 0xD8, 0xFF, 0xD9]);
        var unrelated = await BookmarkOf(ben, shape.Normal.WorkId, shape.Normal.AssetId, 5000);
        var other = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 727);
        frames.Write(shape.Normal.AssetId, other.Id, [0xFF, 0xD8, 0xFF, 0xD9]);

        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(EditorOp(EditorBookmarkOpKinds.Add, sameSpot.Id)));

        var read = await kit.Service.ReadAsync(shape.Normal.WorkId);
        Assert.Equal(sameSpot.Id, Assert.Single(read.Everyone).Id);
        // The one at the same spot that was not in Global was replaced and is gone, with its picture; nothing else lost anything.
        Assert.DoesNotContain(read.FromProfiles, row => row.Id == other.Id);
        Assert.False(frames.Exists(shape.Normal.AssetId, other.Id));
        Assert.True(frames.Exists(shape.Normal.AssetId, untouched.Id));
        Assert.Contains(read.FromProfiles, row => row.Id == unrelated.Id);
        Assert.Contains(read.FromProfiles, row => row.Id == standing.Id && row.InEveryone);
    }

    [Fact]
    public async Task EditorBookmarks_ClearingGlobal_EmptiesTheSet_AndLeavesEveryProfilesBookmarks()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var first = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 300);
        var second = await BookmarkOf(AddOtherProfile(), shape.Normal.WorkId, shape.Normal.AssetId, 2000);
        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(
            EditorOp(EditorBookmarkOpKinds.Add, first.Id), EditorOp(EditorBookmarkOpKinds.Add, second.Id)));
        Assert.Equal(2, (await kit.Service.ReadAsync(shape.Normal.WorkId)).Everyone.Count);

        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(EditorOp(EditorBookmarkOpKinds.StartOver)));

        var read = await kit.Service.ReadAsync(shape.Normal.WorkId);
        Assert.Equal(EditorBookmarkStates.Pooled, read.State);
        Assert.Empty(read.Everyone);
        // Nobody's bookmark was deleted by clearing.
        Assert.Equal([first.Id, second.Id], read.FromProfiles.Select(row => row.Id));
        Assert.DoesNotContain(await StoredBookmarksAsync(shape.Normal.WorkId), row => row.IsGlobal);
    }

    [Fact]
    public async Task EditorBookmarks_ChangingTheTime_QueuesAPictureForTheNewMoment_AndTheOldPictureGoesAtOnce()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var bookmark = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 724);
        FrameCache().Write(shape.Normal.AssetId, bookmark.Id, [0xFF, 0xD8, 0xFF, 0xD9]);

        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(
            EditorOp(EditorBookmarkOpKinds.Add, bookmark.Id), EditorOp(EditorBookmarkOpKinds.ChangeTime, bookmark.Id, seconds: 1800)));

        Assert.False(FrameCache().Exists(shape.Normal.AssetId, bookmark.Id));
        var queued = await new MediaOperationRepository(_database).GetByIdempotencyKeyAsync(BookmarkFrameService.KeyFor(bookmark.Id, 1800));
        Assert.NotNull(queued);
        Assert.Equal(1800, Assert.Single((await kit.Service.ReadAsync(shape.Normal.WorkId)).Everyone).PositionSeconds);
    }

    [Fact]
    public async Task EditorBookmarks_UndoingASave_PutsEverythingBackAndQueuesThePicturesAgain()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var bookmark = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 724);

        var undo = await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(
            EditorOp(EditorBookmarkOpKinds.Add, bookmark.Id),
            EditorOp(EditorBookmarkOpKinds.ChangeTime, bookmark.Id, seconds: 1800),
            EditorOp(EditorBookmarkOpKinds.Import, items: [new EditorBookmarkImportItem { PositionSeconds = 60, Name = "Titles" }])));
        await undo(default);

        var read = await kit.Service.ReadAsync(shape.Normal.WorkId);
        Assert.Equal(EditorBookmarkStates.Pooled, read.State);
        Assert.Empty(read.Everyone);
        var only = Assert.Single(read.FromProfiles);
        Assert.Equal(724, only.PositionSeconds);
    }

    [Fact]
    public async Task EditorBookmarks_EachChangeLeavesAHistoryLine_InPlainWords()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var bookmark = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 724);

        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(
            EditorOp(EditorBookmarkOpKinds.Add, bookmark.Id),
            EditorOp(EditorBookmarkOpKinds.Rename, bookmark.Id, name: "Opening"),
            EditorOp(EditorBookmarkOpKinds.ChangeTime, bookmark.Id, seconds: 750),
            EditorOp(EditorBookmarkOpKinds.StartOver)));

        var lines = (await kit.Activity.GetRecentAsync(20)).Where(entry => entry.EntityId == shape.Normal.WorkId).Select(entry => entry.Detail).ToList();
        Assert.Contains(lines, line => line!.Contains("added to Global") && line.Contains("12:04"));
        Assert.Contains(lines, line => line!.Contains("renamed") && line.Contains("12:04"));
        Assert.Contains(lines, line => line!.Contains("moved") && line.Contains("12:30"));
        Assert.Contains(lines, line => line!.Contains("Global bookmarks: cleared"));
    }

    [Fact]
    public async Task EditorBookmarks_ABookmarkThatIsNoLongerThere_IsRefusedByTheCheck_AndNothingIsWritten()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var change = ChangeOf(EditorOp(EditorBookmarkOpKinds.Add, Guid.NewGuid()));

        Assert.Equal("A bookmark you changed is no longer there.", await kit.Service.CheckAsync(shape.Normal.WorkId, change));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.ApplyAsync(shape.Normal.WorkId, change));
        Assert.Equal(EditorBookmarkStates.Pooled, (await kit.Service.ReadAsync(shape.Normal.WorkId)).State);
    }

    [Fact]
    public async Task EditorBookmarks_RenamingAProfilesBookmarkThatWasNeverAddedToGlobal_IsRefusedByTheCheck_AndNothingIsWritten()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var bookmark = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 724);
        var change = ChangeOf(EditorOp(EditorBookmarkOpKinds.Rename, bookmark.Id, name: "Opening"));

        Assert.Equal("A bookmark you renamed is no longer in Global.", await kit.Service.CheckAsync(shape.Normal.WorkId, change));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.ApplyAsync(shape.Normal.WorkId, change));
        Assert.Equal(EditorBookmarkStates.Pooled, (await kit.Service.ReadAsync(shape.Normal.WorkId)).State);
        Assert.Null(Assert.Single(await StoredBookmarksAsync(shape.Normal.WorkId)).Label);
    }

    [Fact]
    public async Task EditorBookmarks_ASaveThroughTheEditSessionHandler_ChecksAndWritesThroughTheService()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var bookmark = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 724);
        var handler = new BookmarksEditSessionChangeHandler(kit.Service);
        var change = new EditSessionChange
        {
            Id = "c1",
            Kind = EditSessionChangeKinds.Bookmarks,
            EntityId = shape.Normal.WorkId,
            Bookmarks = ChangeOf(EditorOp(EditorBookmarkOpKinds.Add, bookmark.Id), EditorOp(EditorBookmarkOpKinds.Rename, bookmark.Id, name: "Opening")),
        };

        Assert.Equal(EditSessionChangeKinds.Bookmarks, handler.Kind);
        Assert.Null(await handler.CheckAsync(change));
        var undo = await handler.ApplyAsync(change);

        Assert.NotNull(undo);
        Assert.Equal("Opening", Assert.Single((await kit.Service.ReadAsync(shape.Normal.WorkId)).Everyone).Name);
    }

    [Fact]
    public async Task EditorBookmarks_ANonAdministratorGetsNoListAndNoWrite_EverywhereTheEngineChecks()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var bookmark = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 724);
        var change = ChangeOf(EditorOp(EditorBookmarkOpKinds.Add, bookmark.Id));

        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() => kit.Service.ReadAsync(shape.Normal.WorkId));
        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() => kit.Service.ApplyAsync(shape.Normal.WorkId, change));
        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() => kit.Service.ExportAsync(shape.Normal.WorkId));
        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() =>
            kit.Service.CompareAsync(shape.Normal.WorkId, new EditorBookmarkCompareRequestDto()));
        Assert.Equal(EditorBookmarksService.NotAllowed, await kit.Service.CheckAsync(shape.Normal.WorkId, change));
        Assert.DoesNotContain(
            (await new EditorBookmarkRepository(_database).GetByWorksAsync([shape.Normal.WorkId], null)),
            row => row.IsGlobal);
    }

    [Fact]
    public async Task EditorBookmarks_AVaultTitleIsRefused_AndALockedOneIsNotFound()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var unlocked = EditorBookmarksFor(shape.Account, session);
        var secret = await BookmarkOf(VaultProfile, shape.Secret.WorkId, shape.Secret.AssetId, 90);
        var change = ChangeOf(EditorOp(EditorBookmarkOpKinds.Add, secret.Id));

        await Assert.ThrowsAsync<EditorBookmarkVaultException>(() => unlocked.Service.ReadAsync(shape.Secret.WorkId));
        await Assert.ThrowsAsync<EditorBookmarkVaultException>(() => unlocked.Service.ApplyAsync(shape.Secret.WorkId, change));
        Assert.Equal(EditorBookmarksService.InTheVault, await unlocked.Service.CheckAsync(shape.Secret.WorkId, change));

        // The same Vault title with Vault mode off is not found at all.
        var locked = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() => locked.Service.ReadAsync(shape.Secret.WorkId));
    }

    [Fact]
    public async Task EditorBookmarks_Export_IsTheTitlesGlobalListAsAFile_AndImportLaysItBesideTheList()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        var mine = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 761);
        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(
            EditorOp(EditorBookmarkOpKinds.Add, mine.Id), EditorOp(EditorBookmarkOpKinds.Rename, mine.Id, name: "The reveal")));
        await BookmarkOf(AddOtherProfile(), shape.Normal.WorkId, shape.Normal.AssetId, 4000);

        var file = await kit.Service.ExportAsync(shape.Normal.WorkId);

        // Only the Global list is in the file, never a bookmark that is not in it.
        Assert.Equal(1, file.BookmarkCount);
        Assert.Equal(BookmarkSetService.ExportedFileName, file.FileName);
        var compare = await kit.Service.CompareAsync(
            shape.Normal.WorkId,
            new EditorBookmarkCompareRequestDto { ContentBase64 = FileOf(ImdbOfTheFilm, 7200, Item(763), Item(6330)).ContentBase64, RunningTimeSeconds = 7200 });
        Assert.Equal(BookmarkPackMatchResults.Match, compare.Match);
        Assert.Equal([BookmarkClosenessLabels.SameSpot, BookmarkClosenessLabels.New], compare.Rows.Select(row => row.Closeness));
        Assert.Equal(mine.Id, compare.Rows[0].Yours!.Id);
        // Comparing changes nothing.
        Assert.Equal(EditorBookmarkStates.Definitive, (await kit.Service.ReadAsync(shape.Normal.WorkId)).State);
    }

    // ── A show, a season or an album ─────────────────────────────────────────────────────────────

    private sealed class FamilySpy(EditorFamilyMembers? family) : IEditorFamilySource
    {
        public Task<EditorFamilyMembers?> GetFamilyMembersAsync(Guid workId, CancellationToken ct = default) => Task.FromResult(family);
    }

    private async Task<(EditorBookmarkOverviewService Overview, (Guid WorkId, Guid AssetId)[] Episodes)> ShowAsync(VaultShape shape, EditorBookmarkKit kit)
    {
        var episodes = new[]
        {
            await InsertOwnedWorkWithIdAsync(shape.NormalLibrary, "Pilot", "TV"),
            await InsertOwnedWorkWithIdAsync(shape.NormalLibrary, "Second", "TV"),
            await InsertOwnedWorkWithIdAsync(shape.NormalLibrary, "Return", "TV"),
        };
        var family = new EditorFamilyMembers(Guid.NewGuid(), "The Show", "TV",
        [
            new EditorFamilyGroup(Guid.NewGuid(), "Season 1", 1,
            [
                new EditorFamilyMember(episodes[0].WorkId, "Pilot", 1),
                new EditorFamilyMember(episodes[1].WorkId, "Second", 2),
            ]),
            new EditorFamilyGroup(Guid.NewGuid(), "Season 2", 2, [new EditorFamilyMember(episodes[2].WorkId, "Return", 1)]),
        ]);
        var overview = new EditorBookmarkOverviewService(
            kit.Scope, kit.Service, new FamilySpy(family), kit.Sets, new VaultLibraryRepository(_database), frames: FrameService());
        return (overview, episodes);
    }

    [Fact]
    public async Task EditorBookmarks_TheShowOverview_IsGroupedBySeason_WithWhatGlobalHoldsOnEachEpisode()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var (overview, episodes) = await ShowAsync(shape, kit);
        MakeVideo(episodes[0].AssetId);
        await BookmarkOf(VaultProfile, episodes[0].WorkId, episodes[0].AssetId, 100);
        await BookmarkOf(AddOtherProfile(), episodes[0].WorkId, episodes[0].AssetId, 900);
        var third = await BookmarkOf(VaultProfile, episodes[2].WorkId, episodes[2].AssetId, 50);
        await kit.Service.ApplyAsync(episodes[2].WorkId, ChangeOf(
            EditorOp(EditorBookmarkOpKinds.Add, third.Id), EditorOp(EditorBookmarkOpKinds.Rename, third.Id, name: "Cold open")));

        var result = await overview.OverviewAsync(Guid.NewGuid());

        Assert.Equal("The Show", result.RootTitle);
        Assert.Equal(["Season 1", "Season 2"], result.Groups.Select(group => group.Title));
        Assert.Equal(["S1 E1", "S1 E2"], result.Groups[0].Rows.Select(row => row.Code));
        // Profiles' bookmarks on the first episode are not pooled into Global: it holds only what was added.
        Assert.Equal([0, 0], result.Groups[0].Rows.Select(row => row.EveryoneCount));
        Assert.All(result.Groups[0].Rows, row => Assert.Equal(EditorBookmarkStates.Pooled, row.State));
        var returned = Assert.Single(result.Groups[1].Rows);
        Assert.Equal("S2 E1", returned.Code);
        Assert.Equal(EditorBookmarkStates.Definitive, returned.State);
        Assert.Equal(1, returned.EveryoneCount);
        Assert.Equal(1, result.EveryoneTotal);
        Assert.Equal(1, result.DefinitiveCount);
    }

    [Fact]
    public async Task EditorBookmarks_TheShowFile_NamesEachEpisodeBySeasonAndEpisodeNumber_AndImportMatchesThemBackTheSameWay()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var (overview, episodes) = await ShowAsync(shape, kit);
        var pilot = await BookmarkOf(VaultProfile, episodes[0].WorkId, episodes[0].AssetId, 100);
        var returned = await BookmarkOf(VaultProfile, episodes[2].WorkId, episodes[2].AssetId, 50);
        await kit.Service.ApplyAsync(episodes[0].WorkId, ChangeOf(
            EditorOp(EditorBookmarkOpKinds.Add, pilot.Id), EditorOp(EditorBookmarkOpKinds.Rename, pilot.Id, name: "Pilot scene")));
        await kit.Service.ApplyAsync(episodes[2].WorkId, ChangeOf(
            EditorOp(EditorBookmarkOpKinds.Add, returned.Id), EditorOp(EditorBookmarkOpKinds.Rename, returned.Id, name: "Return scene")));

        var file = await overview.ExportAsync(Guid.NewGuid());

        var content = BookmarkShowPackArchive.Read(Convert.FromBase64String(file.ContentBase64));
        Assert.Equal(2, file.BookmarkCount);
        Assert.Equal([(1, 1), (2, 1)], content.Episodes.Select(episode => (episode.SeasonNumber, episode.EpisodeNumber)));
        Assert.DoesNotContain(content.Episodes, episode => episode.SeasonNumber == 1 && episode.EpisodeNumber == 2);

        var incoming = BookmarkShowPackArchive.Write(
            new BookmarkShowPackContent("From a friend",
            [
                new BookmarkShowPackEpisode(1, 1, "Pilot", null, [new BookmarkPackBookmark(101, null, null, null, "Pilot scene", null), new BookmarkPackBookmark(700, null, null, null, "New one", null)]),
                new BookmarkShowPackEpisode(1, 2, "Second", null, [new BookmarkPackBookmark(30, null, null, null, "Second scene", null)]),
                new BookmarkShowPackEpisode(9, 9, "Not in this show", null, [new BookmarkPackBookmark(30, null, null, null, "Lost", null)]),
            ]),
            DateTimeOffset.UtcNow);
        var compare = await overview.CompareAsync(
            Guid.NewGuid(), new EditorBookmarkCompareRequestDto { ContentBase64 = Convert.ToBase64String(incoming) });

        Assert.Equal(1, compare.UnmatchedEpisodes);
        var matchedPilot = compare.Episodes.Single(episode => episode.SeasonNumber == 1 && episode.EpisodeNumber == 1);
        Assert.Equal(episodes[0].WorkId, matchedPilot.WorkId);
        Assert.Equal([BookmarkClosenessLabels.SameSpot, BookmarkClosenessLabels.New], matchedPilot.Compare!.Rows.Select(row => row.Closeness));
        Assert.Equal(1, matchedPilot.NewCount);
        var second = compare.Episodes.Single(episode => episode.SeasonNumber == 1 && episode.EpisodeNumber == 2);
        Assert.Equal(1, second.NewCount);
        Assert.Null(compare.Episodes.Single(episode => episode.SeasonNumber == 9).WorkId);
    }

    [Fact]
    public async Task EditorBookmarks_AFileThatIsNotAShowFile_IsRefusedWithAPlainReason()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var (overview, _) = await ShowAsync(shape, EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account)));

        var refused = await Assert.ThrowsAsync<BookmarkPackInvalidException>(() =>
            overview.CompareAsync(Guid.NewGuid(), new EditorBookmarkCompareRequestDto { ContentBase64 = Convert.ToBase64String("not a zip"u8.ToArray()) }));

        Assert.Equal("This is not a show bookmark file.", refused.Message);
    }

    [Fact]
    public async Task EditorBookmarks_TheShowOverviewAndFile_AreForAdministratorsOnly()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var (overview, _) = await ShowAsync(shape, EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account)));

        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() => overview.OverviewAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() => overview.ExportAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() =>
            overview.CompareAsync(Guid.NewGuid(), new EditorBookmarkCompareRequestDto()));
    }

    // ── What a file carries and what it refuses ──────────────────────────────────────────────────

    private static byte[] Jpeg(int length, byte marker = 0x11)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, marker);
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[^2] = 0xFF; bytes[^1] = 0xD9;
        return bytes;
    }

    /// <summary>A bookmark with a name, a private note and a private description, as a profile would have made it.</summary>
    private Task<AudiobookBookmarkDto> PrivateBookmarkOf(Guid profile, Guid workId, Guid assetId, double at) =>
        new AudiobookBookmarkRepository(_database).CreateAsync(
            profile, workId, new CreateAudiobookBookmarkRequestDto
            {
                AssetId = assetId,
                PositionSeconds = at,
                DurationSeconds = 5400,
                Label = "The reveal",
                Note = "my private thought",
                Description = "my private details",
            });

    [Fact]
    public async Task EditorBookmarks_AnExportBeforeAnySetIsMade_IsEmpty_AndAGlobalSetCarriesItsNamesAndNotes()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        var someone = await PrivateBookmarkOf(AddOtherProfile(), shape.Normal.WorkId, shape.Normal.AssetId, 761);

        // Nobody has made a Global set yet: it is empty, so there is no file, and nothing of anyone's private bookmarks leaves the library.
        await Assert.ThrowsAsync<BookmarkPackInvalidException>(() => kit.Service.ExportAsync(shape.Normal.WorkId));

        // The administrator's own Global list keeps what they wrote.
        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(EditorOp(EditorBookmarkOpKinds.Add, someone.Id)));
        var definitive = BookmarkPackArchive.Read(Convert.FromBase64String((await kit.Service.ExportAsync(shape.Normal.WorkId)).ContentBase64));

        var definitiveItem = Assert.Single(definitive.Bookmarks);
        Assert.Equal("The reveal", definitiveItem.Note);
        Assert.Equal("my private details", definitiveItem.Description);
    }

    [Fact]
    public async Task EditorBookmarks_AShowExportLeavesOutEpisodesWithNoGlobalSet_AndAnEpisodeWithOneKeepsItsNotes()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var (overview, episodes) = await ShowAsync(shape, kit);
        var pilot = await PrivateBookmarkOf(AddOtherProfile(), episodes[0].WorkId, episodes[0].AssetId, 100);
        var returned = await PrivateBookmarkOf(AddOtherProfile(), episodes[2].WorkId, episodes[2].AssetId, 50);
        await kit.Service.ApplyAsync(episodes[2].WorkId, ChangeOf(EditorOp(EditorBookmarkOpKinds.Add, returned.Id)));

        var file = BookmarkShowPackArchive.Read(Convert.FromBase64String((await overview.ExportAsync(Guid.NewGuid())).ContentBase64));

        // The first episode has no Global set, so another profile's bookmark on it is not in the file; the third was added.
        Assert.DoesNotContain(file.Episodes, episode => episode.SeasonNumber == 1 && episode.EpisodeNumber == 1);
        var curatedItem = Assert.Single(file.Episodes.Single(episode => episode.SeasonNumber == 2 && episode.EpisodeNumber == 1).Bookmarks);
        Assert.Equal("The reveal", curatedItem.Note);
        Assert.Equal("my private details", curatedItem.Description);
        Assert.NotEqual(pilot.Id, returned.Id);
    }

    [Fact]
    public async Task EditorBookmarks_AVideoBookmarkTravelsWithItsPicture_AndOnlyARealSmallJpegGoesIntoTheFile()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        var framed = await PrivateBookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 761);
        var broken = await PrivateBookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 3000);
        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(
            EditorOp(EditorBookmarkOpKinds.Add, framed.Id), EditorOp(EditorBookmarkOpKinds.Add, broken.Id)));
        var picture = Jpeg(2000);
        FrameCache().Write(shape.Normal.AssetId, framed.Id, picture);
        FrameCache().Write(shape.Normal.AssetId, broken.Id, [0xFF, 0xD8, 0xFF, 0xD9]);

        var file = BookmarkPackArchive.Read(Convert.FromBase64String((await kit.Service.ExportAsync(shape.Normal.WorkId)).ContentBase64));

        Assert.Equal(picture, file.Bookmarks.Single(item => item.Time == 761).Frame);
        Assert.Null(file.Bookmarks.Single(item => item.Time == 3000).Frame);

        // And the compare hands the file's picture to the row, so the person sees it before choosing.
        var compare = await kit.Service.CompareAsync(
            shape.Normal.WorkId,
            new EditorBookmarkCompareRequestDto
            {
                ContentBase64 = FileOf(ImdbOfTheFilm, 7200, new BookmarkPackBookmark(5000, null, null, null, "with a picture", null, Frame: picture)).ContentBase64,
                RunningTimeSeconds = 7200,
            });
        Assert.Equal(Convert.ToBase64String(picture), Assert.Single(compare.Rows).Incoming.FrameBase64);
    }

    [Fact]
    public async Task EditorBookmarks_AShowFileCarriesEachEpisodesPictures()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var (overview, episodes) = await ShowAsync(shape, kit);
        MakeVideo(episodes[0].AssetId);
        var bookmark = await PrivateBookmarkOf(VaultProfile, episodes[0].WorkId, episodes[0].AssetId, 100);
        await kit.Service.ApplyAsync(episodes[0].WorkId, ChangeOf(EditorOp(EditorBookmarkOpKinds.Add, bookmark.Id)));
        var picture = Jpeg(1500, 0x22);
        FrameCache().Write(episodes[0].AssetId, bookmark.Id, picture);

        var file = BookmarkShowPackArchive.Read(Convert.FromBase64String((await overview.ExportAsync(Guid.NewGuid())).ContentBase64));

        Assert.Equal(picture, Assert.Single(Assert.Single(file.Episodes).Bookmarks).Frame);
    }

    [Fact]
    public async Task EditorBookmarks_AnImportKeepsAValidPictureTheFileCarried_AndMakesItsOwnForAnyOtherPicture()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var picture = Jpeg(2000, 0x33);
        var oversized = Jpeg(BookmarkPackLimits.MaximumFrameBytes + 1);
        var items = new[]
        {
            new EditorBookmarkImportItem { PositionSeconds = 100, Name = "Valid", FrameBase64 = Convert.ToBase64String(picture) },
            new EditorBookmarkImportItem { PositionSeconds = 200, Name = "Not base64", FrameBase64 = "this is !! not base64" },
            new EditorBookmarkImportItem { PositionSeconds = 300, Name = "Not a picture", FrameBase64 = Convert.ToBase64String("hello there"u8.ToArray()) },
            new EditorBookmarkImportItem { PositionSeconds = 400, Name = "Too large", FrameBase64 = Convert.ToBase64String(oversized) },
            new EditorBookmarkImportItem { PositionSeconds = 500, Name = "No picture" },
        };

        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(EditorOp(EditorBookmarkOpKinds.Import, items: items)));

        var everyone = (await kit.Service.ReadAsync(shape.Normal.WorkId)).Everyone;
        var frames = FrameService();
        var queue = new MediaOperationRepository(_database);
        var valid = everyone.Single(row => row.Name == "Valid");
        Assert.Equal(picture, frames.ReadFrameBytes(valid.AssetId, valid.Id));
        // A picture the file supplied needs no job to make one.
        Assert.Null(await queue.GetByIdempotencyKeyAsync(BookmarkFrameService.KeyFor(valid.Id, 100)));
        foreach (var name in new[] { "Not base64", "Not a picture", "Too large", "No picture" })
        {
            var row = everyone.Single(item => item.Name == name);
            Assert.Null(frames.ReadFrameBytes(row.AssetId, row.Id));
            Assert.NotNull(await queue.GetByIdempotencyKeyAsync(BookmarkFrameService.KeyFor(row.Id, row.PositionSeconds)));
        }
    }

    [Fact]
    public async Task EditorBookmarks_AFileOverTheEncodedSizeLimit_IsRefusedBeforeItIsDecoded()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var (overview, _) = await ShowAsync(shape, kit);
        // Valid base64 all the way: if anything decoded it first, it would fail on the content, not the size.
        var titleTooLarge = new string('A', (int)(BookmarkPackLimits.MaximumFileBytes * 4L / 3) + 100);
        var showTooLarge = new string('A', (int)(EditorBookmarkLimits.MaximumShowFileBytes * 4L / 3) + 100);

        var title = await Assert.ThrowsAsync<BookmarkPackInvalidException>(() =>
            kit.Service.CompareAsync(shape.Normal.WorkId, new EditorBookmarkCompareRequestDto { ContentBase64 = titleTooLarge }));
        var show = await Assert.ThrowsAsync<BookmarkPackInvalidException>(() =>
            overview.CompareAsync(Guid.NewGuid(), new EditorBookmarkCompareRequestDto { ContentBase64 = showTooLarge }));

        Assert.Equal("This is not a bookmark file, or it is too large.", title.Message);
        Assert.Equal("This is not a bookmark file, or it is too large.", show.Message);
        await Assert.ThrowsAsync<BookmarkPackInvalidException>(() =>
            kit.Service.CompareAsync(shape.Normal.WorkId, new EditorBookmarkCompareRequestDto { ContentBase64 = string.Empty }));
    }

    [Fact]
    public async Task EditorBookmarks_AKidsProfile_IsRefusedEverywhere_EvenOnAnAdministratorAccount()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var bookmark = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 724);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET role='RestrictedProfile' WHERE id=@profile;", new { profile = VaultProfile });
        }

        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var (overview, _) = await ShowAsync(shape, kit);
        var change = ChangeOf(EditorOp(EditorBookmarkOpKinds.Add, bookmark.Id));

        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() => kit.Service.ReadAsync(shape.Normal.WorkId));
        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() => kit.Service.ApplyAsync(shape.Normal.WorkId, change));
        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() => kit.Service.ExportAsync(shape.Normal.WorkId));
        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() => kit.Service.CompareAsync(shape.Normal.WorkId, new EditorBookmarkCompareRequestDto()));
        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() => overview.OverviewAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() => overview.ExportAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<PlayerBookmarkAdminDeniedException>(() => overview.CompareAsync(Guid.NewGuid(), new EditorBookmarkCompareRequestDto()));
        Assert.Equal(EditorBookmarksService.NotAllowed, await kit.Service.CheckAsync(shape.Normal.WorkId, change));
        Assert.DoesNotContain(await new EditorBookmarkRepository(_database).GetByWorksAsync([shape.Normal.WorkId], null), row => row.IsGlobal);
    }

    [Fact]
    public async Task EditorBookmarks_ABook_HasNoGlobalSet_SoEveryChangeIsRefused_AndNothingIsWritten()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var book = await InsertOwnedWorkWithIdAsync(shape.NormalLibrary, "A book", "Books");
        var place = await BookmarkOf(VaultProfile, book.WorkId, book.AssetId, 3);
        var imports = ChangeOf(EditorOp(EditorBookmarkOpKinds.Import, items:
        [
            new EditorBookmarkImportItem { PositionSeconds = 3, Locator = Spot(3, 5), ChapterIndex = 3, Name = "The turn" },
        ]));
        var add = ChangeOf(EditorOp(EditorBookmarkOpKinds.Add, place.Id));

        Assert.Equal(EditorBookmarksService.NoGlobal, await kit.Service.CheckAsync(book.WorkId, imports));
        Assert.Equal(EditorBookmarksService.NoGlobal, await kit.Service.CheckAsync(book.WorkId, add));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.ApplyAsync(book.WorkId, imports));
        Assert.Equal(EditorBookmarksService.NoGlobal, refused.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.ApplyAsync(book.WorkId, add));

        // Nothing was flagged, imported or marked as a made set; the profile's own bookmark is untouched.
        var read = await kit.Service.ReadAsync(book.WorkId);
        Assert.Equal(EditorBookmarkStates.Pooled, read.State);
        Assert.Empty(read.Everyone);
        Assert.Equal(place.Id, Assert.Single(await StoredBookmarksAsync(book.WorkId)).Id);
    }
}
