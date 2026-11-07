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

// The editor's Bookmarks tab (universal editor v5 D37, WP15). Only an administrator reads or changes a title's Everyone's bookmarks, the
// Engine checks it on the read and on the commit, and a Vault title is refused. The first change makes the list a definitive set that the
// player then shows; Start over pools it again; Change time queues a new picture; and the show's own overview and file are keyed by season
// and episode number.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private sealed record EditorBookmarkKit(
        EditorBookmarksService Service, PlayerCatalogueScope Scope, SystemActivityRepository Activity, BookmarkSetService Sets, PlayerService Player);

    private EditorBookmarkKit EditorBookmarksFor(Guid account, Guid? session)
    {
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
        var sets = new BookmarkSetService(scope, bookmarks, new BookmarkRecognitionRepository(_database), vault, closeness);
        var activity = new SystemActivityRepository(_database);
        var service = new EditorBookmarksService(
            scope, new EditorBookmarkRepository(_database), closeness, sets, FrameService(), vault, activity, NullLogger<EditorBookmarksService>.Instance);
        var player = new PlayerService(null!, null!, null!, null!, null!, null!, null!, bookmarks, null!, scope, null!, null!, closeness: null, frames: FrameService());
        return new EditorBookmarkKit(service, scope, activity, sets, player);
    }

    private static EditorBookmarkOp EditorOp(
        string kind, Guid? bookmark = null, string? name = null, double? seconds = null, IReadOnlyList<EditorBookmarkImportItem>? items = null) =>
        new() { Id = Guid.NewGuid().ToString("N"), Kind = kind, BookmarkId = bookmark, Name = name, PositionSeconds = seconds, Items = items ?? [] };

    private static EditorBookmarkChangeDto ChangeOf(params EditorBookmarkOp[] ops) => new() { Ops = ops };

    [Fact]
    public async Task EditorBookmarks_AnAdministratorAddsAProfilesBookmark_ReviewSeesItAndSaveMakesItTheDefinitiveSetThePlayerShows()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var ben = AddOtherProfile();
        var opening = await BookmarkOf(ben, shape.Normal.WorkId, shape.Normal.AssetId, 724);
        var elsewhere = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 3000);

        var before = await kit.Service.ReadAsync(shape.Normal.WorkId);
        Assert.Equal(EditorBookmarkStates.Pooled, before.State);
        Assert.Equal(2, before.FromProfiles.Count);
        Assert.Equal("Someone else", before.FromProfiles.First(row => row.Id == opening.Id).OwnerName);

        var change = ChangeOf(EditorOp(EditorBookmarkOpKinds.Remove, elsewhere.Id), EditorOp(EditorBookmarkOpKinds.Rename, opening.Id, name: "Opening"));
        Assert.Null(await kit.Service.CheckAsync(shape.Normal.WorkId, change));
        await kit.Service.ApplyAsync(shape.Normal.WorkId, change);

        var after = await kit.Service.ReadAsync(shape.Normal.WorkId);
        Assert.Equal(EditorBookmarkStates.Definitive, after.State);
        var everyone = Assert.Single(after.Everyone);
        Assert.Equal(opening.Id, everyone.Id);
        Assert.Equal("Opening", everyone.Name);
        // The bookmark that left Everyone's stays with its owner, under Mine.
        Assert.Contains(await kit.Player.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId), row => row.Id == elsewhere.Id && !row.IsGlobal);
        Assert.Contains(await kit.Player.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId), row => row.Id == opening.Id && row.IsGlobal);
    }

    [Fact]
    public async Task EditorBookmarks_AddingABookmarkAtTheSameSpotAsOneInEveryones_MergesThem_AndOnlyTheReplacedOnesPictureGoes()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var ben = AddOtherProfile();
        var standing = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 724);
        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(EditorOp(EditorBookmarkOpKinds.Rename, standing.Id, name: "Opening")));
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
        // The one at the same spot that was not in Everyone's was replaced and is gone, with its picture; nothing else lost anything.
        Assert.DoesNotContain(read.FromProfiles, row => row.Id == other.Id);
        Assert.False(frames.Exists(shape.Normal.AssetId, other.Id));
        Assert.True(frames.Exists(shape.Normal.AssetId, untouched.Id));
        Assert.Contains(read.FromProfiles, row => row.Id == unrelated.Id);
        Assert.Contains(read.FromProfiles, row => row.Id == standing.Id && row.InEveryone);
    }

    [Fact]
    public async Task EditorBookmarks_StartingOver_PoolsEveryProfilesBookmarksAgain()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var first = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 300);
        await BookmarkOf(AddOtherProfile(), shape.Normal.WorkId, shape.Normal.AssetId, 2000);
        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(EditorOp(EditorBookmarkOpKinds.Remove, first.Id)));
        Assert.Single((await kit.Service.ReadAsync(shape.Normal.WorkId)).Everyone);

        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(EditorOp(EditorBookmarkOpKinds.StartOver)));

        var read = await kit.Service.ReadAsync(shape.Normal.WorkId);
        Assert.Equal(EditorBookmarkStates.Pooled, read.State);
        Assert.Equal(2, read.Everyone.Count);
    }

    [Fact]
    public async Task EditorBookmarks_ChangingTheTime_QueuesAPictureForTheNewMoment_AndTheOldPictureGoesAtOnce()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        MakeVideo(shape.Normal.AssetId);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var bookmark = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 724);
        FrameCache().Write(shape.Normal.AssetId, bookmark.Id, [0xFF, 0xD8, 0xFF, 0xD9]);

        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(EditorOp(EditorBookmarkOpKinds.ChangeTime, bookmark.Id, seconds: 1800)));

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
            EditorOp(EditorBookmarkOpKinds.ChangeTime, bookmark.Id, seconds: 1800),
            EditorOp(EditorBookmarkOpKinds.Import, items: [new EditorBookmarkImportItem { PositionSeconds = 60, Name = "Titles" }])));
        await undo(default);

        var read = await kit.Service.ReadAsync(shape.Normal.WorkId);
        Assert.Equal(EditorBookmarkStates.Pooled, read.State);
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
            EditorOp(EditorBookmarkOpKinds.Rename, bookmark.Id, name: "Opening"),
            EditorOp(EditorBookmarkOpKinds.ChangeTime, bookmark.Id, seconds: 750),
            EditorOp(EditorBookmarkOpKinds.StartOver)));

        var lines = (await kit.Activity.GetRecentAsync(20)).Where(entry => entry.EntityId == shape.Normal.WorkId).Select(entry => entry.Detail).ToList();
        Assert.Contains(lines, line => line!.Contains("renamed") && line.Contains("12:04"));
        Assert.Contains(lines, line => line!.Contains("moved") && line.Contains("12:30"));
        Assert.Contains(lines, line => line!.Contains("started over from profiles"));
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
            Bookmarks = ChangeOf(EditorOp(EditorBookmarkOpKinds.Rename, bookmark.Id, name: "Opening")),
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
    public async Task EditorBookmarks_Export_IsTheTitlesEveryonesListAsAFile_AndImportLaysItBesideTheList()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        GiveIds(shape.Normal.WorkId, ImdbOfTheFilm);
        var mine = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 761);
        await kit.Service.ApplyAsync(shape.Normal.WorkId, ChangeOf(EditorOp(EditorBookmarkOpKinds.Rename, mine.Id, name: "The reveal")));
        await BookmarkOf(AddOtherProfile(), shape.Normal.WorkId, shape.Normal.AssetId, 4000);

        var file = await kit.Service.ExportAsync(shape.Normal.WorkId);

        // Only Everyone's list is in the file, never a bookmark that is not in it.
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
            kit.Scope, kit.Service, new FamilySpy(family), kit.Sets, new VaultLibraryRepository(_database));
        return (overview, episodes);
    }

    [Fact]
    public async Task EditorBookmarks_TheShowOverview_IsGroupedBySeason_WithWhatEveryoneSeesOnEachEpisode()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var kit = EditorBookmarksFor(shape.Account, NewVaultSession(shape.Account));
        var (overview, episodes) = await ShowAsync(shape, kit);
        MakeVideo(episodes[0].AssetId);
        await BookmarkOf(VaultProfile, episodes[0].WorkId, episodes[0].AssetId, 100);
        await BookmarkOf(AddOtherProfile(), episodes[0].WorkId, episodes[0].AssetId, 900);
        var third = await BookmarkOf(VaultProfile, episodes[2].WorkId, episodes[2].AssetId, 50);
        await kit.Service.ApplyAsync(episodes[2].WorkId, ChangeOf(EditorOp(EditorBookmarkOpKinds.Rename, third.Id, name: "Cold open")));

        var result = await overview.OverviewAsync(Guid.NewGuid());

        Assert.Equal("The Show", result.RootTitle);
        Assert.Equal(["Season 1", "Season 2"], result.Groups.Select(group => group.Title));
        Assert.Equal(["S1 E1", "S1 E2"], result.Groups[0].Rows.Select(row => row.Code));
        Assert.Equal([2, 0], result.Groups[0].Rows.Select(row => row.EveryoneCount));
        var returned = Assert.Single(result.Groups[1].Rows);
        Assert.Equal("S2 E1", returned.Code);
        Assert.Equal(EditorBookmarkStates.Definitive, returned.State);
        Assert.Equal(3, result.EveryoneTotal);
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
        await kit.Service.ApplyAsync(episodes[0].WorkId, ChangeOf(EditorOp(EditorBookmarkOpKinds.Rename, pilot.Id, name: "Pilot scene")));
        await kit.Service.ApplyAsync(episodes[2].WorkId, ChangeOf(EditorOp(EditorBookmarkOpKinds.Rename, returned.Id, name: "Return scene")));

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
}
