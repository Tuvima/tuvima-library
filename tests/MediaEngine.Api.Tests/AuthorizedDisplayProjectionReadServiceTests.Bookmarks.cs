using Dapper;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

// Bookmarks in videos and audiobooks (Videos spec WP15, D43). The player makes only personal bookmarks (the Global set is made in the editor), and a bookmark on a Vault file follows the Vault rules: while Vault mode is off it cannot be made, listed or deleted
// (not found), and unlocking brings it back.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    // The Global list as the player reads it: only the rows an administrator flagged, never other profiles' bookmarks.
    private PlayerBookmarkVisibility BookmarkVisibility() =>
        new(new EditorBookmarkRepository(_database), new BookmarkClosenessProvider(() => null), new VaultLibraryRepository(_database));

    private (PlayerService Player, PlayerCatalogueScope Scope) BookmarkPlayer(Guid account, Guid? session)
    {
        var context = VaultContext(account, session);
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

    private static CreateAudiobookBookmarkRequestDto BookmarkAt(Guid assetId, double seconds, bool global = false) => new()
    {
        AssetId = assetId,
        PositionSeconds = seconds,
        DurationSeconds = 5400,
        Note = "the scene",
        Description = "A longer description of the scene.",
        IsGlobal = global,
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bookmarks_ThePlayerMakesOnlyPersonalOnes_EvenForAServerAdministrator(bool administrator)
    {
        var shape = await SeedVaultAsync(administrator, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var (player, scope) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));

        Assert.Equal(administrator, await scope.IsServerAdministratorAsync(default));

        var personal = await player.CreateAudiobookBookmarkAsync(null, shape.Normal.WorkId, BookmarkAt(shape.Normal.AssetId, 750));
        Assert.False(personal.IsGlobal);
        Assert.Equal("A longer description of the scene.", personal.Description);

        // The Global set is made in the editor's Bookmarks tab; the player refuses the share flag (the route answers 400), whoever asks.
        Assert.False(PlayerService.TryNormalizeAudiobookBookmarkRequest(
            BookmarkAt(shape.Normal.AssetId, 900, global: true), out _, out var refused));
        Assert.Contains("always your own", refused);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            player.CreateAudiobookBookmarkAsync(null, shape.Normal.WorkId, BookmarkAt(shape.Normal.AssetId, 900, global: true)));
        Assert.Single(await player.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId));
    }

    [Fact]
    public async Task Bookmarks_AGlobalBookmark_IsSeenByAStandardProfile_ButNotDeletedFromThePlayer()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var (standard, _) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));
        // Made earlier in the editor by an administrator (the profile here stands in for the maker).
        var shared = await new AudiobookBookmarkRepository(_database).CreateAsync(
            VaultProfile, shape.Normal.WorkId, BookmarkAt(shape.Normal.AssetId, 300, global: true));

        var seen = Assert.Single(await standard.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId));
        Assert.True(seen.IsGlobal);
        Assert.False(await standard.DeleteAudiobookBookmarkAsync(null, shared.Id));
        Assert.Single(await standard.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId));
    }

    [Fact]
    public async Task Bookmarks_AnAdministrator_CannotDeleteAGlobalBookmarkFromThePlayer_ItIsRemovedInTheEditor()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: false);
        var (admin, _) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));
        var shared = await new AudiobookBookmarkRepository(_database).CreateAsync(
            VaultProfile, shape.Normal.WorkId, BookmarkAt(shape.Normal.AssetId, 300, global: true));
        var personal = await admin.CreateAudiobookBookmarkAsync(null, shape.Normal.WorkId, BookmarkAt(shape.Normal.AssetId, 600));

        Assert.False(await admin.DeleteAudiobookBookmarkAsync(null, shared.Id));
        Assert.Contains(await admin.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId), row => row.Id == shared.Id);
        Assert.True(await admin.DeleteAudiobookBookmarkAsync(null, personal.Id));
    }

    [Fact]
    public async Task Bookmarks_OnAMusicTrack_AreRefusedWhenMade_AndTheListIsEmpty()
    {
        var shape = await SeedVaultAsync(administrator: false, hasAStaleHiddenRowOnTheVaultLibrary: false);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE works SET media_type = 'Music' WHERE id = @workId;", new { workId = shape.Normal.WorkId });
        }

        var (player, _) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));

        // Music has no bookmarks at all (global bookmarks v5 D4): nothing is made, and the list stays empty.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            player.CreateAudiobookBookmarkAsync(null, shape.Normal.WorkId, BookmarkAt(shape.Normal.AssetId, 42)));
        Assert.Empty(await player.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId));
        Assert.Empty(await StoredBookmarksAsync(shape.Normal.WorkId));
    }

    [Fact]
    public async Task Bookmarks_OnAVaultVideo_AreNotFoundWhileLocked_AndReturnOnceUnlocked()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var (unlocked, _) = BookmarkPlayer(shape.Account, session);
        var saved = await unlocked.CreateAudiobookBookmarkAsync(null, shape.Secret.WorkId, BookmarkAt(shape.Secret.AssetId, 120));
        var shared = await new AudiobookBookmarkRepository(_database).CreateAsync(
            VaultProfile, shape.Secret.WorkId, BookmarkAt(shape.Secret.AssetId, 240, global: true));
        Assert.Equal(2, (await unlocked.GetAudiobookBookmarksAsync(null, shape.Secret.WorkId)).Count);

        // A new request on a session that is not unlocked: the bookmarks do not exist, personal or shared.
        var (locked, _) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));
        Assert.Empty(await locked.GetAudiobookBookmarksAsync(null, shape.Secret.WorkId));
        await Assert.ThrowsAsync<PlayerResourceDeniedException>(() =>
            locked.CreateAudiobookBookmarkAsync(null, shape.Secret.WorkId, BookmarkAt(shape.Secret.AssetId, 10)));
        Assert.False(await locked.DeleteAudiobookBookmarkAsync(null, saved.Id));
        Assert.False(await locked.DeleteAudiobookBookmarkAsync(null, shared.Id));

        // Nothing was lost by locking.
        var (again, _) = BookmarkPlayer(shape.Account, session);
        Assert.Equal(2, (await again.GetAudiobookBookmarksAsync(null, shape.Secret.WorkId)).Count);
    }
}
