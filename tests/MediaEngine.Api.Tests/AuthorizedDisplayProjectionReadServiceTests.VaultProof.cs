using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.Services;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.Storage.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

// WP17 part C: seeded proof for the Part A read services and the bookmark case that mixes Vault and ordinary files in one work.
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    [Fact]
    public async Task VaultProof_ABookmarkOnTheVaultFileOfAMixedWork_IsAbsentFromEveryListWhileLocked_AndReturnsOnceUnlocked()
    {
        var shape = await SeedVaultAsync(administrator: true, grantedTheVaultLibrary: true);
        var mixedWork = shape.Normal.WorkId;
        var ordinaryAsset = shape.Normal.AssetId;
        var vaultAsset = Guid.NewGuid();
        await InsertAssetForWorkAsync(mixedWork, vaultAsset, shape.VaultLibrary, "Hidden cut", createWork: false);
        var bookmarks = new AudiobookBookmarkRepository(_database);
        var ordinaryMark = await OwnBookmarkAsync(bookmarks, mixedWork, ordinaryAsset, 100);
        var vaultMark = await OwnBookmarkAsync(bookmarks, mixedWork, vaultAsset, 200);
        var sharedVaultMark = await OwnBookmarkAsync(bookmarks, mixedWork, vaultAsset, 300, global: true);

        var (locked, _) = BookmarkPlayer(shape.Account, NewVaultSession(shape.Account));
        var seenLocked = await locked.GetAudiobookBookmarksAsync(null, mixedWork);

        Assert.DoesNotContain(seenLocked, mark => mark.AssetId == vaultAsset);
        Assert.DoesNotContain(seenLocked, mark => mark.Id == vaultMark.Id || mark.Id == sharedVaultMark.Id);
        Assert.Equal([ordinaryMark.Id], seenLocked.Select(mark => mark.Id));
        Assert.False(await locked.DeleteAudiobookBookmarkAsync(null, vaultMark.Id));
        Assert.False(await locked.DeleteAudiobookBookmarkAsync(null, sharedVaultMark.Id));

        var session = NewVaultSession(shape.Account);
        await UnlockVaultAsync(session);
        var (unlocked, _) = BookmarkPlayer(shape.Account, session);
        var seenUnlocked = await unlocked.GetAudiobookBookmarksAsync(null, mixedWork);
        Assert.Contains(seenUnlocked, mark => mark.Id == vaultMark.Id);
        Assert.Contains(seenUnlocked, mark => mark.Id == sharedVaultMark.Id);
    }
}
