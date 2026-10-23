using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

// Bookmarks in videos and audiobooks (Videos spec WP15, D43). The Engine, not the Dashboard, decides who may share a bookmark with
// everyone, and a bookmark on a Vault file follows the Vault rules: while Vault mode is off it cannot be made, listed or deleted
// (not found), and unlocking brings it back.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
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
            new AudiobookBookmarkRepository(_database), null!, scope, null!, null!);
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
    public async Task Bookmarks_OnlyAServerAdministratorCanShareOneWithEveryone(bool administrator)
    {
        var shape = await SeedVaultAsync(administrator, grantedTheVaultLibrary: false);
        var (player, scope) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));

        Assert.Equal(administrator, await scope.IsServerAdministratorAsync(default));

        var personal = await player.CreateAudiobookBookmarkAsync(null, shape.Normal.WorkId, BookmarkAt(shape.Normal.AssetId, 750));
        Assert.False(personal.IsGlobal);
        Assert.Equal("A longer description of the scene.", personal.Description);

        if (administrator)
        {
            var shared = await player.CreateAudiobookBookmarkAsync(null, shape.Normal.WorkId, BookmarkAt(shape.Normal.AssetId, 900, global: true));
            Assert.True(shared.IsGlobal);
        }
        else
        {
            await Assert.ThrowsAsync<PlayerBookmarkSharingDeniedException>(() =>
                player.CreateAudiobookBookmarkAsync(null, shape.Normal.WorkId, BookmarkAt(shape.Normal.AssetId, 900, global: true)));
            Assert.Single(await player.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId));
        }
    }

    [Fact]
    public async Task Bookmarks_AGlobalBookmark_IsSeenByAStandardProfile_ButOnlyAnAdministratorCanDeleteIt()
    {
        var shape = await SeedVaultAsync(administrator: false, grantedTheVaultLibrary: false);
        var (standard, _) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));
        // Made earlier by an administrator (the profile here stands in for the maker).
        var shared = await new AudiobookBookmarkRepository(_database).CreateAsync(
            VaultProfile, shape.Normal.WorkId, BookmarkAt(shape.Normal.AssetId, 300, global: true));

        var seen = Assert.Single(await standard.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId));
        Assert.True(seen.IsGlobal);
        Assert.False(await standard.DeleteAudiobookBookmarkAsync(null, shared.Id));
        Assert.Single(await standard.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId));
    }

    [Fact]
    public async Task Bookmarks_AnAdministrator_CanDeleteAGlobalBookmark()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: false);
        var (admin, _) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));
        var shared = await admin.CreateAudiobookBookmarkAsync(null, shape.Normal.WorkId, BookmarkAt(shape.Normal.AssetId, 300, global: true));

        Assert.True(await admin.DeleteAudiobookBookmarkAsync(null, shared.Id));
        Assert.Empty(await admin.GetAudiobookBookmarksAsync(null, shape.Normal.WorkId));
    }

    [Fact]
    public async Task Bookmarks_OnAVaultVideo_AreNotFoundWhileLocked_AndReturnOnceUnlocked()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var (unlocked, _) = BookmarkPlayer(shape.Account, session);
        var saved = await unlocked.CreateAudiobookBookmarkAsync(null, shape.Secret.WorkId, BookmarkAt(shape.Secret.AssetId, 120));
        var shared = await unlocked.CreateAudiobookBookmarkAsync(null, shape.Secret.WorkId, BookmarkAt(shape.Secret.AssetId, 240, global: true));
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
