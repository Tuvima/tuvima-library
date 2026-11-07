using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

// The player's Bookmarks panel (unified player v2 D21, acceptance A11). The Engine returns, in one list, the viewer's own bookmarks (Mine) and the
// title's Everyone list: the administrator's set once there is one, otherwise every profile's bookmarks pooled with same-spot ones merged. Each row is
// marked by the Engine (isYours, inEveryone), so the Dashboard never compares profile ids. "May see the title, may see its bookmarks": a Vault file
// has no pooled view, a Kid profile gets Everyone only for titles it may watch, and another profile's row can be neither renamed nor deleted.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private void MakeDefinitive(Guid workId, Guid madeBy)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT OR REPLACE INTO bookmark_definitive_sets (work_id, made_at, made_by) VALUES (@workId, @madeAt, @madeBy);",
            new { workId = GuidSql.ToBlob(workId), madeAt = DateTimeOffset.UtcNow.ToString("O"), madeBy = GuidSql.ToBlob(madeBy) });
    }

    private Task<IReadOnlyList<EditorBookmarkRow>> StoredBookmarksAsync(Guid workId) =>
        new EditorBookmarkRepository(_database).GetByWorksAsync([workId], null);

    private (PlayerService Player, PlayerCatalogueScope Scope) BookmarkPlayerFor(DefaultHttpContext context)
    {
        var services = new ServiceCollection()
            .AddSingleton<IRequestAuthorityResolver>(new RequestAuthorityResolver(_accounts, _applications))
            .AddSingleton(CreateService(context, RealReaders()))
            .BuildServiceProvider();
        context.RequestServices = services;
        var scope = new PlayerCatalogueScope(new HttpContextAccessor { HttpContext = context });
        var player = new PlayerService(null!, null!, null!, null!, null!, null!, null!,
            new AudiobookBookmarkRepository(_database), null!, scope, null!, null!, bookmarkVisibility: BookmarkVisibility());
        return (player, scope);
    }

    [Fact]
    public async Task PlayerBookmarks_WithNoAdministratorsSet_EveryoneIsTheProfilesPooled_AndEachRowSaysWhoseItIs()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var other = AddOtherProfile();
        var mine = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 120);
        var theirs = await BookmarkOf(other, shape.Normal.WorkId, shape.Normal.AssetId, 600);
        var (player, _) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));

        var listed = await player.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId);

        Assert.Equal([mine.Id, theirs.Id], listed.Select(row => row.Id));
        var own = listed.Single(row => row.Id == mine.Id);
        var another = listed.Single(row => row.Id == theirs.Id);
        Assert.True(own.IsYours);
        Assert.True(own.InEveryone);
        Assert.False(another.IsYours);
        Assert.True(another.InEveryone);
        Assert.Equal("Someone else", another.OwnerName);
    }

    [Fact]
    public async Task PlayerBookmarks_TwoProfilesBookmarkingTheSameSpot_AreOneRowInEveryone_AndBothKeepTheirOwnUnderMine()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var other = AddOtherProfile();
        var mine = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 300);
        var theirs = await BookmarkOf(other, shape.Normal.WorkId, shape.Normal.AssetId, 301);
        var (player, _) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));

        var listed = await player.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId);

        // One of the two stands in Everyone's list; the viewer's own stays theirs either way.
        Assert.Single(listed, row => row.InEveryone);
        Assert.Contains(listed, row => row.Id == mine.Id && row.IsYours);
        Assert.DoesNotContain(listed, row => row.Id == theirs.Id && !row.InEveryone);
    }

    [Fact]
    public async Task PlayerBookmarks_OnceTheAdministratorHasASet_ItReplacesThePool_AndOthersPersonalOnesLeaveTheList()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var other = AddOtherProfile();
        var mine = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 120);
        var curated = await BookmarkOf(other, shape.Normal.WorkId, shape.Normal.AssetId, 240, global: true);
        var theirs = await BookmarkOf(other, shape.Normal.WorkId, shape.Normal.AssetId, 360);
        var (player, _) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));
        Assert.Equal(3, (await player.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId)).Count);

        MakeDefinitive(shape.Normal.WorkId, other);
        var listed = await player.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId);

        Assert.Equal([mine.Id, curated.Id], listed.Select(row => row.Id));
        Assert.False(listed.Single(row => row.Id == mine.Id).InEveryone);
        Assert.True(listed.Single(row => row.Id == curated.Id).InEveryone);
        Assert.DoesNotContain(listed, row => row.Id == theirs.Id);
    }

    [Fact]
    public async Task PlayerBookmarks_AnotherProfilesBookmarkOnATitleTheViewerCannotSee_NeverAppears_AndNoFrameOrEditReachesIt()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var other = AddOtherProfile();
        var secret = await BookmarkOf(other, shape.Secret.WorkId, shape.Secret.AssetId, 90);
        var (locked, _) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));

        Assert.Empty(await locked.GetAudiobookBookmarksAsync(null, shape.Secret.WorkId));
        Assert.Null(await locked.GetBookmarkFramePathAsync(null, secret.Id));
        Assert.Null(await locked.UpdateOwnAudiobookBookmarkAsync(null, secret.Id, new UpdateAudiobookBookmarkRequestDto { Label = "Mine now" }));
        Assert.False(await locked.DeleteAudiobookBookmarkAsync(null, secret.Id));
        // The row is untouched.
        var stored = Assert.Single(await StoredBookmarksAsync(shape.Secret.WorkId));
        Assert.Equal(secret.Id, stored.Id);
        Assert.Null(stored.Label);
    }

    [Fact]
    public async Task PlayerBookmarks_AVaultTitleHasNoPooledView_EvenForAnUnlockedProfile()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var other = AddOtherProfile();
        var mine = await BookmarkOf(VaultProfile, shape.Secret.WorkId, shape.Secret.AssetId, 60);
        var theirs = await BookmarkOf(other, shape.Secret.WorkId, shape.Secret.AssetId, 600);
        var (unlocked, _) = BookmarkPlayer(shape.Account, session);

        var listed = await unlocked.GetAudiobookBookmarksAsync(null, shape.Secret.WorkId);

        Assert.Equal(mine.Id, Assert.Single(listed).Id);
        Assert.DoesNotContain(listed, row => row.Id == theirs.Id);
        Assert.Null(await unlocked.GetBookmarkFramePathAsync(null, theirs.Id));
    }

    [Fact]
    public async Task PlayerBookmarks_AWorkWithOrdinaryAndVaultFiles_PoolsOnlyItsOrdinaryFiles()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var vaultCopy = Guid.NewGuid();
        await InsertAssetForWorkAsync(shape.Normal.WorkId, vaultCopy, shape.VaultLibrary, "Vault copy", createWork: false);
        var other = AddOtherProfile();
        var ordinary = await BookmarkOf(other, shape.Normal.WorkId, shape.Normal.AssetId, 100);
        var inVault = await BookmarkOf(other, shape.Normal.WorkId, vaultCopy, 200);
        var (unlocked, _) = BookmarkPlayer(shape.Account, session);

        var listed = await unlocked.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId);

        Assert.Contains(listed, row => row.Id == ordinary.Id && row.InEveryone);
        Assert.DoesNotContain(listed, row => row.Id == inVault.Id);
    }

    [Fact]
    public async Task PlayerBookmarks_AKidProfile_GetsEveryoneOnlyForTitlesItMayWatch()
    {
        var (account, profile, _, items) = await SeedKidsCatalogueAsync();
        var other = AddOtherProfile();
        var family = await BookmarkOf(other, items.FamilyFilm.WorkId, items.FamilyFilm.AssetId, 100);
        var mature = await BookmarkOf(other, items.MatureFilm.WorkId, items.MatureFilm.AssetId, 100);
        KidsLimitTestSupport.SetLimit(_database, profile, watch: "PG", read: "G", listen: "G", showUnrated: false);
        var (kid, _) = BookmarkPlayerFor(HumanContext(account, profile));

        var allowed = await kid.GetAudiobookBookmarksAsync(null, items.FamilyFilm.WorkId);
        var denied = await kid.GetAudiobookBookmarksAsync(null, items.MatureFilm.WorkId);

        Assert.Equal(family.Id, Assert.Single(allowed).Id);
        Assert.True(allowed[0].InEveryone);
        Assert.Empty(denied);
        Assert.Null(await kid.GetBookmarkFramePathAsync(null, mature.Id));
    }

    [Fact]
    public async Task PlayerBookmarks_EditingAndDeleting_AreRefusedForAnotherProfilesRow_AndForOnesInTheAdministratorsSet()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var other = AddOtherProfile();
        var theirs = await BookmarkOf(other, shape.Normal.WorkId, shape.Normal.AssetId, 360);
        var mine = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 120);
        var curated = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 900, global: true);
        var (player, _) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));

        // Another profile's row, even though it is visible in Everyone: not found, nothing changes.
        Assert.Null(await player.UpdateOwnAudiobookBookmarkAsync(null, theirs.Id, new UpdateAudiobookBookmarkRequestDto { Label = "Hijacked", Note = "x" }));
        Assert.False(await player.DeleteAudiobookBookmarkAsync(null, theirs.Id));
        // The viewer's own row that sits in the administrator's set is changed in the editor, never here.
        Assert.Null(await player.UpdateOwnAudiobookBookmarkAsync(null, curated.Id, new UpdateAudiobookBookmarkRequestDto { Label = "Renamed" }));
        var untouched = await StoredBookmarksAsync(shape.Normal.WorkId);
        Assert.DoesNotContain(untouched, row => row.Label == "Hijacked" || row.Label == "Renamed");
        Assert.Contains(untouched, row => row.Id == theirs.Id);

        // The viewer's own row is renamed, and the changed row comes back marked the same way the list marks it.
        var renamed = await player.UpdateOwnAudiobookBookmarkAsync(null, mine.Id, new UpdateAudiobookBookmarkRequestDto { Label = "  The reveal ", Note = "Watch the window" });
        Assert.NotNull(renamed);
        Assert.Equal("The reveal", renamed!.Label);
        Assert.Equal("Watch the window", renamed.Note);
        Assert.True(renamed.IsYours);
        Assert.True(await player.DeleteAudiobookBookmarkAsync(null, mine.Id));
    }

    [Fact]
    public async Task PlayerBookmarks_ANameOrNoteOverTheLimit_IsRefused_AndNothingChanges()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var mine = await BookmarkOf(VaultProfile, shape.Normal.WorkId, shape.Normal.AssetId, 120);
        var (player, _) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));

        await Assert.ThrowsAsync<ArgumentException>(() => player.UpdateOwnAudiobookBookmarkAsync(
            null, mine.Id, new UpdateAudiobookBookmarkRequestDto { Label = new string('a', PlayerService.MaximumBookmarkLabelLength + 1) }));
        await Assert.ThrowsAsync<ArgumentException>(() => player.UpdateOwnAudiobookBookmarkAsync(
            null, mine.Id, new UpdateAudiobookBookmarkRequestDto { Note = new string('a', AudiobookBookmarkNotePolicy.MaximumLength + 1) }));
        Assert.Null((await StoredBookmarksAsync(shape.Normal.WorkId)).Single().Label);
    }
}
